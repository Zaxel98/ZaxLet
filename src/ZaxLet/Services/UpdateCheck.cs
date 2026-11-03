using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace ZaxLet;

/// <summary>
/// Logika murni pembaruan otomatis (tanpa jaringan/UI supaya bisa diuji): membaca rilis GitHub, membandingkan versi, memeriksa SHA-256,
/// dan mengekstrak arsip dengan aman. Rilis harus memuat aset "ZaxLet-update.zip" dan hash-nya (kolom "digest" GitHub, atau aset
/// "ZaxLet-update.zip.sha256"). Pembaruan hanya diunduh dari github.com / githubusercontent.com.
/// </summary>
public static class UpdateCheck
{
    public const string AssetName = "ZaxLet-update.zip";

    public sealed record Release(string Tag, Version Version, string Url, string Sha256, string Notes) { public string ShaUrl { get; init; } }

    public static Version ParseVersion(string tag)
    {
        tag = (tag ?? "").Trim().TrimStart('v', 'V');
        int cut = tag.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) tag = tag[..cut];
        if (!tag.Contains('.')) tag += ".0";
        return Version.TryParse(tag, out var v) ? v : null;
    }

    public static bool HostAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Rilis terbaru dari JSON API GitHub; null bila tidak ada aset pembaruan. Hash kosong bila rilis tidak menyertakannya (pemanggil harus menolak).</summary>
    public static Release ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (!r.TryGetProperty("tag_name", out var tag) || ParseVersion(tag.GetString()) is not { } ver) return null;
        string url = null, sha = "", shaUrl = null;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : "";
                var dl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                if (name == AssetName)
                {
                    url = dl;
                    if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String && dg.GetString() is { } s && s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = s[7..];
                }
                else if (name == AssetName + ".sha256") shaUrl = dl;
            }
        if (url == null) return null;
        var notes = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "";
        return new Release(tag.GetString(), ver, url, sha.Trim().ToLowerInvariant(), notes) { ShaUrl = shaUrl };
    }

    public static bool IsNewer(Version latest, Version current) => latest != null && current != null && latest > current;

    /// <summary>Isi berkas .sha256 ("hash  nama" atau hash saja) -> 64 heksa huruf kecil, atau "".</summary>
    public static string ReadShaFile(string text)
    {
        var t = (text ?? "").Trim().Split(' ', '\t', '\r', '\n').FirstOrDefault() ?? "";
        return t.Length == 64 && t.All(Uri.IsHexDigit) ? t.ToLowerInvariant() : "";
    }

    public static bool Verify(string file, string expectedSha256)
    {
        if (string.IsNullOrEmpty(expectedSha256) || expectedSha256.Length != 64) return false;
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ekstrak arsip ke folder baru; menolak entri yang keluar dari folder tujuan (zip-slip) dan arsip tanpa ZaxLet.exe.</summary>
    public static void ExtractSafe(string zip, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        using var za = ZipFile.OpenRead(zip);
        if (!za.Entries.Any(e => e.FullName.Equals("ZaxLet.exe", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Arsip pembaruan tidak memuat ZaxLet.exe");
        foreach (var e in za.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destDir, e.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Entri arsip tidak aman: " + e.FullName);
            if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            e.ExtractToFile(target, true);
        }
    }
}
