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


/// <summary>Now-playing lewat SMTC (semua pemutar yang tampil di kartu media Windows) + lirik tersinkron dari LRCLIB.</summary>
public sealed class MediaService
{
    public static readonly MediaService I = new();

    public event Action Changed;
    public bool HasSession, Playing;
    public string Title = "", Artist = "", Album = "";
    public BitmapSource Cover;
    public Color? CoverColor;
    public TimeSpan Duration;
    public List<LyricLine> Lyrics = new();
    public string LyricState = "";   // "", "loading", "none", "ok"

    GlobalSystemMediaTransportControlsSessionManager _mgr;
    GlobalSystemMediaTransportControlsSession _s;
    TimeSpan _pos;
    DateTimeOffset _posAt;
    string _key = "";
    int _coverTries, _propTries;

    static readonly HttpClient Http = MakeHttp();
    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ZaxLet/1.0 (personal)");
        return h;
    }

    static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    /// <summary>Dipanggil saat lirik dinyalakan lagi di tengah lagu: cari lirik lagu yang sedang diputar.</summary>
    public void EnsureLyrics()
    {
        if (!Settings.Cur.MusicLyrics || !HasSession || Lyrics.Count > 0 || LyricState is "loading" or "retry" || string.IsNullOrWhiteSpace(Title)) return;
        LyricState = "loading";
        _ = LoadLyrics(_key, Title, Artist, Album);
    }

    public async Task StartAsync()
    {
        Settings.Applied += () => Ui(EnsureLyrics);
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Ui(async () => await RefreshAll());
            await RefreshAll();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    async Task RefreshAll()
    {
        try
        {
            var s = _mgr?.GetCurrentSession();
            if (!ReferenceEquals(s, _s))
            {
                if (_s != null) Detach();
                _s = s;
                if (_s != null) Attach();
            }
            if (_s == null)
            {
                HasSession = Playing = false;
                Title = Artist = Album = "";
                Cover = null; CoverColor = null; Lyrics = new(); LyricState = ""; _key = "";
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
        GlobalSystemMediaTransportControlsSessionMediaProperties p;
        try { p = await _s.TryGetMediaPropertiesAsync(); _propTries = 0; }
        catch (Exception ex)
        {
            // sesi baru kadang belum siap (0x80070015): coba lagi sebentar lagi
            if (_propTries++ >= 5) { App.Log(ex); return; }
            _ = Task.Delay(1000).ContinueWith(_ => Ui(async () => { await RefreshProps(); Changed?.Invoke(); }));
            return;
        }
        var key = $"{p.Artist}|{p.Title}";
        bool newTrack = key != _key;
        Title = p.Title ?? ""; Artist = p.Artist ?? ""; Album = p.AlbumTitle ?? "";
        if (newTrack)
        {
            _key = key;
            _coverTries = 0;
            Lyrics = new(); LyricState = "loading";
            Cover = null; CoverColor = null;
            if (Settings.Cur.MusicLyrics) _ = LoadLyrics(key, Title, Artist, Album); else LyricState = "off";   // lirik dimatikan: tidak mengunduh
        }
        // sebagian pemutar mengirim sampul terlambat: coba lagi beberapa kali selama lagunya sama
        if (p.Thumbnail == null && Cover == null && _coverTries++ < 3)
            _ = Task.Delay(1500).ContinueWith(_ => Ui(async () => { if (key == _key) { await RefreshProps(); Changed?.Invoke(); } }));
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
        var i = _s.GetPlaybackInfo();
        Playing = i.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    void RefreshTimeline()
    {
        if (_s == null) return;
        var t = _s.GetTimelineProperties();
        Duration = t.EndTime - t.StartTime;
        _pos = t.Position - t.StartTime;
        _posAt = t.LastUpdatedTime;
    }

    public TimeSpan Position()
    {
        var p = _pos;
        if (Playing) p += DateTimeOffset.Now - _posAt;
        if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }

    public string CurrentLine()
    {
        int i = CurrentIndex();
        return i < 0 ? "" : Lyrics[i].Text;
    }

    public int CurrentIndex()
    {
        if (Lyrics.Count == 0) return -1;
        var p = Position() + TimeSpan.FromMilliseconds(200 + Settings.Cur.LyricOffsetMs);
        int lo = 0, hi = Lyrics.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int m = (lo + hi) / 2;
            if (Lyrics[m].At <= p) { ans = m; lo = m + 1; } else hi = m - 1;
        }
        return ans;
    }

    /// <summary>
    /// Posisi karaoke: baris yang sedang dinyanyikan dan seberapa jauh (0..1). LRCLIB hanya punya waktu per baris, jadi lamanya
    /// menyanyikan baris diperkirakan dari panjang teks (tempo bisa diatur) dan tidak melewati awal baris berikutnya.
    /// </summary>
    public (int Index, double Progress) Karaoke()
    {
        int i = CurrentIndex();
        if (i < 0) return (-1, 0);
        var l = Lyrics[i];
        var (sung, vexp, contig) = Est(i);
        var t = Position() + TimeSpan.FromMilliseconds(Settings.Cur.LyricOffsetMs);
        double elapsed = (t - l.At).TotalSeconds, timeP = Math.Clamp(elapsed / sung, 0, 1);
        DbgK = (i, l.Text, sung, timeP, 0, 0, false);

        // Mode vokal: progres = lama vokal benar-benar bersuara sejak baris mulai / perkiraan lama bernyanyi. Jeda instrumental menahan isi,
        // intro panjang tidak mengisi lebih dulu, dan baris yang selesai lebih awal (vokal berhenti) langsung penuh. Tanpa data audio: waktu.
        var af = AudioFlow.I;
        if (!Settings.Cur.LyricFlow || !af.Fresh || elapsed <= 0 || af.CapturingFor < elapsed + 0.1) { _kIdx = -1; return (i, timeP); }

        // Keandalan: bila selama 12 dtk terakhir hampir selalu "bersuara" (instrumen/lagu keras menutupi vokal) atau hampir tak pernah,
        // pembacaan vokal tidak bisa dipercaya untuk lagu ini dan progres kembali ke hitungan waktu.
        double win = Math.Min(12, af.CapturingFor);
        double vr = win > 4 ? af.VoicedSince(win) / win : 0.5;
        if (vr > 0.88 || vr < 0.12) { _kIdx = -1; return (i, timeP); }

        double voiced = af.VoicedSince(elapsed);
        DbgK = (i, l.Text, sung, timeP, voiced, vr, true);
        var now = DateTime.UtcNow;
        if (i != _kIdx)   // pindah baris: belajar dari baris sebelumnya seberapa besar bagian jaraknya yang benar-benar bersuara
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

        // Batas toleransi: hasil audio hanya boleh menyimpang ±FlowTolSec dari hitungan waktu, jadi bagian lagu yang lebih keras dari vokal
        // (atau vokal yang tak terbaca) tidak bisa menarik isi terlalu jauh ke depan atau menahannya terlalu lama.
        // Dibatasi juga sebagai bagian baris (±18% untuk baris bersambung yang waktunya bisa diandalkan, ±35% lainnya): toleransi dalam detik saja
        // terlalu longgar untuk baris pendek.
        // Asimetris: hasil audio cenderung mendahului (instrumen keras terbaca sebagai vokal), jadi batas atas lebih ketat dari batas bawah.
        double tolS = Math.Clamp(Settings.Cur.FlowTolSec, 0.3, 8) / sung;
        p = Math.Clamp(p, timeP - Math.Min(tolS, contig ? 0.20 : 0.35), timeP + Math.Min(tolS, contig ? 0.12 : 0.30));
        bool done = (voiced >= expect * 0.55 && af.SilentFor > 0.45 && elapsed >= sung * 0.6)   // vokal sudah berhenti: baris selesai
                    || (contig && elapsed >= sung);                                              // baris bersambung: penuh saat jatah waktunya habis
        if (done) p = 1;
        else
        {
            // tidak pernah mundur dalam satu baris, dan maju paling cepat 2,5x kecepatan normal (tanpa lompatan)
            double dt = Math.Min(1, (now - _kAt).TotalSeconds);
            p = Math.Max(p, _kP);
            if (_kP > 0) p = Math.Min(p, _kP + dt * 2.5 / sung);
        }
        _kP = p; _kAt = now;
        return (i, Math.Clamp(p, 0, 1));
    }

    double _gapFrac = 0.75;   // bagian jarak ke baris berikutnya yang benar-benar bersuara (dipelajari dari audio)
    double _kBase; bool _kContig;

    /// <summary>
    /// Perkiraan lama baris: (rentang tampil, lama bersuara yang diharapkan, bersambung?). Baris yang disusul baris lain dalam 7 dtk dianggap
    /// mengisi jaraknya (lagu lambat/panjang tidak lagi terisi terlalu cepat); baris sebelum jeda panjang memakai panjang teks.
    /// </summary>
    (double Span, double VoicedExpect, bool Contiguous) Est(int i)
    {
        var l = Lyrics[i];
        bool hasNext = i + 1 < Lyrics.Count;
        double gap = hasNext ? (Lyrics[i + 1].At - l.At).TotalSeconds : 5;
        if (hasNext && gap <= 7)
        {
            double basis = Math.Max(0.8, gap - 0.25);
            return (Math.Max(0.8, basis * 1.0), Math.Max(0.6, basis * _gapFrac), true);   // 1,0: jalan tengah; 0,97 → isi cenderung dini (t90 0,84), 1,06 → 31% baris tak sempat penuh (diukur pada 7 lagu)
        }
        double perChar = Settings.Cur.LyricPace switch { "fast" => 0.11, "slow" => 0.18, _ => 0.14 };
        double span = Math.Max(0.8, Math.Min(gap - 0.2, Math.Max(1.5, l.Text.Length * perChar)));
        return (span, Math.Max(0.6, span * _ratio), false);
    }

    /// <summary>Nilai antara terakhir Karaoke() untuk diagnosis (flag --karaoke-trace).</summary>
    public (int Idx, string Text, double Sung, double TimeP, double Voiced, double VoicedRatio, bool Flow) DbgK;

    string _lblKey = "";

    /// <summary>
    /// Label untuk belajar pola vokal (dipanggil dari thread audio): 1 = pasti ada vokal, 0 = pasti instrumen, -1 = tidak yakin (batas baris).
    /// Vokal: bagian tengah baris yang dinyanyikan. Instrumen: intro sebelum baris pertama, baris kosong/♪, dan jeda panjang setelah baris selesai.
    /// </summary>
    public int VocalLabel()
    {
        var L = Lyrics;
        if (L.Count == 0 || !Playing) return -1;
        if (_lblKey != _key) { _lblKey = _key; AudioFlow.I.ResetModel(); }   // lagu baru: mulai belajar dari nol
        var t = Position() + TimeSpan.FromMilliseconds(Settings.Cur.LyricOffsetMs);
        if (t < L[0].At - TimeSpan.FromSeconds(0.6)) return t > TimeSpan.FromSeconds(1) ? 0 : -1;
        int lo = 0, hi = L.Count - 1, i = -1;
        while (lo <= hi) { int m = (lo + hi) / 2; if (L[m].At <= t) { i = m; lo = m + 1; } else hi = m - 1; }
        if (i < 0) return -1;
        var l = L[i];
        var next = i + 1 < L.Count ? L[i + 1].At : l.At + TimeSpan.FromSeconds(6);
        double since = (t - l.At).TotalSeconds, gap = (next - l.At).TotalSeconds, left = (next - t).TotalSeconds;
        if (string.IsNullOrWhiteSpace(l.Text) || l.Text.Trim() == "♪") return since > 0.6 && left > 0.5 ? 0 : -1;
        double est = Est(i).Span;
        if (since >= 0.5 && since <= est * 0.65) return 1;
        if (gap >= 5 && since >= est + 1.2 && left >= 0.6) return 0;
        return -1;
    }

    int _kIdx = -1; double _kSung, _kVoiced, _ratio = 1, _kP; DateTime _kAt;   // pembelajaran rasio dan keadaan progres karaoke vokal

    // ---------- kontrol ----------
    public void PlayPause() => _ = _s?.TryTogglePlayPauseAsync();
    public void Next() => _ = _s?.TrySkipNextAsync();
    public void Prev() => _ = _s?.TrySkipPreviousAsync();
    public void Seek(TimeSpan to)
    {
        if (_s == null) return;
        _pos = to; _posAt = DateTimeOffset.Now;
        _ = _s.TryChangePlaybackPositionAsync(to.Ticks);
    }

    // ---------- lirik ----------
    /// <summary>
    /// Cari lirik tersinkron di LRCLIB. /api/get (tepat) kadang 503, jadi selalu ada cadangan /api/search (judul+artis, lalu
    /// judul yang dibersihkan + pencarian bebas). Gangguan server dicoba ulang beberapa kali sebelum menyerah.
    /// </summary>
    async Task LoadLyrics(string key, string title, string artist, string album)
    {
        await Task.Delay(600); // beri waktu agar durasi terisi
        if (string.IsNullOrWhiteSpace(title)) return;

        // cache disk: lagu yang sudah pernah dicari langsung tampil dan tidak bergantung pada server LRCLIB
        var cache = Path.Combine(Settings.Dir, "lyrics",
            Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes($"{artist}|{title}".ToLowerInvariant()))) + ".lrc");
        try
        {
            // cache lama yang tidak wajar (cap waktu palsu dari versi sebelumnya) diabaikan dan dicari ulang
            if (File.Exists(cache) && LrcSane(File.ReadAllText(cache), Duration > TimeSpan.Zero ? (int)Duration.TotalSeconds : 0) && Lrc.Parse(File.ReadAllText(cache)) is { Count: > 0 } cached && key == _key)
            {
                Lyrics = cached; LyricState = "ok";
                PrefetchRomaji();
                Ui(() => Changed?.Invoke());
                return;
            }
        }
        catch { /* cache rusak: cari ulang */ }

        // lagu yang baru saja dipastikan tidak ada di sumber mana pun: jangan tanya server lagi untuk sementara (NetEase pun menolak kueri berulang)
        var negKey = $"{artist}|{title}".ToLowerInvariant();
        if (NoLyrics.TryGetValue(negKey, out var until) && DateTime.UtcNow < until)
        {
            if (key == _key) { Lyrics = new(); LyricState = "none"; Ui(() => Changed?.Invoke()); }
            return;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (key != _key) return;
            bool error = false;
            string synced = null;
            try { synced = await Fetch(title, artist, album, () => error = true); }
            catch (Exception ex) { error = true; App.Log(ex); }
            if (key != _key) return;
            if (synced != null)
            {
                Lyrics = Lrc.Parse(synced);
                LyricState = Lyrics.Count > 0 ? "ok" : "none";
                PrefetchRomaji();
                if (Lyrics.Count > 0)
                    try { Directory.CreateDirectory(Path.GetDirectoryName(cache)); File.WriteAllText(cache, synced); } catch { }
                Ui(() => Changed?.Invoke());
                return;
            }
            if (!error) { NoLyrics[negKey] = DateTime.UtcNow.AddMinutes(20); break; }   // ada sumber yang menjawab: memang tidak ada lirik bersinkron
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
        if (Settings.Cur.LyricRomaji || Settings.Cur.IdleRomaji) Romaji.Prefetch(Lyrics.Select(l => l.Text).ToList());
    }

    /// <summary>
    /// Tiga sumber dengan "hedging" bertahap: LRCLIB mulai langsung; NetEase baru mulai bila LRCLIB belum menjawab dalam 1 dtk atau menjawab
    /// kosong; Kugou bila keduanya belum menghasilkan dalam 2 dtk. Lagu yang ada di LRCLIB (kasus umum) tidak memicu permintaan tambahan,
    /// sedangkan lagu yang tidak ada langsung pindah ke sumber berikutnya tanpa menunggu pencarian LRCLIB yang lambat habis. Hasil tetap
    /// dipilih menurut prioritas (LRCLIB > NetEase > Kugou).
    /// </summary>
    async Task<string> Fetch(string title, string artist, string album, Action markError, int? durOverride = null)
    {
        var dur = durOverride ?? (Duration > TimeSpan.Zero ? (int)Duration.TotalSeconds : 0);
        var clean = CleanTitle(title);
        var art = CleanArtist(artist);

        // "Sibuk" hanya bila SEMUA sumber yang dicoba gagal berkomunikasi; satu sumber yang menjawab (walau kosong) berarti lagunya memang tidak ada.
        int attempted = 0, failed = 0;
        async Task<string> First()
        {
            Interlocked.Increment(ref attempted);
            try { var r = await FetchLrclib(title, artist, album, dur); return r != null && LrcSane(r, dur) ? r : null; }
            catch (Exception) { Interlocked.Increment(ref failed); return null; }   // LRCLIB sibuk / dilewati pemutus sirkuit: tidak menunda sumber lain
        }
        var main = MainTitle(title);
        var t1 = First();
        async Task<string> Backup(Func<string, string, int, Task<string>> src, Func<bool> hit, int delayMs, params Task[] waitFor)
        {
            await Task.WhenAny(Task.WhenAll(waitFor), Task.Delay(delayMs));
            if (hit()) return null;                                   // sumber yang lebih utama sudah dapat: tidak perlu meminta
            Interlocked.Increment(ref attempted);
            bool answered = false;
            // varian kueri: judul lengkap + artis bersih, judul utama (tanpa subjudul "-…-"/"~…~") + artis, judul utama saja; durasi tetap penyaring
            foreach (var (tt, a) in new[] { (clean, art), (main, art), (main, "") }.Distinct())
            {
                try { if (await src(tt, a, dur) is { } lrc && LrcSane(lrc, dur)) return lrc; answered = true; }
                catch (Exception ex) { App.Log(ex); }
            }
            if (!answered) Interlocked.Increment(ref failed);
            return null;
        }
        bool Got(params Task<string>[] ts) => ts.Any(t => t.IsCompletedSuccessfully && t.Result != null);
        // Daftar sumber cadangan berurutan (maksimal 10): tiap sumber baru dimulai 1 dtk setelah yang sebelumnya bila belum ada hasil.
        var chain = new List<Task<string>> { t1 };
        foreach (var (src, k) in new Func<string, string, int, Task<string>>[] { FetchLyricsPlus, FetchNetease, FetchQQ, FetchKugou }.Select((s, k) => (s, k)))
        {
            var prev = chain.ToArray();
            chain.Add(Backup(src, () => Got(prev), 1000 * (k + 1), prev));
        }
        // Pilih menurut prioritas urutan di atas. Bila sumber yang lebih rendah sudah dapat tetapi yang lebih utama masih mencari,
        // yang utama diberi masa tenggang 0,8 dtk lalu hasil yang ada dipakai (LRCLIB kadang butuh beberapa detik untuk menyimpulkan "tidak ada").
        var all = chain.ToArray();
        static string Res(Task<string> t) => t.IsCompletedSuccessfully ? t.Result : null;
        var graceEnd = DateTime.MaxValue;
        while (true)
        {
            bool pending = false; string best = null;
            foreach (var t in all)
            {
                if (!t.IsCompleted) { pending = true; break; }   // yang lebih utama belum selesai
                if ((best = Res(t)) != null) break;
            }
            if (best != null) return best;
            if (!pending)
            {
                if (attempted > 0 && failed >= attempted) { markError(); return null; }
                // Putaran terakhir: belum ada yang cocok → ulangi sumber Asia dengan toleransi durasi ±15 dtk (versi lain/durasi Spotify berbeda),
                // tetapi lirik harus mencakup 60%–105% lagu agar tidak salah versi.
                if (dur > 0)
                {
                    try
                    {
                        foreach (var src in new Func<string, string, int, Task<string>>[] { FetchNetease, FetchQQ, FetchKugou })
                            foreach (var (tt, a) in new[] { (clean, art), (main, art), (main, "") }.Distinct())
                                try
                                {
                                {
                                    var got = await src(tt, a, 0);   // tanpa penyaring durasi sumber; kecocokan dinilai dari cakupan lirik terhadap durasi sebenarnya di bawah
                                    if (got != null && LrcSane(got, dur) && Lrc.Parse(got) is { Count: > 0 } pl
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

    // Pemutus sirkuit LRCLIB: server sering 503/429. Setelah gagal, LRCLIB dilewati sementara (15, 30, 60, 120, 180 dtk) sehingga sumber cadangan
    // langsung dipakai dan server yang sedang sibuk tidak terus ditembak; sekali berhasil, hitungan gagal diulang dari nol.
    static DateTime _lrDownUntil; static int _lrFails;

    static void LrTrip()
    {
        _lrFails = Math.Min(_lrFails + 1, 5);
        _lrDownUntil = DateTime.UtcNow.AddSeconds(Math.Min(180, 15 * (1 << (_lrFails - 1))));
    }

    /// <summary>Satu permintaan LRCLIB: null = 404 (tidak ada); melempar bila server sibuk/gagal (5xx, 429, timeout 6 dtk) dan memutus sirkuit.</summary>
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

        // 1) pencocokan tepat
        var get = $"https://lrclib.net/api/get?track_name={E(title)}&artist_name={E(artist)}";
        if (!string.IsNullOrWhiteSpace(album)) get += $"&album_name={E(album)}";
        if (dur > 0) get += $"&duration={dur}";
        using (var exact = await LrAsk(get))
            if (exact != null && ReadSynced(exact.RootElement) is { } ex0) return ex0;

        // 2) pencarian: judul+artis, lalu judul bersih, lalu kueri bebas
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
            // pilih yang durasinya paling dekat; tanpa durasi, ambil hasil bersinkron pertama
            string best = null; double bestDiff = double.MaxValue;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var s = ReadSynced(el);
                if (s == null) continue;
                double diff = dur > 0 && el.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? Math.Abs(d.GetDouble() - dur) : 0;
                if (diff < bestDiff) { best = s; bestDiff = diff; }
            }
            if (best != null && (dur <= 0 || bestDiff <= 10)) return best;   // versi lain (durasi jauh) = sinkron meleset: lanjut ke kueri/sumber berikutnya
        }

        return null;
    }

    // "Artis (CV. Pengisi Suara)", "A, B", "A feat. B", "A & B" -> artis pertama tanpa keterangan
    static string CleanArtist(string a)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(a ?? "", @"\s*[\(（\[].*?[\)）\]]", "");
        s = System.Text.RegularExpressions.Regex.Split(s, @"\s*(?:,|、|&|/|;|×)\s*|\s+(?:feat\.?|ft\.?|starring|with|vs\.?)\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)[0];   // "Sheryl Nome starring May'n" -> "Sheryl Nome"
        return s.Trim();
    }

    /// <summary>Uji sumber cadangan satu per satu (flag --lyrics-test): jumlah baris dari NetEase dan Kugou.</summary>
    public async Task<string> DebugSources(string title, string artist, int dur)
    {
        string R(string name, string lrc) => $"{name}: {(lrc == null ? "tidak ada" : Lrc.Parse(lrc).Count + " baris")}";
        string a, b, c;
        var sw = System.Diagnostics.Stopwatch.StartNew();   // gabungan dulu: pengujian satuan sesudahnya memanggil server yang sama lagi
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
    /// Lirik bersinkron yang wajar? Menolak (1) cap waktu buatan: jarak antar baris nyaris seragam dan pendek (lirik polos yang diberi [00:01],
    /// [00:02]…) dan (2) cakupan waktu yang jauh lebih pendek dari lagu (versi lain / TV-size), yang membuat karaoke macet di baris terakhir.
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
        if (med <= 2.5 && d.Count(x => Math.Abs(x - med) < 0.02) >= d.Count * 0.85) return false;   // jarak seragam = cap waktu palsu
        if (dur > 30 && L[^1].At.TotalSeconds < dur * 0.4) return false;                           // berhenti jauh sebelum lagu selesai
        return true;
    }

    volatile int _durTol = 6;   // toleransi durasi kandidat (detik); dilonggarkan hanya pada putaran terakhir

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

    // buang baris kredit (作词/作曲/詞/曲…) dan baris judul "Judul - Artis" di awal; hasil harus punya ≥3 baris bersinkron
    static string CleanLrc(string lrc)
    {
        var kept = lrc.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\[[^\]]*\]\s*(作词|作曲|编曲|制作|作詞|編曲|词|曲|詞|Lyrics|Music|Arrange|Composer)[^:：]{0,6}[:：]")).ToList();
        int first = kept.FindIndex(l => l.StartsWith("[00:0") && l.Contains(" - "));   // "[00:00.00]Judul (Live) - Artis"
        if (first >= 0 && first < 4) kept.RemoveAt(first);
        var joined = string.Join("\n", kept);
        return Lrc.Parse(joined).Count >= 3 ? joined : null;
    }

    /// <summary>NetEase: judul cocok dan durasi dalam ±6 detik (bila diketahui).</summary>
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

    // "AXIA -Daisuki de Daikirai-" / "Judul ~Sub~" / "Judul - Remaster" -> "AXIA" / "Judul": subjudul sering membuat pencarian katalog Asia kosong
    static string MainTitle(string t)
    {
        var c = CleanTitle(t);
        var m = Regex.Replace(c, @"\s+[-~～–—].*$", "").Trim();
        return m.Length >= 2 ? m : c;
    }

    /// <summary>
    /// LyricsPlus (lyricsplus.binimum.org): agregator komunitas tanpa kunci yang mengambil dari Apple Music, QQ Music, dan Deezer; lirik
    /// per baris dengan waktu profesional. 404 = tidak ada. Judul yang dikembalikan harus cocok agar tidak salah lagu.
    /// </summary>
    async Task<string> FetchLyricsPlus(string title, string artist, int dur)
    {
        if (string.IsNullOrWhiteSpace(artist)) return null;   // API mewajibkan artis (400 bila kosong)
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

    /// <summary>QQ Music: pencarian lewat smartbox (judul cocok), lirik LRC langsung (tanpa base64); katalog kuat untuk lagu Jepang/Mandarin.</summary>
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

    /// <summary>Kugou: kandidat lirik (judul cocok, durasi ±6 dtk), unduh sebagai LRC (base64).</summary>
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

    // "Judul - Remastered 2011", "Judul (feat. X)" -> "Judul"
    static string CleanTitle(string t)
    {
        var cut = System.Text.RegularExpressions.Regex.Replace(t ?? "", @"\s*[\(\[].*?[\)\]]", "");
        int i = cut.IndexOf(" - ", StringComparison.Ordinal);
        return (i > 0 ? cut[..i] : cut).Trim();
    }

    static string ReadSynced(JsonElement e) =>
        e.TryGetProperty("syncedLyrics", out var v) && v.ValueKind == JsonValueKind.String && v.GetString().Length > 0 ? v.GetString() : null;

    // ---------- warna dominan sampul ----------
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
                double weight = sat * (mx / 255.0) + 0.02; // utamakan piksel berwarna dan terang
                r += R * weight; g += G * weight; b += B * weight; wt += weight;
            }
            if (wt <= 0) return null;
            r /= wt; g /= wt; b /= wt;
            double max = Math.Max(r, Math.Max(g, b));
            double k = max > 0 ? Math.Min(1.0, 210 / max) : 1; // jaga tidak terlalu terang
            return Color.FromRgb((byte)(r * k), (byte)(g * k), (byte)(b * k));
        }
        catch { return null; }
    }
}
