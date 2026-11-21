using System;
using System.Runtime.InteropServices;

namespace ZaxLet;

/// <summary>
/// Audio HP (PCM 16-bit stereo 48 kHz, frame sampai 6144 byte / kelipatan 4 dari /aud/ws) diputar lewat waveOut sebagai aliran tambahan di output bawaan.
/// Windows mencampurnya dengan Spotify dkk, jadi pemutar lain tidak ter-pause.
/// Memakai bantalan jitter adaptif berbasis byte (192 byte/ms): mulai dengan prebuffer ~120 ms, naik bertahap hingga ~400 ms saat underrun,
/// dan turun perlahan setelah stabil 60 detik tanpa underrun. Saat antrean melewati batas atas (prebuffer + 200 ms),
/// potongan dilewati secara berkala agar mengejar waktu nyata tanpa membuang semua potongan baru atau meninggalkan tundaan permanen.
/// </summary>
public static class PhoneAudio
{
    const int Rate = 48000, BytesPerMs = Rate * 4 / 1000, Slots = 96, SlotBytes = 8192, Done = 1;
    const int MinPrebufferMs = 120, MaxPrebufferMs = 400, PrebufferStepUpMs = 40, PrebufferStepDownMs = 20;
    const int HighWatermarkGapMs = 200, CatchupTargetGapMs = 80, StablePeriodMs = 60_000, LogIntervalMs = 5000;
    const int PendCap = 131072; // 128 KB (> 680 ms, kelipatan 4) untuk menampung prebuffer sebelum waveOutRestart

    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Fmt { public short Tag, Ch; public int Rate, Avg; public short Align, Bits, Cb; }
    [StructLayout(LayoutKind.Sequential)] struct Hdr { public IntPtr Data; public int Len, Rec; public IntPtr User; public int Flags, Loops; public IntPtr Next, Res; }
    [StructLayout(LayoutKind.Sequential)] struct MmTime { public int Type; public uint Val; public int Pad; } // TIME_BYTES = 4, sizeof = 12

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref Fmt f, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr h, ref MmTime pmmt, int cbmmt);

    static readonly int HdrSize = Marshal.SizeOf<Hdr>(), MmTimeSize = Marshal.SizeOf<MmTime>();
    static readonly int LenOff = (int)Marshal.OffsetOf<Hdr>(nameof(Hdr.Len)), FlagsOff = (int)Marshal.OffsetOf<Hdr>(nameof(Hdr.Flags));
    static readonly object Lk = new();
    static readonly IntPtr[] H = new IntPtr[Slots];
    static readonly bool[] Queued = new bool[Slots];
    static readonly int[] SlotLen = new int[Slots];
    static readonly byte[] PendBuf = new byte[PendCap];

    static IntPtr _h;
    static int _next, _queuedBytes, _pendHead, _pendTail, _pendCount;
    static int _prebufferMs = MinPrebufferMs, _skipSeq, _underruns;
    static long _writtenTotal, _playedTotal, _droppedBytes, _lastPushTick, _lastLogTick, _stableSinceTick, _maxGapMs;
    static uint _lastPos;
    static bool _playing, _catchingUp;

    public static DateTime At;
    public static bool Active => (DateTime.Now - At).TotalSeconds < 2;

    static bool Busy(int i) => Queued[i] && (Marshal.ReadInt32(H[i], FlagsOff) & Done) == 0;

    static void ReclaimDone()
    {
        for (int k = 0; k < Slots; k++)
        {
            if (Queued[k] && (Marshal.ReadInt32(H[k], FlagsOff) & Done) != 0)
            {
                Queued[k] = false;
                _queuedBytes -= SlotLen[k];
                SlotLen[k] = 0;
            }
        }
        if (_queuedBytes < 0) _queuedBytes = 0;
    }

    static int EffectiveQueuedBytes()
    {
        var mt = new MmTime { Type = 4 };
        if (waveOutGetPosition(_h, ref mt, MmTimeSize) == 0 && mt.Type == 4)
        {
            uint delta = unchecked(mt.Val - _lastPos);
            if (delta < 0x80000000u) { _lastPos = mt.Val; _playedTotal += delta; }
            long hw = _writtenTotal - _playedTotal;
            if (hw < 0) hw = 0;
            return (int)Math.Min(_queuedBytes, hw);
        }
        return _queuedBytes;
    }

    static bool WriteSlot(byte[] src, int offset, int len)
    {
        if (Busy(_next)) { _droppedBytes += len; return false; }
        Marshal.Copy(src, offset, Marshal.ReadIntPtr(H[_next]), len);
        Marshal.WriteInt32(H[_next], LenOff, len);
        waveOutWrite(_h, H[_next], HdrSize);
        Queued[_next] = true;
        SlotLen[_next] = len;
        _next = (_next + 1) % Slots;
        _queuedBytes += len;
        _writtenTotal += len;
        return true;
    }

    static void AppendPending(byte[] pcm)
    {
        int excess = _pendCount + pcm.Length - PendCap;
        if (excess > 0)
        {
            int drop = Math.Min(_pendCount, (excess + 3) & ~3);
            _pendHead = (_pendHead + drop) % PendCap;
            _pendCount -= drop;
            _droppedBytes += drop;
        }
        int first = Math.Min(pcm.Length, PendCap - _pendTail);
        Buffer.BlockCopy(pcm, 0, PendBuf, _pendTail, first);
        if (first < pcm.Length) Buffer.BlockCopy(pcm, first, PendBuf, 0, pcm.Length - first);
        _pendTail = (_pendTail + pcm.Length) % PendCap;
        _pendCount += pcm.Length;
    }

    static void FlushPending()
    {
        while (_pendCount > 0)
        {
            int chunk = Math.Min(SlotBytes, Math.Min(_pendCount, PendCap - _pendHead));
            if (!WriteSlot(PendBuf, _pendHead, chunk)) break;
            _pendHead = (_pendHead + chunk) % PendCap;
            _pendCount -= chunk;
        }
    }

    public static void Push(byte[] pcm)
    {
        if (pcm == null || pcm.Length == 0 || pcm.Length > SlotBytes || (pcm.Length & 3) != 0) return;
        string logLine = null;

        lock (Lk)
        {
            At = DateTime.Now;
            long now = Environment.TickCount64;
            if (_lastPushTick != 0)
            {
                long gap = now - _lastPushTick;
                if (gap > _maxGapMs) _maxGapMs = gap;
            }
            _lastPushTick = now;

            if (_h == IntPtr.Zero && !Open(now)) return;

            ReclaimDone();
            int queued = EffectiveQueuedBytes();

            if (_playing && queued == 0)
            {
                waveOutPause(_h);
                _playing = false;
                _catchingUp = false;
                _underruns++;
                _prebufferMs = Math.Min(MaxPrebufferMs, _prebufferMs + PrebufferStepUpMs);
                _stableSinceTick = now;
            }
            else if (_playing && _prebufferMs > MinPrebufferMs && now - _stableSinceTick >= StablePeriodMs)
            {
                _prebufferMs = Math.Max(MinPrebufferMs, _prebufferMs - PrebufferStepDownMs);
                _stableSinceTick = now;
            }

            if (!_playing)
            {
                AppendPending(pcm);
                if (queued + _pendCount >= _prebufferMs * BytesPerMs)
                {
                    FlushPending();
                    waveOutRestart(_h);
                    _playing = true;
                }
            }
            else
            {
                int highWatermark = (_prebufferMs + HighWatermarkGapMs) * BytesPerMs;
                int catchupTarget = (_prebufferMs + CatchupTargetGapMs) * BytesPerMs;
                if (queued > highWatermark) _catchingUp = true;
                else if (queued <= catchupTarget) _catchingUp = false;

                if (_catchingUp)
                {
                    int stride = queued > highWatermark + 100 * BytesPerMs ? 2 : 4;
                    if (_skipSeq++ % stride == 0) _droppedBytes += pcm.Length;
                    else WriteSlot(pcm, 0, pcm.Length);
                }
                else
                {
                    WriteSlot(pcm, 0, pcm.Length);
                }
            }

            if (now - _lastLogTick >= LogIntervalMs)
            {
                _lastLogTick = now;
                int qMs = (EffectiveQueuedBytes() + _pendCount) / BytesPerMs;
                logLine = $"PhoneAudio: queue={qMs}ms prebuf={_prebufferMs}ms underruns={_underruns} dropped={_droppedBytes}B maxGap={_maxGapMs}ms";
                _maxGapMs = 0;
            }
        }

        if (logLine != null) App.Log(new Exception(logLine));
    }

    static bool Open(long now)
    {
        var f = new Fmt { Tag = 1, Ch = 2, Rate = Rate, Avg = Rate * 4, Align = 4, Bits = 16 };
        if (waveOutOpen(out _h, -1, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0) { _h = IntPtr.Zero; return false; }
        waveOutPause(_h);
        for (int i = 0; i < Slots; i++)
        {
            H[i] = Marshal.AllocHGlobal(HdrSize);
            Marshal.Copy(new byte[HdrSize], 0, H[i], HdrSize);
            Marshal.WriteIntPtr(H[i], Marshal.AllocHGlobal(SlotBytes));
            Marshal.WriteInt32(H[i], LenOff, SlotBytes);
            waveOutPrepareHeader(_h, H[i], HdrSize);
        }
        _prebufferMs = MinPrebufferMs;
        _stableSinceTick = now;
        _lastLogTick = now;
        return true;
    }

    /// <summary>HP berhenti mengirim (soket ditutup): buang antrean dan lepas perangkat.</summary>
    public static void Stop()
    {
        lock (Lk)
        {
            if (_h == IntPtr.Zero) return;
            waveOutReset(_h);   // menandai semua potongan selesai, jadi bisa di-unprepare
            for (int i = 0; i < Slots; i++)
            {
                waveOutUnprepareHeader(_h, H[i], HdrSize);
                Marshal.FreeHGlobal(Marshal.ReadIntPtr(H[i]));
                Marshal.FreeHGlobal(H[i]);
                Queued[i] = false;
                SlotLen[i] = 0;
            }
            waveOutClose(_h);
            _h = IntPtr.Zero; _next = 0; _playing = false; _catchingUp = false;
            _queuedBytes = 0; _writtenTotal = 0; _playedTotal = 0; _lastPos = 0;
            _pendHead = 0; _pendTail = 0; _pendCount = 0;
            _prebufferMs = MinPrebufferMs; _skipSeq = 0;
            _underruns = 0; _droppedBytes = 0; _lastPushTick = 0; _lastLogTick = 0; _stableSinceTick = 0; _maxGapMs = 0;
        }
    }
}
