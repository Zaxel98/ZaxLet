using System;
using System.Globalization;

namespace ZaxLet;

/// <summary>Penentu apakah jam saat ini masuk rentang jam tenang [from, to), termasuk yang melewati tengah malam.</summary>
public static class QuietHours
{
    public static bool TryParseHm(string s, out TimeSpan t)
    {
        t = default;
        return !string.IsNullOrWhiteSpace(s)
            && TimeSpan.TryParseExact(s.Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out t)
            && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);
    }

    /// <summary>True bila <paramref name="on"/> menyala, format jam valid, dan <paramref name="now"/> berada di dalam [from, to). Input tidak valid dianggap mati.</summary>
    public static bool IsActive(DateTime now, bool on, string fromStr, string toStr)
    {
        if (!on || !TryParseHm(fromStr, out var from) || !TryParseHm(toStr, out var to)) return false;
        if (from == to) return false;
        var t = now.TimeOfDay;
        return from < to ? t >= from && t < to : t >= from || t < to;
    }
}
