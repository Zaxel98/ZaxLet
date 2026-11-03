using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ZaxLet;

/// <summary>
/// Daftar lagu (segmen/bab) dari video YouTube panjang seperti kompilasi atau album penuh. SMTC hanya memberi judul video, kanal dan
/// durasi, jadi videonya dicari lewat halaman hasil pencarian YouTube (cocok bila judulnya mirip dan durasinya selisih ≤ 3 dtk), lalu
/// stempel waktu di deskripsinya dibaca ("00:04:55 Artis · Judul"). Tanpa kunci API; hanya judul dan nama kanal yang dikirim ke youtube.com.
/// </summary>
public static class YtChapters
{
    public sealed record Chapter(TimeSpan At, string Text);

    static readonly Regex Line = new(@"^[\s\-–•·\[\(]*(?:(?<h>\d{1,2}):)?(?<m>\d{1,2}):(?<s>\d{2})[\]\)]?\s*[-–—:.|]?\s*(?<t>\S.*)$", RegexOptions.Compiled);
    static readonly Regex Numbering = new(@"^\d{1,3}[\.\)]\s+", RegexOptions.Compiled);

    /// <summary>Baris berstempel waktu di deskripsi. Syarat seperti bab YouTube: sedikitnya 3, naik terus, dimulai dekat 0:00; selain itu null.</summary>
    public static List<Chapter> ParseChapters(string description)
    {
        var list = new List<Chapter>();
        foreach (var raw in (description ?? "").Split('\n'))
        {
            var m = Line.Match(raw.Trim());
            if (!m.Success) continue;
            var at = new TimeSpan(m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value) : 0, int.Parse(m.Groups["m"].Value), int.Parse(m.Groups["s"].Value));
            var text = Numbering.Replace(m.Groups["t"].Value.Trim(), "");
            if (text.Length == 0 || list.Count > 0 && at <= list[^1].At) continue;
            list.Add(new Chapter(at, text));
        }
        return list.Count >= 3 && list[0].At < TimeSpan.FromSeconds(10) ? list : null;
    }

    static string Key(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", "");

    static double Seconds(string len)
    {
        double t = 0;
        foreach (var part in (len ?? "").Split(':')) { if (!double.TryParse(part, out var v)) return -1; t = t * 60 + v; }
        return t;
    }

    static IEnumerable<JsonElement> Renderers(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.NameEquals("videoRenderer")) yield return p.Value;
                else foreach (var x in Renderers(p.Value)) yield return x;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray()) foreach (var x in Renderers(i)) yield return x;
    }

    /// <summary>ID video pertama di halaman hasil pencarian yang judulnya cocok dan durasinya ≤ 3 dtk dari durasi pemutar.</summary>
    public static string PickVideo(string html, string title, double durSec)
    {
        var m = Regex.Match(html ?? "", @"var ytInitialData = (\{.*?\});</script>", RegexOptions.Singleline);
        if (!m.Success) return null;
        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        var want = Key(title);
        foreach (var v in Renderers(doc.RootElement))
        {
            if (!v.TryGetProperty("videoId", out var id) || !v.TryGetProperty("lengthText", out var lt) || !lt.TryGetProperty("simpleText", out var ls)) continue;
            var name = v.TryGetProperty("title", out var t) && t.TryGetProperty("runs", out var runs) ? string.Concat(runs.EnumerateArray().Select(r => r.GetProperty("text").GetString())) : "";
            var got = Key(name);
            bool same = want.Length > 0 && got.Length > 0 && (got.Contains(want) || want.Contains(got));
            if (same && Math.Abs(Seconds(ls.GetString()) - durSec) <= 3) return id.GetString();
        }
        return null;
    }

    static readonly HttpClient Http = MakeHttp();
    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.Add("Accept-Language", "en");
        h.DefaultRequestHeaders.Add("Cookie", "CONSENT=YES+1; SOCS=CAI");   // lewati halaman persetujuan cookie
        return h;
    }

    /// <summary>Segmen video yang sedang diputar, atau null bila videonya tidak ketemu / tidak punya daftar lagu.</summary>
    public static async Task<List<Chapter>> FetchAsync(string title, string channel, double durSec)
    {
        var q = Uri.EscapeDataString((title + " " + channel).Trim());
        var id = PickVideo(await Http.GetStringAsync($"https://www.youtube.com/results?search_query={q}&sp=EgIQAQ%253D%253D"), title, durSec);
        if (id == null) return null;
        var page = await Http.GetStringAsync("https://www.youtube.com/watch?v=" + id);
        var d = Regex.Match(page, "\"shortDescription\":\"((?:[^\"\\\\]|\\\\.)*)\"");
        return d.Success ? ParseChapters(JsonSerializer.Deserialize<string>("\"" + d.Groups[1].Value + "\"")) : null;
    }
}
