using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZaxLet;

public sealed record WorkerInfo(
    string Name,
    string Repo,
    string State,
    string Phase,
    string Model,
    int TimeoutMin,
    DateTimeOffset Started,
    DateTimeOffset Updated,
    int Pid,
    string[] Detail,
    string Title = "");

/// <summary>
/// Parser murni untuk status worker Hydrogen (JSON di ~/.config/ponytail/workers). Tanpa WPF atau Settings.
/// </summary>
public static class AiTaskParse
{
    static readonly Regex TokenRx = new(@"^Durasi: \d+ s; token: (\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Mem-parse JSON status worker (tahan BOM UTF-8 dan file terpotong).</summary>
    public static WorkerInfo ParseWorker(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        json = json.TrimStart('\uFEFF');
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;

            var name = Str(r, "name");
            var state = Str(r, "state");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(state)) return null;

            var repo = Str(r, "repo");
            var phase = Str(r, "phase");
            var model = Str(r, "model");
            int timeout = r.TryGetProperty("timeoutMin", out var tm) && tm.TryGetInt32(out int tv) ? tv : 0;
            int pid = r.TryGetProperty("pid", out var pe) && pe.TryGetInt32(out int pv) ? pv : 0;
            var started = Time(r, "startedAt");
            var updated = Time(r, "updatedAt");

            var detail = Array.Empty<string>();
            if (r.TryGetProperty("detail", out var de) && de.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in de.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
                detail = list.ToArray();
            }

            var title = Str(r, "title");
            return new WorkerInfo(name, repo, state, phase, model, timeout, started, updated, pid, detail, title);
        }
        catch { return null; }
    }

    /// <summary>Menentukan status efektif worker: BERJALAN menjadi MATI bila PID mati atau diam melebihi TimeoutMin + 10 menit.</summary>
    public static string Effective(WorkerInfo w, DateTimeOffset now, Func<int, bool> pidAlive)
    {
        if (w == null) return "MATI";
        if (w.State != "BERJALAN") return w.State;
        if (w.Pid <= 0 || pidAlive == null || !pidAlive(w.Pid)) return "MATI";
        if (w.Updated != DateTimeOffset.MinValue && (now - w.Updated).TotalMinutes > w.TimeoutMin + 10) return "MATI";
        return "BERJALAN";
    }

    /// <summary>Memetakan teks fase worker ke tahap 1..4, atau 0 bila tidak dikenal.</summary>
    public static int Stage(string phase)
    {
        if (string.IsNullOrEmpty(phase)) return 0;
        if (phase.Contains("worktree", StringComparison.OrdinalIgnoreCase)) return 1;
        if (phase.Contains("Hydrogen", StringComparison.OrdinalIgnoreCase)) return 2;
        if (phase.Contains("memeriksa", StringComparison.OrdinalIgnoreCase)) return 3;
        if (phase.Contains("reviewer", StringComparison.OrdinalIgnoreCase)) return 4;
        return 0;
    }

    /// <summary>Mengambil vonis reviewer ("OK" atau "PERIKSA") dari baris detail, atau null bila tidak ada.</summary>
    public static string Verdict(string[] detail)
    {
        if (detail == null) return null;
        string verdict = null;
        foreach (var entry in detail)
        {
            if (string.IsNullOrEmpty(entry)) continue;
            foreach (var raw in entry.Split('\n'))
            {
                var t = raw.TrimStart();
                if (!t.StartsWith("VONIS:", StringComparison.OrdinalIgnoreCase)) continue;
                var rest = t.Substring(6).TrimStart();
                if (rest.StartsWith("OK", StringComparison.OrdinalIgnoreCase)) verdict = "OK";
                else if (rest.StartsWith("PERIKSA", StringComparison.OrdinalIgnoreCase)) verdict = "PERIKSA";
            }
        }
        return verdict;
    }

    /// <summary>Mengambil jumlah token worker utama dari baris detail ("Durasi: ...; token: N"), 0 bila tidak ada.</summary>
    public static long Tokens(string[] detail)
    {
        if (detail == null || detail.Length == 0) return 0;
        var m = TokenRx.Match(string.Join("\n", detail));
        return m.Success && long.TryParse(m.Groups[1].Value, out long tok) ? tok : 0;
    }

    /// <summary>Mengelompokkan jenis worker berdasarkan awalan nama ("audit", "verify", atau "other").</summary>
    public static string Kind(string name)
    {
        if (string.IsNullOrEmpty(name)) return "other";
        var n = name.ToLowerInvariant();
        if (n == "aud" || n == "audit" || n.StartsWith("aud-") || n.StartsWith("audit-")) return "audit";
        if (n == "ver" || n == "verify" || n == "verifikasi" || n.StartsWith("ver-") || n.StartsWith("verify-") || n.StartsWith("verifikasi-")) return "verify";
        return "other";
    }

    /// <summary>Label tampilan untuk jenis worker.</summary>
    public static string KindTitle(string kind) => kind switch
    {
        "audit" => "Audit",
        "verify" => "Verify",
        _ => "Lainnya",
    };

    /// <summary>Keterangan ringkas worker dari Title atau nama yang dirapikan.</summary>
    public static string Describe(WorkerInfo w)
    {
        if (w == null) return "";
        if (!string.IsNullOrWhiteSpace(w.Title)) return w.Title.Trim();
        var name = w.Name ?? "";
        var kind = Kind(name);
        if (kind == "other") return name.Replace('-', ' ');
        var rest = StripKindPrefix(name, kind).Replace('-', ' ').Trim();
        var title = KindTitle(kind);
        return string.IsNullOrEmpty(rest) ? title : $"{title} · {rest}";
    }

    static string StripKindPrefix(string name, string kind)
    {
        string[] prefixes = kind == "audit"
            ? new[] { "audit-", "aud-" }
            : new[] { "verifikasi-", "verify-", "ver-" };
        foreach (var p in prefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return name.Substring(p.Length);
        return "";
    }

    static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static DateTimeOffset Time(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var dt)
            ? dt
            : DateTimeOffset.MinValue;
}
