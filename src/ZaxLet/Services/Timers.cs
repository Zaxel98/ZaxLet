using System;
using System.Windows.Threading;

namespace ZaxLet;

public enum TMode { Timer, Pomodoro, Stopwatch }

/// <summary>Timer / Pomodoro / Stopwatch. Hidup di luar tab agar tetap berjalan saat pill tertutup.</summary>
public sealed class TimerService
{
    public static readonly TimerService I = new();
    public event Action Tick;
    public TMode Mode = TMode.Timer;
    public bool Running, PomoBreak;
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
        Running = false; _t.Stop(); PomoBreak = false; _acc = TimeSpan.Zero;
        _left = Duration();
        Tick?.Invoke();
    }

    TimeSpan Duration() => Mode switch
    {
        TMode.Pomodoro => TimeSpan.FromMinutes(PomoBreak ? Settings.Cur.PomoBreak : Settings.Cur.PomoWork),
        _ => Total
    };

    void Step()
    {
        if (Mode != TMode.Stopwatch && Value <= TimeSpan.Zero)
        {
            Running = false; _t.Stop();
            if (Mode == TMode.Pomodoro)
            {
                PomoBreak = !PomoBreak;
                IslandWindow.Instance?.Alert(PomoBreak ? $"Pomodoro selesai · istirahat {Settings.Cur.PomoBreak} menit" : "Istirahat selesai · fokus lagi", 10);
                _left = Duration();
                Start();
                return;
            }
            IslandWindow.Instance?.Alert("Timer selesai", 10);
            _left = Duration();
        }
        Tick?.Invoke();
    }
}

/// <summary>Pengingat istirahat mata: tiap N menit.</summary>
public sealed class EyeService
{
    public static readonly EyeService I = new();
    public DateTime Next;
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };

    EyeService() { _t.Tick += (_, _) => Check(); }

    public void Apply()
    {
        _t.Stop();
        if (!Settings.Cur.EyeOn) return;
        Next = DateTime.Now.AddMinutes(Settings.Cur.EyeMinutes);
        _t.Start();
    }

    public void BreakNow() => IslandWindow.Instance?.Alert("Istirahat mata · lihat jauh 20 detik", 20);

    void Check()
    {
        if (DateTime.Now < Next) return;
        BreakNow();
        Next = DateTime.Now.AddMinutes(Settings.Cur.EyeMinutes);
    }
}

/// <summary>Beri tahu di pill saat masuk waktu sholat (tanpa audio adzan).</summary>
public static class PrayerAlerts
{
    static readonly DispatcherTimer T = new() { Interval = TimeSpan.FromSeconds(15) };
    static string _last = "";

    public static readonly (string Key, string Label, double Fajr, double Isha)[] Methods =
    {
        ("kemenag", "Kemenag RI", 20, 18), ("mwl", "Muslim World League", 18, 17), ("isna", "ISNA", 15, 15),
        ("egypt", "Mesir", 19.5, 17.5), ("karachi", "Karachi", 18, 18),
    };

    /// <summary>Jadwal untuk tanggal tertentu sesuai metode, mazhab Asar, ihtiyat, dan lokasi di Pengaturan.</summary>
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
            if (!Settings.Cur.PrayerAlert) return;
            var now = DateTime.Now;
            foreach (var (name, at) in For(now.Date).All)
            {
                var key = $"{now:yyyyMMdd}{name}";
                if (now >= at && now < at.AddMinutes(2) && key != _last)
                {
                    _last = key;
                    IslandWindow.Instance?.Alert($"Waktu {name} · {at:HH.mm}", 30);
                    if (Settings.Cur.PrayerPauseMusic && MediaService.I.Playing) MediaService.I.PlayPause();
                }
            }
        };
        T.Start();
    }
}
