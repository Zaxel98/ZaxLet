using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZaxLetCam;

/// <summary>
/// Pembaca bingkai NV12 dari berkas bersama yang ditulis ZaxLet (header 64 byte: magic, lebar, tinggi, seq, waktu; lalu piksel).
/// Seq ganjil = sedang ditulis. Berkas biasa (bukan objek Global\) agar bisa dibaca akun LocalService milik Frame Server.
/// </summary>
unsafe static class Frames
{
    const int Magic = 0x4D414349; // "ICAM"
    static MemoryMappedFile _mf;
    static MemoryMappedViewAccessor _va;
    static byte* _p;
    static long _retryAt;

    static bool Open()
    {
        if (_p != null) return true;
        if (Environment.TickCount64 < _retryAt) return false;
        _retryAt = Environment.TickCount64 + 1000;
        try
        {
            var fs = new FileStream(K.FramePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 64 + K.FrameBytes) { fs.Dispose(); return false; }
            _mf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
            _va = _mf.CreateViewAccessor(0, 64 + K.FrameBytes, MemoryMappedFileAccess.Read);
            _va.SafeMemoryMappedViewHandle.AcquirePointer(ref _p);
            return true;
        }
        catch (Exception e) { Log.W("open frame: " + e.Message); return false; }
    }

    /// <summary>Salin bingkai terbaru ke dst; bila ZaxLet tidak mengirim (belum jalan / HP putus) isi bingkai abu-abu.</summary>
    public static void Read(IntPtr dst)
    {
        if (Open())
        {
            for (int i = 0; i < 4; i++)
            {
                long s1 = Volatile.Read(ref *(long*)(_p + 16));
                if ((s1 & 1) != 0) { Thread.Yield(); continue; }
                if (*(int*)_p != Magic || *(int*)(_p + 4) != K.Width || *(int*)(_p + 8) != K.Height) break;
                if (DateTime.UtcNow.ToFileTimeUtc() - *(long*)(_p + 24) > 15_000_000) break; // > 1,5 dtk tanpa bingkai baru
                Buffer.MemoryCopy(_p + 64, (void*)dst, K.FrameBytes, K.FrameBytes);
                if (Volatile.Read(ref *(long*)(_p + 16)) == s1) return;
            }
        }
        var d = (byte*)dst;
        new Span<byte>(d, K.Width * K.Height).Fill(40);
        new Span<byte>(d + K.Width * K.Height, K.Width * K.Height / 2).Fill(128);
    }
}

/// <summary>CLSID yang didaftarkan; Frame Server membuat ini lalu memanggil ActivateObject untuk mendapat media source.</summary>
[ComVisible(true), Guid(K.Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class Activator : AttrBase, IMFActivate
{
    MediaSource _src;

    public int ActivateObject(ref Guid riid, out IntPtr obj)
    {
        obj = IntPtr.Zero;
        try
        {
            Log.W("ActivateObject " + riid);
            _src = new MediaSource();
            var p = Marshal.GetComInterfaceForObject(_src, typeof(IMFMediaSourceEx));
            try { return Marshal.QueryInterface(p, ref riid, out obj); }
            finally { Marshal.Release(p); }
        }
        catch (Exception e) { Log.W("ActivateObject: " + e); return K.E_FAIL; }
    }

    public int ShutdownObject() { try { _src?.Shutdown(); } catch (Exception e) { Log.W("ShutdownObject: " + e.Message); } _src = null; return K.S_OK; }
    public int DetachObject() => K.E_NOTIMPL;
}

public sealed class MediaSource : AttrBase, IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator, IMFGetService, IKsControl
{
    readonly object _lock = new();
    readonly IMFMediaEventQueue _q;
    readonly IMFPresentationDescriptor _pd;
    readonly MediaStream _stream;
    bool _shut, _announced;

    public MediaSource()
    {
        Marshal.ThrowExceptionForHR(MF.MFCreateEventQueue(out _q));
        _stream = new MediaStream(this);
        Marshal.ThrowExceptionForHR(MF.MFCreatePresentationDescriptor(1, new[] { _stream.Descriptor }, out _pd));
        _pd.SelectStream(0);
    }

    internal IntPtr SelfPtr() => Marshal.GetComInterfaceForObject(this, typeof(IMFMediaSource));

    // ---- IMFMediaEventGenerator ----
    public int GetEvent(uint flags, out IntPtr ev) { ev = IntPtr.Zero; var q = _q; return _shut || q == null ? K.MF_E_SHUTDOWN : q.GetEvent(flags, out ev); }
    public int BeginGetEvent(IntPtr cb, IntPtr state) { var q = _q; return _shut || q == null ? K.MF_E_SHUTDOWN : q.BeginGetEvent(cb, state); }
    public int EndGetEvent(IntPtr res, out IntPtr ev) { ev = IntPtr.Zero; var q = _q; return _shut || q == null ? K.MF_E_SHUTDOWN : q.EndGetEvent(res, out ev); }
    public int QueueEvent(uint met, ref Guid ext, int hr, IntPtr pv) => _shut ? K.MF_E_SHUTDOWN : _q.QueueEventParamVar(met, ref ext, hr, pv);

    // ---- IMFMediaSource ----
    public int GetCharacteristics(out uint c) { c = 1; return K.S_OK; } // MFMEDIASOURCE_IS_LIVE

    public int CreatePresentationDescriptor(out IntPtr pd)
    {
        pd = IntPtr.Zero;
        try
        {
            lock (_lock)
            {
                if (_shut) return K.MF_E_SHUTDOWN;
                _pd.Clone(out var c);
                pd = Marshal.GetComInterfaceForObject(c, typeof(IMFPresentationDescriptor));
                return K.S_OK;
            }
        }
        catch (Exception e) { Log.W("CreatePD: " + e.Message); return K.E_FAIL; }
    }

    public int Start(IntPtr pd, IntPtr timeFmt, IntPtr startPos)
    {
        try
        {
            lock (_lock)
            {
                if (_shut) return K.MF_E_SHUTDOWN;
                Log.W("Start");
                var now = new PropVariant { vt = 20, h = MF.MFGetSystemTime() };
                var none = Guid.Empty;
                var sp = _stream.SelfPtr();
                try { Marshal.ThrowExceptionForHR(_q.QueueEventParamUnk(_announced ? K.MEUpdatedStream : K.MENewStream, ref none, 0, sp)); }
                finally { Marshal.Release(sp); }
                _announced = true;
                _stream.Start();
                unsafe { Marshal.ThrowExceptionForHR(_q.QueueEventParamVar(K.MESourceStarted, ref none, 0, (IntPtr)(&now))); }
                return K.S_OK;
            }
        }
        catch (Exception e) { Log.W("Start: " + e); return K.E_FAIL; }
    }

    public int Stop()
    {
        try
        {
            lock (_lock)
            {
                if (_shut) return K.MF_E_SHUTDOWN;
                Log.W("Stop");
                _stream.Stop();
                var now = new PropVariant { vt = 20, h = MF.MFGetSystemTime() };
                var none = Guid.Empty;
                unsafe { Marshal.ThrowExceptionForHR(_q.QueueEventParamVar(K.MESourceStopped, ref none, 0, (IntPtr)(&now))); }
                return K.S_OK;
            }
        }
        catch (Exception e) { Log.W("Stop: " + e); return K.E_FAIL; }
    }

    public int Pause() => K.E_NOTIMPL;

    public int Shutdown()
    {
        lock (_lock)
        {
            if (_shut) return K.S_OK;
            _shut = true;
            Log.W("Shutdown");
            try { _stream.Dispose(); _q.Shutdown(); } catch (Exception e) { Log.W("Shutdown: " + e.Message); }
            return K.S_OK;
        }
    }

    // ---- IMFMediaSourceEx ----
    public int GetSourceAttributes(out IntPtr a) { a = Marshal.GetComInterfaceForObject(this, typeof(IMFAttributes)); return K.S_OK; }
    public int GetStreamAttributes(uint id, out IntPtr a)
    {
        a = IntPtr.Zero;
        if (id != 0) return K.E_FAIL;
        a = Marshal.GetComInterfaceForObject(_stream, typeof(IMFAttributes));
        return K.S_OK;
    }
    public int SetD3DManager(IntPtr mgr) => K.S_OK;

    // ---- IMFGetService / IKsControl ----
    public int GetService(ref Guid svc, ref Guid riid, out IntPtr obj) { obj = IntPtr.Zero; return K.E_NOINTERFACE; }
    public int KsProperty(IntPtr prop, uint propLen, IntPtr data, uint dataLen, out uint ret) { ret = 0; return K.SET_NOT_FOUND; }
    public int KsMethod(IntPtr m, uint mLen, IntPtr data, uint dataLen, out uint ret) { ret = 0; return K.SET_NOT_FOUND; }
    public int KsEvent(IntPtr e, uint eLen, IntPtr data, uint dataLen, out uint ret) { ret = 0; return K.SET_NOT_FOUND; }
}

public sealed class MediaStream : AttrBase, IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator, IKsControl, IDisposable
{
    readonly MediaSource _src;
    readonly IMFMediaEventQueue _q;
    readonly ConcurrentQueue<IntPtr> _tokens = new();
    readonly Thread _th;
    volatile bool _run, _dead;
    public IMFStreamDescriptor Descriptor { get; }

    public MediaStream(MediaSource src)
    {
        _src = src;
        Marshal.ThrowExceptionForHR(MF.MFCreateEventQueue(out _q));

        Marshal.ThrowExceptionForHR(MF.MFCreateMediaType(out var mt));
        mt.G(K.MajorType, K.Video);
        mt.G(K.SubType, K.NV12);
        mt.U64(K.FrameSize, ((ulong)K.Width << 32) | (uint)K.Height);
        mt.U64(K.FrameRate, ((ulong)K.Fps << 32) | 1u);
        mt.U64(K.Par, (1UL << 32) | 1u);
        mt.U32(K.Interlace, 2); // progresif
        mt.U32(K.AllIndep, 1);
        mt.U32(K.FixedSize, 1);
        mt.U32(K.Stride, K.Width);
        mt.U32(K.SampleSize, K.FrameBytes);
        mt.U32(K.AvgBitrate, (uint)((long)K.FrameBytes * 8 * K.Fps));

        Marshal.ThrowExceptionForHR(MF.MFCreateStreamDescriptor(0, 1, new[] { mt }, out var sd));
        sd.GetMediaTypeHandler(out var h);
        h.SetCurrentMediaType(mt);
        Descriptor = sd;
        Tag(sd);
        Tag(A);

        _th = new Thread(Pump) { IsBackground = true, Name = "ZaxLetCam pump" };
        _th.Start();
    }

    static void Tag(IMFAttributes a)
    {
        a.G(K.StreamCategory, K.PinVideoCapture);
        a.U32(K.StreamId, 0);
        a.U32(K.FsShared, 1);
    }

    internal IntPtr SelfPtr() => Marshal.GetComInterfaceForObject(this, typeof(IMFMediaStream));

    public void Start()
    {
        _run = true;
        var none = Guid.Empty;
        _q.QueueEventParamVar(K.MEStreamStarted, ref none, 0, IntPtr.Zero);
    }

    public void Stop()
    {
        _run = false;
        var none = Guid.Empty;
        _q.QueueEventParamVar(K.MEStreamStopped, ref none, 0, IntPtr.Zero);
        while (_tokens.TryDequeue(out var t)) if (t != IntPtr.Zero) Marshal.Release(t);
    }

    public void Dispose()
    {
        _dead = true; _run = false;
        while (_tokens.TryDequeue(out var t)) if (t != IntPtr.Zero) Marshal.Release(t);
        try { _q.Shutdown(); } catch { }
    }

    // ---- IMFMediaEventGenerator ----
    public int GetEvent(uint flags, out IntPtr ev) { ev = IntPtr.Zero; return _dead ? K.MF_E_SHUTDOWN : _q.GetEvent(flags, out ev); }
    public int BeginGetEvent(IntPtr cb, IntPtr state) => _dead ? K.MF_E_SHUTDOWN : _q.BeginGetEvent(cb, state);
    public int EndGetEvent(IntPtr res, out IntPtr ev) { ev = IntPtr.Zero; return _dead ? K.MF_E_SHUTDOWN : _q.EndGetEvent(res, out ev); }
    public int QueueEvent(uint met, ref Guid ext, int hr, IntPtr pv) => _dead ? K.MF_E_SHUTDOWN : _q.QueueEventParamVar(met, ref ext, hr, pv);

    // ---- IMFMediaStream / IMFMediaStream2 ----
    public int GetMediaSource(out IntPtr src) { src = _src.SelfPtr(); return K.S_OK; }
    public int GetStreamDescriptor(out IntPtr sd) { sd = Marshal.GetComInterfaceForObject(Descriptor, typeof(IMFStreamDescriptor)); return K.S_OK; }

    public int RequestSample(IntPtr token)
    {
        if (_dead) return K.MF_E_SHUTDOWN;
        if (token != IntPtr.Zero) Marshal.AddRef(token);
        _tokens.Enqueue(token);
        return K.S_OK;
    }

    public int SetStreamState(int state) { _run = state == 2; return K.S_OK; }
    public int GetStreamState(out int state) { state = _run ? 2 : 0; return K.S_OK; }

    // ---- IKsControl ----
    public int KsProperty(IntPtr prop, uint propLen, IntPtr data, uint dataLen, out uint ret) { ret = 0; return K.SET_NOT_FOUND; }
    public int KsMethod(IntPtr m, uint mLen, IntPtr data, uint dataLen, out uint ret) { ret = 0; return K.SET_NOT_FOUND; }
    public int KsEvent(IntPtr e, uint eLen, IntPtr data, uint dataLen, out uint ret) { ret = 0; return K.SET_NOT_FOUND; }

    // Satu bingkai tiap 1/30 dtk untuk satu permintaan; tanpa permintaan, bingkai dilewati.
    void Pump()
    {
        var sw = Stopwatch.StartNew();
        double next = 0, step = 1000.0 / K.Fps;
        int n = 0;
        while (!_dead)
        {
            double wait = next - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) { Thread.Sleep((int)wait); continue; }
            next += step;
            if (next < sw.Elapsed.TotalMilliseconds - 100) next = sw.Elapsed.TotalMilliseconds + step; // tertinggal jauh: sinkron ulang
            if (!_run || !_tokens.TryDequeue(out var token)) continue;
            try { Deliver(token); if (++n == 1) Log.W("first sample delivered"); }
            catch (Exception e) { Log.W("Deliver: " + e.Message); }
            finally { if (token != IntPtr.Zero) Marshal.Release(token); }
        }
    }

    void Deliver(IntPtr token)
    {
        Marshal.ThrowExceptionForHR(MF.MFCreateSample(out var s));
        Marshal.ThrowExceptionForHR(MF.MFCreateMemoryBuffer(K.FrameBytes, out var b));
        try
        {
            b.Lock(out var p, out _, out _);
            try { Frames.Read(p); } finally { b.Unlock(); }
            b.SetCurrentLength(K.FrameBytes);
            s.AddBuffer(b);
            s.SetSampleTime(MF.MFGetSystemTime());
            s.SetSampleDuration(10_000_000L / K.Fps);
            if (token != IntPtr.Zero) { var k = K.Token; Marshal.ThrowExceptionForHR(s.SetUnknown(ref k, token)); }

            var sp = Marshal.GetComInterfaceForObject(s, typeof(IMFSample));
            try { var none = Guid.Empty; Marshal.ThrowExceptionForHR(_q.QueueEventParamUnk(K.MEMediaSample, ref none, 0, sp)); }
            finally { Marshal.Release(sp); }
        }
        finally { Marshal.ReleaseComObject(b); Marshal.ReleaseComObject(s); }
    }
}
