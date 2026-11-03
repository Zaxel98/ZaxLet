using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ZaxLet;

/// <summary>Teks durasi bebas untuk pengaturan: "45", "30dtk", "10m", "1.5 jam", "1j30m", "500ms".</summary>
public static class DurText
{
    public static string Fmt(double sec)
    {
        var inv = CultureInfo.InvariantCulture;
        string s = L.T("dtk"), mn = L.T("mnt"), hr = L.T("jam");
        if (sec < 1) return Math.Round(sec * 1000) + " ms";
        if (sec < 60) return Math.Round(sec, 1).ToString("0.#", inv) + " " + s;
        if (sec < 3600) { int m = (int)(sec / 60), r = (int)Math.Round(sec - m * 60); return r == 0 ? $"{m} {mn}" : $"{m} {mn} {r} {s}"; }
        int h = (int)(sec / 3600), mm = (int)Math.Round((sec - h * 3600) / 60);
        return mm == 0 ? $"{h} {hr}" : $"{h} {hr} {mm} {mn}";
    }

    /// <summary>Angka tanpa satuan dikali <paramref name="bare"/> detik (1 = detik, 60 = menit). False bila tidak terbaca.</summary>
    public static bool Parse(string text, double bare, out double sec)
    {
        sec = 0;
        var ms = Regex.Matches((text ?? "").ToLowerInvariant().Replace(',', '.'), @"(\d+(?:\.\d+)?)\s*(ms|detik|dtk|d|s|menit|mnt|min|m|jam|j|h)?");
        if (ms.Count == 0) return false;
        foreach (Match m in ms)
        {
            var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            sec += m.Groups[2].Value switch
            {
                "ms" => n / 1000,
                "menit" or "mnt" or "min" or "m" => n * 60,
                "jam" or "j" or "h" => n * 3600,
                "detik" or "dtk" or "d" or "s" => n,
                _ => n * bare,
            };
        }
        return true;
    }
}
