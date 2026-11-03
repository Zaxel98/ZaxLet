using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
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
