using System;
using System.Collections.Generic;
using System.Linq;

namespace ZaxLet;

/// <summary>A song listened to for at least 30 seconds. T = Unix seconds when finished.</summary>
public sealed record PlayEntry(long T, string Title, string Artist, int Sec);

/// <summary>Spotify-style listening recap: top songs and artists over N days. Pure (no files or WPF), so it is testable.</summary>
public static class PlayStats
{
    public const int MinSec = 30;   // matches Spotify's play-count threshold

    public sealed record Row(string Name, string Sub, int Plays, int Sec);

    static IEnumerable<PlayEntry> Within(IEnumerable<PlayEntry> all, DateTime now, int days)
    {
        long from = new DateTimeOffset(now.ToUniversalTime()).ToUnixTimeSeconds() - days * 86400L;
        return all.Where(e => e.T >= from);
    }

    public static List<Row> TopSongs(IEnumerable<PlayEntry> all, DateTime now, int days, int n) =>
        Within(all, now, days).GroupBy(e => (e.Title.ToLowerInvariant(), e.Artist.ToLowerInvariant()))
            .Select(g => new Row(g.First().Title, g.First().Artist, g.Count(), g.Sum(x => x.Sec)))
            .OrderByDescending(r => r.Plays).ThenByDescending(r => r.Sec).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(n).ToList();

    public static List<Row> TopArtists(IEnumerable<PlayEntry> all, DateTime now, int days, int n) =>
        Within(all, now, days).Where(e => e.Artist.Length > 0).GroupBy(e => e.Artist.ToLowerInvariant())
            .Select(g => new Row(g.First().Artist, "", g.Count(), g.Sum(x => x.Sec)))
            .OrderByDescending(r => r.Plays).ThenByDescending(r => r.Sec).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(n).ToList();

    /// <summary>One file line: "epoch\tduration\tartist\ttitle" (tabs and newlines in names replaced with spaces).</summary>
    public static string ToLine(PlayEntry e) => $"{e.T}\t{e.Sec}\t{Clean(e.Artist)}\t{Clean(e.Title)}";

    public static PlayEntry FromLine(string line)
    {
        var p = (line ?? "").Split('\t');
        return p.Length == 4 && long.TryParse(p[0], out var t) && int.TryParse(p[1], out var s) && p[3].Length > 0 ? new PlayEntry(t, p[3], p[2], s) : null;
    }

    static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
