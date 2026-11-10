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
/// FPS tanpa menyuntik DLL ke game (beda dengan RTSS): mendengarkan peristiwa Present dari penyedia ETW Microsoft-Windows-DXGI, sama seperti PresentMon.
/// Jumlah Present per detik per proses = FPS. Mencakup DirectX 9/10/11/12 lewat DXGI (windowed, borderless, dan fullscreen); Vulkan/OpenGL murni tidak.
/// Sesi ETW butuh hak admin atau keanggotaan grup "Performance Log Users"; tanpa itu <see cref="Start"/> mengembalikan 5 (akses ditolak).
/// </summary>
public sealed class FpsEtw : IDisposable
{
    const string Session = "ZaxLetFpsTrace";
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    const ushort PresentStart = 42;   // Microsoft-Windows-DXGI: IDXGISwapChain::Present dimulai

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
    RecordCb _cb;   // dipegang supaya tidak dikumpulkan GC selagi ETW memanggilnya

    /// <summary>0 = berjalan; 5 = akses ditolak (butuh hak admin/grup); lainnya = kode Win32.</summary>
    public uint Start()
    {
        var props = Props();
        try
        {
            uint r = StartTraceW(out _session, Session, props);
            if (r == 183)   // 183 = sesi lama masih ada (ZaxLet sebelumnya mati mendadak): hentikan lalu mulai lagi
            {
                var stop = Props();
                ControlTraceW(0, Session, stop, 1);
                Marshal.FreeHGlobal(stop);
                r = StartTraceW(out _session, Session, props);
            }
            if (r != 0) return r;

            var g = Dxgi;
            r = EnableTraceEx2(_session, ref g, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);   // enable, verbose, semua keyword
            if (r != 0) { Dispose(); return r; }

            _cb = OnRecord;
            var lf = Marshal.AllocHGlobal(448);   // EVENT_TRACE_LOGFILEW (x64)
            for (int i = 0; i < 448; i += 8) Marshal.WriteInt64(lf, i, 0);
            Marshal.WriteIntPtr(lf, 8, Marshal.StringToHGlobalUni(Session));      // LoggerName
            Marshal.WriteInt32(lf, 28, 0x10000100);                               // PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD
            Marshal.WriteIntPtr(lf, 424, Marshal.GetFunctionPointerForDelegate(_cb));   // EventRecordCallback
            _open = OpenTraceW(lf);
            if (_open == ulong.MaxValue) { Dispose(); return (uint)Marshal.GetLastWin32Error(); }

            _thread = new Thread(() => ProcessTrace(new[] { _open }, 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "fps-etw" };
            _thread.Start();
            return 0;
        }
        finally { Marshal.FreeHGlobal(props); }
    }

    // EVENT_TRACE_PROPERTIES (x64, 120 byte) + nama sesi di belakangnya
    static IntPtr Props()
    {
        int total = 120 + (Session.Length + 1) * 2;
        var p = Marshal.AllocHGlobal(total);
        for (int i = 0; i < total; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, total);          // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);             // Wnode.ClientContext: timestamp QPC
        Marshal.WriteInt32(p, 44, 0x20000);       // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);            // BufferSize (KB)
        Marshal.WriteInt32(p, 52, 4);             // MinimumBuffers
        Marshal.WriteInt32(p, 56, 16);            // MaximumBuffers
        Marshal.WriteInt32(p, 64, 0x100);         // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);             // FlushTimer: 1 detik
        Marshal.WriteInt32(p, 116, 120);          // LoggerNameOffset
        var name = System.Text.Encoding.Unicode.GetBytes(Session);
        Marshal.Copy(name, 0, p + 120, name.Length);
        return p;
    }

    void OnRecord(IntPtr rec)
    {
        // EVENT_RECORD.EventHeader: ProcessId di offset 12, EventDescriptor.Id di offset 40
        if ((ushort)Marshal.ReadInt16(rec, 40) != PresentStart) return;
        int pid = Marshal.ReadInt32(rec, 12);
        lock (_gate) { _counts[pid] = _counts.GetValueOrDefault(pid) + 1; }
    }

    /// <summary>FPS per proses sejak pemanggilan terakhir (hanya yang menampilkan frame).</summary>
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
            try { ControlTraceW(_session, null, p, 1); } finally { Marshal.FreeHGlobal(p); }   // stop sesi: ProcessTrace kembali
            _session = 0;
        }
        if (_open != ulong.MaxValue) { CloseTrace(_open); _open = ulong.MaxValue; }
    }
}

/// <summary>
/// Sumber FPS untuk overlay. Langsung bila ZaxLet boleh membuat sesi ETW; bila tidak, pengguna bisa mengizinkan lewat UAC sekali per sesi ZaxLet
/// (proses pembantu `ZaxLet.exe --fps-helper` berjalan sebagai admin dan mengirim angka lewat named pipe), atau masuk grup "Performance Log Users" supaya tanpa UAC.
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

    /// <summary>Coba sesi langsung (tanpa UAC). Aman dipanggil berulang.</summary>
    public static void TryDirect()
    {
        if (_direct != null) return;
        _tried = true;
        var e = new FpsEtw();
        if (e.Start() == 0) _direct = e; else e.Dispose();
    }

    static bool _refused;

    /// <summary>FPS dipilih, belum ada izin, dan pengguna belum menolak: tombol Overlay menawarkan "Izinkan FPS".</summary>
    public static bool NeedsAsk => !Available && !_refused && PerfFormat.Items(Settings.Cur.PerfItems).Contains("fps");

    /// <summary>Dari aksi pengguna (tombol Overlay, Pengaturan): coba langsung, lalu UAC. Ditolak = pill menjelaskan cara mengulang.</summary>
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

    /// <summary>Minta izin admin sekali (UAC) untuk proses pembantu. False bila ditolak/dibatalkan.</summary>
    public static bool StartElevated()
    {
        if (Available) return true;
        try
        {
            var name = "zaxlet-fps-" + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var psi = new ProcessStartInfo(Environment.ProcessPath, $"--fps-helper {name}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            Process.Start(psi);
            var pipe = _pipe;
            new Thread(() => ReadLoop(pipe)) { IsBackground = true, Name = "fps-pipe" }.Start();
            return true;
        }
        catch (Exception ex)   // 1223 = pengguna menolak UAC
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
            pipe.WaitForConnection();
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
        finally { if (_pipe == pipe) _pipe = null; }
    }

    /// <summary>FPS proses yang sedang di depan; bila tidak ada, proses lain yang paling lancar (≥ 10 fps), selain ZaxLet sendiri. Null bila tidak ada frame.</summary>
    static double _ema;

    /// <summary>Penghalusan: ETW mengirim peristiwa dalam gumpalan (flush 1 dtk), jadi hitungan per detik berayun (25-90 pada game 60 fps); rata-rata bergerak menenangkannya.</summary>
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

/// <summary>`ZaxLet.exe --fps-helper NAMA_PIPE`: berjalan sebagai admin, menulis "pid=fps;pid=fps" tiap detik ke pipe milik ZaxLet, berhenti bila pipe putus.</summary>
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
                w.WriteLine(string.Join(";", s.Select(k => $"{k.Key}={k.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}")));   // IOException saat ZaxLet menutup pipe: keluar
            }
        }
        catch { }
    }
}
