using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZaxLet;

/// <summary>
/// "Flow" audio: membaca keluaran suara PC (WASAPI loopback, lokal, tidak direkam) dan menandai kapan vokal sedang bernyanyi, dari energi
/// pita 300-3400 Hz relatif terhadap level terendah/tertinggi lagu yang sedang jalan. Dipakai karaoke: progres isi lirik maju hanya saat
/// ada suara vokal, bukan dihitung dari panjang teks. Capture hanya berjalan selama ada yang memintanya (tab Musik terbuka dan lagu main).
/// </summary>
public sealed class AudioFlow
{
    public static readonly AudioFlow I = new();

    // ---- COM WASAPI (hanya bagian yang dipakai) ----
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

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

    // ---- keadaan ----
    const int Frame = 1024, Hist = 1200;   // bingkai analisis ±21 ms; riwayat ±25 dtk
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

    public bool Active => _run;
    /// <summary>Ada data audio dalam ±0,35 dtk terakhir (loopback tidak mengirim apa pun saat hening total).</summary>
    public bool Fresh => _run && Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastData) < Stopwatch.Frequency * 0.35;
    /// <summary>Sudah berapa detik capture berjalan (riwayat suara tidak lebih lama dari ini).</summary>
    public double CapturingFor => _run ? (Stopwatch.GetTimestamp() - Interlocked.Read(ref _startedAt)) / (double)Stopwatch.Frequency : 0;
    /// <summary>Detik sejak vokal terakhir terdeteksi.</summary>
    public double SilentFor => (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastVoice)) / (double)Stopwatch.Frequency;

    public void Acquire() { lock (_lk) { _users++; if (!_run) Start(); } }
    public void Release() { lock (_lk) if (--_users <= 0) { _users = 0; _run = false; } }

    /// <summary>Total detik bersuara dalam <paramref name="secondsAgo"/> detik terakhir.</summary>
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
                at = _v[idx];   // lebih tua dari riwayat: pakai yang paling lama
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
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
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
                        if (fill == Frame) { Analyze(buf, win, rate, silent); fill = 0; }
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

    // ---- belajar pola vokal vs instrumen per lagu ----
    readonly VocalModel _model = new();
    readonly double[] _prevP = new double[Frame / 2];
    long _lastTrain;
    /// <summary>Label dari lirik untuk posisi putar sekarang: 1 = vokal, 0 = instrumen, -1 = tidak yakin (dipasang MediaService).</summary>
    public Func<int> Labeler;
    public void ResetModel() { _model.Reset(); Array.Clear(_prevP); }
    public string ModelInfo => $"model {(_model.Ready ? "siap" : "belajar")} akurasi={_model.Accuracy:0.00} sampel vokal/instrumen={_model.Count1}/{_model.Count0}";

    // Ciri satu bingkai: [dB pita vokal, vokal relatif ke seluruh pita (dB), terang vs bass (dB), perubahan spektrum (flux), pusat spektrum vokal (kHz)]
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

    // satu bingkai: bila model lagu ini sudah belajar, pakai model; sebelum itu heuristik energi pita vokal (300-3400 Hz) relatif ke rentang level
    void Analyze(float[] frame, float[] win, int rate, bool silent)
    {
        long now = Stopwatch.GetTimestamp();
        var x = Features(frame, win, rate);
        double db = x[0], ratio = Math.Pow(10, x[1] / 10);
        bool raw = false;
        if (!silent && db > -90)
        {
            Interlocked.Exchange(ref _lastData, now);
            _peak = Math.Max(db, _peak - 0.02);                 // puncak meluruh pelan (±1 dB/dtk)
            _floor = Math.Min(db, _floor + 0.02);               // lantai naik pelan: mengikuti bagian lagu yang paling pelan
            _minDb = Math.Min(_minDb, db); _maxDb = Math.Max(_maxDb, db);
            double span = _peak - _floor;
            raw = span > 6 && db - _floor > 0.5 * span && ratio > 0.30;

            // belajar: waktu lirik memberi label (di dalam baris = vokal, jeda panjang/intro = instrumen); latih ulang tiap 3 dtk
            int label = -1;
            try { label = Labeler?.Invoke() ?? -1; } catch { }
            if (label >= 0) _model.Add(x, label == 1);
            if (now - _lastTrain > Stopwatch.Frequency * 3) { _lastTrain = now; _model.Train(); }
            if (_model.Ready) raw = _model.Prob(x) > 0.55;
        }
        _on = Math.Clamp(_on + (raw ? 1 : -1), 0, 4);   // histeresis: 2 bingkai untuk mulai, ±4 untuk berhenti
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
