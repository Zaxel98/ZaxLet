using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ZaxLet;

public sealed record WorkerRow(WorkerInfo Info, string State);

public sealed record ClaudeTask(List<PlanStep> Steps, bool Live, int OtherLive);

/// <summary>
/// Pemindai file lokal untuk status worker Hydrogen (~/.config/ponytail/workers/*.json)
/// dan rencana tugas sesi Claude (~/.claude/projects/*/*.jsonl).
/// </summary>
public static class AiTaskScan
{
    static readonly byte[] KwPlan = Encoding.ASCII.GetBytes("update_plan");
    static readonly byte[] KwTodo = Encoding.ASCII.GetBytes("TodoWrite");
    static readonly byte[] KwWrap = Encoding.ASCII.GetBytes("wrap_up");
    static readonly object _cacheLock = new();
    static readonly Dictionary<string, (DateTime MtimeUtc, long Length, int MaxTailBytes, List<PlanStep> Steps)> _claudeCache = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Memindai sesi Claude JSONL dalam 1 jam terakhir dan mengembalikan rencana tugas terbaru.</summary>
    public static ClaudeTask Claude(string projectsDir, DateTime now, int maxTailBytes = 2_000_000)
    {
        // shortcut: rencana yang lebih tua dari ekor 2 MB tidak terbaca, naikkan maxTailBytes bila perlu
        if (string.IsNullOrEmpty(projectsDir) || !Directory.Exists(projectsDir)) return null;

        var cutoff = now - TimeSpan.FromHours(1);
        var liveCutoff = TimeSpan.FromMinutes(30);
        var sessions = new List<(List<PlanStep> Steps, DateTime Mtime, bool HasActive, bool IsLive)>();

        IEnumerable<string> projDirs;
        try { projDirs = Directory.EnumerateDirectories(projectsDir); }
        catch { return null; }

        foreach (var proj in projDirs)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(proj, "*.jsonl"); }
            catch { continue; }

            foreach (var file in files)
            {
                try
                {
                    var fi = new FileInfo(file);
                    var mtime = fi.LastWriteTime;
                    if (mtime < cutoff) continue;
                    var mtimeUtc = fi.LastWriteTimeUtc;
                    var len = fi.Length;

                    List<PlanStep> steps;
                    bool hit;
                    lock (_cacheLock)
                    {
                        hit = _claudeCache.TryGetValue(file, out var cached)
                            && cached.MtimeUtc == mtimeUtc
                            && cached.Length == len
                            && cached.MaxTailBytes == maxTailBytes;
                        steps = hit ? cached.Steps : null;
                    }
                    if (!hit)
                    {
                        steps = AiTaskParse.ReplayPlan(ReadTailLines(file, maxTailBytes));
                        lock (_cacheLock)
                            _claudeCache[file] = (mtimeUtc, len, maxTailBytes, steps);
                    }
                    if (steps.Count == 0) continue;

                    bool hasActive = steps.Any(s => s.State == StepState.Active);
                    bool isLive = hasActive && (now - mtime) <= liveCutoff;
                    sessions.Add((steps, mtime, hasActive, isLive));
                }
                catch { /* file terkunci atau terhapus dilewati */ }
            }
        }

        lock (_cacheLock)
        {
            foreach (var k in _claudeCache.Keys.Where(k => !File.Exists(k)).ToList())
                _claudeCache.Remove(k);
        }

        if (sessions.Count == 0) return null;

        var chosen = sessions
            .OrderByDescending(s => s.HasActive)
            .ThenByDescending(s => s.Mtime)
            .First();

        int otherLive = sessions.Count(s => s.IsLive && !ReferenceEquals(s.Steps, chosen.Steps));
        return new ClaudeTask(chosen.Steps, chosen.IsLive, otherLive);
    }

    static IEnumerable<string> ReadTailLines(string path, int maxTailBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        if (len == 0) return Array.Empty<string>();

        int toRead = (int)Math.Min(len, Math.Max(1, maxTailBytes));
        long start = len - toRead;
        if (start > 0) fs.Seek(start, SeekOrigin.Begin);

        var buf = new byte[toRead];
        int read = 0;
        while (read < toRead)
        {
            int n = fs.Read(buf, read, toRead - read);
            if (n <= 0) break;
            read += n;
        }

        int pos = 0;
        if (start > 0)
        {
            int nl = Array.IndexOf(buf, (byte)'\n', 0, read);
            if (nl < 0) return Array.Empty<string>();
            pos = nl + 1;
        }

        var lines = new List<string>();
        while (pos < read)
        {
            int nl = Array.IndexOf(buf, (byte)'\n', pos, read - pos);
            int end = nl >= 0 ? nl : read;
            var span = new ReadOnlySpan<byte>(buf, pos, end - pos);
            if (span.IndexOf(KwPlan) >= 0 || span.IndexOf(KwTodo) >= 0 || span.IndexOf(KwWrap) >= 0)
                lines.Add(Encoding.UTF8.GetString(span));
            pos = nl >= 0 ? nl + 1 : read;
        }
        return lines;
    }

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
