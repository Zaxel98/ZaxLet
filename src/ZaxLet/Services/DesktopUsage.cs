using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ZaxLet;

/// <summary>
/// Claude plan quota recorded by the Claude Desktop app itself (plan-usage-history.json, every ±15 minutes): 5-hour window ("fh")
/// and weekly ("sd") percentages. Plain file without a token, so ZaxLet can read it when Claude Code in the terminal has no login of its own.
/// No reset time inside, and the age of the last snapshot is shown so stale numbers do not look fresh.
/// </summary>
public static class DesktopUsage
{
    public sealed record Sample(long T, double FiveHour, double Weekly);

    /// <summary>Latest snapshot containing numbers, or null. Resilient to empty files, other formats, and multiple organizations (newest wins).</summary>
    public static Sample ParseLatest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("samples", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        Sample best = null;
        foreach (var s in arr.EnumerateArray())
        {
            if (!s.TryGetProperty("t", out var t) || !t.TryGetInt64(out var ms) || !s.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Object) continue;
            double fh = Num(u, "fh"), sd = Num(u, "sd");
            if (fh < 0 && sd < 0) continue;
            if (best == null || ms >= best.T) best = new Sample(ms, fh, sd);
        }
        return best;
    }

    static double Num(JsonElement u, string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : -1;

    /// <summary>Newest file across all install types: Store version (Claude-Data\…), regular installer (%APPDATA%\Claude), and package folder.</summary>
    public static string FindFile()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var c = new List<string> { Path.Combine(roam, "Claude", "plan-usage-history.json") };
        try { foreach (var d in Directory.EnumerateDirectories(Path.Combine(local, "Claude-Data"))) c.Add(Path.Combine(d, "plan-usage-history.json")); } catch { }
        try { foreach (var d in Directory.EnumerateDirectories(Path.Combine(local, "Packages"), "Claude_*")) c.Add(Path.Combine(d, "LocalCache", "Roaming", "Claude", "plan-usage-history.json")); } catch { }
        return c.Where(File.Exists).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
    }

    public static string ReadAll(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);   // Claude app may be writing to it
        using var rd = new StreamReader(fs);
        return rd.ReadToEnd();
    }
}
