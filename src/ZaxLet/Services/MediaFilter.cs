using System;
using System.Linq;

namespace ZaxLet;

/// <summary>Daftar aplikasi yang musiknya diabaikan island (mis. "discord, zoom"): cocok bila salah satu kata ada di nama aplikasi sumber (SourceAppUserModelId), tanpa membedakan huruf besar kecil.</summary>
public static class MediaFilter
{
    static readonly string[] AdTitles = { "advertisement", "iklan", "spotify", "spotify free" };

    /// <summary>
    /// Iklan Spotify: selama iklan, Spotify mengisi slot judul dengan teks templat (mis. "Dengarkan musik bebas iklan.") dan artisnya "Spotify".
    /// Lagu asli tidak pernah berartis persis "Spotify". Tanpa artis, judul templat hanya dihitung iklan bila sumbernya Spotify (appId boleh null untuk catatan lama).
    /// </summary>
    public static bool IsAd(string appId, string title, string artist)
    {
        var t = (title ?? "").Trim(); var a = (artist ?? "").Trim();
        if (a.Equals("Spotify", StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Length > 0 || t.Length == 0 || appId == null || !appId.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return false;
        return Array.Exists(AdTitles, x => t.Equals(x, StringComparison.OrdinalIgnoreCase)) || t.Contains("iklan", StringComparison.OrdinalIgnoreCase) || t.Contains("advert", StringComparison.OrdinalIgnoreCase);
    }

    public static bool Ignored(string appId, string list)
    {
        if (string.IsNullOrWhiteSpace(list) || string.IsNullOrEmpty(appId)) return false;
        return list.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).Any(t => t.Length >= 2 && appId.Contains(t, StringComparison.OrdinalIgnoreCase));   // minimal 2 huruf: satu huruf akan mencocokkan hampir semuanya
    }
}
