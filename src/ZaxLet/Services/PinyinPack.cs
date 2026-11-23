using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ZaxLet;

/// <summary>
/// Mandarin pinyin table for the Latin script Pack (user-downloaded, not part of the installer). Sourced from mozillazg/pinyin-data (MIT)
/// at a pinned tag with pinned SHA-256; converted on device into a small gzip archive with the first reading per character ("C" + "pinyin" per line).
/// </summary>
public static class PinyinPack
{
    public const string Url = "https://raw.githubusercontent.com/mozillazg/pinyin-data/v0.16.0/pinyin.txt";
    public const string Sha256 = "2342a6f98c85dbc3c8d26aef31744876a36326c6466e51e9a0f16898c36c2011";

    /// <summary>Line "U+4E2D: zhōng,zhòng  # 中" -> "中zhōng". Basic CJK block (3400-9FFF) and 〇 only.</summary>
    public static byte[] Convert(string txt)
    {
        var sb = new StringBuilder();
        foreach (var raw in (txt ?? "").Split('\n'))
        {
            var l = raw.TrimEnd('\r');
            int colon = l.IndexOf(':');
            if (!l.StartsWith("U+", StringComparison.Ordinal) || colon < 3) continue;
            if (!int.TryParse(l.AsSpan(2, colon - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)) continue;
            if (!(cp >= 0x3400 && cp <= 0x9FFF || cp == 0x3007)) continue;
            var rest = l[(colon + 1)..];
            int hash = rest.IndexOf('#');
            if (hash >= 0) rest = rest[..hash];
            var first = rest.Split(',')[0].Trim();
            if (first.Length > 0) sb.Append((char)cp).Append(first).Append('\n');
        }
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal)) gz.Write(Encoding.UTF8.GetBytes(sb.ToString()));
        return ms.ToArray();
    }
}
