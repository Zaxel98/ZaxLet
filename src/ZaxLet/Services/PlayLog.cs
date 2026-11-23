using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZaxLet;

/// <summary>
/// Logs listened songs (title, artist, duration) to playlog.tsv in the data folder, on this PC only. Computed from play/pause transitions and track changes,
/// so no timer is needed: called from MediaService.Changed. Entries older than 90 days are pruned on first use.
/// </summary>
public static class PlayLog
{
    static string _title = "", _artist = "";
    static bool _playing;
    static DateTime _since;   // start of the currently playing segment
    static double _acc;       // accumulated seconds from completed segments
    static bool _trimmed;
    static string PathOf => Path.Combine(Settings.Dir, "playlog.tsv");

    public static void Update(string title, string artist, bool playing)
    {
        if (!Settings.Cur.PlayLogOn) return;
        title ??= ""; artist ??= "";
        var now = DateTime.Now;
        if (title != _title || artist != _artist)
        {
            Finish(now);
            _title = title; _artist = artist; _acc = 0; _playing = false;
        }
        if (playing && !_playing) { _since = now; _playing = true; }
        else if (!playing && _playing) { _acc += (now - _since).TotalSeconds; _playing = false; }
    }

    static void Finish(DateTime now)
    {
        if (_playing) _acc += (now - _since).TotalSeconds;
        _playing = false;
        if (_title.Length == 0 || _acc < PlayStats.MinSec) return;
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            Trim();
            File.AppendAllText(PathOf, PlayStats.ToLine(new PlayEntry(new DateTimeOffset(now.ToUniversalTime()).ToUnixTimeSeconds(), _title, _artist, (int)_acc)) + "\n");
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Flush the currently playing song to the log (called before opening recap and on app exit).</summary>
    public static void Flush()
    {
        if (!Settings.Cur.PlayLogOn) return;
        var t = _title; var a = _artist; var p = _playing;
        Finish(DateTime.Now);
        _title = t; _artist = a; _acc = 0; _playing = p; _since = DateTime.Now;
    }

    public static List<PlayEntry> Load()
    {
        try { return File.Exists(PathOf) ? File.ReadLines(PathOf).Select(PlayStats.FromLine).Where(e => e != null && !MediaFilter.IsAd(null, e.Title, e.Artist)).ToList() : new(); }   // ads from older logs are excluded from recap
        catch { return new(); }
    }

    public static void Clear() { try { File.Delete(PathOf); } catch { } }

    static void Trim()
    {
        if (_trimmed) return;
        _trimmed = true;
        try
        {
            if (!File.Exists(PathOf)) return;
            long from = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() - 90 * 86400L;
            var keep = File.ReadLines(PathOf).Select(PlayStats.FromLine).Where(e => e != null && e.T >= from && !MediaFilter.IsAd(null, e.Title, e.Artist)).Select(PlayStats.ToLine).ToList();
            File.WriteAllLines(PathOf, keep);
        }
        catch { }
    }
}
