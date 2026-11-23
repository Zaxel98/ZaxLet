using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ZaxLet;

/// <summary>
/// Meeting mode: once a meeting is detected (see <see cref="MeetingDetect"/>), the pill shows a microphone button to mute Windows'
/// default microphone (applies across all apps), music is paused until the meeting ends, and phone notifications are held like Focus Mode.
/// Polled from a background thread every 2 seconds (<see cref="Poll"/>) and only when the feature is enabled.
/// </summary>
public sealed class MeetingService
{
    public static readonly MeetingService I = new();
    public event Action Changed;                 // UI thread
    public string App;                           // null = no active meeting
    public bool Active => App != null;

    bool _pausedMusic;

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    /// <summary>Is the default microphone muted? Refreshed on each Poll and ToggleMic so pill rendering does not call COM.</summary>
    public bool MicMuted;

    public void ToggleMic()
    {
        bool mute = !MicMuted;
        if (AudioEndpoint.SetMuted(mute, AudioEndpoint.Capture)) MicMuted = mute;
        Changed?.Invoke();
    }

    /// <summary>From background thread. Cheap when nothing is using the microphone: a single registry read.</summary>
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
            bool m = AudioEndpoint.Muted(AudioEndpoint.Capture) == true;   // can be changed externally (laptop mic key, meeting app)
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
            if (!Settings.Cur.MeetHold) IslandWindow.Instance?.Alert(L.T("Rapat selesai"), 3, null, false);   // when notifications are held, FocusService announces it with the count
        }
        Changed?.Invoke();
    }
}
