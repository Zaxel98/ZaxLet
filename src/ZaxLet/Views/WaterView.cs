using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ZaxLet;

/// <summary>Cincin besar berisi persen (tab Minum).</summary>
sealed class BigRing : Canvas
{
    readonly Path _arc = new() { StrokeThickness = 8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly TextBlock _txt = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = UI.Fg, TextAlignment = TextAlignment.Center };
    readonly double _s;

    public BigRing(double size)
    {
        _s = Width = Height = size;
        var track = new Ellipse { Width = size - 8, Height = size - 8, Stroke = UI.Track, StrokeThickness = 8 };
        SetLeft(track, 4); SetTop(track, 4);
        Children.Add(track); Children.Add(_arc);
        _txt.Width = size; SetTop(_txt, size / 2 - 14);
        Children.Add(_txt);
    }

    public void Set(double frac, Brush color)
    {
        _txt.Text = Math.Round(frac * 100) + "%";
        frac = Math.Clamp(frac, 0.005, 0.999);
        double c = _s / 2, r = (_s - 8) / 2, a = 2 * Math.PI * frac;
        var fig = new PathFigure { StartPoint = new Point(c, c - r) };
        fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, frac > .5, SweepDirection.Clockwise, true));
        var g = new PathGeometry(); g.Figures.Add(fig);
        _arc.Data = g; _arc.Stroke = color;
    }
}

public sealed class WaterView : TabView
{
    public override string Key => "water";
    public override string Glyph => "\U0001F4A7";
    public override string Label => "Minum";
    public override double H => 170;

    readonly BigRing _ring = new(96);
    readonly TextBlock _amount = new() { Foreground = UI.Fg, VerticalAlignment = VerticalAlignment.Center }, _next = UI.T("", 11.5, UI.Dim);
    readonly StackPanel _btns = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    readonly StackPanel _week = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Height = 70 };

    public WaterView()
    {
        var g = UI.Cols(110, -1, 0);
        g.Children.Add(_ring);
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0) };
        mid.Children.Add(UI.T("Minum hari ini", 12, UI.Dim));
        mid.Children.Add(_amount);
        mid.Children.Add(_next);
        mid.Children.Add(_btns);
        g.Children.Add(mid.At(0, 1));
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(UI.T("7 hari", 11, UI.Faint));
        _week.Margin = new Thickness(0, 4, 0, 0);
        right.Children.Add(_week);
        g.Children.Add(right.At(0, 2));
        Children.Add(g);
    }

    public override void OnShow() { HydrationService.I.Changed += Render; Render(); }
    public override void OnHide() => HydrationService.I.Changed -= Render;

    void Render()
    {
        var w = HydrationService.I;
        var s = Settings.Cur;
        double f = (double)w.Today / w.Goal;
        _ring.Set(f, f >= 1 ? UI.B("#FF22D35A") : UI.B("#FF38BDF8"));
        _amount.Inlines.Clear();
        _amount.Inlines.Add(new System.Windows.Documents.Run(w.Today.ToString()) { FontSize = 24, FontWeight = FontWeights.SemiBold });
        _amount.Inlines.Add(new System.Windows.Documents.Run($" / {w.Goal} ml") { FontSize = 12, Foreground = UI.Dim });
        _next.Text = !s.WaterOn ? L.T("Pengingat mati")
            : w.Today >= w.Goal ? L.T("Target tercapai 🎉")
            : w.NextReminder() is { } n ? L.F("Pengingat berikutnya {0}", n.ToString("HH.mm")) : L.T("Di luar jam aktif");

        _btns.Children.Clear();
        void Add(int ml)
        {
            var b = UI.Btn(UI.T("+" + ml, 12.5, ml == s.WaterCupMl ? Brushes.Black : UI.Fg, true), () => w.Add(ml),
                           ml == s.WaterCupMl ? UI.Accent : UI.Card, 10, new Thickness(10, 5, 10, 5));
            b.Margin = new Thickness(0, 0, 5, 0);
            _btns.Children.Add(b);
        }
        Add(s.WaterCupMl);
        foreach (var ml in new[] { 150, 330, 500 }) if (ml != s.WaterCupMl) Add(ml);
        _btns.Children.Add(UI.Btn(UI.Icon("", 12, UI.Dim), () => w.Undo(), UI.Card, 10, new Thickness(9, 5, 9, 5)));

        _week.Children.Clear();
        foreach (var (day, ml) in w.Last7())
        {
            double h = Math.Clamp((double)ml / w.Goal, 0.04, 1) * 52;
            var col = new StackPanel { Width = 15, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Bottom };
            col.Children.Add(new Border { Height = h, CornerRadius = new CornerRadius(3), Background = ml >= w.Goal ? UI.B("#FF22D35A") : day == DateTime.Today ? UI.B("#FF38BDF8") : UI.CardOn });
            var d = UI.T(day.ToString("ddd")[..1].ToUpper(), 9.5, UI.Faint);
            d.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(d);
            _week.Children.Add(col);
        }
    }
}
