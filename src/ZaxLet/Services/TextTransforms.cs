using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZaxLet;

/// <summary>Transformasi teks murni untuk item riwayat clipboard (tanpa ketergantungan UI).</summary>
public static class TextTransforms
{
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string ToUpper(string text) =>
        (text ?? "").ToUpper(CultureInfo.CurrentCulture);

    public static string ToLower(string text) =>
        (text ?? "").ToLower(CultureInfo.CurrentCulture);

    public static string ToTitleCase(string text) =>
        CultureInfo.CurrentCulture.TextInfo.ToTitleCase((text ?? "").ToLower(CultureInfo.CurrentCulture));

    /// <summary>Pangkas tepi, padatkan spasi/tab berulang menjadi satu spasi, dan batasi baris baru 3+ menjadi 2.</summary>
    public static string CleanSpaces(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>Ganti semua baris baru menjadi satu spasi.</summary>
    public static string SingleLine(string text) =>
        Regex.Replace((text ?? "").Trim(), @"[\r\n]+", " ");

    public static string DecodeUrl(string text)
    {
        try { return Uri.UnescapeDataString(text ?? ""); }
        catch { return text ?? ""; }
    }

    /// <summary>Mengembalikan true hanya bila teks adalah Base64 valid yang menghasilkan teks UTF-8 valid.</summary>
    public static bool TryDecodeBase64(string text, out string decoded)
    {
        decoded = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        try
        {
            var buf = new byte[(s.Length * 3) / 4 + 4];
            if (!Convert.TryFromBase64String(s, buf, out int written) || written == 0) return false;
            var result = StrictUtf8.GetString(buf, 0, written);
            if (result.Length == 0) return false;
            foreach (char c in result)
            {
                if (char.IsControl(c) && c is not ('\r' or '\n' or '\t')) return false;
            }
            decoded = result;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
