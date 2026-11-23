using System;
using System.Globalization;

namespace ZaxLet;

/// <summary>Determines whether the current time falls within the quiet hours range [from, to), including overnight ranges.</summary>
public static class QuietHours
{
    public static bool TryParseHm(string s, out TimeSpan t)
    {
        t = default;
        return !string.IsNullOrWhiteSpace(s)
            && TimeSpan.TryParseExact(s.Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out t)
            && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);
    }

    /// <summary>True if <paramref name="on"/> is enabled, time format is valid, and <paramref name="now"/> is within [from, to). Invalid input is treated as off.</summary>
    public static bool IsActive(DateTime now, bool on, string fromStr, string toStr)
    {
        if (!on || !TryParseHm(fromStr, out var from) || !TryParseHm(toStr, out var to)) return false;
        if (from == to) return false;
        var t = now.TimeOfDay;
        return from < to ? t >= from && t < to : t >= from || t < to;
    }
}
