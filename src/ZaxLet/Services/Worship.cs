using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZaxLet;

/// <summary>
/// Custom weekly worship schedule (for non-Islamic prayer items), one entry per line or separated by semicolons:
/// "day time name", e.g., "min 08:00 Kebaktian; jum,sab 18.30 Latihan koor; tiap 21:00 Doa malam". Pure, no WPF, so it is testable.
/// </summary>
public static class Worship
{
    public sealed record Item(DayOfWeek Day, TimeSpan At, string Label);

    static readonly Dictionary<string, DayOfWeek> Days = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sen"] = DayOfWeek.Monday, ["senin"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday,
        ["sel"] = DayOfWeek.Tuesday, ["selasa"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday,
        ["rab"] = DayOfWeek.Wednesday, ["rabu"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["kam"] = DayOfWeek.Thursday, ["kamis"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday,
        ["jum"] = DayOfWeek.Friday, ["jumat"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday,
        ["sab"] = DayOfWeek.Saturday, ["sabtu"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday,
        ["min"] = DayOfWeek.Sunday, ["minggu"] = DayOfWeek.Sunday, ["ahad"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday,
    };

    /// <summary>Unparseable entries are skipped (does not throw).</summary>
    public static List<Item> Parse(string text)
    {
        var r = new List<Item>();
        foreach (var raw in (text ?? "").Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var t = parts[1].Replace('.', ':');
            if (!TimeSpan.TryParseExact(t, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var at) || at >= TimeSpan.FromDays(1)) continue;
            var label = parts.Length > 2 ? parts[2].Trim() : "";
            var days = new List<DayOfWeek>();
            if (parts[0].Equals("tiap", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("setiap", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("daily", StringComparison.OrdinalIgnoreCase) || parts[0] == "*")
                for (int d = 0; d < 7; d++) days.Add((DayOfWeek)d);
            else
                foreach (var d in parts[0].Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries))
                    if (Days.TryGetValue(d.Trim(), out var dow)) days.Add(dow);
            foreach (var d in days) r.Add(new Item(d, at, label));
        }
        return r;
    }

    /// <summary>Entries due at <paramref name="now"/> (within a two-minute window from their scheduled time).</summary>
    public static IEnumerable<Item> Due(IEnumerable<Item> items, DateTime now)
    {
        foreach (var i in items)
            if (i.Day == now.DayOfWeek && now.TimeOfDay >= i.At && now.TimeOfDay < i.At + TimeSpan.FromMinutes(2)) yield return i;
    }
}
