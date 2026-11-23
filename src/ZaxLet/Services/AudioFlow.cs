using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZaxLet;

/// <summary>
/// Audio "Flow": reads PC audio output (WASAPI loopback, local, not recorded) and detects when vocals are singing, from
/// 300-3400 Hz band energy relative to the current song's floor/peak levels. Used by karaoke: lyric progress advances only when
/// vocals are present, rather than by text length. Capture only runs while requested (Music tab open and song playing).
/// </summary>
public sealed class AudioFlow
{
    public static readonly AudioFlow I = new();

    // ---- COM WASAPI (only the parts used) ----
    // Enumerator/IMMDevice shared with AudioEndpoint: two [ComImport] classes with the same CLSID make `new` fail (InvalidCast) while the other RCW is alive.

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    // ---- state ----
    const int Frame = 1024, Hist = 1200;   // analysis frame ~21 ms; history ~25s
    readonly object _lk = new();
    readonly long[] _t = new long[Hist];
    readonly double[] _v = new double[Hist];
    int _head, _count, _users;
    Thread _th;
    volatile bool _run;
    long _lastData, _lastVoice, _startedAt;
    double _voiced, _peak = -200, _floor = 200, _minDb = 200, _maxDb = -200;
    int _on;
    long _frames;

    /// <summary>Receives each mono frame of PC output (buffer is reused: copy if needed). Used by song recognizer, only while capturing a clip.</summary>
    public event Action<float[], int> Tap;

    public bool Active => _run;
    /// <summary>Audio data received within the last ~0.35s (loopback sends nothing during complete silence).</summary>
    public bool Fresh => _run && Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastData) < Stopwatch.Frequency * 0.35;
    /// <summary>How many seconds capture has been running (voice history is no older than this).</summary>
    public double CapturingFor => _run ? (Stopwatch.GetTimestamp() - Interlocked.Read(ref _startedAt)) / (double)Stopwatch.Frequency : 0;
    /// <summary>Seconds since vocals were last detected.</summary>
    public double SilentFor => (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastVoice)) / (double)Stopwatch.Frequency;

    public void Acquire() { lock (_lk) { _users++; if (!_run) Start(); } }
    public void Release() { lock (_lk) if (--_users <= 0) { _users = 0; _run = false; } }

    /// <summary>Total voiced seconds in the last <paramref name="secondsAgo"/> seconds.</summary>
    public double VoicedSince(double secondsAgo)
    {
        lock (_lk)
        {
            if (_count == 0) return 0;
            long target = Stopwatch.GetTimestamp() - (long)(secondsAgo * Stopwatch.Frequency);
            double now = _v[(_head - 1 + Hist) % Hist], at = 0;
            for (int k = 0; k < _count; k++)
            {
                int idx = (_head - 1 - k + Hist * 2) % Hist;
                if (_t[idx] <= target) { at = _v[idx]; break; }
                at = _v[idx];   // older than history: use oldest available
            }
            return Math.Max(0, now - at);
        }
    }

    public string DebugStats() => $"frames={_frames} voicedSec={_voiced:0.0} capturing={CapturingFor:0.0}s dB[{_minDb:0}..{_maxDb:0}] floor={_floor:0} peak={_peak:0} {ModelInfo}";

    void Start()
    {
        if (_run) return;
        _run = true;
        _frames = 0; _voiced = 0; _count = 0; _head = 0; _peak = -200; _floor = 200; _minDb = 200; _maxDb = -200; _on = 0;
        Interlocked.Exchange(ref _startedAt, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _lastVoice, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 10);
        _th = new Thread(Loop) { IsBackground = true, Name = "zaxlet-flow", Priority = ThreadPriority.BelowNormal };
        _th.Start();
    }

    static void Ok(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    void Loop()
    {
        IAudioClient ac = null;
        IntPtr pf = IntPtr.Zero;
        try
        {
            var en = (AudioEndpoint.IEnumerator)new AudioEndpoint.MMDeviceEnumeratorCom();
            Ok(en.GetDefaultAudioEndpoint(0, 0, out var dev));   // eRender, eConsole
            var iidAc = typeof(IAudioClient).GUID;
            Ok(dev.Activate(ref iidAc, 23, IntPtr.Zero, out var o));
            ac = (IAudioClient)o;
            Ok(ac.GetMixFormat(out pf));
            int tag = Marshal.ReadInt16(pf, 0) & 0xFFFF, ch = Marshal.ReadInt16(pf, 2), rate = Marshal.ReadInt32(pf, 4), bits = Marshal.ReadInt16(pf, 14);
            bool isFloat = tag == 3 || (tag == 0xFFFE && Marshal.ReadInt32(pf, 24) == 3);
            if (!(isFloat && bits == 32) && !(!isFloat && bits == 16)) throw new NotSupportedException($"format audio {tag}/{bits} bit");
            Ok(ac.Initialize(0, 0x00020000, 2_000_000, 0, pf, IntPtr.Zero));   // shared + loopback, buffer 200 ms
            var iidCap = typeof(IAudioCaptureClient).GUID;
            Ok(ac.GetService(ref iidCap, out var cs));
            var cap = (IAudioCaptureClient)cs;
            Ok(ac.Start());

            var win = Dsp.Hann(Frame);
            var buf = new float[Frame];
            int fill = 0;
            while (_run)
            {
                Thread.Sleep(12);
                while (_run && cap.GetNextPacketSize(out var n) == 0 && n > 0)
                {
                    Ok(cap.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                    bool silent = (flags & 2) != 0;
                    for (int f = 0; f < frames; f++)
                    {
                        float s = 0;
                        if (!silent)
                        {
                            for (int c = 0; c < ch; c++)
                                s += isFloat ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, (f * ch + c) * 4)) : Marshal.ReadInt16(data, (f * ch + c) * 2) / 32768f;
                            s /= ch;
                        }
                        buf[fill++] = s;
                        if (fill == Frame) { Tap?.Invoke(buf, rate); Analyze(buf, win, rate, silent); fill = 0; }
                    }
                    Ok(cap.ReleaseBuffer(frames));
                }
            }
            ac.Stop();
        }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            _run = false;
            if (pf != IntPtr.Zero) Marshal.FreeCoTaskMem(pf);
            if (ac != null) Marshal.ReleaseComObject(ac);
        }
    }

    // ---- learn vocal vs instrumental pattern per song ----
    readonly VocalModel _model = new();
    readonly double[] _prevP = new double[Frame / 2];
    long _lastTrain;
    /// <summary>Label from lyrics for current playback position: 1 = vocal, 0 = instrumental, -1 = uncertain (set by MediaService).</summary>
    public Func<int> Labeler;
    public void ResetModel() { _model.Reset(); Array.Clear(_prevP); }
    public string ModelInfo => $"model {(_model.Ready ? "siap" : "belajar")} akurasi={_model.Accuracy:0.00} sampel vokal/instrumen={_model.Count1}/{_model.Count0}";

    // Single-frame features: [vocal band dB, vocal relative to full band (dB), brightness vs bass (dB), spectral flux, vocal spectral centroid (kHz)]
    float[] Features(float[] frame, float[] win, int rate)
    {
        var p = Dsp.Power(frame, win);
        double hz = (double)rate / Frame, voc = 0, all = 0, mid = 0, low = 0, cw = 0, flux = 0, mag = 0;
        for (int k = 1; k < p.Length; k++)
        {
            double f = k * hz, pk = p[k];
            if (f >= 60 && f <= 12000) all += pk;
            if (f >= 300 && f <= 3400)
            {
                voc += pk; cw += pk * f;
                double m = Math.Sqrt(pk); mag += m; flux += Math.Abs(m - Math.Sqrt(_prevP[k]));
            }
            if (f >= 1000 && f <= 3400) mid += pk;
            if (f >= 60 && f < 300) low += pk;
            _prevP[k] = pk;
        }
        const double e = 1e-12;
        return new[]
        {
            (float)(10 * Math.Log10(voc + e)),
            (float)(10 * Math.Log10(voc + e) - 10 * Math.Log10(all + e)),
            (float)(10 * Math.Log10(mid + e) - 10 * Math.Log10(low + e)),
            (float)(flux / (mag + 1e-9)),
            (float)(cw / (voc + e) / 1000),
        };
    }

    // single frame: if this song's model is trained, use it; before that, use vocal band energy heuristic (300-3400 Hz) relative to level range
    void Analyze(float[] frame, float[] win, int rate, bool silent)
    {
        long now = Stopwatch.GetTimestamp();
        var x = Features(frame, win, rate);
        double db = x[0], ratio = Math.Pow(10, x[1] / 10);
        bool raw = false;
        if (!silent && db > -90)
        {
            Interlocked.Exchange(ref _lastData, now);
            _peak = Math.Max(db, _peak - 0.02);                 // peak decays slowly (~1 dB/s)
            _floor = Math.Min(db, _floor + 0.02);               // floor rises slowly: tracks the quietest part of the song
            _minDb = Math.Min(_minDb, db); _maxDb = Math.Max(_maxDb, db);
            double span = _peak - _floor;
            raw = span > 6 && db - _floor > 0.5 * span && ratio > 0.30;

            // training: lyric timing provides labels (inside a line = vocal, long gap/intro = instrumental); retrain every 3s
            int label = -1;
            try { label = Labeler?.Invoke() ?? -1; } catch { }
            if (label >= 0) _model.Add(x, label == 1);
            if (now - _lastTrain > Stopwatch.Frequency * 3) { _lastTrain = now; _model.Train(); }
            if (_model.Ready) raw = _model.Prob(x) > 0.55;
        }
        _on = Math.Clamp(_on + (raw ? 1 : -1), 0, 4);   // hysteresis: 2 frames to start, ~4 to stop
        bool voice = _on >= 2;
        double dt = (double)Frame / rate;
        lock (_lk)
        {
            if (voice) { _voiced += dt; Interlocked.Exchange(ref _lastVoice, now); }
            _frames++;
            _t[_head] = now; _v[_head] = _voiced;
            _head = (_head + 1) % Hist;
            _count = Math.Min(_count + 1, Hist);
        }
    }
}
