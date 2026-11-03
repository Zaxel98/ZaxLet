using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZaxLet;

/// <summary>
/// Agenda dari kalender iCal pribadi (tautan https:// atau webcal://): acara sekali, seharian, dan berulang (RRULE FREQ DAILY/WEEKLY/MONTHLY/YEARLY
/// dengan INTERVAL, COUNT, UNTIL, BYDAY untuk mingguan, dan EXDATE). Zona waktu: waktu berakhiran Z dikonversi ke lokal; TZID dianggap sudah lokal
/// (shortcut: kalender lintas zona waktu bisa meleset beberapa jam).
/// </summary>
public static class Ical
{
    public sealed record Ev(DateOnly Date, string Time, string Title);   // Time = "" untuk acara seharian

    public static string NormalizeUrl(string url)
    {
        url = (url ?? "").Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        return url;
    }

    public static bool LooksValidUrl(string url) => NormalizeUrl(url).StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    public static bool LooksLikeIcal(string text) => text != null && text.Contains("BEGIN:VCALENDAR", StringComparison.Ordinal);

    static List<string> Unfold(string ics)
    {
        var lines = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && lines.Count > 0) lines[^1] += raw[1..];
            else lines.Add(raw);
        }
        return lines;
    }

    static string Unescape(string s) => s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    // nilai tanggal/waktu iCal -> (tanggal lokal, jam lokal atau null bila seharian)
    static (DateOnly Date, TimeOnly? Time)? ParseWhen(string value)
    {
        value = value.Trim();
        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return (DateOnly.FromDateTime(d), null);
        bool utc = value.EndsWith("Z");
        var v = utc ? value[..^1] : value;
        if (v.Length >= 15 && DateTime.TryParseExact(v[..15], "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            if (utc) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
            return (DateOnly.FromDateTime(dt), TimeOnly.FromDateTime(dt));
        }
        return null;
    }

    sealed class Block { public string Start, Summary, Rrule, Status; public List<string> Ex = new(); }

    /// <summary>Semua acara yang jatuh pada [from, to] (inklusif), urut tanggal lalu jam.</summary>
    public static List<Ev> Parse(string ics, DateOnly from, DateOnly to)
    {
        var res = new List<Ev>();
        Block b = null;
        foreach (var l in Unfold(ics ?? ""))
        {
            if (l.StartsWith("BEGIN:VEVENT", StringComparison.Ordinal)) { b = new Block(); continue; }
            if (l.StartsWith("END:VEVENT", StringComparison.Ordinal)) { if (b != null) Expand(b, from, to, res); b = null; continue; }
            if (b == null) continue;
            int colon = l.IndexOf(':');
            if (colon < 0) continue;
            var name = l[..colon]; var val = l[(colon + 1)..];
            var key = name.Split(';')[0].ToUpperInvariant();
            switch (key)
            {
                case "DTSTART": b.Start = val; break;
                case "SUMMARY": b.Summary = Unescape(val); break;
                case "RRULE": b.Rrule = val; break;
                case "STATUS": b.Status = val.Trim(); break;
                case "EXDATE": b.Ex.AddRange(val.Split(',').Select(x => x.Trim())); break;
            }
        }
        return res.OrderBy(e => e.Date).ThenBy(e => e.Time, StringComparer.Ordinal).ToList();
    }

    static void Expand(Block b, DateOnly from, DateOnly to, List<Ev> res)
    {
        if (b.Start == null || string.IsNullOrWhiteSpace(b.Summary) || b.Status == "CANCELLED") return;
        if (ParseWhen(b.Start) is not { } w) return;
        var title = b.Summary;
        string time = w.Time is { } t ? t.ToString("HH.mm") : "";
        var skip = new HashSet<DateOnly>(b.Ex.Select(x => ParseWhen(x)).Where(x => x != null).Select(x => x.Value.Date));
        void Add(DateOnly d) { if (d >= from && d <= to && !skip.Contains(d)) res.Add(new Ev(d, time, title)); }

        if (b.Rrule == null) { Add(w.Date); return; }

        var r = b.Rrule.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        string freq = r.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        int interval = int.TryParse(r.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
        int count = int.TryParse(r.GetValueOrDefault("COUNT"), out var c) && c > 0 ? c : int.MaxValue;
        DateOnly until = r.TryGetValue("UNTIL", out var u) && ParseWhen(u) is { } uw ? uw.Date : DateOnly.MaxValue;
        var last = until < to ? until : to;
        int n = 0, guard = 0;

        IEnumerable<DateOnly> Occurrences()
        {
            switch (freq)
            {
                case "DAILY": for (var d = w.Date; ; d = d.AddDays(interval)) yield return d;
                case "WEEKLY":
                {
                    var days = r.TryGetValue("BYDAY", out var by)
                        ? by.Split(',').Select(x => x.Trim().ToUpperInvariant()).Select(x => x.Length >= 2 ? x[^2..] : x).Select(Dow).Where(x => x != null).Select(x => x.Value).OrderBy(x => ((int)x + 6) % 7).ToList()
                        : new List<DayOfWeek> { w.Date.DayOfWeek };
                    var weekStart = w.Date.AddDays(-(((int)w.Date.DayOfWeek + 6) % 7));   // Senin minggu DTSTART
                    for (var ws = weekStart; ; ws = ws.AddDays(7 * interval))
                        foreach (var dw in days) { var d = ws.AddDays(((int)dw + 6) % 7); if (d >= w.Date) yield return d; }
                }
                case "MONTHLY":
                    for (int m = 0; ; m += interval) { var first = new DateOnly(w.Date.Year, w.Date.Month, 1).AddMonths(m); if (w.Date.Day <= DateTime.DaysInMonth(first.Year, first.Month)) yield return new DateOnly(first.Year, first.Month, w.Date.Day); }
                case "YEARLY":
                    for (int y = 0; ; y += interval) { int yr = w.Date.Year + y; if (yr > 9990) yield break; if (w.Date.Month != 2 || w.Date.Day != 29 || DateTime.IsLeapYear(yr)) yield return new DateOnly(yr, w.Date.Month, w.Date.Day); }
                default: yield break;
            }
        }

        foreach (var d in Occurrences())
        {
            if (++guard > 4000 || d > last || n >= count) break;
            n++;
            Add(d);
        }
    }

    static DayOfWeek? Dow(string s) => s switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null
    };
}
