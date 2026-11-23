using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ZaxLet;

public sealed record WorkerRow(WorkerInfo Info, string State);

/// <summary>
/// Local file scanner for Hydrogen worker status (~/.config/ponytail/workers/*.json).
/// </summary>
public static class AiTaskScan
{
    /// <summary>Scans the worker JSON folder, filters for active or finished <= 1 hour ago, and sorts them.</summary>
    public static List<WorkerRow> Workers(string dir, DateTimeOffset now, Func<int, bool> pidAlive)
    {
        var list = new List<WorkerRow>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.json"); }
        catch { return list; }

        foreach (var file in files)
        {
            try
            {
                var w = AiTaskParse.ParseWorker(File.ReadAllText(file));
                if (w == null) continue;
                var eff = AiTaskParse.Effective(w, now, pidAlive);
                bool keep = eff == "BERJALAN" || eff == "MATI"
                    || (w.Updated != DateTimeOffset.MinValue && now - w.Updated <= TimeSpan.FromHours(1));
                if (keep) list.Add(new WorkerRow(w, eff));
            }
            catch { /* skip files currently being written or locked */ }
        }

        list.Sort((a, b) =>
        {
            int c = Rank(a.State).CompareTo(Rank(b.State));
            return c != 0 ? c : b.Info.Updated.CompareTo(a.Info.Updated);
        });
        return list;
    }

    static int Rank(string state) => state switch
    {
        "BERJALAN" => 0,
        "MATI" => 1,
        _ => 2,
    };

    /// <summary>Selects up to <paramref name="max"/> page-1 rows (BERJALAN highest stage then earliest Started first) plus remaining BERJALAN count.</summary>
    public static (List<WorkerRow> Shown, int MoreRunning) PageOne(List<WorkerRow> rows, int max)
    {
        if (rows == null || rows.Count == 0) return (new List<WorkerRow>(), 0);
        if (max < 0) max = 0;

        var ordered = rows.Where(r => r.State == "BERJALAN")
            .OrderByDescending(r => AiTaskParse.Stage(r.Info.Phase))
            .ThenBy(r => r.Info.Started)
            .Concat(rows.Where(r => r.State != "BERJALAN"))
            .ToList();

        var shown = ordered.Take(max).ToList();
        int moreRunning = ordered.Skip(max).Count(r => r.State == "BERJALAN");
        return (shown, moreRunning);
    }

    /// <summary>Groups workers by task (GroupKey, case-insensitive), newest group first; within each group BERJALAN first then newest first.</summary>
    public static List<(string Title, List<WorkerRow> Rows)> Groups(List<WorkerRow> rows)
    {
        var result = new List<(string Title, List<WorkerRow> Rows)>();
        if (rows == null || rows.Count == 0) return result;

        foreach (var g in rows.GroupBy(r => AiTaskParse.GroupKey(r.Info), StringComparer.OrdinalIgnoreCase))
        {
            var title = AiTaskParse.GroupKey(g.First().Info);
            var list = g
                .OrderByDescending(r => r.State == "BERJALAN")
                .ThenByDescending(r => r.Info.Started)
                .ToList();
            result.Add((title, list));
        }

        return result
            .OrderByDescending(g => g.Rows.Max(r => r.Info.Started))
            .ToList();
    }

    /// <summary>Checks whether the process with the given PID is still running.</summary>
    public static bool PidAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }
}
