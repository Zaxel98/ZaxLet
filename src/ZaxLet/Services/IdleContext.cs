using System;
using System.Collections.Generic;

namespace ZaxLet;

/// <summary>Extra line in the idle pill by time of day: morning weather + next prayer, noon today's tasks, afternoon prayer + remaining water, night tomorrow's schedule. Null if empty.</summary>
public static class IdleContext
{
    public static string Compose(DateTime now, string weather, (string Name, DateTime At)? prayer, int waterLeftMl, int tasksToday, string tomorrow)
    {
        var parts = new List<string>();
        void Add(string s) { if (!string.IsNullOrWhiteSpace(s)) parts.Add(s); }
        string Pr() => prayer is { } p ? L.T(p.Name) + " " + p.At.ToString("HH.mm") : null;
        int h = now.Hour;
        if (h >= 5 && h < 10) { Add(weather); Add(Pr()); }
        else if (h >= 10 && h < 15) { if (tasksToday > 0) Add(L.F("{0} tugas hari ini", tasksToday)); }
        else if (h >= 15 && h < 19) { Add(Pr()); if (waterLeftMl > 0) Add(L.F("Minum {0} ml lagi", waterLeftMl)); }
        else Add(tomorrow);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
