using System;
using System.Windows.Threading;

namespace ZaxLet;

public enum TMode { Timer, Stopwatch }

/// <summary>Timer / Stopwatch. Lives outside the tab so it keeps running when the pill is closed.</summary>
public sealed class TimerService
{
    public static readonly TimerService I = new();
    public event Action Tick;
    public TMode Mode = TMode.Timer;
    public bool Running;
    public TimeSpan Total = TimeSpan.FromMinutes(25);

    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(500) };
    TimeSpan _left = TimeSpan.FromMinutes(25), _acc;
    DateTime _end, _startAt;

    TimerService() { _t.Tick += (_, _) => Step(); }

    public TimeSpan Value => Mode == TMode.Stopwatch
        ? _acc + (Running ? DateTime.Now - _startAt : TimeSpan.Zero)
        : Running ? Max0(_end - DateTime.Now) : _left;

    static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    public void SetMode(TMode m)
    {
        if (m == Mode) return;
        Mode = m;
        Reset();
    }

    public void SetTotal(TimeSpan t) { Total = t; if (Mode == TMode.Timer) Reset(); }

    public void Start()
    {
        if (Running) return;
        if (Mode == TMode.Stopwatch) _startAt = DateTime.Now;
        else { if (_left <= TimeSpan.Zero) _left = Duration(); _end = DateTime.Now + _left; }
        Running = true;
        _t.Start();
        Tick?.Invoke();
    }

    public void Pause()
    {
        if (!Running) return;
        if (Mode == TMode.Stopwatch) _acc += DateTime.Now - _startAt; else _left = Max0(_end - DateTime.Now);
        Running = false;
        _t.Stop();
        Tick?.Invoke();
    }

    public void Reset()
    {
        Running = false; _t.Stop(); _acc = TimeSpan.Zero;
        _left = Duration();
        Tick?.Invoke();
    }

    TimeSpan Duration() => Total;

    void Step()
    {
        if (Mode != TMode.Stopwatch && Value <= TimeSpan.Zero)
        {
            Running = false; _t.Stop();
            IslandWindow.Instance?.Alert(L.T("Timer selesai"), 10);
            _left = Duration();
        }
        Tick?.Invoke();
    }
}

/// <summary>Notify on the pill when prayer time arrives (without adzan audio).</summary>
public static class PrayerAlerts
{
    static readonly DispatcherTimer T = new() { Interval = TimeSpan.FromSeconds(15) };
    static string _last = "";

    public static readonly (string Key, string Label, double Fajr, double Isha)[] Methods =
    {
        ("kemenag", "Kemenag RI", 20, 18), ("mwl", "Muslim World League", 18, 17), ("isna", "ISNA", 15, 15),
        ("egypt", "Mesir", 19.5, 17.5), ("karachi", "Karachi", 18, 18), ("jakim", "JAKIM (Malaysia)", 18, 18),
    };

    /// <summary>Schedule for a given date according to method, Asr school, ihtiyat, and location in Settings.</summary>
    public static PrayerCalc.Times For(DateTime date)
    {
        var s = Settings.Cur;
        var m = Array.Find(Methods, x => x.Key == s.PrayerMethod);
        if (m.Key == null) m = Methods[0];
        return PrayerCalc.Compute(date, s.Lat, s.Lon, Settings.TzHours, m.Fajr, m.Isha, s.Ihtiyat, s.AsrHanafi ? 2 : 1);
    }

    public static void Start()
    {
        T.Tick += (_, _) =>
        {
            var s = Settings.Cur;
            var now = DateTime.Now;
            if (s.WorshipMode == "off") return;
            if (s.WorshipMode == "other")   // custom weekly schedule (Worship): pill reminder only
            {
                foreach (var w in Worship.Due(Worship.Parse(s.WorshipText), now))
                {
                    var wk = $"{now:yyyyMMdd}{w.At}{w.Label}";
                    if (wk == _last) continue;
                    _last = wk;
                    IslandWindow.Instance?.Alert(w.Label == "" ? L.T("Waktu ibadah") : L.F("Waktu {0} · {1}", w.Label, w.At.ToString(@"hh\.mm")), 30);
                }
                return;
            }
            if (!s.PrayerAlert) return;
            var times = For(now.Date);
            var list = new System.Collections.Generic.List<(string Name, DateTime At)>(times.All);
            if (s.ImsakOn) list.Add(("Imsak", times.Fajr.AddMinutes(-10)));   // 10 minutes before Subuh
            foreach (var (name, at) in list)
            {
                var key = $"{now:yyyyMMdd}{name}";
                if (now >= at && now < at.AddMinutes(2) && key != _last)
                {
                    _last = key;
                    bool imsak = name == "Imsak";
                    bool adhan = !imsak && s.AdhanOn && AdhanPlayer.Play(s.AdhanFile);
                    IslandWindow.Instance?.Alert(L.F("Waktu {0} · {1}", L.T(name), at.ToString("HH.mm")) + (name == "Maghrib" && s.ImsakOn ? " · " + L.T("buka puasa") : "") + (adhan ? " · " + L.T("klik untuk hentikan adzan") : ""), 30);
                    if (!imsak && s.PrayerPauseMusic && MediaService.I.Playing)
                    {
                        MediaService.I.PlayPause();
                        if (s.PrayerResumeAsk) ResumeLater();
                    }
                }
            }
        };
        T.Start();
    }

    static DispatcherTimer _resumeT;

    /// <summary>Music does not auto-resume after adzan: a few minutes later the pill offers to play it again (click to resume).</summary>
    static void ResumeLater()
    {
        _resumeT?.Stop();
        _resumeT = new DispatcherTimer { Interval = TimeSpan.FromMinutes(4) };
        _resumeT.Tick += (_, _) =>
        {
            _resumeT.Stop();
            if (!MediaService.I.Playing) IslandWindow.Instance?.Alert(L.T("Lanjutkan musik? Klik di sini"), 30, () => { if (!MediaService.I.Playing) MediaService.I.PlayPause(); }, false);
        };
        _resumeT.Start();
    }
}
