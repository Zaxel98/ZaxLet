using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>
/// Catatan minum harian + pengingat lembut: hanya di jam aktif, jarak antar pengingat bebas diatur, dan (mode pintar)
/// hanya bila asupan tertinggal dari jadwal linear menuju target. Minum menunda pengingat berikutnya.
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

    /// <summary>Kapan pengingat berikutnya (null bila mati, target tercapai, atau di luar jam aktif).</summary>
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
        if (IslandWindow.Instance?.Fullscreen == true) return;   // jangan ganggu saat layar penuh (video, game, presentasi)
        if (NextReminder() is not { } due || now < due) return;
        if (s.WaterSmart)
        {
            double expected = Goal * (now - from).TotalSeconds / (to - from).TotalSeconds;
            if (Today >= expected - s.WaterCupMl / 2.0) return;   // masih sesuai jadwal: tidak perlu mengingatkan
        }
        _lastRemind = now;
        IslandWindow.Instance?.Alert(L.F("Waktunya minum · {0}/{1} ml", Today, Goal), Math.Max(2, s.WaterShowSec));
    }
}
