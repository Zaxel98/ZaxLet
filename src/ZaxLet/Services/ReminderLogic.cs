using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ZaxLet;

public sealed class ReminderItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public DateTime DueAt { get; set; }
}

/// <summary>Pure labeled-reminder logic (no UI/timer) for parsing and due evaluation.</summary>
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
