using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>One-time labeled reminders (independent of TimerService/Pomodoro), saved to reminders.json.</summary>
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
