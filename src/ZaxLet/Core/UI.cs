using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZaxLet;

/// <summary>Pembangun kontrol kecil supaya tampilan tab cukup ditulis dalam kode, tanpa XAML per tab.</summary>
public static class UI
{
    public static Brush B(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        b.Freeze();
        return b;
    }

    public static readonly Brush Fg = B("#FFFFFFFF"), Dim = B("#A6FFFFFF"), Faint = B("#59FFFFFF"),
        Card = B("#1AFFFFFF"), CardOn = B("#33FFFFFF"),
        Bad = B("#FFFF5C5C"), Warn = B("#FFFFB454"), Good = B("#FF34D399"), Track = B("#26FFFFFF");

    // warna aksen bisa diganti dari Pengaturan; dibaca saat tampilan digambar ulang
    public static Brush Accent { get; private set; } = B("#FF2DD4BF");
    public static Brush AccentBg { get; private set; } = B("#2E2DD4BF");
    public static readonly (string Key, string Label, string Hex)[] Accents =
    {
        ("teal", "Teal", "2DD4BF"), ("blue", "Biru", "60A5FA"), ("purple", "Ungu", "A78BFA"),
        ("pink", "Pink", "F472B6"), ("orange", "Oranye", "FB923C"), ("white", "Putih", "FFFFFF"),
    };

    public static void SetAccent(string key)
    {
        var hex = Array.Find(Accents, a => a.Key == key).Hex ?? "2DD4BF";
        Accent = B("#FF" + hex);
        AccentBg = B("#2E" + hex);
    }

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static TextBlock T(string s, double size = 13, Brush fg = null, bool bold = false) => new()
    {
        Text = s, FontSize = size, Foreground = fg ?? Fg,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };

    /// <summary>Lebar teks dalam DIP untuk font UI (dipakai agar komponen idle punya lebar tetap).</summary>
    public static double TextWidth(string s, double size, FontWeight weight) =>
        new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White, 1.0).WidthIncludingTrailingWhitespace;

    public static TextBlock Icon(string glyph, double size = 16, Brush fg = null) => new()
    {
        Text = glyph, FontFamily = glyph.Length > 0 && char.IsSurrogate(glyph[0]) ? new FontFamily("Segoe UI Symbol") : IconFont, FontSize = size, Foreground = fg ?? Fg,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };

    public static Border Btn(UIElement content, Action click, Brush bg = null, double radius = 10, Thickness? pad = null)
    {
        var b = new Border
        {
            Child = content, Background = bg ?? Card, CornerRadius = new CornerRadius(radius),
            Padding = pad ?? new Thickness(10, 6, 10, 6), Cursor = Cursors.Hand
        };
        // umpan balik halus: sedikit meredup saat disorot, menyusut sebentar saat ditekan
        var sc = new ScaleTransform(1, 1);
        b.RenderTransform = sc;
        b.RenderTransformOrigin = new Point(.5, .5);
        void Scale(double to, int ms)
        {
            var a = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        b.MouseEnter += (_, _) => b.Opacity = .8;
        b.MouseLeave += (_, _) => { b.Opacity = 1; Scale(1, 140); };
        b.PreviewMouseLeftButtonDown += (_, _) => Scale(.93, 70);
        b.MouseLeftButtonUp += (_, _) => { Scale(1, 160); click(); };
        return b;
    }

    public static Border IconBtn(string glyph, Action click, double size = 18, Brush bg = null) =>
        Btn(Icon(glyph, size), click, bg ?? Brushes.Transparent, 999, new Thickness(8));

    public static Grid Cols(params double[] widths)
    {
        var g = new Grid();
        foreach (var w in widths)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(-w, GridUnitType.Star) : w == 0 ? GridLength.Auto : new GridLength(w) });
        return g;
    }

    public static Grid Rows(params double[] heights)
    {
        var g = new Grid();
        foreach (var h in heights)
            g.RowDefinitions.Add(new RowDefinition { Height = h < 0 ? new GridLength(-h, GridUnitType.Star) : h == 0 ? GridLength.Auto : new GridLength(h) });
        return g;
    }

    public static T At<T>(this T el, int row = 0, int col = 0, int colSpan = 1) where T : UIElement
    {
        Grid.SetRow(el, row); Grid.SetColumn(el, col); Grid.SetColumnSpan(el, colSpan);
        return el;
    }

    public static string Span(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "0m";
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{Math.Max(1, (int)t.TotalMinutes)}m";
    }

    public static string Tok(long n) =>
        n >= 1_000_000_000 ? (n / 1e9).ToString("0.##") + "B" :
        n >= 1_000_000 ? (n / 1e6).ToString("0.##") + "M" :
        n >= 1_000 ? (n / 1e3).ToString("0.#") + "K" : n.ToString();

    public static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}

/// <summary>Bar progres tipis.</summary>
public sealed class Bar : Grid
{
    readonly Border _fill;
    double _f;

    public Bar(double h = 4)
    {
        Height = h;
        Children.Add(new Border { Background = UI.Track, CornerRadius = new CornerRadius(h / 2) });
        _fill = new Border { Background = UI.Fg, CornerRadius = new CornerRadius(h / 2), HorizontalAlignment = HorizontalAlignment.Left };
        Children.Add(_fill);
        SizeChanged += (_, _) => Apply();
    }

    public void Set(double frac, Brush color = null)
    {
        _f = Math.Clamp(double.IsNaN(frac) ? 0 : frac, 0, 1);
        if (color != null) _fill.Background = color;
        Apply();
    }

    void Apply() => _fill.Width = ActualWidth * _f;
}

/// <summary>Dasar semua tab: ukuran yang diinginkan + hook tampil/sembunyi (polling hanya saat terlihat).</summary>
public abstract class TabView : Grid
{
    public abstract string Key { get; }
    public abstract string Glyph { get; }
    public abstract string Label { get; }
    public virtual double H => 190;
    public virtual bool CenterV => true;   // tab dipaksa setinggi tab lain; true = isi dipusatkan, false = isi mengisi tinggi
    public virtual void OnShow() { }
    public virtual void OnHide() { }
    protected TabView() { Margin = new Thickness(18, 12, 18, 4); }
}

/// <summary>Cincin kemajuan kecil (donat) berhuruf di tengah, seperti pada bar BrimDeck.</summary>
public sealed class RingBadge : Canvas
{
    readonly System.Windows.Shapes.Path _arc = new() { StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };

    public RingBadge(string letter, double size = 22)
    {
        Width = Height = size;
        var track = new System.Windows.Shapes.Ellipse { Width = size - 2.6, Height = size - 2.6, Stroke = UI.Track, StrokeThickness = 2.6 };
        SetLeft(track, 1.3); SetTop(track, 1.3);
        Children.Add(track);
        Children.Add(_arc);
        var t = new TextBlock { Text = letter, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = UI.Fg, Width = size, TextAlignment = TextAlignment.Center };
        SetTop(t, (size - 12) / 2);
        Children.Add(t);
    }

    public void Set(double frac, Brush color)
    {
        frac = Math.Clamp(double.IsNaN(frac) ? 0 : frac, 0.02, 0.999);
        double c = Width / 2, r = (Width - 2.6) / 2, a = 2 * Math.PI * frac;
        var fig = new PathFigure { StartPoint = new Point(c, c - r) };
        fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, frac > .5, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        _arc.Data = g;
        _arc.Stroke = color;
    }
}

/// <summary>Grafik garis mini (sparkline) untuk riwayat CPU / jaringan.</summary>
public sealed class Spark : Canvas
{
    readonly System.Windows.Shapes.Polyline _line = new() { StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round };
    readonly System.Collections.Generic.Queue<double> _v = new();
    readonly int _n;
    readonly double? _fixedMax;

    public Spark(double w, double h, Brush stroke, int n = 40, double? fixedMax = null)
    {
        Width = w; Height = h; _n = n; _fixedMax = fixedMax;
        _line.Stroke = stroke;
        Children.Add(_line);
    }

    public void Push(double v)
    {
        _v.Enqueue(v);
        while (_v.Count > _n) _v.Dequeue();
        double max = _fixedMax ?? Math.Max(1e-6, System.Linq.Enumerable.Max(_v));
        var pts = new PointCollection();
        int i = 0;
        foreach (var x in _v) pts.Add(new Point(i++ * (Width / (_n - 1)), Height - 1 - Math.Min(1, x / max) * (Height - 2)));
        _line.Points = pts;
    }
}

/// <summary>Ikon bergambar: baterai berpetir, logo penyedia AI.</summary>
public static class Icons
{
    public static readonly Brush BatGood = UI.B("#FF22D35A"), BatCharge = UI.B("#FF3B82F6");

    public static Canvas Battery(double p, bool charging)
    {
        p = Math.Clamp(p, 0, 1);
        var c = new Canvas { Width = 27, Height = 14 };
        Brush fill = charging ? BatCharge : p <= .2 ? UI.Bad : BatGood;
        var line = UI.B("#D9FFFFFF");
        c.Children.Add(new Border { Width = 24, Height = 14, CornerRadius = new CornerRadius(4), BorderBrush = line, BorderThickness = new Thickness(1.4) });
        var bar = new Border { Width = Math.Max(2.5, 17.5 * p), Height = 8, CornerRadius = new CornerRadius(2), Background = fill };
        Canvas.SetLeft(bar, 3); Canvas.SetTop(bar, 3);
        c.Children.Add(bar);
        var nub = new Border { Width = 2.4, Height = 6, CornerRadius = new CornerRadius(0, 2, 2, 0), Background = line };
        Canvas.SetLeft(nub, 24.6); Canvas.SetTop(nub, 4);
        c.Children.Add(nub);
        if (charging)
        {
            var bolt = UI.Icon("", 9, Brushes.White);
            Canvas.SetLeft(bolt, 8.5); Canvas.SetTop(bolt, 1.8);
            c.Children.Add(bolt);
        }
        return c;
    }

    /// <summary>Asterisk koral khas Claude.</summary>
    public static UIElement ClaudeLogo(double s)
    {
        var c = new Canvas { Width = s, Height = s };
        var col = UI.B("#FFD97757");
        double r = s * .44, m = s / 2;
        foreach (var deg in new[] { 0, 45, 90, 135 })
        {
            double a = deg * Math.PI / 180;
            c.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = m - r * Math.Cos(a), Y1 = m - r * Math.Sin(a), X2 = m + r * Math.Cos(a), Y2 = m + r * Math.Sin(a),
                Stroke = col, StrokeThickness = s * .17, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }
        return c;
    }

    /// <summary>Kotak hijau dengan prompt terminal untuk Codex.</summary>
    public static UIElement CodexLogo(double s) => new Border
    {
        Width = s, Height = s, CornerRadius = new CornerRadius(s * .3), Background = UI.B("#FF10A37F"),
        Child = UI.Icon("", s * .62, Brushes.White)
    };
}
