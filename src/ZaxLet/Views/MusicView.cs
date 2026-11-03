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
    readonly Grid _player, _empty;
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
    int _lastIdx = -2;
    DateTime _lastFull = DateTime.MinValue;

    public MusicView()
    {
        // kosong
        _empty = new Grid { Visibility = Visibility.Collapsed };
        var es = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        es.Children.Add(UI.Icon("", 34, UI.Faint));
        es.Children.Add(new TextBlock { Text = "Tidak ada musik yang diputar", Foreground = UI.Dim, FontSize = 13, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(es);

        // kiri: sampul + judul, progres, kontrol
        _left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };   // jarak ke pembatas lirik
        var left = _left;
        var head = UI.Cols(72, -1);
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
        prog.Children.Add(_bar.At(0, 1));
        prog.Children.Add(_rem.At(0, 2));
        _bar.MouseLeftButtonDown += (_, e) =>
        {
            if (_m.Duration <= TimeSpan.Zero || _bar.ActualWidth <= 0) return;
            _m.Seek(TimeSpan.FromSeconds(_m.Duration.TotalSeconds * e.GetPosition(_bar).X / _bar.ActualWidth));
        };
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
        ctlRow.Children.Add(kids[0]);
        ctlRow.Children.Add(trio);
        left.Children.Add(ctlRow);

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
        _zone.Children.Add(_lyrics);
        _zone.Children.Add(sync);
        _divider = new Border { Width = 1, Background = UI.Track, Margin = new Thickness(0, 8, 0, 8) };

        _player = UI.Cols(300, 1, -1);
        _player.Children.Add(left);
        _player.Children.Add(_divider.At(0, 1));
        _player.Children.Add(_zone.At(0, 2));

        Children.Add(_player);
        Children.Add(_empty);
        _t.Tick += (_, _) => OnTick();
    }

    void Nudge(int deltaMs)
    {
        Settings.Cur.LyricOffsetMs = Math.Clamp(Settings.Cur.LyricOffsetMs + deltaMs, -5000, 5000);
        Settings.Save();
        UpdateOffsetLabel();
    }

    void SeekToLine(int k)
    {
        if (k < 0 || k >= _m.Lyrics.Count) return;
        _m.Seek(_m.Lyrics[k].At - TimeSpan.FromMilliseconds(Settings.Cur.LyricOffsetMs));
    }

    void UpdateOffsetLabel() => _off.Text = (Settings.Cur.LyricOffsetMs / 1000.0).ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + " dtk";

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

    public override void OnShow() { _m.Changed += OnChanged; Romaji.Ready += OnRomajiReady; Settings.Applied += OnRomajiReady; _lastIdx = -2; UpdateOffsetLabel(); _t.Start(); SyncFlow(true); OnChanged(); }
    public override void OnHide() { _m.Changed -= OnChanged; Romaji.Ready -= OnRomajiReady; Settings.Applied -= OnRomajiReady; _t.Stop(); SyncFlow(false); }

    void OnChanged() { Render(); PaintLyrics(); }

    void OnTick()
    {
        // 30 fps saat lirik karaoke berjalan, selain itu 4 fps
        bool fast = Settings.Cur.LyricKaraoke && Settings.Cur.MusicLyrics && _m.Playing && _m.Lyrics.Count > 0;
        var want = TimeSpan.FromMilliseconds(fast ? 33 : 250);
        if (_t.Interval != want) _t.Interval = want;
        SyncFlow(true);
        if ((DateTime.Now - _lastFull).TotalMilliseconds >= 250) Render();
        PaintLyrics();
    }

    void Render()
    {
        _lastFull = DateTime.Now;
        bool has = _m.HasSession;
        _player.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (!has) return;

        _title.Text = string.IsNullOrEmpty(_m.Title) ? "—" : _m.Title;
        _artist.Text = string.IsNullOrEmpty(_m.Album) ? _m.Artist : $"{_m.Artist} - {_m.Album}";
        if (!ReferenceEquals(_cover.Source, _m.Cover)) _cover.Source = _m.Cover;
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
        var L = _m.Lyrics;
        if (L.Count == 0)
        {
            _prev.Set(""); _next.Set(""); SetRomaji("");
            _cur.Set(_m.LyricState switch { "loading" => "Mencari lirik…", "retry" => "Server lirik sibuk, mencoba lagi…", _ => "Lirik tidak tersedia" });
            _cur.Paint(-1);
            _lastIdx = -2;
            return;
        }

        var (i, p) = _m.Karaoke();
        if (i != _lastIdx)
        {
            _lastIdx = i;
            // "hanya romaji": baris Jepang diganti romajinya; baris/lagu non-Jepang (Romaji.Of = null) tetap tampil apa adanya
            string T(int k) => k >= 0 && k < L.Count ? (string.IsNullOrWhiteSpace(L[k].Text) ? "♪" : RomajiOnly ? Romaji.Of(L[k].Text) ?? L[k].Text : L[k].Text) : "";
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
        var r = Settings.Cur.LyricRomaji && !RomajiOnly ? Romaji.Of(line) : null;   // mode hanya romaji: teks utama sudah romaji
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
