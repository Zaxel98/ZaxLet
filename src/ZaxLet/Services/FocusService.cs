using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>
/// Mode Fokus: notifikasi HP ditahan (tidak muncul di pill) selama hitung mundur manual, atau selama fase kerja Pomodoro bila diizinkan.
/// Kode OTP tetap lewat. Saat selesai, pill memberi ringkasan jumlah notifikasi yang tertahan; isinya tetap ada di tab Komunikasi.
/// </summary>
public sealed class FocusService
{
    public static readonly FocusService I = new();
    public event Action Changed;
    public DateTime Until;
    public bool Manual;
    public int HeldCount;

    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _quietT = new() { Interval = TimeSpan.FromMinutes(1) };
    bool _was, _meetOnly, _quietOnly;

    FocusService()
    {
        _t.Tick += (_, _) => Check();
        _quietT.Tick += (_, _) => Check();
        TimerService.I.Tick += Check;   // fase Pomodoro berganti atau dijeda
        MeetingService.I.Changed += Check;
        Settings.Applied += Check;
        _was = Active;
        _meetOnly = OnlyMeeting;
        _quietOnly = OnlyQuiet;
        if (Settings.Cur.QuietOn) _quietT.Start();
    }

    bool PomoWork => Settings.Cur.FocusPomo && TimerService.I.Mode == TMode.Pomodoro && TimerService.I.Running && !TimerService.I.PomoBreak;
    bool InMeeting => Settings.Cur.MeetHold && MeetingService.I.Active;
    bool InQuiet => QuietHours.IsActive(DateTime.Now, Settings.Cur.QuietOn, Settings.Cur.QuietFrom, Settings.Cur.QuietTo);
    public bool Active => Manual && DateTime.Now < Until || PomoWork || InMeeting || InQuiet;
    public TimeSpan Left => Manual ? Until - DateTime.Now : PomoWork ? TimerService.I.Value : TimeSpan.Zero;
    public bool OnlyMeeting => !Manual && !PomoWork && InMeeting;
    bool OnlyQuiet => !Manual && !PomoWork && !InMeeting && InQuiet;

    public void Start(int minutes)
    {
        if (!Active) HeldCount = 0;
        Manual = true; Until = DateTime.Now.AddMinutes(Math.Clamp(minutes, 1, 240)); _was = true; _meetOnly = false; _quietOnly = false;
        _t.Start();
        Changed?.Invoke();
    }

    public void Stop() { Manual = false; Check(); }

    /// <summary>True bila notifikasi ini ditahan (dipanggil sebelum ditampilkan di pill).</summary>
    public bool Hold(PhoneNotif n)
    {
        if (!Active || Otp.Find(n.Title, n.Text) != null) return false;
        HeldCount++;
        Changed?.Invoke();
        return true;
    }

    void Check()
    {
        if (Settings.Cur.QuietOn) _quietT.Start(); else _quietT.Stop();
        if (Manual && DateTime.Now >= Until) Manual = false;
        bool a = Active;
        if (!Manual) _t.Stop();
        if (_was && !a)
        {
            int n = HeldCount; HeldCount = 0;
            string end = _meetOnly ? "Rapat selesai" : _quietOnly ? "Jam tenang selesai" : "Fokus selesai";
            IslandWindow.Instance?.Alert(n > 0 ? L.F(end + " · {0} notifikasi tertahan", n) : L.T(end), 12, n > 0 ? () => IslandWindow.Instance?.OpenTab("apps") : null);
        }
        if (a) { _meetOnly = OnlyMeeting; _quietOnly = OnlyQuiet; }
        _was = a;
        Changed?.Invoke();
    }
}
