using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ZaxLet;

public sealed class PrayerView : TabView
{
    public override string Key => "prayer";
    public override string Glyph => "";
    public override string Label => "Sholat";
    public override bool CenterV => true;
    public override double H => 228;

    readonly Canvas _sky = new() { ClipToBounds = true };
    readonly Border _skyBox;
    readonly TextBlock _next = UI.T("", 17, null, true), _left = UI.T("", 13, null, true);
    readonly UniformGrid _tiles = new() { Rows = 1, Columns = 5, Margin = new Thickness(0, 6, 0, 0) };
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    PrayerCalc.Times _times;
    DateTime _day;

    public PrayerView()
    {
        _skyBox = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = _sky, Height = 92 };   // kurva kecil agar tab padat
        var root = UI.Rows(-1, 0);
        var skyGrid = new Grid();
        skyGrid.Children.Add(_skyBox);
        var head = UI.Cols(-1, 0);
        head.Margin = new Thickness(12, 8, 12, 0);
        head.VerticalAlignment = VerticalAlignment.Top;
        head.Children.Add(_next);
        head.Children.Add(_left.At(0, 1));
        skyGrid.Children.Add(head);
        root.Children.Add(skyGrid);
        root.Children.Add(_tiles.At(1));
        Children.Add(root);
        _sky.SizeChanged += (_, _) => DrawSky();
        _t.Tick += (_, _) => Tick();
    }

    public override void OnShow() { _times = null; _t.Start(); Tick(); }
    public override void OnHide() => _t.Stop();

    void Ensure()
    {
        if (_times != null && _day == DateTime.Today) return;
        _day = DateTime.Today;
        var s = Settings.Cur;
        _times = PrayerAlerts.For(_day);
        BuildTiles();
    }

    (string Name, DateTime At) Next(out int idx)
    {
        var now = DateTime.Now;
        var all = _times.All;
        for (idx = 0; idx < all.Length; idx++) if (all[idx].At > now) return all[idx];
        idx = 0;
        var t = PrayerAlerts.For(_day.AddDays(1));
        return t.All[0];
    }

    void BuildTiles()
    {
        _tiles.Children.Clear();
        foreach (var (name, at) in _times.All)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            bool night = name is "Maghrib" or "Isya";
            sp.Children.Add(UI.Icon(night ? "" : "", 13, name == "Subuh" ? UI.Faint : night ? UI.B("#FF93A4F5") : UI.B("#FFFFB454")));
            sp.Children.Add(new TextBlock { Text = L.T(name), FontSize = 11.5, Foreground = UI.Dim, HorizontalAlignment = HorizontalAlignment.Center, Name = null });
            sp.Children.Add(new TextBlock { Text = at.ToString("HH.mm"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = UI.Fg, HorizontalAlignment = HorizontalAlignment.Center });
            _tiles.Children.Add(new Border { Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(0, 5, 0, 5), CornerRadius = new CornerRadius(12), Background = UI.Card, Child = sp, Tag = name });
        }
    }

    void Tick()
    {
        Ensure();
        var (name, at) = Next(out int idx);
        _next.Text = $"{L.T(name)} {at:HH.mm}";
        var d = at - DateTime.Now;
        _left.Text = d.TotalHours >= 1 ? L.F("{0}j {1}m lagi", (int)d.TotalHours, d.Minutes) : L.F("{0}m {1}d lagi", Math.Max(0, d.Minutes), Math.Max(0, d.Seconds));
        for (int i = 0; i < _tiles.Children.Count; i++)
        {
            var b = (Border)_tiles.Children[i];
            bool on = i == idx && at.Date == DateTime.Today;
            b.Background = on ? UI.AccentBg : UI.Card;
            var time = (TextBlock)((StackPanel)b.Child).Children[1];
            time.Foreground = on ? UI.Accent : UI.Fg;
        }
        DrawSky();
    }


    // Siang: matahari menempuh terbit -> Maghrib. Malam: bulan menempuh Maghrib -> terbit berikutnya (juga setelah tengah malam).
    void DrawSky()
    {
        if (_times == null || _sky.ActualWidth < 10) return;
        double w = _sky.ActualWidth, h = _sky.ActualHeight, ground = h * .74;
        var now = DateTime.Now;
        var yest = PrayerAlerts.For(_day.AddDays(-1));
        var tom = PrayerAlerts.For(_day.AddDays(1));
        bool day = now >= _times.Sunrise && now < _times.Maghrib;
        DateTime t0, t1;
        if (day) (t0, t1) = (_times.Sunrise, _times.Maghrib);
        else if (now >= _times.Maghrib) (t0, t1) = (_times.Maghrib, tom.Sunrise);
        else (t0, t1) = (yest.Maghrib, _times.Sunrise);

        var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        if (day)
        {
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x6A, 0x5A, 0xE0), 0));
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0xE2, 0x86, 0x9E), .5));
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0xA4, 0x5E), .74));
        }
        else
        {
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x08, 0x0C, 0x24), 0));
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x1B, 0x2A, 0x5E), .74));
        }
        _skyBox.Background = bg;
        _sky.Children.Clear();
        if (!day)   // bintang tetap (posisi pseudo-acak yang sama tiap gambar)
            for (int i = 0; i < 16; i++)
            {
                double fx = (i * 0.6180339 + .13) % 1, fy = (i * 0.3819660 + .21) % 1;
                var star = new Ellipse { Width = i % 3 == 0 ? 2.2 : 1.5, Height = i % 3 == 0 ? 2.2 : 1.5, Fill = Brushes.White, Opacity = .45 + .4 * ((i * 7) % 5) / 5 };
                Canvas.SetLeft(star, 8 + fx * (w - 16)); Canvas.SetTop(star, 6 + fy * (ground - 26));
                _sky.Children.Add(star);
            }
        _sky.Children.Add(new Rectangle { Width = w, Height = h - ground, Fill = new SolidColorBrush(day ? Color.FromRgb(0x8A, 0x4B, 0x2E) : Color.FromRgb(0x0A, 0x0E, 0x1E)), Margin = new Thickness(0, ground, 0, 0) });

        double x0 = 14, x1 = w - 14, amp = ground - 22, span = (t1 - t0).TotalSeconds;
        Point P(DateTime t)
        {
            double f = Math.Clamp((t - t0).TotalSeconds / span, 0, 1);
            return new Point(x0 + (x1 - x0) * f, ground - amp * Math.Sin(Math.PI * f));
        }
        var line = new Polyline { Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        for (int i = 0; i <= 60; i++) line.Points.Add(P(t0.AddSeconds(span * i / 60)));
        _sky.Children.Add(line);
        foreach (var set in new[] { yest, _times, tom })
            foreach (var (_, at) in set.All)
            {
                if (at < t0.AddMinutes(-1) || at > t1.AddMinutes(1)) continue;
                var p = P(at);
                var dot = new Ellipse { Width = 7, Height = 7, Fill = Brushes.White };
                Canvas.SetLeft(dot, p.X - 3.5); Canvas.SetTop(dot, p.Y - 3.5);
                _sky.Children.Add(dot);
            }

        var sp = P(now);
        UIElement body;
        if (day)
        {
            var sun = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x60)) };
            Canvas.SetLeft(sun, sp.X - 9); Canvas.SetTop(sun, sp.Y - 9);
            body = sun;
        }
        else
        {
            // bulan sabit: lingkaran terang dikurangi lingkaran yang digeser
            var crescent = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(0, 0), 9, 9), new EllipseGeometry(new Point(4.5, -3), 8, 8));
            body = new System.Windows.Shapes.Path { Data = crescent, Fill = new SolidColorBrush(Color.FromRgb(0xE6, 0xEC, 0xFF)), RenderTransform = new TranslateTransform(sp.X, sp.Y) };
        }
        _sky.Children.Add(body);
    }
}
