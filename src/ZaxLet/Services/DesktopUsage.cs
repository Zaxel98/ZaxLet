using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ZaxLet;

/// <summary>
/// Kuota paket Claude yang dicatat aplikasi Claude Desktop sendiri (plan-usage-history.json, tiap ±15 menit): persentase jendela 5 jam ("fh")
/// dan mingguan ("sd"). Berkas biasa tanpa token, jadi ZaxLet cukup membacanya bila Claude Code di terminal tidak punya login sendiri.
/// Tidak ada waktu reset di dalamnya, dan umur cuplikan terakhir ditampilkan supaya angka basi tidak tampak segar.
/// </summary>
public static class DesktopUsage
{
    public sealed record Sample(long T, double FiveHour, double Weekly);

    /// <summary>Cuplikan terbaru yang berisi angka, atau null. Tahan terhadap berkas kosong, format lain, dan beberapa organisasi (terbaru menang).</summary>
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

    /// <summary>Berkas terbaru dari semua jenis pemasangan: versi Store (Claude-Data\…), pemasang biasa (%APPDATA%\Claude), dan folder paket.</summary>
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
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);   // aplikasi Claude bisa sedang menulisnya
        using var rd = new StreamReader(fs);
        return rd.ReadToEnd();
    }
}
