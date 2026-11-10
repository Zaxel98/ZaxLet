using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ZaxLet;

/// <summary>
/// Mode rapat: begitu rapat terdeteksi (lihat <see cref="MeetingDetect"/>), pill menampilkan tombol mikrofon untuk membisukan mikrofon
/// utama Windows (berlaku di semua aplikasi), musik dijeda sampai rapat selesai, dan notifikasi HP ditahan seperti Mode Fokus.
/// Dibaca dari thread latar tiap 2 detik (<see cref="Poll"/>) dan hanya bila fiturnya menyala.
/// </summary>
public sealed class MeetingService
{
    public static readonly MeetingService I = new();
    public event Action Changed;                 // thread UI
    public string App;                           // null = tidak ada rapat
    public bool Active => App != null;

    bool _pausedMusic;

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    /// <summary>Mikrofon utama sedang bisu? Disegarkan tiap Poll dan tiap ToggleMic, supaya menggambar pill tidak memanggil COM.</summary>
    public bool MicMuted;

    public void ToggleMic()
    {
        bool mute = !MicMuted;
        if (AudioEndpoint.SetMuted(mute, AudioEndpoint.Capture)) MicMuted = mute;
        Changed?.Invoke();
    }

    /// <summary>Dari thread latar. Murah bila tidak ada yang memakai mikrofon: satu pembacaan registri.</summary>
    public void Poll()
    {
        string app = null;
        if (Settings.Cur.MeetOn)
        {
            var mic = PrivacyWatch.InUse("microphone");
            if (mic.Count > 0)
            {
                var titles = new List<string>();
                EnumWindows((h, _) => { if (IsWindowVisible(h)) { var sb = new StringBuilder(200); if (GetWindowText(h, sb, 200) > 0) titles.Add(sb.ToString()); } return true; }, IntPtr.Zero);
                app = MeetingDetect.Classify(mic, titles);
            }
        }
        if (app != null)
        {
            bool m = AudioEndpoint.Muted(AudioEndpoint.Capture) == true;   // bisa diubah dari luar (tombol mic laptop, aplikasi rapat)
            if (m != MicMuted) { MicMuted = m; System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke()); }
        }
        if (app == App) return;
        var was = App; App = app;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Transition(was, app));
    }

    void Transition(string was, string now)
    {
        var ms = MediaService.I;
        if (was == null && now != null)
        {
            if (Settings.Cur.MeetPause && ms.Playing) { ms.PlayPause(); _pausedMusic = true; }
            IslandWindow.Instance?.Alert(L.F("Rapat {0} dimulai · mikrofon ada di pill", now), 4, null, false);
        }
        else if (was != null && now == null)
        {
            if (_pausedMusic && !ms.Playing) ms.PlayPause();
            _pausedMusic = false;
            if (!Settings.Cur.MeetHold) IslandWindow.Instance?.Alert(L.T("Rapat selesai"), 3, null, false);   // bila notifikasi ditahan, FocusService yang mengumumkan beserta jumlahnya
        }
        Changed?.Invoke();
    }
}
