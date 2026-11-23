using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace ZaxLet;


/// <summary>Now-playing via SMTC (all players shown in the Windows media card) + synced lyrics from LRCLIB.</summary>
public sealed class MediaService
{
    static readonly int[] SleepPresets = { 15, 30, 45, 60 };
    const int SleepFadeSec = 20;
    const int SleepFadeSteps = 40;   // 500 ms per step for ~20 seconds

    public enum SleepModeKind { Off, Timed, EndOfTrack, Fading }
    public SleepModeKind SleepMode { get; private set; } = SleepModeKind.Off;
    public int SleepPresetMinutes { get; private set; }
    DateTime _sleepEndUtc;
    System.Threading.Timer _sleepT;
    readonly object _sleepLk = new();
    float? _fadeOrigVol;
    float? _fadeLastSetVol;
    int _fadeStep;
    bool _pausingForSleep;

    public TimeSpan SleepRemaining
    {
        get
        {
            lock (_sleepLk)
            {
                if (SleepMode != SleepModeKind.Timed) return TimeSpan.Zero;
                var rem = _sleepEndUtc - DateTime.UtcNow;
                return rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
            }
        }
    }

    public static readonly MediaService I = new();

    public event Action Changed;
    public bool HasSession, Playing, IsAd;
    public string Title = "", Artist = "", Album = "";
    public BitmapSource Cover;
    public Color? CoverColor;
    public TimeSpan RawDuration;
    /// <summary>Duration of the currently playing song; on segmented compilation videos = segment length, not the whole video.</summary>
    public TimeSpan Duration => _chap != null && _chapIdx >= 0 ? _chapEnd == TimeSpan.MaxValue ? TimeSpan.Zero : _chapEnd - _chapAt : RawDuration;
    public List<LyricLine> Lyrics = new();
    public string LyricState = "";   // "", "loading", "none", "ok"

    GlobalSystemMediaTransportControlsSessionManager _mgr;
    GlobalSystemMediaTransportControlsSession _s;
    TimeSpan _pos;
    DateTimeOffset _posAt;
    readonly object _posLk = new();
    string _key = "", _rawTitle = "", _rawArtist = "", _baseKey = "", _vidTitle = "", _vidArtist = "";
    int _coverTries, _propTries;
    List<YtChapters.Chapter> _chap;          // compilation video segments (null = regular video)
    int _chapIdx = -1;
    TimeSpan _chapAt, _chapEnd;
    bool _chapOpen;                          // recognized song: length unknown
    DateTime _seekAt = DateTime.MinValue;
    static bool _packHinted;

    static readonly HttpClient Http = MakeHttp();
    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ZaxLet/1.0 (personal)");
        return h;
    }

    static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    // per-song lyric time offset (key "artist|title"); songs without their own offset use the default in Settings.
    // Title/Artist references are compared so dictionary lookup does not run on every karaoke frame.
    string _offT, _offA; int _offV;
    readonly object _offLk = new();
    string OffKey => $"{Artist}|{Title}".ToLowerInvariant();
    public int Offset
    {
        get
        {
            lock (_offLk)
            {
                if (!ReferenceEquals(_offT, Title) || !ReferenceEquals(_offA, Artist))
                { _offT = Title; _offA = Artist; _offV = Settings.Cur.LyricOffsets.TryGetValue(OffKey, out var v) ? v : Settings.Cur.LyricOffsetMs; }
                return _offV;
            }
        }
        set
        {
            value = Math.Clamp(value, -5000, 5000);
            lock (_offLk)
            {
                var d = Settings.Cur.LyricOffsets;
                if (value == Settings.Cur.LyricOffsetMs) d.Remove(OffKey);
                else { if (d.Count >= 1000) d.Remove(d.Keys.First()); d[OffKey] = value; }
                _offV = value; _offT = Title; _offA = Artist;
                Settings.Save();
            }
        }
    }

    // lyrics marked wrong by the user (content hash): no longer used, so the next source wins
    static readonly HashSet<string> Rejected = LoadRejected();
    static string RejectedFile => Path.Combine(Settings.Dir, "lyrics", "rejected.txt");
    static HashSet<string> LoadRejected()
    {
        try { return File.Exists(RejectedFile) ? new HashSet<string>(File.ReadAllLines(RejectedFile)) : new(); } catch { return new(); }
    }
    static string LrcHash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)));
    static bool IsRejected(string lrc) => lrc != null && Rejected.Contains(LrcHash(lrc));
    static string CachePath(string artist, string title) => Path.Combine(Settings.Dir, "lyrics",
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes($"{artist}|{title}".ToLowerInvariant()))) + ".lrc");

    /// <summary>"Wrong lyrics": drop the displayed lyrics from cache, remember not to use them again, then search for a version from another source.</summary>
    public void RejectLyrics()
    {
        if (!HasSession || Lyrics.Count == 0 || string.IsNullOrWhiteSpace(Title)) return;
        try
        {
            var cache = CachePath(Artist, Title);
            if (File.Exists(cache))
            {
                Rejected.Add(LrcHash(File.ReadAllText(cache)));
                Directory.CreateDirectory(Path.GetDirectoryName(RejectedFile));
                File.WriteAllLines(RejectedFile, Rejected);
                File.Delete(cache);
            }
        }
        catch (Exception ex) { App.Log(ex); }
        NoLyrics.Remove($"{Artist}|{Title}".ToLowerInvariant());
        Lyrics = new(); LyricState = "loading";
        Changed?.Invoke();
        _ = LoadLyrics(_key, Title, Artist, Album);
    }

    /// <summary>"Search again" when lyrics are missing or failed to load: forget temporary negative result and query sources again.</summary>
    public void RetryLyrics()
    {
        if (!HasSession || string.IsNullOrWhiteSpace(Title) || LyricState is "loading" or "retry" or "listen") return;
        NoLyrics.Remove($"{Artist}|{Title}".ToLowerInvariant());
        Lyrics = new(); LyricState = "loading";
        Changed?.Invoke();
        _ = LoadLyrics(_key, Title, Artist, Album);
    }

    /// <summary>Called when lyrics are turned back on mid-song: search lyrics for the currently playing track.</summary>
    public void EnsureLyrics()
    {
        if (!Settings.Cur.MusicLyrics || !HasSession || Lyrics.Count > 0 || LyricState is "loading" or "retry" or "listen" || string.IsNullOrWhiteSpace(Title)) return;
        LyricState = "loading";
        _ = LoadLyrics(_key, Title, Artist, Album);
    }

    public async Task StartAsync()
    {
        Settings.Applied += () => Ui(EnsureLyrics);
        Settings.Applied += () => { if (Settings.Cur.MediaIgnore != _ignoreSig) Ui(async () => { _ignoreSig = Settings.Cur.MediaIgnore; await RefreshAll(); }); };   // ignore list changed: re-pick session
        try
        {
            if (Application.Current != null) Application.Current.Exit += (_, _) => RestoreSleepVolume();
            _ignoreSig = Settings.Cur.MediaIgnore;
            _uiaT = new System.Threading.Timer(_ => PollYouTube(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);   // runs only while a session exists (SyncPoll)
            Settings.Applied += () => Ui(SyncPoll);
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Ui(async () => await RefreshAll());
            _mgr.SessionsChanged += (_, _) => Ui(async () => await RefreshAll());   // ignored session was active and another session started: re-pick
            await RefreshAll();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Media session used by the island: the active one, unless its app is in the ignore list (then another non-ignored session, if any).</summary>
    GlobalSystemMediaTransportControlsSession Pick()
    {
        var cur = _mgr?.GetCurrentSession();
        var list = Settings.Cur.MediaIgnore;
        if (cur == null || !MediaFilter.Ignored(cur.SourceAppUserModelId, list)) return cur;
        foreach (var x in _mgr.GetSessions()) if (!MediaFilter.Ignored(x.SourceAppUserModelId, list)) return x;
        return null;
    }

    string _ignoreSig = "";

    async Task RefreshAll()
    {
        try
        {
            var s = Pick();
            if (!ReferenceEquals(s, _s))
            {
                if (_s != null) Detach();
                _s = s;
                if (_s != null) Attach();
            }
            SyncPoll();
            if (_s == null)
            {
                HasSession = Playing = false;
                Title = Artist = Album = "";
                Cover = null; CoverColor = null; Lyrics = new(); LyricState = ""; _key = "";
                CancelSleepTimer();
                Changed?.Invoke();
                return;
            }
            HasSession = true;
            await RefreshProps();
            RefreshPlayback();
            RefreshTimeline();
            Changed?.Invoke();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void Attach()
    {
        _s.MediaPropertiesChanged += OnProps;
        _s.PlaybackInfoChanged += OnPlayback;
        _s.TimelinePropertiesChanged += OnTimeline;
    }

    void Detach()
    {
        _s.MediaPropertiesChanged -= OnProps;
        _s.PlaybackInfoChanged -= OnPlayback;
        _s.TimelinePropertiesChanged -= OnTimeline;
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, object a) => Ui(async () => { await RefreshProps(); Changed?.Invoke(); });
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, object a) => Ui(() => { RefreshPlayback(); RefreshTimeline(); Changed?.Invoke(); });
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, object a) => Ui(RefreshTimeline);

    async Task RefreshProps()
    {
        var s = _s;   // _s may change during the await below
        if (s == null) return;
        GlobalSystemMediaTransportControlsSessionMediaProperties p;
        try { p = await s.TryGetMediaPropertiesAsync(); _propTries = 0; }
        catch (Exception ex)
        {
            // new session is sometimes not ready yet (0x80070015): retry shortly
            if (_propTries++ >= 5) { App.Log(ex); return; }
            _ = Task.Delay(1000).ContinueWith(_ => Ui(async () => { await RefreshProps(); Changed?.Invoke(); }));
            return;
        }
        var key = $"{p.Artist}|{p.Title}";
        bool newTrack = key != _baseKey;
        if (newTrack) { _vidTitle = p.Title ?? ""; _vidArtist = p.Artist ?? ""; _chap = null; _chapIdx = -1; _chapOpen = false; lock (_posLk) { _goodTimeline = false; _uiaValAt = DateTime.MinValue; } }
        if (_chapIdx < 0)   // on segmented videos, title and artist follow the currently playing segment
        {
            _rawTitle = _vidTitle; _rawArtist = _vidArtist;
            var shown = TrackId.Parse(_rawTitle, _rawArtist).Show;   // YouTube title "Artist - Title (Audio)" + channel "artistVEVO" -> clean pair
            Title = shown.Title; Artist = shown.Artist; Album = p.AlbumTitle ?? "";
            IsAd = MediaFilter.IsAd(s.SourceAppUserModelId, p.Title, p.Artist) || MediaFilter.ShortSpotifyAd(s.SourceAppUserModelId, RawDuration);
            if (IsAd) { Title = ZaxLet.L.T("Iklan"); Album = ""; }   // Spotify template text in title slot replaced with ad label
        }
        if (newTrack)
        {
            bool hadBase = _baseKey.Length > 0;
            _baseKey = _key = key;
            Translit.Begin(Array.Empty<string>());
            _coverTries = 0;
            Lyrics = new(); LyricState = "loading";
            Cover = null; CoverColor = null;
            if (hadBase) OnTrackChangedForSleep();
            _ = BeginTrack(key);
        }
        // some players send cover art late: retry a few times while the song is the same
        if (p.Thumbnail == null && Cover == null && _coverTries++ < 3)
            _ = Task.Delay(1500).ContinueWith(_ => Ui(async () => { if (key == _baseKey) { await RefreshProps(); Changed?.Invoke(); } }));
        if (p.Thumbnail != null && Cover == null)
        {
            try
            {
                using var st = await p.Thumbnail.OpenReadAsync();
                var ms = new MemoryStream();
                await st.AsStreamForRead().CopyToAsync(ms);
                ms.Position = 0;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 256;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                Cover = bi;
                CoverColor = Dominant(bi);
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    void RefreshPlayback()
    {
        var s = _s;
        if (s == null) return;   // event arrived after session disappeared
        var i = s.GetPlaybackInfo();
        if (i == null) return;
        bool wasPlaying = Playing;
        Playing = i.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        Shuffle = i.Controls.IsShuffleEnabled ? i.IsShuffleActive : null;   // null: this player does not support shuffle via Windows
        if (wasPlaying && !Playing && !_pausingForSleep && SleepMode is SleepModeKind.Timed or SleepModeKind.Fading)
            CancelSleepTimer();
    }

    public bool? Shuffle;
    public void ToggleShuffle() { var s = _s; if (s != null && Shuffle is { } on) _ = s.TryChangeShuffleActiveAsync(!on); }

    /// <summary>Re-read duration/position from Windows. Browsers (YouTube) often delay or omit timeline events, so this is called periodically.</summary>
    public void PollTimeline()
    {
        if ((DateTime.Now - _seekAt).TotalSeconds < 1.5) return;   // give the browser time to apply the seek before reading overwrites the new position
        try { RefreshTimeline(); } catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Player allows seeking (YouTube in browsers allows it when the page sends duration).</summary>
    public bool CanSeek => _s != null && Duration > TimeSpan.Zero;

    /// <summary>
    /// Recognize the currently audible song (like Shazam) when its title is missing or wrong: capture ~12 s of PC audio output, send to AudD
    /// with the user's token, then set the result as the currently playing track (title, lyrics, and lyric position sync).
    /// </summary>
    public async Task Identify()
    {
        if (!HasSession || LyricState is "listen" or "ident") return;
        var token = Settings.Cur.SongIdToken;
        string baseKey = _baseKey;
        void State(string s) { LyricState = s; Lyrics = new(); Ui(() => Changed?.Invoke()); }
        if (string.IsNullOrWhiteSpace(token)) { State("notoken"); return; }
        State("listen");
        try
        {
            var (hit, endPos) = await SongId.RecognizeAsync(token, RawPosition);
            if (baseKey != _baseKey) return;   // track changed while listening
            if (hit == null) { State("norec"); return; }
            // start position of that song in the stream: snippet end minus (offset in song + snippet length)
            var start = endPos - TimeSpan.FromSeconds(hit.At + SongId.Seconds);
            _chapOpen = true;
            _chap = new List<YtChapters.Chapter> { new(start < TimeSpan.Zero ? TimeSpan.Zero : start, hit.Artist + " · " + hit.Title) };
            _chapIdx = -1;
            SyncChapter(RawPosition());
        }
        catch (Exception ex) { App.Log(ex); State("norec"); }
    }

    void RefreshTimeline()
    {
        var s = _s;
        if (s == null) return;
        lock (_posLk) if ((DateTime.Now - _uiaAt).TotalSeconds < 6) return;   // position from YouTube page is fresh: more reliable than browser report
        var t = s.GetTimelineProperties();
        if (t == null) return;   // session closing: WinRT can return null
        var len = t.EndTime - t.StartTime;
        lock (_posLk)
        {
            // browser sometimes reports an empty timeline (position 0, duration 0) mid-song, e.g., when the page clears media state:
            // ignore it, continue from the last valid estimate rather than jumping to the start
            if (len <= TimeSpan.Zero && _goodTimeline) return;
            if (len > TimeSpan.Zero) _goodTimeline = true;
            RawDuration = len;
            _pos = t.Position - t.StartTime;
            _posAt = t.LastUpdatedTime;
        }
        if (!IsAd && MediaFilter.ShortSpotifyAd(s.SourceAppUserModelId, len))   // ad with unrecognized text: detected by its duration
        {
            IsAd = true; Title = ZaxLet.L.T("Iklan"); Album = ""; Lyrics = new(); LyricState = "ad";
            Changed?.Invoke();
        }
    }

    bool _goodTimeline;

    // ---------- actual position from YouTube page ----------
    DateTime _uiaAt = DateTime.MinValue;
    double _uiaVal; DateTime _uiaValAt = DateTime.MinValue;   // previous bar reading, to detect stuck values
    int _uiaBusy;
    System.Threading.Timer _uiaT;

    bool _ytOn;
    void SyncPoll()
    {
        bool on = _s != null && Settings.Cur.YtProgress;
        if (on == _ytOn || _uiaT == null) return;
        _ytOn = on;
        _uiaT.Change(on ? 2000 : System.Threading.Timeout.Infinite, on ? 2000 : System.Threading.Timeout.Infinite);
    }

    /// <summary>Every 2 s read the YouTube progress bar (if its video tab is active) and use that as the true position; also fills in duration omitted by the browser.</summary>
    void PollYouTube()
    {
        if (!Settings.Cur.YtProgress || !HasSession || string.IsNullOrEmpty(_vidTitle) || Interlocked.Exchange(ref _uiaBusy, 1) == 1) return;
        try
        {
            double dur;
            lock (_posLk) dur = RawDuration.TotalSeconds;
            var r = YtProgress.Read(_vidTitle, dur);
            if (r == null) return;
            lock (_posLk)
            {
                if ((DateTime.Now - _seekAt).TotalSeconds < 2) return;   // after our seek, wait for the page to apply it
                // closed/hidden tabs: YouTube stops updating its bar, so the value freezes. Readings that did not advance since the last read
                // are ignored: position keeps being estimated from the last valid reading. Windows (SMTC) reports from Firefox are also stale (measured: not updated
                // for 351 s while video plays), so it must not take over (_uiaAt stays fresh); otherwise position rewinds to a stale value.
                var now = DateTime.Now;
                bool live = !Playing || _uiaValAt == DateTime.MinValue || Math.Abs((r.Pos - _uiaVal) - (now - _uiaValAt).TotalSeconds) <= 1.2;   // bar holds whole seconds: valid diff = interval ±1; stuck = 0
                _uiaVal = r.Pos; _uiaValAt = now;
                if (!live) { _uiaAt = now; return; }
                _pos = TimeSpan.FromSeconds(r.Pos + 0.5);   // bar holds floored seconds
                _posAt = DateTimeOffset.Now; _uiaAt = DateTime.Now; _goodTimeline = true;
                if (RawDuration <= TimeSpan.Zero) RawDuration = TimeSpan.FromSeconds(r.Max);
            }
        }
        catch (Exception ex) { App.Log(ex); }
        finally { Volatile.Write(ref _uiaBusy, 0); }
    }

    TimeSpan RawPosition()
    {
        TimeSpan p, dur; DateTimeOffset at;
        lock (_posLk) { p = _pos; at = _posAt; dur = RawDuration; }
        if (Playing) p += DateTimeOffset.Now - at;
        if (dur > TimeSpan.Zero && p > dur) p = dur;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }

    /// <summary>Position in the currently playing song (on segmented videos: since the start of the segment).</summary>
    public TimeSpan Position()
    {
        var p = RawPosition();
        if (_chap == null) return p;
        SyncChapter(p);
        return p > _chapAt ? p - _chapAt : TimeSpan.Zero;
    }

    // ---------- compilation video segments ----------
    /// <summary>
    /// Start of a new track: long videos (≥ 8 min) are checked for YouTube chapters; if found, each segment is treated as its own song
    /// (title, lyrics, progress bar, prev/next skip). Otherwise, lyrics are searched for the video title as usual.
    /// </summary>
    async Task BeginTrack(string key)
    {
        await Task.Delay(700);   // give time for duration to populate
        // browsers sometimes omit duration altogether: give direct YouTube page reading time to fill it in
        for (int i = 0; i < 10 && key == _key && Settings.Cur.YtChapters && RawDuration <= TimeSpan.Zero; i++) await Task.Delay(300);
        if (key != _key) return;
        if (Settings.Cur.YtChapters && RawDuration >= TimeSpan.FromMinutes(8))
        {
            try
            {
                var ch = await YtChapters.FetchAsync(_vidTitle, _vidArtist, RawDuration.TotalSeconds);
                if (key == _key && ch != null) { _chap = ch; SyncChapter(RawPosition()); return; }
            }
            catch (Exception ex) { App.Log(ex); }
        }
        if (key != _key) return;
        if (IsAd) { LyricState = "ad"; return; }   // ads have no lyrics: do not download
        if (Settings.Cur.MusicLyrics) await LoadLyrics(key, Title, Artist, Album); else LyricState = "off";   // lyrics disabled: do not download
    }

    void SyncChapter(TimeSpan p)
    {
        var chap = _chap;
        if (chap == null || _chapIdx >= 0 && p >= _chapAt && p < _chapEnd) return;
        int i = Math.Max(0, chap.FindLastIndex(c => c.At <= p));
        if (i == _chapIdx) return;
        int prevIdx = _chapIdx;
        _chapIdx = i; _chapAt = chap[i].At;
        _chapEnd = i + 1 < chap.Count ? chap[i + 1].At : _chapOpen ? TimeSpan.MaxValue : RawDuration > _chapAt ? RawDuration : _chapAt + TimeSpan.FromMinutes(5);
        _rawTitle = chap[i].Text; _rawArtist = "";
        var shown = TrackId.Parse(_rawTitle, "").Show;   // "Artist · Title"
        Title = shown.Title; Artist = shown.Artist; Album = "";
        Lyrics = new(); Translit.Begin(Array.Empty<string>());
        _key = _baseKey + "#" + i;
        if (prevIdx >= 0) OnTrackChangedForSleep();
        if (Settings.Cur.MusicLyrics) { LyricState = "loading"; _ = LoadLyrics(_key, Title, Artist, ""); } else LyricState = "off";
        Ui(() => Changed?.Invoke());
    }

    public string CurrentLine()
    {
        int i = CurrentIndex();
        return i < 0 ? "" : Lyrics[i].Text;
    }

    /// <summary>Estimated duration of the currently displayed line (seconds), for setting marquee speed.</summary>
    public double CurrentLineSpan()
    {
        int i = CurrentIndex();
        return i < 0 ? 0 : Est(i).Span;
    }

    public int CurrentIndex()
    {
        if (Lyrics.Count == 0) return -1;
        var p = Position() + TimeSpan.FromMilliseconds(200 + Offset);
        int lo = 0, hi = Lyrics.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int m = (lo + hi) / 2;
            if (Lyrics[m].At <= p) { ans = m; lo = m + 1; } else hi = m - 1;
        }
        return ans;
    }

    /// <summary>
    /// Karaoke position: the line currently being sung and how far through (0..1). LRCLIB only has per-line timestamps, so singing
    /// duration is estimated from text length (pace is configurable) and does not pass the start of the next line.
    /// </summary>
    public (int Index, double Progress) Karaoke()
    {
        int i = CurrentIndex();
        if (i < 0) return (-1, 0);
        var l = Lyrics[i];
        var (sung, vexp, contig) = Est(i);
        var t = Position() + TimeSpan.FromMilliseconds(Offset);
        double elapsed = (t - l.At).TotalSeconds, timeP = Math.Clamp(elapsed / sung, 0, 1);
        DbgK = (i, l.Text, sung, timeP, 0, 0, false);

        // Vocal mode: progress = actual voiced duration since line start / expected singing duration. Instrumental breaks hold the fill,
        // long intros don't pre-fill, and lines that finish early (vocals stop) fill immediately. Without audio data: time-based.
        var af = AudioFlow.I;
        if (!Settings.Cur.LyricFlow || !af.Fresh || elapsed <= 0 || af.CapturingFor < elapsed + 0.1) { _kIdx = -1; return (i, timeP); }

        // Reliability: if over the last 12 s it was almost always "voiced" (loud instruments/track masking vocals) or almost never,
        // vocal detection cannot be trusted for this song and progress falls back to time-based.
        double win = Math.Min(12, af.CapturingFor);
        double vr = win > 4 ? af.VoicedSince(win) / win : 0.5;
        if (vr > 0.88 || vr < 0.12) { _kIdx = -1; return (i, timeP); }

        double voiced = af.VoicedSince(elapsed);
        DbgK = (i, l.Text, sung, timeP, voiced, vr, true);
        var now = DateTime.UtcNow;
        if (i != _kIdx)   // line changed: learn from the previous line how much of its gap was actually voiced
        {
            if (_kIdx >= 0 && _kBase > 0 && _kVoiced > 0.5)
            {
                double frac = _kVoiced / _kBase;
                if (_kContig) _gapFrac = Math.Clamp(0.8 * _gapFrac + 0.2 * frac, 0.4, 0.95);
                else _ratio = Math.Clamp(0.8 * _ratio + 0.2 * frac, 0.5, 1.3);
            }
            _kIdx = i; _kP = 0;
        }
        _kBase = contig ? vexp / _gapFrac : sung; _kContig = contig; _kVoiced = voiced;
        double expect = Math.Max(0.6, vexp);
        double p = Math.Min(voiced / expect, 0.97);

        // Tolerance bounds: audio result may only deviate ±FlowTolSec from time-based, so sections louder than vocals
        // (or missed vocals) cannot pull the fill too far ahead or hold it back too long.
        // Also bounded as a fraction of the line (±18% for contiguous lines with reliable timing, ±35% otherwise): seconds-only tolerance
        // is too loose for short lines.
        // Asymmetric: audio tends to lead (loud instruments read as vocals), so the upper bound is tighter than the lower bound.
        double tolS = Math.Clamp(Settings.Cur.FlowTolSec, 0.3, 8) / sung;
        p = Math.Clamp(p, timeP - Math.Min(tolS, contig ? 0.20 : 0.35), timeP + Math.Min(tolS, contig ? 0.12 : 0.30));
        bool done = (voiced >= expect * 0.55 && af.SilentFor > 0.45 && elapsed >= sung * 0.6)   // vocals stopped: line finished
                    || (contig && elapsed >= sung);                                              // contiguous line: full when its time budget ends
        if (done) p = 1;
        else
        {
            // never move backward within a line, and advance at most 2.5x normal speed (no jumps)
            double dt = Math.Min(1, (now - _kAt).TotalSeconds);
            p = Math.Max(p, _kP);
            if (_kP > 0) p = Math.Min(p, _kP + dt * 2.5 / sung);
        }
        _kP = p; _kAt = now;
        return (i, Math.Clamp(p, 0, 1));
    }

    double _gapFrac = 0.75;   // fraction of the gap to the next line that is actually voiced (learned from audio)
    double _kBase; bool _kContig;

    /// <summary>
    /// Estimated line duration: (display span, expected voiced duration, contiguous?). A line followed by another within 7 s is assumed
    /// to fill its gap (slow/long songs no longer fill too early); lines before a long pause use text length.
    /// </summary>
    (double Span, double VoicedExpect, bool Contiguous) Est(int i, List<LyricLine> L = null)
    {
        L ??= Lyrics;
        var l = L[i];
        bool hasNext = i + 1 < L.Count;
        double gap = hasNext ? (L[i + 1].At - l.At).TotalSeconds : 5;
        if (hasNext && gap <= 7)
        {
            double basis = Math.Max(0.8, gap - 0.25);
            return (Math.Max(0.8, basis * 1.0), Math.Max(0.6, basis * _gapFrac), true);   // 1.0: middle ground; 0.97 -> fills early (t90 0.84), 1.06 -> 31% of lines never finish filling (measured on 7 songs)
        }
        double perChar = Settings.Cur.LyricPace switch { "fast" => 0.11, "slow" => 0.18, _ => 0.14 };
        double span = Math.Max(0.8, Math.Min(gap - 0.2, Math.Max(1.5, l.Text.Length * perChar)));
        return (span, Math.Max(0.6, span * _ratio), false);
    }

    /// <summary>Last intermediate values of Karaoke() for diagnostics (--karaoke-trace flag).</summary>
    public (int Idx, string Text, double Sung, double TimeP, double Voiced, double VoicedRatio, bool Flow) DbgK;

    string _lblKey = "";

    /// <summary>
    /// Label for learning vocal patterns (called from audio thread): 1 = definitely vocals, 0 = definitely instrumental, -1 = uncertain (line boundary).
    /// Vocals: middle portion of a sung line. Instrumental: intro before first line, empty/♪ lines, and long gaps after a line ends.
    /// </summary>
    public int VocalLabel()
    {
        var L = Lyrics;
        var key = _key;
        if (L.Count == 0 || !Playing) return -1;
        if (_lblKey != key) { _lblKey = key; AudioFlow.I.ResetModel(); }   // new song: start learning from scratch
        var p = RawPosition();
        if (_chap != null)
        {
            var chapAt = _chapAt;
            // chapter switch must happen on UI thread; return -1 first so the model is not trained on old chapter lyrics
            if (_chapIdx < 0 || p < chapAt || p >= _chapEnd) { Ui(() => SyncChapter(RawPosition())); return -1; }
            p = p > chapAt ? p - chapAt : TimeSpan.Zero;
        }
        var t = p + TimeSpan.FromMilliseconds(Offset);
        if (t < L[0].At - TimeSpan.FromSeconds(0.6)) return t > TimeSpan.FromSeconds(1) ? 0 : -1;
        int lo = 0, hi = L.Count - 1, i = -1;
        while (lo <= hi) { int m = (lo + hi) / 2; if (L[m].At <= t) { i = m; lo = m + 1; } else hi = m - 1; }
        if (i < 0) return -1;
        var l = L[i];
        var next = i + 1 < L.Count ? L[i + 1].At : l.At + TimeSpan.FromSeconds(6);
        double since = (t - l.At).TotalSeconds, gap = (next - l.At).TotalSeconds, left = (next - t).TotalSeconds;
        if (string.IsNullOrWhiteSpace(l.Text) || l.Text.Trim() == "♪") return since > 0.6 && left > 0.5 ? 0 : -1;
        double est = Est(i, L).Span;
        if (since >= 0.5 && since <= est * 0.65) return 1;
        if (gap >= 5 && since >= est + 1.2 && left >= 0.6) return 0;
        return -1;
    }

    int _kIdx = -1; double _kVoiced, _ratio = 1, _kP; DateTime _kAt;   // ratio learning and vocal karaoke progress state

    // ---------- controls ----------
    public void PlayPause() => _ = _s?.TryTogglePlayPauseAsync();

    /// <summary>Pause playback if currently playing; returns true if the pause command was sent successfully.</summary>
    public async Task<bool> PauseIfPlayingAsync()
    {
        try { return Playing && _s != null && await _s.TryPauseAsync(); }
        catch (Exception ex) { App.Log(ex); return false; }
    }

    /// <summary>Resume playback if currently paused.</summary>
    public async Task<bool> ResumeAsync()
    {
        try { return !Playing && _s != null && await _s.TryPlayAsync(); }
        catch (Exception ex) { App.Log(ex); return false; }
    }
    // on segmented videos, next/previous skips between segments (songs), not between videos
    public void Next()
    {
        if (_chap != null && _chapIdx >= 0 && _chapIdx + 1 < _chap.Count) SeekAbs(_chap[_chapIdx + 1].At);
        else _ = _s?.TrySkipNextAsync();
    }

    public void Prev()
    {
        if (_chap != null && _chapIdx >= 0)
        {
            if (_chapIdx == 0 || RawPosition() - _chapAt > TimeSpan.FromSeconds(3)) SeekAbs(_chapAt);   // like a regular player: restart song first
            else SeekAbs(_chap[_chapIdx - 1].At);
        }
        else _ = _s?.TrySkipPreviousAsync();
    }

    /// <summary>Seek to a position in the currently playing song (on segmented videos: relative to segment start).</summary>
    public void Seek(TimeSpan to)
    {
        if (Duration > TimeSpan.Zero) to = to > Duration ? Duration : to < TimeSpan.Zero ? TimeSpan.Zero : to;
        SeekAbs(_chap != null && _chapIdx >= 0 ? _chapAt + to : to);
    }

    void SeekAbs(TimeSpan to)
    {
        var s = _s;
        if (s == null) return;
        lock (_posLk) { _pos = to; _posAt = DateTimeOffset.Now; _seekAt = DateTime.Now; _uiaValAt = DateTime.MinValue; }
        _ = s.TryChangePlaybackPositionAsync(to.Ticks);
    }

    // ---------- lyrics ----------
    /// <summary>
    /// Search synced lyrics on LRCLIB. /api/get (exact) sometimes 503s, so there is always a /api/search fallback (title+artist, then
    /// cleaned title + free-text search). Server errors are retried a few times before giving up.
    /// </summary>
    async Task LoadLyrics(string key, string title, string artist, string album)
    {
        await Task.Delay(600); // give time for duration to populate
        if (string.IsNullOrWhiteSpace(title)) return;

        // disk cache: previously searched songs appear immediately without depending on LRCLIB
        var cache = CachePath(artist, title);
        try
        {
            // unreasonable old cache (fake timestamps from an earlier version) is ignored and re-fetched
            if (File.Exists(cache) && LrcSane(File.ReadAllText(cache), Duration > TimeSpan.Zero ? (int)Duration.TotalSeconds : 0) && Lrc.Parse(File.ReadAllText(cache)) is { Count: > 0 } cached && key == _key)
            {
                Lyrics = StripHeader(cached, title); LyricState = "ok";
                PrefetchRomaji();
                Ui(() => Changed?.Invoke());
                return;
            }
        }
        catch { /* corrupt cache: re-fetch */ }

        // songs recently confirmed missing across all sources: don't query servers again for a while (NetEase also rejects repeated queries)
        var negKey = $"{artist}|{title}".ToLowerInvariant();
        if (NoLyrics.TryGetValue(negKey, out var until) && DateTime.UtcNow < until)
        {
            if (key == _key) { Lyrics = new(); LyricState = "none"; Ui(() => Changed?.Invoke()); }
            return;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (key != _key) return;
            bool error = false, answered = false;
            string synced = null;
            // title/artist candidates from title parsing (YouTube often uses "Artist - Title"); the first one to return lyrics is used
            var cands = TrackId.Parse(_rawTitle, _rawArtist).Search;
            for (int ci = 0; ci < cands.Count && synced == null && key == _key; ci++)
            {
                bool bad = false;
                try { synced = await Fetch(cands[ci].Title, cands[ci].Artist, ci == 0 ? album : null, () => bad = true); }
                catch (Exception ex) { bad = true; App.Log(ex); }
                if (bad) error = true; else answered = true;
            }
            error = error && !answered;   // one candidate answered by a source (even empty) means the song wasn't found, not that the server is busy
            if (key != _key) return;
            if (synced != null)
            {
                Lyrics = StripHeader(Lrc.Parse(synced), title);
                LyricState = Lyrics.Count > 0 ? "ok" : "none";
                PrefetchRomaji();
                if (Lyrics.Count > 0)
                    try { Directory.CreateDirectory(Path.GetDirectoryName(cache)); File.WriteAllText(cache, synced); } catch { }
                Ui(() => Changed?.Invoke());
                return;
            }
            if (!error) { NoLyrics[negKey] = DateTime.UtcNow.AddMinutes(20); break; }   // a source answered: there really are no synced lyrics
            LyricState = "retry";
            Ui(() => Changed?.Invoke());
            await Task.Delay(TimeSpan.FromSeconds(4 * (attempt + 1)));
        }
        if (key == _key) { Lyrics = new(); LyricState = "none"; Ui(() => Changed?.Invoke()); }
    }

    static readonly Dictionary<string, DateTime> NoLyrics = new();

    public void RefreshRomaji() => PrefetchRomaji();

    void PrefetchRomaji()
    {
        var lines = Lyrics.Select(l => l.Text).ToList();
        Translit.Begin(lines, Title, Artist);   // Japanese or Mandarin is determined per song: both use Han characters
        if ((Settings.Cur.LyricRomaji || Settings.Cur.IdleRomaji) && Translit.Lang == "ja") Romaji.Prefetch(lines);
        // lyrics requiring a download pack (Japanese kanji, or Mandarin): notify once per session, don't silently omit romanization
        if (Settings.Cur.LyricRomaji && !_packHinted && lines.Count > 0 && (Translit.Lang == "zh" && !Translit.PinyinReady || Translit.Lang == "ja" && !RomajiPack.KanjiInstalled && lines.Any(Translit.HasHan)))
        {
            _packHinted = true;
            IslandWindow.Instance?.Alert(L.T("Tulisan Latin untuk lirik ini butuh Paket tulisan Latin: Pengaturan > Addon"), 8, null, false);
        }
    }

    /// <summary>
    /// Three sources with staged hedging: LRCLIB starts immediately; NetEase starts only if LRCLIB has not answered within 1 s or returned
    /// empty; Kugou if neither produced a result within 2 s. Songs present on LRCLIB (common case) trigger no extra requests,
    /// while missing songs move straight to the next source without waiting for a slow LRCLIB search to time out. Results are still
    /// picked by priority (LRCLIB > NetEase > Kugou).
    /// </summary>
    async Task<string> Fetch(string title, string artist, string album, Action markError, int? durOverride = null)
    {
        var dur = durOverride ?? (Duration > TimeSpan.Zero ? (int)Duration.TotalSeconds : 0);
        var clean = CleanTitle(title);
        var art = CleanArtist(artist);

        // "Busy" only if ALL attempted sources failed to communicate; one source answering (even empty) means the song is missing.
        int attempted = 0, failed = 0;
        async Task<string> First()
        {
            Interlocked.Increment(ref attempted);
            try { var r = await FetchLrclib(title, artist, album, dur); return r != null && LrcSane(r, dur) && !IsRejected(r) ? r : null; }
            catch (Exception) { Interlocked.Increment(ref failed); return null; }   // LRCLIB busy / skipped by circuit breaker: don't delay other sources
        }
        var main = MainTitle(title);
        var t1 = First();
        async Task<string> Backup(Func<string, string, int, Task<string>> src, Func<bool> hit, int delayMs, params Task[] waitFor)
        {
            await Task.WhenAny(Task.WhenAll(waitFor), Task.Delay(delayMs));
            if (hit()) return null;                                   // higher-priority source already succeeded: no need to query
            Interlocked.Increment(ref attempted);
            bool answered = false;
            // query variants: full title + clean artist, main title (without "-…-"/"~…~" subtitle) + artist, main title only; duration still filters
            foreach (var (tt, a) in new[] { (clean, art), (main, art), (main, "") }.Distinct())
            {
                try { if (await src(tt, a, dur) is { } lrc && LrcSane(lrc, dur) && !IsRejected(lrc)) return lrc; answered = true; }
                catch (Exception ex) { App.Log(ex); }
            }
            if (!answered) Interlocked.Increment(ref failed);
            return null;
        }
        bool Got(params Task<string>[] ts) => ts.Any(t => t.IsCompletedSuccessfully && t.Result != null);
        // Sequential backup source list (max 10): each new source starts 1 s after the previous one if no result yet.
        var chain = new List<Task<string>> { t1 };
        foreach (var (src, k) in new Func<string, string, int, Task<string>>[] { FetchLyricsPlus, FetchNetease, FetchQQ, FetchKugou }.Select((s, k) => (s, k)))
        {
            var prev = chain.ToArray();
            chain.Add(Backup(src, () => Got(prev), 1000 * (k + 1), prev));
        }
        // Pick by the priority order above. If a lower-priority source already succeeded while a higher-priority one is still searching,
        // give the higher-priority one an 0.8 s grace period then use the available result (LRCLIB sometimes takes seconds to conclude "not found").
        var all = chain.ToArray();
        static string Res(Task<string> t) => t.IsCompletedSuccessfully ? t.Result : null;
        var graceEnd = DateTime.MaxValue;
        while (true)
        {
            bool pending = false; string best = null;
            foreach (var t in all)
            {
                if (!t.IsCompleted) { pending = true; break; }   // higher-priority source not finished yet
                if ((best = Res(t)) != null) break;
            }
            if (best != null) return best;
            if (!pending)
            {
                if (attempted > 0 && failed >= attempted) { markError(); return null; }
                // Final pass: no match yet -> retry Asian sources with ±15 s duration tolerance (other version/different Spotify duration),
                // but lyrics must cover 60%–105% of the song to avoid picking the wrong version.
                if (dur > 0)
                {
                    try
                    {
                        foreach (var src in new Func<string, string, int, Task<string>>[] { FetchNetease, FetchQQ, FetchKugou })
                            foreach (var (tt, a) in new[] { (clean, art), (main, art), (main, "") }.Distinct())
                                try
                                {
                                {
                                    var got = await src(tt, a, 0);   // without source duration filter; match is judged by lyric coverage against actual duration below
                                    if (got != null && LrcSane(got, dur) && !IsRejected(got) && Lrc.Parse(got) is { Count: > 0 } pl
                                        && pl[^1].At.TotalSeconds >= dur * 0.6 && pl[^1].At.TotalSeconds <= dur + 8) return got;
                                }
                                }
                                catch (Exception ex) { App.Log(ex); }
                    }
                    finally { }
                }
                return null;
            }
            var low = all.FirstOrDefault(t => Res(t) != null);
            if (low != null)
            {
                if (graceEnd == DateTime.MaxValue) graceEnd = DateTime.Now.AddMilliseconds(800);
                if (DateTime.Now >= graceEnd) return low.Result;
            }
            await Task.WhenAny(all.Where(t => !t.IsCompleted).Append(Task.Delay(100)));
        }
    }

    // LRCLIB circuit breaker: server often returns 503/429. After a failure, LRCLIB is skipped temporarily (15, 30, 60, 120, 180 s) so backup sources
    // are used immediately and the busy server is not hammered; once it succeeds, failure count resets to zero.
    static DateTime _lrDownUntil; static int _lrFails;

    static void LrTrip()
    {
        _lrFails = Math.Min(_lrFails + 1, 5);
        _lrDownUntil = DateTime.UtcNow.AddSeconds(Math.Min(180, 15 * (1 << (_lrFails - 1))));
    }

    /// <summary>Single LRCLIB request: null = 404 (not found); throws if server is busy/failing (5xx, 429, 6 s timeout) and trips the circuit breaker.</summary>
    static async Task<JsonDocument> LrAsk(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(6000);
            using var r = await Http.GetAsync(url, cts.Token);
            int sc = (int)r.StatusCode;
            if (sc >= 500 || sc == 429) throw new HttpRequestException("LRCLIB " + sc);
            _lrFails = 0; _lrDownUntil = DateTime.MinValue;
            return r.IsSuccessStatusCode ? JsonDocument.Parse(await r.Content.ReadAsStringAsync()) : null;
        }
        catch { LrTrip(); throw; }
    }

    async Task<string> FetchLrclib(string title, string artist, string album, int dur)
    {
        if (DateTime.UtcNow < _lrDownUntil) throw new InvalidOperationException("LRCLIB dilewati sementara (gangguan sebelumnya)");
        string E(string s) => Uri.EscapeDataString(s ?? "");

        // 1) exact match
        var get = $"https://lrclib.net/api/get?track_name={E(title)}&artist_name={E(artist)}";
        if (!string.IsNullOrWhiteSpace(album)) get += $"&album_name={E(album)}";
        if (dur > 0) get += $"&duration={dur}";
        using (var exact = await LrAsk(get))
            if (exact != null && ReadSynced(exact.RootElement) is { } ex0) return ex0;

        // 2) search: title+artist, then clean title, then free-text query
        var clean = CleanTitle(title);
        var urls = new List<string>
        {
            $"https://lrclib.net/api/search?track_name={E(title)}&artist_name={E(artist)}",
            $"https://lrclib.net/api/search?track_name={E(clean)}&artist_name={E(CleanArtist(artist))}",
            $"https://lrclib.net/api/search?q={E((CleanArtist(artist) + " " + clean).Trim())}",
            $"https://lrclib.net/api/search?q={E(clean)}",
        };
        foreach (var u in urls.Distinct())
        {
            using var doc = await LrAsk(u);
            if (doc == null) continue;
            // pick the one with closest duration; without duration, take the first synced result
            string best = null; double bestDiff = double.MaxValue;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var s = ReadSynced(el);
                if (s == null) continue;
                double diff = dur > 0 && el.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? Math.Abs(d.GetDouble() - dur) : 0;
                if (diff < bestDiff) { best = s; bestDiff = diff; }
            }
            if (best != null && (dur <= 0 || bestDiff <= 10)) return best;   // another version (far duration) = out of sync: continue to next query/source
        }

        return null;
    }

    // "Artist (CV. Voice Actor)", "A, B", "A feat. B", "A & B" -> first artist without notes
    static string CleanArtist(string a)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(a ?? "", @"\s*[\(（\[].*?[\)）\]]", "");
        s = System.Text.RegularExpressions.Regex.Split(s, @"\s*(?:,|、|&|/|;|×)\s*|\s+(?:feat\.?|ft\.?|starring|with|vs\.?)\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)[0];   // "Sheryl Nome starring May'n" -> "Sheryl Nome"
        return s.Trim();
    }

    /// <summary>Test backup sources one by one (--lyrics-test flag): line counts from NetEase and Kugou.</summary>
    public async Task<string> DebugSources(string title, string artist, int dur)
    {
        string R(string name, string lrc) => $"{name}: {(lrc == null ? "tidak ada" : Lrc.Parse(lrc).Count + " baris")}";
        string a, b, c;
        var sw = System.Diagnostics.Stopwatch.StartNew();   // combined first: unit tests afterward hit the same servers again
        bool busy = false;
        try { c = R("Gabungan (hedging)", await Fetch(title, artist, null, () => busy = true, dur)) + $", {sw.ElapsedMilliseconds} ms, sibuk={busy}, LRCLIB dilewati={DateTime.UtcNow < _lrDownUntil}"; } catch (Exception ex) { c = "Gabungan error " + ex.Message; }
        try { a = R("NetEase", await FetchNetease(CleanTitle(title), CleanArtist(artist), dur)); } catch (Exception ex) { a = "NetEase error " + ex.Message; }
        try { b = R("Kugou", await FetchKugou(CleanTitle(title), CleanArtist(artist), dur)); } catch (Exception ex) { b = "Kugou error " + ex.Message; }
        string r; _durTol = 15;
        try { var kl = await FetchKugou(CleanTitle(title), CleanArtist(artist), dur); r = "Kugou longgar: " + (kl == null ? "tidak ada" : Lrc.Parse(kl).Count + " baris, akhir " + Lrc.Parse(kl)[^1].At.TotalSeconds.ToString("0") + "s"); } catch (Exception ex) { r = "Kugou longgar error " + ex.Message; }
        finally { _durTol = 6; }
        return c + "\n" + a + "\n" + b + "\n" + r;
    }

    /// <summary>
    /// Are the synced lyrics sane? Rejects (1) synthetic timestamps: nearly uniform short intervals between lines (plain lyrics given [00:01],
    /// [00:02]…) and (2) time coverage much shorter than the song (another version / TV-size), which makes karaoke get stuck on the last line.
    /// </summary>
    static bool LrcSane(string lrc, int dur)
    {
        if (string.IsNullOrEmpty(lrc)) return false;
        var L = Lrc.Parse(lrc);
        if (L.Count < 3) return false;
        var d = new List<double>();
        for (int i = 1; i < L.Count; i++) d.Add((L[i].At - L[i - 1].At).TotalSeconds);
        var sorted = d.OrderBy(x => x).ToList();
        double med = sorted[sorted.Count / 2];
        if (med <= 2.5 && d.Count(x => Math.Abs(x - med) < 0.02) >= d.Count * 0.85) return false;   // uniform spacing = fake timestamps
        if (dur > 30 && L[^1].At.TotalSeconds < dur * 0.4) return false;                           // ends well before the song finishes
        return true;
    }

    volatile int _durTol = 6;   // candidate duration tolerance (seconds); relaxed only on the final pass

    /// <summary>First line "Artist - Title" inserted by lyric sources as a header rather than part of the song: strip it.</summary>
    static List<LyricLine> StripHeader(List<LyricLine> l, string title)
    {
        var key = Norm(title);
        if (l.Count > 3 && key.Length > 1 && l[0].At < TimeSpan.FromSeconds(8) && l[0].Text.Contains(" - ") && Norm(l[0].Text).Contains(key)) l.RemoveAt(0);
        return l;
    }

    static string Norm(string s) => System.Text.RegularExpressions.Regex.Replace((s ?? "").ToLowerInvariant(), @"[\s\p{P}\p{S}]+", "");

    static async Task<JsonDocument> GetJson(string url, string referer = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer != null) req.Headers.Referrer = new Uri(referer);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        using var r = await Http.SendAsync(req);
        r.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync());
    }

    // drop credit lines (作词/作曲/詞/曲…) and leading "Title - Artist" title lines; result must have ≥3 synced lines
    static string CleanLrc(string lrc)
    {
        var kept = lrc.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\[[^\]]*\]\s*(作词|作曲|编曲|制作|作詞|編曲|词|曲|詞|Lyrics|Music|Arrange|Composer)[^:：]{0,6}[:：]")).ToList();
        int first = kept.FindIndex(l => l.StartsWith("[00:0") && l.Contains(" - "));   // "[00:00.00]Title (Live) - Artist"
        if (first >= 0 && first < 4) kept.RemoveAt(first);
        var joined = string.Join("\n", kept);
        return Lrc.Parse(joined).Count >= 3 ? joined : null;
    }

    /// <summary>NetEase: title matches and duration within ±6 seconds (when known).</summary>
    async Task<string> FetchNetease(string title, string artist, int dur)
    {
        const string Ref = "https://music.163.com/";
        using var search = await GetJson($"https://music.163.com/api/search/get?s={Uri.EscapeDataString((title + " " + artist).Trim())}&type=1&limit=6", Ref);
        if (!search.RootElement.TryGetProperty("result", out var res) || !res.TryGetProperty("songs", out var songs)) return null;
        var want = Norm(title);
        foreach (var s in songs.EnumerateArray())
        {
            var name = Norm(s.GetProperty("name").GetString());
            if (want.Length > 0 && !name.Contains(want) && !want.Contains(name)) continue;
            if (dur > 0 && s.TryGetProperty("duration", out var d) && d.GetDouble() > 0 && Math.Abs(d.GetDouble() / 1000 - dur) > _durTol) continue;
            using var ly = await GetJson($"https://music.163.com/api/song/lyric?id={s.GetProperty("id").GetInt64()}&lv=1&kv=1&tv=-1", Ref);
            if (!ly.RootElement.TryGetProperty("lrc", out var lrc) || !lrc.TryGetProperty("lyric", out var t) || t.GetString() is not { Length: > 0 } text) continue;
            if (CleanLrc(text) is { } ok) return ok;
        }
        return null;
    }

    // "AXIA -Daisuki de Daikirai-" / "Title ~Sub~" / "Title - Remaster" -> "AXIA" / "Title": subtitles often make Asian catalog searches return empty
    static string MainTitle(string t)
    {
        var c = CleanTitle(t);
        var m = Regex.Replace(c, @"\s+[-~～–—].*$", "").Trim();
        return m.Length >= 2 ? m : c;
    }

    /// <summary>
    /// LyricsPlus (lyricsplus.binimum.org): keyless community aggregator pulling from Apple Music, QQ Music, and Deezer; per-line
    /// lyrics with professional timing. 404 = not found. Returned title must match to avoid wrong-song hits.
    /// </summary>
    async Task<string> FetchLyricsPlus(string title, string artist, int dur)
    {
        if (string.IsNullOrWhiteSpace(artist)) return null;   // API requires artist (400 if empty)
        var url = $"https://lyricsplus.binimum.org/v2/lyrics/get?title={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist ?? "")}" + (dur > 0 ? $"&duration={dur}" : "");
        using var r = await Http.GetAsync(url);
        if ((int)r.StatusCode == 404) return null;
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (!root.TryGetProperty("lyrics", out var ls) || ls.ValueKind != JsonValueKind.Array) return null;
        var want = Norm(title);
        if (root.TryGetProperty("metadata", out var md) && md.TryGetProperty("title", out var mt) && mt.GetString() is { Length: > 0 } got)
        {
            var g = Norm(got);
            if (want.Length > 0 && g.Length > 0 && !g.Contains(want) && !want.Contains(g)) return null;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var l in ls.EnumerateArray())
        {
            if (!l.TryGetProperty("time", out var tm) || tm.ValueKind != JsonValueKind.Number) continue;
            var ts = TimeSpan.FromMilliseconds(tm.GetDouble());
            sb.Append($"[{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}]").AppendLine((l.TryGetProperty("text", out var tx) ? tx.GetString() : "")?.Trim() ?? "");
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>QQ Music: search via smartbox (title match), direct LRC lyrics (no base64); strong catalog for Japanese/Mandarin songs.</summary>
    async Task<string> FetchQQ(string title, string artist, int dur)
    {
        const string Ref = "https://y.qq.com/";
        using var search = await GetJson($"https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg?key={Uri.EscapeDataString((title + " " + artist).Trim())}&format=json", Ref);
        if (!search.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("song", out var song) || !song.TryGetProperty("itemlist", out var items)) return null;
        var want = Norm(title);
        int tried = 0;
        foreach (var s in items.EnumerateArray())
        {
            if (tried >= 3) break;
            var name = Norm(s.TryGetProperty("name", out var n) ? n.GetString() : "");
            if (want.Length > 0 && name.Length > 0 && !name.Contains(want) && !want.Contains(name)) continue;
            tried++;
            using var ly = await GetJson($"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={s.GetProperty("mid").GetString()}&g_tk=5381&format=json&nobase64=1", Ref);
            if (!ly.RootElement.TryGetProperty("lyric", out var t) || t.GetString() is not { Length: > 0 } text) continue;
            if (CleanLrc(text) is { } ok && LrcSane(ok, dur)) return ok;
        }
        return null;
    }

    /// <summary>Kugou: lyric candidates (title match, duration ±6 s), downloaded as LRC (base64).</summary>
    async Task<string> FetchKugou(string title, string artist, int dur)
    {
        using var search = await GetJson($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Uri.EscapeDataString((artist + " " + title).Trim())}&duration={(dur > 0 && _durTol <= 6 ? dur * 1000 : "")}&hash=");
        if (!search.RootElement.TryGetProperty("candidates", out var cands)) return null;
        var want = Norm(title);
        int tried = 0;
        foreach (var c in cands.EnumerateArray())
        {
            if (tried >= 4) break;
            var name = Norm(c.TryGetProperty("song", out var sn) ? sn.GetString() : "");
            if (want.Length > 0 && name.Length > 0 && !name.Contains(want) && !want.Contains(name)) continue;
            if (dur > 0 && c.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.GetDouble() > 0 && Math.Abs(d.GetDouble() / 1000 - dur) > _durTol) continue;
            tried++;
            using var dl = await GetJson($"https://lyrics.kugou.com/download?ver=1&client=pc&id={c.GetProperty("id").GetString()}&accesskey={c.GetProperty("accesskey").GetString()}&fmt=lrc&charset=utf8");
            if (!dl.RootElement.TryGetProperty("content", out var ct) || ct.GetString() is not { Length: > 0 } b64) continue;
            if (CleanLrc(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)).TrimStart('﻿')) is { } ok) return ok;
        }
        return null;
    }

    // "Title - Remastered 2011", "Title (feat. X)" -> "Title"
    static string CleanTitle(string t)
    {
        var cut = System.Text.RegularExpressions.Regex.Replace(t ?? "", @"\s*[\(\[].*?[\)\]]", "");
        int i = cut.IndexOf(" - ", StringComparison.Ordinal);
        return (i > 0 ? cut[..i] : cut).Trim();
    }

    static string ReadSynced(JsonElement e) =>
        e.TryGetProperty("syncedLyrics", out var v) && v.ValueKind == JsonValueKind.String && v.GetString().Length > 0 ? v.GetString() : null;

    // ---------- dominant cover color ----------
    static Color? Dominant(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(src, new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[h * stride];
            conv.CopyPixels(px, stride, 0);
            double r = 0, g = 0, b = 0, wt = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double B = px[i], G = px[i + 1], R = px[i + 2];
                double mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
                double sat = mx == 0 ? 0 : (mx - mn) / mx;
                double weight = sat * (mx / 255.0) + 0.02; // favor colorful and bright pixels
                r += R * weight; g += G * weight; b += B * weight; wt += weight;
            }
            if (wt <= 0) return null;
            r /= wt; g /= wt; b /= wt;
            double max = Math.Max(r, Math.Max(g, b));
            double k = max > 0 ? Math.Min(1.0, 210 / max) : 1; // keep from getting too bright
            return Color.FromRgb((byte)(r * k), (byte)(g * k), (byte)(b * k));
        }
        catch { return null; }
    }

    // ---------- music sleep timer ----------
    public void CycleSleepTimer()
    {
        int nextMin = 0;
        bool endOfTrack = false;
        lock (_sleepLk)
        {
            if (SleepMode == SleepModeKind.Off) nextMin = SleepPresets[0];
            else if (SleepMode == SleepModeKind.Timed)
            {
                int idx = Array.IndexOf(SleepPresets, SleepPresetMinutes);
                if (idx >= 0 && idx + 1 < SleepPresets.Length) nextMin = SleepPresets[idx + 1];
                else if (idx == SleepPresets.Length - 1) endOfTrack = true;
            }
        }
        if (endOfTrack) SetSleepEndOfTrack();
        else if (nextMin > 0) SetSleepMinutes(nextMin);
        else CancelSleepTimer();
    }

    public void SetSleepMinutes(int minutes)
    {
        minutes = Math.Clamp(minutes, 1, 480);
        lock (_sleepLk)
        {
            RestoreSleepVolumeLocked();
            SleepMode = SleepModeKind.Timed;
            SleepPresetMinutes = minutes;
            _sleepEndUtc = DateTime.UtcNow.AddMinutes(minutes);
            _sleepT ??= new System.Threading.Timer(_ => OnSleepTick(), null, Timeout.Infinite, Timeout.Infinite);
            _sleepT.Change(TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
        }
        Ui(() => Changed?.Invoke());
    }

    public void SetSleepEndOfTrack()
    {
        lock (_sleepLk)
        {
            RestoreSleepVolumeLocked();
            SleepMode = SleepModeKind.EndOfTrack;
            SleepPresetMinutes = 0;
            _sleepT?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        Ui(() => Changed?.Invoke());
    }

    public void CancelSleepTimer()
    {
        bool had;
        lock (_sleepLk)
        {
            had = SleepMode != SleepModeKind.Off || _fadeOrigVol != null;
            SleepMode = SleepModeKind.Off;
            SleepPresetMinutes = 0;
            _sleepT?.Change(Timeout.Infinite, Timeout.Infinite);
            RestoreSleepVolumeLocked();
        }
        if (had) Ui(() => Changed?.Invoke());
    }

    void RestoreSleepVolume()
    {
        lock (_sleepLk)
        {
            _sleepT?.Change(Timeout.Infinite, Timeout.Infinite);
            RestoreSleepVolumeLocked();
        }
    }

    void RestoreSleepVolumeLocked()
    {
        _fadeLastSetVol = null;
        if (_fadeOrigVol is not float v) return;
        _fadeOrigVol = null;
        try { AudioEndpoint.SetVolume(v, unmute: false); } catch (Exception ex) { App.Log(ex); }
    }

    void OnSleepTick()
    {
        bool notify = false;
        lock (_sleepLk)
        {
            if (SleepMode == SleepModeKind.Timed)
            {
                var rem = _sleepEndUtc - DateTime.UtcNow;
                if (rem > TimeSpan.FromMilliseconds(50))
                {
                    _sleepT?.Change(rem, Timeout.InfiniteTimeSpan);
                    return;
                }
                float? vol = null;
                try { vol = AudioEndpoint.Volume(); } catch (Exception ex) { App.Log(ex); }
                if (vol is float v && v > 0.01f)
                {
                    _fadeOrigVol = v;
                    _fadeLastSetVol = v;
                    _fadeStep = 0;
                    SleepMode = SleepModeKind.Fading;
                    int stepMs = SleepFadeSec * 1000 / SleepFadeSteps;
                    _sleepT?.Change(stepMs, stepMs);
                    notify = true;
                }
                else
                {
                    FinishSleepLocked();
                    notify = true;
                }
            }
            else if (SleepMode == SleepModeKind.Fading)
            {
                float? cur = null;
                try { cur = AudioEndpoint.Volume(); } catch (Exception ex) { App.Log(ex); }
                if (cur is float cv && _fadeLastSetVol is float lv && Math.Abs(cv - lv) > 0.05f)
                {
                    // User adjusted volume manually: stop fading without overwriting user's choice
                    SleepMode = SleepModeKind.Off;
                    SleepPresetMinutes = 0;
                    _sleepT?.Change(Timeout.Infinite, Timeout.Infinite);
                    _fadeOrigVol = null;
                    _fadeLastSetVol = null;
                    notify = true;
                }
                else
                {
                    _fadeStep++;
                    if (_fadeStep < SleepFadeSteps)
                    {
                        float target = (_fadeOrigVol ?? 0f) * (1f - (float)_fadeStep / SleepFadeSteps);
                        try
                        {
                            AudioEndpoint.SetVolume(target, unmute: false);
                            _fadeLastSetVol = target;
                        }
                        catch (Exception ex) { App.Log(ex); }
                    }
                    else
                    {
                        try
                        {
                            AudioEndpoint.SetVolume(0f, unmute: false);
                            _fadeLastSetVol = 0f;
                        }
                        catch (Exception ex) { App.Log(ex); }
                        FinishSleepLocked();
                        notify = true;
                    }
                }
            }
        }
        if (notify) Ui(() => Changed?.Invoke());
    }

    void FinishSleepLocked()
    {
        SleepMode = SleepModeKind.Off;
        SleepPresetMinutes = 0;
        _fadeLastSetVol = null;
        _sleepT?.Change(Timeout.Infinite, Timeout.Infinite);
        _pausingForSleep = true;
        _ = PauseAndRestoreAsync();
    }

    async Task PauseAndRestoreAsync()
    {
        try
        {
            await PauseIfPlayingAsync();
            await Task.Delay(250);
        }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            lock (_sleepLk)
            {
                RestoreSleepVolumeLocked();
                _pausingForSleep = false;
            }
            Ui(() => Changed?.Invoke());
        }
    }

    void OnTrackChangedForSleep()
    {
        bool pause = false;
        lock (_sleepLk)
        {
            if (SleepMode == SleepModeKind.EndOfTrack)
            {
                SleepMode = SleepModeKind.Off;
                SleepPresetMinutes = 0;
                _pausingForSleep = true;
                pause = true;
            }
        }
        if (pause) _ = PauseAfterTrackChangeAsync();
    }

    async Task PauseAfterTrackChangeAsync()
    {
        try { await PauseIfPlayingAsync(); }
        catch (Exception ex) { App.Log(ex); }
        finally { lock (_sleepLk) _pausingForSleep = false; Ui(() => Changed?.Invoke()); }
    }
}
