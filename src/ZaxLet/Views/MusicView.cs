using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ZaxLet;

public sealed class MusicView : TabView
{
    public override string Key => "music";
    public override string Glyph => "";
    public override string Label => "Musik";
    public override bool CenterV => false;

    readonly MediaService _m = MediaService.I;
    readonly Grid _player;
    readonly StackPanel _left;
    readonly Border _divider;
    readonly Image _cover = new() { Stretch = Stretch.UniformToFill };
    readonly TextBlock _title = new() { Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 46 };
    readonly TextBlock _artist = new() { Foreground = UI.Dim, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, Margin = new Thickness(0, 2, 0, 0) };
    readonly TextBlock _pos = UI.T("0:00", 12), _rem = UI.T("-0:00", 12), _play = UI.Icon("", 20, Brushes.Black), _off = UI.T("", 11.5, UI.Dim);
    readonly Bar _bar = new(5) { Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 8, 0) };
    readonly StackPanel _lyrics = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 0, 26) };
    readonly Grid _zone = new();   // zona lirik: baris lirik di tengah, tombol sinkron di sudut kiri bawah
    readonly LyricRow _prev = new(13, .35), _cur = new(18, .45), _next = new(13, .35);
    readonly TextBlock _rom = new() { Foreground = UI.Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5), Visibility = Visibility.Collapsed };   // romaji baris yang sedang dinyanyikan
    readonly TranslateTransform _slide = new();
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Grid _recap = new() { Visibility = Visibility.Collapsed };   // rekap lagu dan artis teratas (ala Spotify Wrapped mingguan)
    bool _recapOn, _recapDirty = true, _recapHasData;
    int _recapDays = 7;
    TextBlock _wrongT;
    StackPanel _syncRow;   // sinkron + cari ulang: tidak berlaku untuk iklan
    Border _shufBtn;
    readonly Bar _vol = new(4);
    readonly TextBlock _volPct = UI.T("", 11, UI.Dim);
    TextBlock _volIcon;
    bool _volDrag;
    int _lastIdx = -2;
    DateTime _lastFull = DateTime.MinValue, _lastPoll = DateTime.MinValue;

    public MusicView()
    {
        // kiri: sampul + judul, progres, kontrol
        _left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };   // jarak ke pembatas lirik
        var left = _left;
        var head = UI.Cols(72, -1, 0);
        var coverBox = new Border { Width = 58, Height = 58, CornerRadius = new CornerRadius(11), Background = UI.Card, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, Child = _cover };
        head.Children.Add(coverBox);
        var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        txt.Children.Add(_title);
        txt.Children.Add(_artist);
        head.Children.Add(txt.At(0, 1));
        left.Children.Add(head);

        var prog = UI.Cols(0, -1, 0);
        prog.Margin = new Thickness(0, 10, 0, 0);
        prog.Children.Add(_pos);
        // area sentuh lebih tinggi dari bar 5 px supaya mudah diklik; klik atau seret untuk maju/mundur (YouTube di peramban juga)
        var hit = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 8, 0) };
        _bar.Margin = new Thickness(0, 9, 0, 9);
        hit.Children.Add(_bar);
        prog.Children.Add(hit.At(0, 1));
        prog.Children.Add(_rem.At(0, 2));
        var lastDragSeek = DateTime.MinValue;
        void SeekFrom(MouseEventArgs e, bool force)
        {
            if (!_m.CanSeek || _bar.ActualWidth <= 0) return;   // CanSeek = ada durasi lagu (pada video bersegmen: durasi segmen)
            if (!force && (DateTime.Now - lastDragSeek).TotalMilliseconds < 120) return;   // jangan membanjiri peramban saat menyeret
            lastDragSeek = DateTime.Now;
            double f = Math.Clamp(e.GetPosition(_bar).X / _bar.ActualWidth, 0, 1);
            _m.Seek(TimeSpan.FromSeconds(_m.Duration.TotalSeconds * f));
            Render();
        }
        hit.MouseLeftButtonDown += (_, e) => { if (_m.CanSeek) { hit.CaptureMouse(); SeekFrom(e, true); } };
        hit.MouseMove += (_, e) => { if (hit.IsMouseCaptured) SeekFrom(e, false); };
        hit.MouseLeftButtonUp += (_, e) => { if (hit.IsMouseCaptured) { SeekFrom(e, true); hit.ReleaseMouseCapture(); } };
        left.Children.Add(prog);

        var ctl = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        ctl.Children.Add(UI.IconBtn("", () => { Settings.Cur.MusicLyrics = !Settings.Cur.MusicLyrics; Settings.Save(); Settings.Apply(); _m.EnsureLyrics(); Render(); PaintLyrics(); }, 17));
        ctl.Children.Add(UI.IconBtn("", _m.Prev, 18));
        var playBtn = UI.Btn(_play, _m.PlayPause, Brushes.White, 999, new Thickness(11));
        playBtn.Margin = new Thickness(6, 0, 6, 0);
        ctl.Children.Add(playBtn);
        ctl.Children.Add(UI.IconBtn("", _m.Next, 18));
        // tombol transport (sebelumnya, putar, berikutnya) dipusatkan tepat di bawah bar progres; tombol lirik di tepi kiri, bukan ikut menggeser grup
        var kids = ctl.Children.Cast<UIElement>().ToList();
        ctl.Children.Clear();
        var ctlRow = UI.Cols(-1, 0, -1);
        ctlRow.Margin = ctl.Margin;
        var trio = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int k = 1; k < kids.Count; k++) trio.Children.Add(kids[k]);
        Grid.SetColumn(trio, 1);
        if (kids[0] is FrameworkElement lyrBtn) { lyrBtn.HorizontalAlignment = HorizontalAlignment.Left; lyrBtn.VerticalAlignment = VerticalAlignment.Center; }
        // kiri: lirik; kanan: acak, seimbang di kedua sisi trio
        _shufBtn = UI.IconBtn("", _m.ToggleShuffle, 15); _shufBtn.ToolTip = L.T("Acak");
        ctlRow.Children.Add(kids[0]);
        ctlRow.Children.Add(trio);
        _shufBtn.HorizontalAlignment = HorizontalAlignment.Right; _shufBtn.VerticalAlignment = VerticalAlignment.Center;
        ctlRow.Children.Add(_shufBtn.At(0, 2));
        left.Children.Add(ctlRow);

        // volume: volume utama Windows (perangkat keluaran bawaan); klik ikon = bisukan, klik atau seret bar = atur
        var volRow = UI.Cols(0, 0, 0);
        volRow.HorizontalAlignment = HorizontalAlignment.Left; volRow.Margin = new Thickness(0, 9, 0, 0);   // sepertiga lebar bar lagu, tetap di kiri; margin atas meratakan jarak ke tombol transport (atas) dan garis tab (bawah)
        _volIcon = UI.Icon("", 13, UI.Dim);
        var volBtn = UI.Btn(_volIcon, () => { AudioEndpoint.SetMuted(!(AudioEndpoint.Muted() ?? false)); PollVolume(); }, Brushes.Transparent, 999, new Thickness(4));
        volBtn.ToolTip = L.T("Bisukan");
        volRow.Children.Add(volBtn);
        var volHit = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 4, 0), Width = 90 };
        _vol.Margin = new Thickness(0, 8, 0, 8);
        volHit.Children.Add(_vol);
        volRow.Children.Add(volHit.At(0, 1));
        _volPct.MinWidth = 32; _volPct.TextAlignment = TextAlignment.Right;
        volRow.Children.Add(_volPct.At(0, 2));
        var lastVol = DateTime.MinValue;
        void VolFrom(MouseEventArgs e, bool force)
        {
            if (_vol.ActualWidth <= 0 || !force && (DateTime.Now - lastVol).TotalMilliseconds < 60) return;
            lastVol = DateTime.Now;
            double f = Math.Clamp(e.GetPosition(_vol).X / _vol.ActualWidth, 0, 1);
            AudioEndpoint.SetVolume((float)f);
            _vol.Set(f); _volPct.Text = Math.Round(f * 100) + "%";
        }
        volHit.MouseLeftButtonDown += (_, e) => { _volDrag = true; volHit.CaptureMouse(); VolFrom(e, true); };
        volHit.MouseMove += (_, e) => { if (volHit.IsMouseCaptured) VolFrom(e, false); };
        volHit.MouseLeftButtonUp += (_, e) => { if (volHit.IsMouseCaptured) { VolFrom(e, true); volHit.ReleaseMouseCapture(); } _volDrag = false; };
        left.Children.Add(volRow);

        // kanan: lirik karaoke (tadi, sekarang, berikutnya) + sinkron cepat
        _lyrics.RenderTransform = _slide;
        // klik baris lirik = loncat ke baris itu (berguna saat bernyanyi / mengulang bagian)
        foreach (var (row, delta) in new[] { (_prev, -1), (_cur, 0), (_next, 1) })
        {
            var d = delta;
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) => SeekToLine(_lastIdx + d);
        }
        _prev.Margin = new Thickness(0, 0, 0, 5);
        _cur.Margin = new Thickness(0, 0, 0, 2);
        _lyrics.Children.Add(_prev);
        _lyrics.Children.Add(_cur);
        _lyrics.Children.Add(_rom);
        _lyrics.Children.Add(_next);
        // sinkron: tetap di sudut kiri bawah zona lirik (tidak ikut bergeser mengikuti tinggi baris lirik)
        var sync = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(22, 0, 0, 2) };
        sync.Children.Add(UI.T("Sinkron", 11.5, UI.Faint));
        var minus = UI.Btn(UI.T("−", 13), () => Nudge(-200), UI.Card, 9, new Thickness(9, 1, 9, 2));
        minus.Margin = new Thickness(8, 0, 4, 0);
        sync.Children.Add(minus);
        _off.MinWidth = 54; _off.TextAlignment = TextAlignment.Center;
        sync.Children.Add(_off);
        sync.Children.Add(UI.Btn(UI.T("+", 13), () => Nudge(200), UI.Card, 9, new Thickness(9, 1, 9, 2)));
        _wrongT = UI.T("Lirik salah?", 11, UI.Dim);
        var wrong = UI.Btn(_wrongT, () => { if (_m.Lyrics.Count > 0) _m.RejectLyrics(); else _m.RetryLyrics(); }, UI.Card, 9, new Thickness(9, 2, 9, 2));
        wrong.Margin = new Thickness(10, 0, 0, 0); wrong.ToolTip = L.T("Lirik salah: tandai lalu cari versi dari sumber lain. Belum ada lirik: cari ulang");
        sync.Children.Add(wrong);
        _zone.Children.Add(_lyrics);
        _zone.Children.Add(sync);
        _syncRow = sync;
        _divider = new Border { Width = 1, Background = UI.Track, Margin = new Thickness(0, 8, 0, 8) };

        _player = UI.Cols(300, 1, -1);
        _player.Children.Add(left);
        _player.Children.Add(_divider.At(0, 1));
        _player.Children.Add(_zone.At(0, 2));
        // Rekap di pojok kanan atas seluruh pemutar (area lirik; saat lirik mati tetap di pojok), supaya tidak memakan ruang judul lagu
        var recapBtn = UI.Btn(UI.T("Rekap", 11, UI.Dim), () => { _recapOn = true; _recapDirty = true; Render(); }, UI.Card, 9, new Thickness(8, 2, 8, 3));
        recapBtn.HorizontalAlignment = HorizontalAlignment.Right; recapBtn.VerticalAlignment = VerticalAlignment.Top; recapBtn.Margin = new Thickness(0, 2, 2, 0);
        Grid.SetColumnSpan(recapBtn, 3);
        _player.Children.Add(recapBtn);

        Children.Add(_player);
        Children.Add(_recap);
        _t.Tick += (_, _) => OnTick();
    }

    // volume bisa diubah dari luar (tombol keyboard, mixer): dibaca tiap detik selama tab terbuka, kecuali sedang diseret
    void PollVolume()
    {
        if (_volDrag) return;
        var v = AudioEndpoint.Volume();
        bool muted = AudioEndpoint.Muted() ?? false;
        if (v is { } f) { _vol.Set(muted ? 0 : f); _volPct.Text = muted ? L.T("Bisu") : Math.Round(f * 100) + "%"; }
        _volIcon.Text = muted || v is 0f ? "" : "";
    }

    void Nudge(int deltaMs)
    {
        _m.Offset += deltaMs;   // tersimpan per lagu
        UpdateOffsetLabel();
    }

    void SeekToLine(int k)
    {
        if (k < 0 || k >= _m.Lyrics.Count) return;
        _m.Seek(_m.Lyrics[k].At - TimeSpan.FromMilliseconds(_m.Offset));
    }

    void UpdateOffsetLabel() => _off.Text = (_m.Offset / 1000.0).ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + L.T("dtk");

    static bool RomajiOnly => Settings.Cur.LyricRomaji && Settings.Cur.RomajiOnly;

    void OnRomajiReady() => Dispatcher.BeginInvoke(() => _lastIdx = -2);   // hasil romaji kanji siap / pengaturan berubah: gambar ulang baris sekarang

    // capture audio hanya selama tab Musik terbuka, lagu main, dan lirik bersinkron ada (beban nol di luar itu)
    bool _flowOn;
    void SyncFlow(bool visible)
    {
        var s = Settings.Cur;
        bool want = visible && s.LyricFlow && s.LyricKaraoke && s.MusicLyrics && _m.Playing && _m.Lyrics.Count > 0;
        if (want && !_flowOn) { AudioFlow.I.Labeler = _m.VocalLabel; AudioFlow.I.Acquire(); _flowOn = true; }
        else if (!want && _flowOn) { AudioFlow.I.Release(); _flowOn = false; }
    }

    void BuildRecap()
    {
        _recap.Children.Clear();
        var all = Settings.Cur.PlayLogOn ? PlayLog.Load() : new List<PlayEntry>();
        var now = DateTime.Now;
        var songs = PlayStats.TopSongs(all, now, _recapDays, 4);   // baris lagu dua tingkat: 4 muat di tinggi tab
        var artists = PlayStats.TopArtists(all, now, _recapDays, 5);
        _recapHasData = songs.Count > 0;

        var root = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        var head = UI.Cols(-1, 0, 0, 0);
        bool idle = !_m.HasSession;   // tidak ada pemutar: tab ini menjadi layar kosong yang berguna (lagu terakhir, rekap, tombol buka pemutar)
        head.Children.Add(UI.T(idle ? "Tidak ada musik yang diputar" : _recapDays == 7 ? "Rekap 7 hari terakhir" : "Rekap 30 hari terakhir", 15, null, true));
        var span = UI.Btn(UI.T(_recapDays == 7 ? "30 hari" : "7 hari", 11.5, UI.Dim), () => { _recapDays = _recapDays == 7 ? 30 : 7; _recapDirty = true; Render(); }, UI.Card, 9, new Thickness(9, 3, 9, 3));
        span.Margin = new Thickness(0, 0, 6, 0);
        head.Children.Add(span.At(0, 2));
        head.Children.Add(idle
            ? UI.Btn(UI.T("Buka pemutar", 11.5, UI.Dim), OpenPlayer, UI.Card, 9, new Thickness(9, 3, 9, 3)).At(0, 3)
            : UI.Btn(UI.T("Kembali", 11.5, UI.Dim), () => { _recapOn = false; Render(); }, UI.Card, 9, new Thickness(9, 3, 9, 3)).At(0, 3));
        root.Children.Add(head);
        if (idle && all.Count > 0)
        {
            var last = all.MaxBy(e => e.T);
            var l = UI.T(L.F("Terakhir diputar: {0}", last.Artist.Length > 0 ? last.Title + " · " + last.Artist : last.Title), 12, UI.Dim);
            l.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(l);
        }
        if (idle && _recapHasData)
        {
            var c = UI.T(_recapDays == 7 ? "Rekap 7 hari terakhir" : "Rekap 30 hari terakhir", 11.5, UI.Faint);
            c.Margin = new Thickness(0, 8, 0, 0);
            root.Children.Add(c);
        }

        if (!_recapHasData)
        {
            var e = UI.T(Settings.Cur.PlayLogOn ? "Belum ada rekap. Lagu yang kamu dengarkan sedikitnya 30 detik dicatat di sini (hanya di PC ini)." : "Rekap dimatikan di Pengaturan > Musik.", 12, UI.Dim);
            e.TextWrapping = TextWrapping.Wrap; e.Margin = new Thickness(0, 12, 0, 0);
            root.Children.Add(e);
        }
        else
        {
            var cols = UI.Cols(-1, -1);
            cols.Margin = new Thickness(0, 10, 0, 0);
            cols.Children.Add(RecapList("Lagu teratas", songs, true).At(0, 0));
            var ar = RecapList("Artis teratas", artists, false); ar.Margin = new Thickness(14, 0, 0, 0);
            cols.Children.Add(ar.At(0, 1));
            root.Children.Add(cols);
        }
        _recap.Children.Add(root);
    }

    /// <summary>Buka Spotify (tautan spotify:) bila terpasang; kalau tidak, YouTube Music di peramban bawaan.</summary>
    static void OpenPlayer()
    {
        foreach (var uri in new[] { "spotify:", "https://music.youtube.com" })
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); return; }
            catch (Exception ex) { App.Log(ex); }
    }

    static StackPanel RecapList(string title, List<PlayStats.Row> rows, bool withSub)
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.T(title, 11.5, UI.Faint));
        int i = 1;
        foreach (var r in rows)
        {
            var line = UI.Cols(18, -1, 0);
            line.Margin = new Thickness(0, 5, 0, 0);
            line.Children.Add(UI.T(i++.ToString(), 12.5, UI.Accent, true));
            var name = new StackPanel();
            name.Children.Add(UI.T(r.Name, 12.5, UI.Fg));
            if (withSub && r.Sub.Length > 0) name.Children.Add(UI.T(r.Sub, 10.5, UI.Dim));
            line.Children.Add(name.At(0, 1));
            var n = UI.T(r.Plays + "×", 11.5, UI.Dim); n.Margin = new Thickness(6, 0, 0, 0);
            line.Children.Add(n.At(0, 2));
            sp.Children.Add(line);
        }
        return sp;
    }

    public override void OnShow() { _recapOn = false; _recapDirty = true; _m.Changed += OnChanged; Romaji.Ready += OnRomajiReady; Settings.Applied += OnRomajiReady; _lastIdx = -2; UpdateOffsetLabel(); _t.Start(); SyncFlow(true); OnChanged(); }
    public override void OnHide() { _m.Changed -= OnChanged; Romaji.Ready -= OnRomajiReady; Settings.Applied -= OnRomajiReady; _t.Stop(); SyncFlow(false); }

    void OnChanged() { Render(); PaintLyrics(); UpdateOffsetLabel(); }

    void OnTick()
    {
        // 30 fps saat lirik karaoke berjalan, selain itu 4 fps
        bool fast = Settings.Cur.LyricKaraoke && Settings.Cur.MusicLyrics && _m.Playing && _m.Lyrics.Count > 0 && !PowerSaver.On;   // hemat baterai: karaoke 4 kali/dtk
        var want = TimeSpan.FromMilliseconds(fast ? 33 : 250);
        if (_t.Interval != want) _t.Interval = want;
        SyncFlow(true);
        if ((DateTime.Now - _lastPoll).TotalSeconds >= 1) { _lastPoll = DateTime.Now; _m.PollTimeline(); PollVolume(); }
        if ((DateTime.Now - _lastFull).TotalMilliseconds >= 250) Render();
        PaintLyrics();
    }

    void Render()
    {
        _lastFull = DateTime.Now;
        bool has = _m.HasSession;
        if (_recapDirty && (_recapOn || !has)) { _recapDirty = false; PlayLog.Flush(); BuildRecap(); }
        _recap.Visibility = _recapOn || !has ? Visibility.Visible : Visibility.Collapsed;
        _player.Visibility = has && !_recapOn ? Visibility.Visible : Visibility.Collapsed;
        if (!has || _recapOn) return;

        _title.Text = string.IsNullOrEmpty(_m.Title) ? "—" : _m.Title;
        // album yang sama dengan judul (single) tidak ditampilkan: sudah ada di baris judul
        bool albumNew = !string.IsNullOrWhiteSpace(_m.Album) && !string.Equals(_m.Album.Trim(), _m.Title.Trim(), StringComparison.OrdinalIgnoreCase);
        _artist.Text = albumNew ? $"{_m.Artist} - {_m.Album}" : _m.Artist;
        if (!ReferenceEquals(_cover.Source, _m.Cover)) _cover.Source = _m.Cover;
        _shufBtn.Visibility = _m.Shuffle == null ? Visibility.Collapsed : Visibility.Visible;
        ((TextBlock)_shufBtn.Child).Foreground = _m.Shuffle == true ? UI.Accent : UI.Faint;
        _shufBtn.ToolTip = L.T(_m.Shuffle == true ? "Acak: menyala" : "Acak: mati");
        _play.Text = _m.Playing ? "" : "";

        var pos = _m.Position();
        _pos.Text = Fmt(pos);
        var rem = _m.Duration - pos;
        _rem.Text = _m.Duration > TimeSpan.Zero ? "-" + Fmt(rem < TimeSpan.Zero ? TimeSpan.Zero : rem) : "";
        _bar.Set(_m.Duration > TimeSpan.Zero ? pos.TotalSeconds / _m.Duration.TotalSeconds : 0);
    }

    void PaintLyrics()
    {
        // lirik dimatikan: kolom lirik hilang dan player memenuhi lebar
        bool on = Settings.Cur.MusicLyrics && _m.HasSession;
        _player.ColumnDefinitions[0].Width = on ? new GridLength(300) : new GridLength(1, GridUnitType.Star);
        _player.ColumnDefinitions[2].Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);   // kolom lirik tidak boleh memakan lebar
        _left.Width = on ? double.NaN : 400;
        _left.HorizontalAlignment = on ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        _divider.Visibility = _zone.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        _syncRow.Visibility = _m.IsAd ? Visibility.Collapsed : Visibility.Visible;
        var L = _m.Lyrics;
        _wrongT.Text = ZaxLet.L.T(L.Count > 0 ? "Lirik salah?" : "Cari ulang");
        if (L.Count == 0)
        {
            _prev.Set(""); _next.Set(""); SetRomaji("");
            _cur.Set(ZaxLet.L.T(_m.LyricState switch
            {
                "loading" => "Mencari lirik…", "retry" => "Server lirik sibuk, mencoba lagi…", "listen" => "Mendengarkan…",
                "ad" => "Iklan sedang diputar", "norec" => "Lagu tidak dikenali", "notoken" => "Isi token pengenal lagu di Pengaturan > Musik", _ => "Lirik tidak tersedia"
            }));   // L = daftar lirik di metode ini
            _cur.Paint(-1);
            _lastIdx = -2;
            return;
        }

        var (i, p) = _m.Karaoke();
        if (i != _lastIdx)
        {
            _lastIdx = i;
            // "hanya romaji": baris Jepang diganti romajinya; baris/lagu non-Jepang (Romaji.Of = null) tetap tampil apa adanya
            string T(int k) => k >= 0 && k < L.Count ? (string.IsNullOrWhiteSpace(L[k].Text) ? "♪" : RomajiOnly ? Translit.Of(L[k].Text) ?? L[k].Text : L[k].Text) : "";
            if (i < 0) { _prev.Set(""); _cur.Set("♪"); _next.Set(T(0)); SetRomaji(""); }
            else { _prev.Set(T(i - 1)); _cur.Set(T(i)); _next.Set(T(i + 1)); SetRomaji(L[i].Text); }
            _prev.Paint(-1); _next.Paint(-1);
            // baris baru naik dari bawah
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            _cur.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(240)));
        }
        _cur.Paint(i < 0 ? -1 : Settings.Cur.LyricKaraoke ? p : 1);
    }

    void SetRomaji(string line)
    {
        var r = Settings.Cur.LyricRomaji && !RomajiOnly ? Translit.Of(line) : null;   // mode hanya romaji: teks utama sudah romaji
        _rom.Text = r ?? "";
        _rom.Visibility = string.IsNullOrEmpty(r) ? Visibility.Collapsed : Visibility.Visible;
    }

    static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}

/// <summary>
/// Satu baris lirik berupa kumpulan kata (WrapPanel: kata yang tidak muat pindah ke baris bawah, tidak pernah terpotong).
/// Paint(p) mengisi kata dari kiri ke kanan: kata yang sudah dinyanyikan putih, kata yang sedang dinyanyikan terisi sebagian
/// (gradasi), sisanya redup. Waktu per kata dibagi sesuai panjang huruf.
/// </summary>
sealed class LyricRow : WrapPanel
{
    static readonly Brush Done = Brushes.White;
    readonly Brush _todo;
    readonly double _size;
    readonly List<TextBlock> _w = new();
    readonly LinearGradientBrush _wipe;
    double[] _a = Array.Empty<double>(), _b = Array.Empty<double>();
    int[] _st = Array.Empty<int>();
    string _text;

    public LyricRow(double size, double dimAlpha)
    {
        _size = size;
        Orientation = Orientation.Horizontal;
        Background = Brushes.Transparent;   // agar seluruh baris bisa diklik, bukan hanya huruf
        var t = new SolidColorBrush(Color.FromArgb((byte)(255 * dimAlpha), 255, 255, 255));
        t.Freeze();
        _todo = t;
        _wipe = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        _wipe.GradientStops.Add(new GradientStop(Colors.White, 0));
        _wipe.GradientStops.Add(new GradientStop(Colors.White, 0));
        _wipe.GradientStops.Add(new GradientStop(t.Color, 0));
        _wipe.GradientStops.Add(new GradientStop(t.Color, 1));
    }

    public void Set(string text)
    {
        text ??= "";
        if (text == _text) return;
        _text = text;
        Children.Clear();
        _w.Clear();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int total = Math.Max(1, words.Sum(x => x.Length + 1));
        _a = new double[words.Length]; _b = new double[words.Length]; _st = new int[words.Length];
        int acc = 0;
        for (int i = 0; i < words.Length; i++)
        {
            _a[i] = (double)acc / total;
            acc += words[i].Length + 1;
            _b[i] = (double)acc / total;
            var tb = new TextBlock { Text = words[i], FontSize = _size, FontWeight = FontWeights.SemiBold, Foreground = _todo, Margin = new Thickness(0, 0, _size * .3, 2) };
            _w.Add(tb);
            Children.Add(tb);
        }
    }

    /// <param name="progress">&lt;0: semua redup; 0..1: bagian baris yang sudah dinyanyikan.</param>
    public void Paint(double progress)
    {
        for (int i = 0; i < _w.Count; i++)
        {
            int st = progress < 0 || progress <= _a[i] ? 0 : progress >= _b[i] - 1e-9 ? 1 : 2;
            if (st == 2)
            {
                double wp = (progress - _a[i]) / (_b[i] - _a[i]);
                _wipe.GradientStops[1].Offset = Math.Max(0, wp - .08);
                _wipe.GradientStops[2].Offset = Math.Min(1, wp + .08);
            }
            if (st == _st[i]) continue;
            _st[i] = st;
            _w[i].Foreground = st == 1 ? Done : st == 0 ? _todo : _wipe;
        }
    }
}
