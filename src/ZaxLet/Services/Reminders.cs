using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace ZaxLet;

public sealed class ReminderItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public DateTime DueAt { get; set; }
}

/// <summary>Logika murni pengingat berlabel (tanpa UI/timer) untuk parsing dan evaluasi jatuh tempo.</summary>
public static class ReminderLogic
{
    static readonly Regex ClockRx = new(@"^(\d{1,2})[:.](\d{2})$", RegexOptions.Compiled);

    public static bool TryParse(string arg, DateTime now, out DateTime due, out string label)
    {
        due = default;
        label = "";
        arg = (arg ?? "").Trim();
        int sp = arg.IndexOf(' ');
        if (sp <= 0) return false;
        string spec = arg[..sp].Trim();
        label = arg[(sp + 1)..].Trim();
        if (label.Length == 0) return false;
        if (label.Length > 200) label = label[..200];

        var cm = ClockRx.Match(spec);
        if (cm.Success)
        {
            int h = int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture);
            int m = int.Parse(cm.Groups[2].Value, CultureInfo.InvariantCulture);
            if (h > 23 || m > 59) return false;
            due = now.Date.AddHours(h).AddMinutes(m);
            if (due <= now) due = due.AddDays(1);
            return true;
        }

        if (DurText.Parse(spec, 60, out var sec) && sec >= 1 && sec <= 365 * 86400)
        {
            due = now.AddSeconds(sec);
            return true;
        }
        return false;
    }

    public static (List<ReminderItem> Remaining, List<(ReminderItem Item, bool IsLate)> Due) EvaluateDue(
        IReadOnlyList<ReminderItem> items, DateTime now, TimeSpan maxLate, TimeSpan lateThreshold)
    {
        var remaining = new List<ReminderItem>();
        var due = new List<(ReminderItem, bool)>();
        foreach (var item in items)
        {
            if (item.DueAt > now) { remaining.Add(item); continue; }
            var late = now - item.DueAt;
            if (late < maxLate) due.Add((item, late >= lateThreshold));
        }
        return (remaining, due);
    }
}

/// <summary>Pengingat berlabel satu kali (independen dari TimerService/Pomodoro), disimpan ke reminders.json.</summary>
public sealed class ReminderService
{
    public static readonly ReminderService I = new();
    static readonly TimeSpan MaxLate = TimeSpan.FromHours(12);

    public event Action Changed;
    readonly List<ReminderItem> _items = Store.Load<List<ReminderItem>>("reminders.json") ?? new();
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };

    ReminderService()
    {
        _t.Tick += (_, _) => CheckDue(startup: false);
    }

    public IReadOnlyList<ReminderItem> Pending => _items.OrderBy(x => x.DueAt).ToList();

    public void Start()
    {
        CheckDue(startup: true);
        if (_items.Count > 0) _t.Start();
    }

    public ReminderItem Add(string arg)
    {
        if (!ReminderLogic.TryParse(arg, DateTime.Now, out var due, out var label)) return null;
        var item = new ReminderItem { Label = label, DueAt = due };
        _items.Add(item);
        SaveAndSync();
        return item;
    }

    public void Remove(string id)
    {
        if (_items.RemoveAll(x => x.Id == id) > 0) SaveAndSync();
    }

    void CheckDue(bool startup)
    {
        if (_items.Count == 0) { _t.Stop(); return; }
        var now = DateTime.Now;
        var threshold = startup ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(2);
        var (remaining, due) = ReminderLogic.EvaluateDue(_items, now, MaxLate, threshold);
        if (remaining.Count == _items.Count) return;

        _items.Clear();
        _items.AddRange(remaining);
        SaveAndSync();

        foreach (var (item, isLate) in due)
        {
            var msg = isLate
                ? L.F("Pengingat (terlambat): {0}", item.Label)
                : L.F("Pengingat: {0}", item.Label);
            IslandWindow.Instance?.Alert(msg, 10);
        }
    }

    void SaveAndSync()
    {
        Store.Save("reminders.json", _items);
        if (_items.Count > 0) _t.Start();
        else _t.Stop();
        Changed?.Invoke();
    }
}
