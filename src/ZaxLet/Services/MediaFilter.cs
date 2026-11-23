using System;
using System.Linq;

namespace ZaxLet;

/// <summary>List of apps whose music is ignored by the island (e.g., "discord, zoom"): matches if any word appears in the source app name (SourceAppUserModelId), case-insensitive.</summary>
public static class MediaFilter
{
    static readonly string[] AdTitles = { "advertisement", "iklan", "spotify", "spotify free" };

    /// <summary>Spotify ad texts that previously slipped through (artist is the advertiser name, not "Spotify"): blocked by title. Add here if more slip through.</summary>
    static readonly string[] AdBlock = { "Lagu-lagu cinta terbaik.", "DENGARKAN SEKARANG", "Benar dia hemat" };

    /// <summary>Spotify ads not recognized by text: source is Spotify and duration is under 45s (real songs are rarely that short).</summary>
    public static bool ShortSpotifyAd(string appId, TimeSpan len) =>
        len > TimeSpan.Zero && len.TotalSeconds < 45 && appId != null && appId.Contains("spotify", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Spotify ad: during ads, Spotify fills the title slot with template text (e.g., "Listen to ad-free music.") and artist "Spotify".
    /// Real songs never have artist literally "Spotify". Without an artist, template titles only count as ads if the source is Spotify (appId may be null for legacy records).
    /// </summary>
    public static bool IsAd(string appId, string title, string artist)
    {
        var t = (title ?? "").Trim(); var a = (artist ?? "").Trim();
        if (a.Equals("Spotify", StringComparison.OrdinalIgnoreCase)) return true;
        if (Array.Exists(AdBlock, x => t.Equals(x, StringComparison.OrdinalIgnoreCase))) return true;
        if (a.Length > 0 || t.Length == 0 || appId == null || !appId.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return false;
        return Array.Exists(AdTitles, x => t.Equals(x, StringComparison.OrdinalIgnoreCase)) || t.Contains("iklan", StringComparison.OrdinalIgnoreCase) || t.Contains("advert", StringComparison.OrdinalIgnoreCase);
    }

    public static bool Ignored(string appId, string list)
    {
        if (string.IsNullOrWhiteSpace(list) || string.IsNullOrEmpty(appId)) return false;
        return list.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).Any(t => t.Length >= 2 && appId.Contains(t, StringComparison.OrdinalIgnoreCase));   // minimum 2 characters: a single letter would match almost everything
    }
}
