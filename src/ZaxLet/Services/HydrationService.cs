using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>
/// Daily water intake log + gentle reminders: only during active hours, configurable interval, and (smart mode)
/// only when intake falls behind a linear schedule toward the goal. Logging a drink postpones the next reminder.
/// </summary>
public sealed class HydrationService
{
    public static readonly HydrationService I = new();
    public event Action Changed;

    readonly Dictionary<string, int> _days = Store.Load<Dictionary<string, int>>("water.json") ?? new();
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(20) };
    DateTime _lastRemind = DateTime.MinValue, _lastDrink = DateTime.MinValue;
    int _lastAdd;

    HydrationService() { _t.Tick += (_, _) => Check(); }

    static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public int Today => _days.GetValueOrDefault(Key(DateTime.Today));
    public int Goal => Math.Max(100, Settings.Cur.WaterGoalMl);

    public void Start() { _t.Start(); }

    public void Add(int ml)
    {
        var k = Key(DateTime.Today);
        _days[k] = Math.Max(0, _days.GetValueOrDefault(k) + ml);
        _lastAdd = ml; _lastDrink = DateTime.Now;
        Save();
    }

    /// <summary>Reset today's count to zero (mislogged, or start over).</summary>
    public void Reset() { _days[Key(DateTime.Today)] = 0; _lastAdd = 0; Save(); }

    public void Undo() { if (_lastAdd != 0) { Add(-_lastAdd); _lastAdd = 0; } }

    void Save()
    {
        foreach (var old in _days.Keys.Where(k => string.CompareOrdinal(k, Key(DateTime.Today.AddDays(-60))) < 0).ToList()) _days.Remove(old);
        Store.Save("water.json", _days);
        Changed?.Invoke();
    }

    public List<(DateTime Day, int Ml)> Last7() =>
        Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(i - 6)).Select(d => (d, _days.GetValueOrDefault(Key(d)))).ToList();

    static TimeSpan Parse(string s, TimeSpan def) => TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var t) ? t : def;
    (DateTime From, DateTime To) Window()
    {
        var s = Settings.Cur;
        var f = Parse(s.WaterFrom, TimeSpan.FromHours(8)); var t = Parse(s.WaterTo, TimeSpan.FromHours(22));
        if (t <= f) t = f.Add(TimeSpan.FromHours(1));
        return (DateTime.Today + f, DateTime.Today + t);
    }

    /// <summary>When the next reminder is due (null if disabled, goal reached, or outside active hours).</summary>
    public DateTime? NextReminder()
    {
        var s = Settings.Cur;
        if (!s.WaterOn || Today >= Goal) return null;
        var (from, to) = Window();
        var due = new[] { _lastRemind, _lastDrink, from }.Max().AddSeconds(Math.Max(10, s.WaterEverySec));
        if (due < DateTime.Now) due = DateTime.Now;
        if (due < from) due = from;
        return due > to ? null : due;
    }

    void Check()
    {
        var s = Settings.Cur;
        if (!s.WaterOn || Today >= Goal) return;
        var now = DateTime.Now;
        var (from, to) = Window();
        if (now < from || now > to) return;
        if (IslandWindow.Instance?.Fullscreen == true) return;   // do not disturb during fullscreen (video, game, presentation)
        if (NextReminder() is not { } due || now < due) return;
        if (s.WaterSmart)
        {
            double expected = Goal * (now - from).TotalSeconds / (to - from).TotalSeconds;
            if (Today >= expected - s.WaterCupMl / 2.0) return;   // still on schedule: no need to remind
        }
        _lastRemind = now;
        IslandWindow.Instance?.Alert(L.F("Waktunya minum · {0}/{1} ml", Today, Goal), Math.Max(2, s.WaterShowSec));
    }
}
