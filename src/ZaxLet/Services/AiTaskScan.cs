using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ZaxLet;

public sealed record WorkerRow(WorkerInfo Info, string State);

/// <summary>
/// Pemindai file lokal untuk status worker Hydrogen (~/.config/ponytail/workers/*.json).
/// </summary>
public static class AiTaskScan
{
    /// <summary>Memindai folder worker JSON, menyaring yang aktif atau selesai <= 1 jam, dan mengurutkannya.</summary>
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
            catch { /* file sedang ditulis atau terkunci dilewati */ }
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

    /// <summary>Memeriksa apakah proses dengan PID tersebut masih berjalan.</summary>
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
