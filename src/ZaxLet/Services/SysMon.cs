using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WF = System.Windows.Forms;

namespace ZaxLet;

/// <summary>Sampling ringan tanpa driver: CPU (ntdll), RAM, jaringan, baterai, GPU NVIDIA via nvidia-smi, suhu CPU opsional via LibreHardwareMonitor.</summary>
public sealed class SysMon
{
    public double Cpu;
    public double[] Cores = Array.Empty<double>();
    public double MemUsedGb, MemTotalGb, MemPct;
    public double NetDownKb, NetUpKb;
    public double BatPct = -1; public bool Charging; public string BatLeft = "";
    public double? GpuUtil, GpuTemp, GpuPower, CpuTemp;
    public List<(string Name, long Bytes, string Path)> Top = new();
    public readonly PerfFormat.PingRing GwPing = new(), NetPing = new();
    public bool NetUp { get; private set; }
    public bool HasGateway { get; private set; }
    public string ConnectionType { get; private set; } = "none";

    static readonly IPAddress InternetPingAddr = new(new byte[] { 1, 1, 1, 1 });
    DateTime _pingAt = DateTime.MinValue;
    int _pingBusy;
    string _lastGw;

    Native.ProcPerf[] _prev;
    long _rx, _tx; DateTime _netAt;
    DateTime _slowAt = DateTime.MinValue;
    bool _lhmTried;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(500) };

    public void Sample()
    {
        SampleCpu();
        var ms = new Native.MemStatus { Length = (uint)Marshal.SizeOf<Native.MemStatus>() };
        if (Native.GlobalMemoryStatusEx(ref ms))
        {
            MemTotalGb = ms.TotalPhys / 1073741824.0;
            MemUsedGb = (ms.TotalPhys - ms.AvailPhys) / 1073741824.0;
            MemPct = MemUsedGb / MemTotalGb;
        }
        SampleNet();
        SampleBattery();
    }

    void SampleCpu()
    {
        int n = Environment.ProcessorCount, sz = Marshal.SizeOf<Native.ProcPerf>();
        var buf = Marshal.AllocHGlobal(n * sz);
        try
        {
            if (Native.NtQuerySystemInformation(8, buf, n * sz, out _) != 0) return;
            var cur = new Native.ProcPerf[n];
            for (int i = 0; i < n; i++) cur[i] = Marshal.PtrToStructure<Native.ProcPerf>(buf + i * sz);
            if (_prev != null)
            {
                var cores = new double[n];
                double busy = 0, all = 0;
                for (int i = 0; i < n; i++)
                {
                    double idle = cur[i].Idle - _prev[i].Idle;
                    double total = (cur[i].Kernel + cur[i].User) - (_prev[i].Kernel + _prev[i].User); // Kernel sudah termasuk Idle
                    cores[i] = total > 0 ? Math.Clamp(1 - idle / total, 0, 1) : 0;
                    busy += total - idle; all += total;
                }
                Cores = cores;
                Cpu = all > 0 ? Math.Clamp(busy / all, 0, 1) : 0;
            }
            _prev = cur;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    void SampleNet()
    {
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var s = nic.GetIPv4Statistics();
            rx += s.BytesReceived; tx += s.BytesSent;
        }
        var now = DateTime.Now;
        if (_netAt != default)
        {
            var dt = (now - _netAt).TotalSeconds;
            if (dt > 0) { NetDownKb = Math.Max(0, (rx - _rx) / 1024.0 / dt); NetUpKb = Math.Max(0, (tx - _tx) / 1024.0 / dt); }
        }
        _rx = rx; _tx = tx; _netAt = now;
        ConnectionType = DetectConnectionType();
    }

    static readonly string[] VirtualKeywords = { "Hyper-V", "vEthernet", "VMware", "WSL" };
    static readonly string[] VpnKeywords = { "Tailscale", "WireGuard", "OpenVPN", "Wintun", "TAP-", "VPN" };

    /// <summary>Keputusan peringatan internet setelah debounce: bacaan pertama tidak memicu; hanya berubah bila status benar-benar berbeda.</summary>
    public static (bool? NextState, bool? AlertOnline) DecideNetAlert(bool? lastState, bool currentUp)
    {
        if (lastState == null) return (currentUp, null);
        if (lastState.Value == currentUp) return (lastState, null);
        return (currentUp, currentUp);
    }

    /// <summary>True bila adapter adalah loopback atau virtual (Hyper-V, VMware, WSL).</summary>
    public static bool IsVirtualAdapter(NetworkInterfaceType type, string name, string description)
    {
        if (type == NetworkInterfaceType.Loopback) return true;
        foreach (var kw in VirtualKeywords)
            if (ContainsCi(name, kw) || ContainsCi(description, kw)) return true;
        return false;
    }

    /// <summary>Klasifikasikan adapter aktif ke "VPN/Tailscale", "Wi-Fi", "Ethernet", atau null bila diabaikan.</summary>
    public static string ClassifyConnection(NetworkInterfaceType type, string name, string description)
    {
        if (type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return "VPN/Tailscale";
        foreach (var kw in VpnKeywords)
            if (ContainsCi(name, kw) || ContainsCi(description, kw)) return "VPN/Tailscale";
        if (IsVirtualAdapter(type, name, description)) return null;
        if (type == NetworkInterfaceType.Wireless80211) return "Wi-Fi";
        if (type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Ethernet3Megabit) return "Ethernet";
        return null;
    }

    static bool ContainsCi(string s, string kw) => !string.IsNullOrEmpty(s) && s.Contains(kw, StringComparison.OrdinalIgnoreCase);

    static bool HasValidGateway(NetworkInterface nic)
    {
        foreach (var g in nic.GetIPProperties().GatewayAddresses)
        {
            var a = g.Address;
            if (a == null || IPAddress.IsLoopback(a)) continue;
            if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IPv6Any.Equals(a) && !IPAddress.IPv6None.Equals(a)) return true;
        }
        return false;
    }

    /// <summary>Jenis koneksi aktif (VPN/Tailscale, Ethernet, Wi-Fi, atau none) dari antarmuka Up yang memiliki gateway.</summary>
    public static string DetectConnectionType()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return "none";
            string best = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var kind = ClassifyConnection(nic.NetworkInterfaceType, nic.Name, nic.Description);
                if (kind == null || !HasValidGateway(nic)) continue;
                if (kind == "VPN/Tailscale") return kind;
                if (kind == "Ethernet" || best == null) best = kind;
            }
            return best ?? "none";
        }
        catch { return "none"; }
    }

    void SampleBattery()
    {
        var ps = WF.SystemInformation.PowerStatus;
        if ((ps.BatteryChargeStatus & WF.BatteryChargeStatus.NoSystemBattery) != 0) { BatPct = -1; return; }
        BatPct = Math.Clamp(ps.BatteryLifePercent, 0, 1);
        Charging = ps.PowerLineStatus == WF.PowerLineStatus.Online;
        BatLeft = !Charging && ps.BatteryLifeRemaining > 0 ? UI.Span(TimeSpan.FromSeconds(ps.BatteryLifeRemaining)) + " left" : Charging ? "charging" : "";
    }

    /// <summary>Bagian mahal (proses, GPU, suhu) dipanggil tiap beberapa detik dari thread latar.</summary>
    public async Task SampleSlowAsync()
    {
        if ((DateTime.Now - _slowAt).TotalSeconds < 3) return;
        _slowAt = DateTime.Now;
        await Task.WhenAll(Task.Run(SampleTop), Task.Run(SampleGpu), SampleCpuTemp());
    }

    /// <summary>Ping Gateway (LAN) dan 1.1.1.1 (Internet) tiap ~3 dtk (atau ~6 dtk saat PowerSaver). Hanya dipanggil saat tab Monitor terlihat.</summary>
    public async Task<bool> SamplePingAsync()
    {
        var now = DateTime.UtcNow;
        if ((now - _pingAt).TotalSeconds < (PowerSaver.On ? 6 : 2.8)) return false;
        if (Interlocked.CompareExchange(ref _pingBusy, 1, 0) != 0) return false;
        try
        {
            _pingAt = now;
            var (up, gw) = FindGateway();
            NetUp = up;
            if (!up)
            {
                HasGateway = false;
                _lastGw = null;
                GwPing.Reset();
                NetPing.Reset();
                return true;
            }
            if (gw == null)
            {
                HasGateway = false;
                _lastGw = null;
                GwPing.Reset();
            }
            else
            {
                HasGateway = true;
                var gwStr = gw.ToString();
                if (gwStr != _lastGw) { _lastGw = gwStr; GwPing.Reset(); }
            }

            var gwTask = gw != null ? PingOneAsync(gw) : Task.FromResult<long?>(null);
            var netTask = PingOneAsync(InternetPingAddr);
            await Task.WhenAll(gwTask, netTask);
            if (gw != null) GwPing.Push(gwTask.Result);
            NetPing.Push(netTask.Result);
            return true;
        }
        finally { Interlocked.Exchange(ref _pingBusy, 0); }
    }

    static (bool netUp, IPAddress gw) FindGateway()
    {
        bool anyUp = false;
        IPAddress ipv6 = null;
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return (false, null);
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                anyUp = true;
                foreach (var g in nic.GetIPProperties().GatewayAddresses)
                {
                    var a = g.Address;
                    if (a == null || IPAddress.IsLoopback(a)) continue;
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(a))
                        return (true, a);
                    if (ipv6 == null && a.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IPv6Any.Equals(a) && !IPAddress.IPv6None.Equals(a))
                        ipv6 = a;
                }
            }
        }
        catch { }
        return (anyUp, ipv6);
    }

    static async Task<long?> PingOneAsync(IPAddress addr)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(addr, 1000);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch { return null; }
    }

    /// <summary>Ambil suhu CPU dan GPU saja tanpa daftar proses (dipakai layanan peringatan ringan).</summary>
    public async Task SampleTempsAsync()
    {
        await Task.WhenAll(_noNvidia ? Task.CompletedTask : Task.Run(SampleGpu), SampleCpuTemp());
    }

    void SampleTop()
    {
        // jumlahkan per nama proses; ingat PID terbesar tiap nama untuk mengambil jalur exe (ikon + nama deskriptif)
        var map = new Dictionary<string, (long Bytes, int Pid, long Best)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                long ws = p.WorkingSet64;
                var cur = map.GetValueOrDefault(p.ProcessName);
                map[p.ProcessName] = (cur.Bytes + ws, ws > cur.Best ? p.Id : cur.Pid, Math.Max(cur.Best, ws));
            }
            catch { }
            finally { p.Dispose(); }
        }
        Top = map.OrderByDescending(k => k.Value.Bytes).Take(6).Select(k => (k.Key, k.Value.Bytes, ExePath(k.Key, k.Value.Pid))).ToList();
    }

    static readonly Dictionary<string, string> Paths = new();

    static string ExePath(string name, int pid)
    {
        if (Paths.TryGetValue(name, out var cached)) return cached;
        string path = null;
        try { using var p = Process.GetProcessById(pid); path = p.MainModule?.FileName; } catch { /* proses sistem: tanpa ikon */ }
        return Paths[name] = path;
    }

    bool _noNvidia = Environment.GetEnvironmentVariable("ZAXLET_NO_NVIDIA") == "1";   // uji jalur cadangan (laptop tanpa NVIDIA) di PC ber-NVIDIA
    PdhQuery _gpuQ;
    int _gpuIdx = -2;

    // GPU non-NVIDIA (Intel/AMD) atau tanpa nvidia-smi: beban dari penghitung "GPU Engine" Windows (semua merek); suhu hanya NVIDIA
    void SampleGpuCounters()
    {
        _gpuQ ??= new PdhQuery();
        if (_gpuIdx == -2) _gpuIdx = _gpuQ.Add(@"\GPU Engine(*)\Utilization Percentage");
        if (!_gpuQ.Collect()) return;
        var perEngine = new Dictionary<string, double>();   // jumlahkan semua proses per mesin (adapter + mesin), seperti Task Manager
        foreach (var (name, v) in _gpuQ.Read(_gpuIdx))
        {
            int a = name.IndexOf("_luid_", StringComparison.Ordinal);
            if (a < 0) continue;
            var key = name[a..];
            perEngine[key] = perEngine.GetValueOrDefault(key) + v;
        }
        if (perEngine.Count > 0) GpuUtil = Math.Min(1, perEngine.Values.Max() / 100);
    }

    void SampleGpu()
    {
        if (_noNvidia) { SampleGpuCounters(); return; }
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu,temperature.gpu,power.draw --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            var line = p.StandardOutput.ReadLine();
            p.WaitForExit(1500);
            var f = line?.Split(',');
            if (f is { Length: >= 3 })
            {
                GpuUtil = double.Parse(f[0], CultureInfo.InvariantCulture) / 100;
                GpuTemp = double.Parse(f[1], CultureInfo.InvariantCulture);
                GpuPower = double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : null;
            }
        }
        catch (System.ComponentModel.Win32Exception) { GpuUtil = GpuTemp = GpuPower = null; _noNvidia = true; SampleGpuCounters(); }   // nvidia-smi tidak ada
        catch { GpuUtil = GpuTemp = GpuPower = null; }
    }

    // suhu CPU: LibreHardwareMonitor (remote web server port 8085) bila sedang berjalan, selain itu suhu zona termal ACPI dari Windows
    bool _lhmOk;
    PdhQuery _tzQ;
    int _tzHp = -2, _tzK = -2;

    async Task SampleCpuTemp()
    {
        double? t = null;
        if (!_lhmTried || _lhmOk || (DateTime.Now.Second % 30) == 0)   // LHM yang gagal dicoba lagi tiap ±30 dtk saja
        {
            _lhmTried = true;
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("http://localhost:8085/data.json"));
                t = FindTemp(doc.RootElement);
            }
            catch { }
            _lhmOk = t != null;
        }
        CpuTemp = t ?? ThermalZoneC();
    }

    // "Thermal Zone Information" (tanpa admin): suhu zona ACPI tertinggi. Bukan sensor inti CPU, tetapi pada laptop mendekati suhu paket CPU.
    double? ThermalZoneC()
    {
        _tzQ ??= new PdhQuery();
        if (_tzHp == -2) { _tzHp = _tzQ.Add(@"\Thermal Zone Information(*)\High Precision Temperature"); _tzK = _tzQ.Add(@"\Thermal Zone Information(*)\Temperature"); }
        if (!_tzQ.Collect()) return null;
        double? best = null;
        foreach (var (_, v) in _tzQ.Read(_tzHp)) { var c = v / 10 - 273.15; if (c is > 15 and < 120 && (best == null || c > best)) best = c; }   // persepuluh Kelvin
        if (best == null)
            foreach (var (_, v) in _tzQ.Read(_tzK)) { var c = v - 273.15; if (c is > 15 and < 120 && (best == null || c > best)) best = c; }
        return best;
    }

    static double? FindTemp(JsonElement n)
    {
        if (n.TryGetProperty("Text", out var t) && n.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.String)
        {
            var name = t.GetString() ?? "";
            var val = v.GetString() ?? "";
            if (val.Contains('°') && (name.Contains("Package") || name.Contains("Tctl") || name.Contains("Tdie") || name == "CPU"))
            {
                var num = new string(val.TakeWhile(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            }
        }
        if (n.TryGetProperty("Children", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var c in ch.EnumerateArray())
                if (FindTemp(c) is { } r) return r;
        return null;
    }
}

/// <summary>Kecepatan unduh/unggah untuk pill idle: murah (hanya statistik NIC), diambil paling cepat tiap 1,5 detik.</summary>
public static class NetMeter
{
    public static double DownKb, UpKb;
    static long _rx, _tx;
    static DateTime _at;

    public static void Sample()
    {
        var now = DateTime.Now;
        if ((now - _at).TotalSeconds < 1.5) return;
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var s = nic.GetIPv4Statistics();
            rx += s.BytesReceived; tx += s.BytesSent;
        }
        if (_at != default)
        {
            var dt = (now - _at).TotalSeconds;
            DownKb = Math.Max(0, (rx - _rx) / 1024.0 / dt);
            UpKb = Math.Max(0, (tx - _tx) / 1024.0 / dt);
        }
        _rx = rx; _tx = tx; _at = now;
    }

    /// <summary>"3.4M" / "420K" seperti BrimDeck.</summary>
    public static string Fmt(double kb) => kb >= 1024 ? $"{kb / 1024:0.0}M" : $"{kb:0}K";
}
