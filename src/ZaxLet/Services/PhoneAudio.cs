using System;
using System.Runtime.InteropServices;

namespace ZaxLet;

/// <summary>
/// Audio HP (PCM 16-bit stereo 48 kHz, potongan 10 ms dari /aud/ws) diputar lewat waveOut sebagai aliran tambahan di output bawaan.
/// Windows mencampurnya dengan Spotify dkk, jadi pemutar lain tidak ter-pause. Bantalan 6 potongan (~60 ms) meredam jitter jaringan;
/// antrean lebih dari 10 potongan (~100 ms) = potongan baru dibuang agar tundaan tidak menumpuk.
/// Bila putus-putus di Wi-Fi ramai: naikkan Prebuffer/MaxQueue (tundaan naik, patah-patah turun).
/// </summary>
public static class PhoneAudio
{
    const int Rate = 48000, Slots = 32, SlotBytes = 8192, Prebuffer = 6, MaxQueue = 10, Done = 1;
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Fmt { public short Tag, Ch; public int Rate, Avg; public short Align, Bits, Cb; }
    [StructLayout(LayoutKind.Sequential)] struct Hdr { public IntPtr Data; public int Len, Rec; public IntPtr User; public int Flags, Loops; public IntPtr Next, Res; }
    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref Fmt f, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

    static readonly int HdrSize = Marshal.SizeOf<Hdr>(), LenOff = (int)Marshal.OffsetOf<Hdr>(nameof(Hdr.Len)), FlagsOff = (int)Marshal.OffsetOf<Hdr>(nameof(Hdr.Flags));
    static readonly object Lk = new();
    static readonly IntPtr[] H = new IntPtr[Slots];
    static readonly bool[] Queued = new bool[Slots];
    static IntPtr _h;
    static int _next;
    static bool _playing;
    public static DateTime At;
    public static bool Active => (DateTime.Now - At).TotalSeconds < 2;

    static bool Busy(int i) => Queued[i] && (Marshal.ReadInt32(H[i], FlagsOff) & Done) == 0;

    public static void Push(byte[] pcm)
    {
        if (pcm.Length == 0 || pcm.Length > SlotBytes) return;
        lock (Lk)
        {
            At = DateTime.Now;
            if (_h == IntPtr.Zero && !Open()) return;
            int busy = 0;
            for (int k = 0; k < Slots; k++) if (Busy(k)) busy++;
            if (_playing && busy == 0) { waveOutPause(_h); _playing = false; }   // antrean kosong (jaringan tersendat): isi bantalan lagi supaya tidak patah-patah
            if (busy >= MaxQueue || Busy(_next)) return;   // sudah tertinggal: buang potongan baru (klik kecil) daripada tundaan makin besar
            Marshal.Copy(pcm, 0, Marshal.ReadIntPtr(H[_next]), pcm.Length);
            Marshal.WriteInt32(H[_next], LenOff, pcm.Length);
            waveOutWrite(_h, H[_next], HdrSize);
            Queued[_next] = true; _next = (_next + 1) % Slots; busy++;
            if (!_playing && busy >= Prebuffer) { waveOutRestart(_h); _playing = true; }
        }
    }

    static bool Open()
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
            }
            waveOutClose(_h);
            _h = IntPtr.Zero; _next = 0; _playing = false;
        }
    }
}
