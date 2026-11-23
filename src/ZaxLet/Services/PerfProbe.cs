using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace ZaxLet;

/// <summary>
/// Sensor reader for the performance overlay, driverless and without admin. Alive only while the overlay is shown:
/// CPU/clock/temp from Windows performance counters (PDH), RAM from GlobalMemoryStatusEx, NVIDIA GPU from a single
/// streaming <c>nvidia-smi -lms</c> process (not a new process every second); without NVIDIA: GPU load and VRAM from "GPU Engine"/"GPU Adapter Memory" counters.
/// Metrics unavailable without a driver (voltage, FPS) are intentionally not offered.
/// </summary>
public sealed class PerfProbe : IDisposable
{
    readonly PdhQuery _q = new();
    readonly int _util, _freq, _perf, _tz, _tzHp, _cores, _dRead, _dWrite;
    int _gEng = -1, _gMem = -1;
    Process _smi;
    volatile PerfFormat.Smi _last;
    DateTime _lastAt;
    bool _gpuFallback;

    readonly bool _wantFps, _wantVolt;
    readonly int _cpuPwr;
    double? _cpuV, _gpuV;
    DateTime _voltAt;
    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(700) };

    /// <summary>items = selected metrics: FPS (ETW session) and voltages (LibreHardwareMonitor) are only touched when selected.</summary>
    public PerfProbe(HashSet<string> items = null)
    {
        _wantFps = items?.Contains("fps") == true;
        _wantVolt = items != null && (items.Contains("cpuvolt") || items.Contains("gpuvolt"));
        if (_wantFps) FpsSource.TryDirect();
        _cpuPwr = _q.Add(@"\Energy Meter(RAPL_Package0_PKG)\Power");
        _util = _q.Add(@"\Processor Information(_Total)\% Processor Utility");
        _freq = _q.Add(@"\Processor Information(_Total)\Processor Frequency");
        _perf = _q.Add(@"\Processor Information(_Total)\% Processor Performance");
        _tzHp = _q.Add(@"\Thermal Zone Information(*)\High Precision Temperature");
        _tz = _q.Add(@"\Thermal Zone Information(*)\Temperature");
        _cores = _q.Add(@"\Processor Information(*)\% Processor Utility");
        _dRead = _q.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _dWrite = _q.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        StartSmi();
        if (_smi == null) AddGpuFallback();
        _q.Collect();   // rate counters only have a value on the second read
    }

    void AddGpuFallback()
    {
        _gpuFallback = true;
        _gEng = _q.Add(@"\GPU Engine(*)\Utilization Percentage");
        _gMem = _q.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
    }

    void StartSmi()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", $"--query-gpu={PerfFormat.SmiQuery} --format=csv,noheader,nounits -lms 1000")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            _smi = Process.Start(psi);
            KillWithParent(_smi);
            _smi.OutputDataReceived += (_, e) => { if (PerfFormat.ParseSmi(e.Data) is { } s) { _last = s; _lastAt = DateTime.Now; } };
            _smi.BeginOutputReadLine();
        }
        catch { _smi = null; }   // no nvidia-smi (Intel/AMD): Windows counter path
    }

    // nvidia-smi -lms never exits on its own: if ZaxLet dies abruptly (crash, force kill) the process would linger. A job object makes it exit with us.
    [DllImport("kernel32.dll")] static extern IntPtr CreateJobObjectW(IntPtr attrs, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, byte[] info, int len);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    static IntPtr _job;

    static void KillWithParent(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObjectW(IntPtr.Zero, null);
                var info = new byte[144];   // JOBOBJECT_EXTENDED_LIMIT_INFORMATION (x64); LimitFlags is at offset 16
                BitConverter.GetBytes(0x2000).CopyTo(info, 16);   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                SetInformationJobObject(_job, 9, info, info.Length);
            }
            AssignProcessToJobObject(_job, p.Handle);
        }
        catch { }
    }

    // LibreHardwareMonitor (Remote Web Server on port 8085) as voltage source: CPU voltage requires a kernel-level driver that ZaxLet must not bundle,
    // whereas LHM already ships one. Failing/not running = "butuh LHM", never made-up numbers.
    async System.Threading.Tasks.Task FetchVolts()
    {
        try
        {
            var (c, g) = PerfFormat.ParseLhmVolts(await Http.GetStringAsync("http://localhost:8085/data.json"));
            _cpuV = c; _gpuV = g;
        }
        catch { _cpuV = _gpuV = null; }
    }

    public static async System.Threading.Tasks.Task<bool> LhmUp()
    {
        try { await Http.GetStringAsync("http://localhost:8085/data.json"); return true; }
        catch { return false; }
    }

    double? First(int idx) { var l = _q.Read(idx); return l.Count > 0 ? l[0].Value : null; }

    /// <summary>Display-ready values per metric key (<see cref="PerfFormat.Metrics"/>); keys unavailable on this PC are omitted.</summary>
    public Dictionary<string, string> Read()
    {
        var v = new Dictionary<string, string>();
        _q.Collect();
        var inv = CultureInfo.InvariantCulture;

        if (First(_util) is { } u) v["cpu"] = $"{Math.Min(100, u):0}%";
        if (First(_freq) is { } baseMhz && First(_perf) is { } perf) v["cpuclk"] = PerfFormat.Mhz(baseMhz * perf / 100);
        double? t = null;
        foreach (var (_, x) in _q.Read(_tzHp)) { var c = x / 10 - 273.15; if (c is > 15 and < 120 && (t == null || c > t)) t = c; }
        if (t == null) foreach (var (_, x) in _q.Read(_tz)) { var c = x - 273.15; if (c is > 15 and < 120 && (t == null || c > t)) t = c; }
        if (t != null) v["cputemp"] = $"{t:0}°C";
        if (First(_cpuPwr) is { } mw && mw > 0) v["cpupwr"] = $"{mw / 1000:0.#} W";   // Energy Meter (RAPL) in milliwatts; not present on all PCs
        if (_wantFps)
            v["fps"] = FpsSource.Read() is { } fps ? $"{fps:0}" : FpsSource.Available ? "–" : "butuh izin";
        if (_wantVolt)
        {
            if (DateTime.UtcNow - _voltAt > TimeSpan.FromSeconds(3)) { _voltAt = DateTime.UtcNow; _ = FetchVolts(); }
            v["cpuvolt"] = _cpuV is { } cv ? $"{cv:0.000} V" : "butuh LHM";
            v["gpuvolt"] = _gpuV is { } gv ? $"{gv:0.000} V" : "butuh LHM";
        }
        double? busiest = null;
        foreach (var (name, x) in _q.Read(_cores)) if (!name.Contains("_Total") && (busiest == null || x > busiest)) busiest = x;   // "0,3" = processor group 0, core 3
        if (busiest != null) v["cpucore"] = $"inti {Math.Min(100, busiest.Value):0}%";
        if (First(_dRead) is { } dr && First(_dWrite) is { } dw) v["disk"] = $"R {PerfFormat.Rate(dr / 1024)} W {PerfFormat.Rate(dw / 1024)}";
        NetMeter.Sample();
        v["net"] = $"↓{PerfFormat.Rate(NetMeter.DownKb)} ↑{PerfFormat.Rate(NetMeter.UpKb)}";

        var ms = new Native.MemStatus { Length = (uint)Marshal.SizeOf<Native.MemStatus>() };
        if (Native.GlobalMemoryStatusEx(ref ms))
            v["ram"] = $"{PerfFormat.Gb((ms.TotalPhys - ms.AvailPhys) / 1073741824.0)}/{PerfFormat.Gb(ms.TotalPhys / 1073741824.0)} GB";

        var s = _last;
        if (s != null && DateTime.Now - _lastAt < TimeSpan.FromSeconds(5))
        {
            if (s.Util is { } g) v["gpu"] = $"{g:0}%";
            if (s.Gr is { } c1) v["gpuclk"] = PerfFormat.Mhz(c1);
            if (s.Mem is { } c2) v["gpumem"] = "mem " + PerfFormat.Mhz(c2);
            if (s.MemUtil is { } mb) v["gpumemutil"] = $"bus {mb:0}%";
            if (s.Fan is { } fn) v["gpufan"] = $"kipas {fn:0}%";
            if (s.PState != null) v["gpustate"] = s.PState;
            if (PerfFormat.ThrottleText(s.Throttle) is { } th) v["gputhr"] = "batas: " + th;
            if (s.Temp is { } gt) v["gputemp"] = $"{gt:0}°C";
            if (s.Power is { } pw) v["gpupwr"] = $"{pw:0.#} W";
            if (s.MemUsedMb is { } mu && s.MemTotalMb is { } mt) v["vram"] = $"{PerfFormat.Gb(mu / 1024)}/{PerfFormat.Gb(mt / 1024)} GB";
        }
        else if (_gpuFallback)
        {
            var perEngine = new Dictionary<string, double>();   // sum all processes per engine, like Task Manager
            foreach (var (name, x) in _q.Read(_gEng))
            {
                int a = name.IndexOf("_luid_", StringComparison.Ordinal);
                if (a >= 0) perEngine[name[a..]] = perEngine.GetValueOrDefault(name[a..]) + x;
            }
            if (perEngine.Count > 0) v["gpu"] = $"{Math.Min(100, perEngine.Values.Max()):0}%";
            var mem = _q.Read(_gMem);
            if (mem.Count > 0) v["vram"] = PerfFormat.Gb(mem.Max(m => m.Value) / 1073741824.0) + " GB";
        }
        return v;
    }

    public void Dispose()
    {
        try { if (_smi is { HasExited: false }) _smi.Kill(); } catch { }
        _smi?.Dispose();
        _q.Dispose();
    }
}
