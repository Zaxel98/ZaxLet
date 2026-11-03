using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ZaxLet;

/// <summary>
/// Tabel pinyin Mandarin untuk Paket tulisan Latin (diunduh pengguna, bukan bagian dari installer). Sumbernya data teks mozillazg/pinyin-data (MIT)
/// pada tag tetap dengan SHA-256 dipaku; diubah di perangkat menjadi arsip gzip kecil berisi bacaan pertama tiap aksara ("A" + "pinyin" per baris).
/// </summary>
public static class PinyinPack
{
    public const string Url = "https://raw.githubusercontent.com/mozillazg/pinyin-data/v0.16.0/pinyin.txt";
    public const string Sha256 = "2342a6f98c85dbc3c8d26aef31744876a36326c6466e51e9a0f16898c36c2011";

    /// <summary>Baris "U+4E2D: zhōng,zhòng  # 中" -> "中zhōng". Hanya blok CJK dasar (3400-9FFF) dan 〇.</summary>
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
