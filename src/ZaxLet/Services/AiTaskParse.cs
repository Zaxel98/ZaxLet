using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZaxLet;

public enum StepState { Pending, Active, Done }

public sealed record PlanStep(string Text, StepState State);

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
    string[] Detail);

/// <summary>
/// Parser murni untuk rencana tugas Claude (JSONL update_plan / TodoWrite / wrap_up)
/// dan status worker Hydrogen (JSON di ~/.config/ponytail/workers). Tanpa WPF atau Settings.
/// </summary>
public static class AiTaskParse
{
    static readonly Regex TokenRx = new(@"^Durasi: \d+ s; token: (\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Memutar ulang baris JSONL sesi Claude menjadi daftar langkah rencana terakhir.</summary>
    public static List<PlanStep> ReplayPlan(IEnumerable<string> jsonlLines)
    {
        var steps = new List<PlanStep>();
        if (jsonlLines == null) return steps;

        foreach (var line in jsonlLines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.Contains("update_plan", StringComparison.Ordinal)
                && !line.Contains("TodoWrite", StringComparison.Ordinal)
                && !line.Contains("wrap_up", StringComparison.Ordinal))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (Str(root, "type") != "assistant") continue;
                if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object || Str(block, "type") != "tool_use") continue;
                    var name = Str(block, "name");
                    if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) continue;

                    if (name == "mcp__ccd_turn__update_plan" || name == "update_plan")
                        ApplyUpdatePlan(steps, input);
                    else if (name == "mcp__ccd_turn__wrap_up" || name == "wrap_up")
                        MarkAllDone(steps);
                    else if (name == "TodoWrite")
                        ApplyItems(steps, input, "todos");
                }
            }
            catch { /* baris terpotong atau bukan JSON valid dilewati */ }
        }
        return steps;
    }

    static void ApplyUpdatePlan(List<PlanStep> steps, JsonElement input)
    {
        if (input.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            ApplyItems(steps, input, "items");
            return;
        }

        if (input.TryGetProperty("advance", out var adv) && adv.ValueKind == JsonValueKind.True)
        {
            int active = steps.FindIndex(s => s.State == StepState.Active);
            if (active >= 0)
            {
                steps[active] = steps[active] with { State = StepState.Done };
                int next = steps.FindIndex(active + 1, s => s.State == StepState.Pending);
                if (next >= 0) steps[next] = steps[next] with { State = StepState.Active };
            }
            else
            {
                int first = steps.FindIndex(s => s.State == StepState.Pending);
                if (first >= 0) steps[first] = steps[first] with { State = StepState.Active };
            }
        }

        if (input.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in done.EnumerateArray())
            {
                if (el.TryGetInt32(out int idx1) && idx1 >= 1 && idx1 <= steps.Count)
                    steps[idx1 - 1] = steps[idx1 - 1] with { State = StepState.Done };
            }
        }

        if (input.TryGetProperty("start", out var start) && start.TryGetInt32(out int start1))
        {
            if (start1 >= 1 && start1 <= steps.Count)
                steps[start1 - 1] = steps[start1 - 1] with { State = StepState.Active };
        }
    }

    static void ApplyItems(List<PlanStep> steps, JsonElement input, string prop)
    {
        if (!input.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        steps.Clear();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var text = Str(item, "content");
            var st = Str(item, "status") switch
            {
                "in_progress" => StepState.Active,
                "completed" => StepState.Done,
                _ => StepState.Pending,
            };
            steps.Add(new PlanStep(text, st));
        }
    }

    static void MarkAllDone(List<PlanStep> steps)
    {
        for (int i = 0; i < steps.Count; i++)
            steps[i] = steps[i] with { State = StepState.Done };
    }

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

            return new WorkerInfo(name, repo, state, phase, model, timeout, started, updated, pid, detail);
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

    static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static DateTimeOffset Time(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var dt)
            ? dt
            : DateTimeOffset.MinValue;
}
