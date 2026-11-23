using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZaxLet;

/// <summary>
/// FPS without injecting a DLL into games (unlike RTSS): listens for Present events from the Microsoft-Windows-DXGI ETW provider, like PresentMon.
/// Present count per second per process = FPS. Covers DirectX 9/10/11/12 via DXGI (windowed, borderless, and fullscreen); pure Vulkan/OpenGL are not covered.
/// ETW sessions require admin rights or membership in "Performance Log Users"; without it <see cref="Start"/> returns 5 (access denied).
/// </summary>
public sealed class FpsEtw : IDisposable
{
    const string Session = "ZaxLetFpsTrace";
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    const ushort PresentStart = 42;   // Microsoft-Windows-DXGI: IDXGISwapChain::Present started

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong handle, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong handle, string name, IntPtr props, uint code);
    [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, IntPtr p);
    [DllImport("advapi32.dll")] static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);

    delegate void RecordCb(IntPtr record);

    readonly object _gate = new();
    Dictionary<int, int> _counts = new();
    DateTime _since = DateTime.UtcNow;
    ulong _session, _open = ulong.MaxValue;
    Thread _thread;
    RecordCb _cb;   // kept alive so GC does not collect it while ETW calls it

    /// <summary>0 = running; 5 = access denied (needs admin/group rights); others = Win32 error code.</summary>
    public uint Start()
    {
        var props = Props();
        try
        {
            uint r = StartTraceW(out _session, Session, props);
            if (r == 183)   // 183 = old session still exists (previous ZaxLet crashed): stop and restart
            {
                var stop = Props();
                ControlTraceW(0, Session, stop, 1);
                Marshal.FreeHGlobal(stop);
                r = StartTraceW(out _session, Session, props);
            }
            if (r != 0) return r;

            var g = Dxgi;
            r = EnableTraceEx2(_session, ref g, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);   // enable, verbose, all keywords
            if (r != 0) { Dispose(); return r; }

            _cb = OnRecord;
            var lf = Marshal.AllocHGlobal(448);   // EVENT_TRACE_LOGFILEW (x64)
            var lfName = IntPtr.Zero;
            try
            {
                for (int i = 0; i < 448; i += 8) Marshal.WriteInt64(lf, i, 0);
                lfName = Marshal.StringToHGlobalUni(Session);
                Marshal.WriteIntPtr(lf, 8, lfName);                                   // LoggerName
                Marshal.WriteInt32(lf, 28, 0x10000100);                               // PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD
                Marshal.WriteIntPtr(lf, 424, Marshal.GetFunctionPointerForDelegate(_cb));   // EventRecordCallback
                _open = OpenTraceW(lf);
            }
            finally
            {
                if (lfName != IntPtr.Zero) Marshal.FreeHGlobal(lfName);
                Marshal.FreeHGlobal(lf);
            }
            if (_open == ulong.MaxValue) { Dispose(); return (uint)Marshal.GetLastWin32Error(); }

            _thread = new Thread(() => ProcessTrace(new[] { _open }, 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "fps-etw" };
            _thread.Start();
            return 0;
        }
        finally { Marshal.FreeHGlobal(props); }
    }

    // EVENT_TRACE_PROPERTIES (x64, 120 bytes) + session name appended
    static IntPtr Props()
    {
        int total = 120 + (Session.Length + 1) * 2;
        var p = Marshal.AllocHGlobal(total);
        for (int i = 0; i < total; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, total);          // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);             // Wnode.ClientContext: QPC timestamp
        Marshal.WriteInt32(p, 44, 0x20000);       // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);            // BufferSize (KB)
        Marshal.WriteInt32(p, 52, 4);             // MinimumBuffers
        Marshal.WriteInt32(p, 56, 16);            // MaximumBuffers
        Marshal.WriteInt32(p, 64, 0x100);         // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);             // FlushTimer: 1 second
        Marshal.WriteInt32(p, 116, 120);          // LoggerNameOffset
        var name = System.Text.Encoding.Unicode.GetBytes(Session);
        Marshal.Copy(name, 0, p + 120, name.Length);
        return p;
    }

    void OnRecord(IntPtr rec)
    {
        // EVENT_RECORD.EventHeader: ProcessId at offset 12, EventDescriptor.Id at offset 40
        if ((ushort)Marshal.ReadInt16(rec, 40) != PresentStart) return;
        int pid = Marshal.ReadInt32(rec, 12);
        lock (_gate) { _counts[pid] = _counts.GetValueOrDefault(pid) + 1; }
    }

    /// <summary>FPS per process since last call (only processes presenting frames).</summary>
    public Dictionary<int, double> Snapshot()
    {
        Dictionary<int, int> c;
        DateTime t0;
        lock (_gate) { c = _counts; _counts = new(); t0 = _since; _since = DateTime.UtcNow; }
        double s = Math.Max(0.2, (DateTime.UtcNow - t0).TotalSeconds);
        return c.ToDictionary(k => k.Key, k => k.Value / s);
    }

    public void Dispose()
    {
        if (_session != 0)
        {
            var p = Props();
            try { ControlTraceW(_session, null, p, 1); } finally { Marshal.FreeHGlobal(p); }   // stop session: ProcessTrace returns
            _session = 0;
        }
        if (_open != ulong.MaxValue) { CloseTrace(_open); _open = ulong.MaxValue; }
    }
}

/// <summary>
/// FPS source for the overlay. Direct when ZaxLet can create an ETW session; otherwise the user can allow via UAC once per ZaxLet session
/// (helper process `ZaxLet.exe --fps-helper` runs as admin and sends numbers over a named pipe), or join the "Performance Log Users" group to avoid UAC.
/// </summary>
public static class FpsSource
{
    public const string GroupCommand = "net localgroup \"Performance Log Users\" %USERNAME% /add";

    static FpsEtw _direct;
    static NamedPipeServerStream _pipe;
    static volatile Dictionary<int, double> _remote = new();
    static DateTime _remoteAt;
    static bool _tried;

    public static bool Available => _direct != null || _pipe != null;
    public static bool Denied => _tried && !Available;

    /// <summary>Try direct session (no UAC). Safe to call repeatedly.</summary>
    public static void TryDirect()
    {
        if (_direct != null) return;
        _tried = true;
        var e = new FpsEtw();
        if (e.Start() == 0) _direct = e; else e.Dispose();
    }

    static bool _refused;

    /// <summary>FPS selected, no permission yet, and user hasn't refused: Overlay button offers "Allow FPS".</summary>
    public static bool NeedsAsk => !Available && !_refused && PerfFormat.Items(Settings.Cur.PerfItems).Contains("fps");

    /// <summary>From user action (Overlay button, Settings): try direct, then UAC. Denied = pill explains how to retry.</summary>
    public static bool Ask()
    {
        _refused = false;
        if (Available) return true;
        TryDirect();
        if (Available || StartElevated()) return true;
        _refused = true;
        IslandWindow.Instance?.Alert(L.T("FPS butuh izin admin. Coba lagi dan pilih Ya di jendela Windows."), 8, null, false);
        return false;
    }

    /// <summary>Request admin permission once (UAC) for helper process. False if denied/cancelled.</summary>
    public static bool StartElevated()
    {
        if (Available) return true;
        try
        {
            var name = "zaxlet-fps-" + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var psi = new ProcessStartInfo(Environment.ProcessPath, $"--fps-helper {name}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            Process.Start(psi)?.Dispose();
            var pipe = _pipe;
            new Thread(() => ReadLoop(pipe)) { IsBackground = true, Name = "fps-pipe" }.Start();
            return true;
        }
        catch (Exception ex)   // 1223 = user declined UAC
        {
            _pipe?.Dispose(); _pipe = null;
            if (ex is not System.ComponentModel.Win32Exception) App.Log(ex);
            return false;
        }
    }

    static void ReadLoop(NamedPipeServerStream pipe)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            pipe.WaitForConnectionAsync(cts.Token).GetAwaiter().GetResult();
            using var r = new StreamReader(pipe);
            string line;
            while ((line = r.ReadLine()) != null)
            {
                var d = new Dictionary<int, double>();
                foreach (var part in line.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=');
                    if (kv.Length == 2 && int.TryParse(kv[0], out var pid) && double.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) d[pid] = f;
                }
                _remote = d; _remoteAt = DateTime.UtcNow;
            }
        }
        catch { }
        finally
        {
            if (_pipe == pipe) _pipe = null;
            pipe.Dispose();
        }
    }

    /// <summary>FPS of the foreground process; if none, the smoothest other process (>= 10 fps), excluding ZaxLet itself. Null if no frames.</summary>
    static double _ema;

    /// <summary>Smoothing: ETW sends events in bursts (1 s flush), so per-second counts swing (25-90 in a 60 fps game); moving average steadies it.</summary>
    public static double? Read()
    {
        var raw = ReadRaw();
        if (raw is not { } f) { _ema = 0; return null; }
        _ema = _ema <= 0 ? f : _ema * 0.7 + f * 0.3;
        return _ema;
    }

    static double? ReadRaw()
    {
        Dictionary<int, double> d = null;
        if (_direct != null) d = _direct.Snapshot();
        else if (_pipe != null && DateTime.UtcNow - _remoteAt < TimeSpan.FromSeconds(4)) d = _remote;
        if (d == null || d.Count == 0) return null;
        d = d.Where(k => k.Key != Environment.ProcessId).ToDictionary(k => k.Key, k => k.Value);
        int fg = Native.ForegroundPid();
        if (d.TryGetValue(fg, out var f) && f >= 1) return f;
        var best = d.OrderByDescending(k => k.Value).FirstOrDefault();
        return best.Value >= 10 ? best.Value : null;
    }

    public static void Stop() { _direct?.Dispose(); _direct = null; _pipe?.Dispose(); _pipe = null; }
}

/// <summary>`ZaxLet.exe --fps-helper PIPE_NAME`: runs as admin, writes "pid=fps;pid=fps" every second to ZaxLet's pipe, exits when pipe breaks.</summary>
public static class FpsHelper
{
    public static void Run(string pipeName)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(10000);
            using var etw = new FpsEtw();
            if (etw.Start() != 0) return;
            using var w = new StreamWriter(pipe) { AutoFlush = true };
            while (true)
            {
                Thread.Sleep(1000);
                var s = etw.Snapshot();
                w.WriteLine(string.Join(";", s.Select(k => $"{k.Key}={k.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}")));   // IOException when ZaxLet closes pipe: exit
            }
        }
        catch { }
    }
}
