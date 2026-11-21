using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZaxLet;

/// <summary>Satu baris teks: dipusatkan bila muat, berjalan seperti running text bila lebih panjang dari lebarnya.</summary>
public sealed class Marquee : Grid
{
    readonly TextBlock _t = new() { FontSize = 12.5, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TranslateTransform _x = new();

    public Marquee()
    {
        ClipToBounds = true;
        _t.RenderTransform = _x;
        Children.Add(_t);
        SizeChanged += (_, _) => Restart();
    }

    public string Text
    {
        get => _t.Text;
        set { if (_t.Text == value) return; _t.Text = value; Restart(); }
    }

    public Brush Foreground { get => _t.Foreground; set => _t.Foreground = value; }

    /// <summary>Lama baris (detik) untuk lirik: teks panjang digeser sekali sepanjang lama itu dan berhenti di ujung. 0 = berjalan bolak-balik terus.</summary>
    public double PaceSeconds { get; set; }

    void Restart()
    {
        _x.BeginAnimation(TranslateTransform.XProperty, null);
        _x.X = 0;
        _t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = _t.DesiredSize.Width, avail = ActualWidth;
        if (avail <= 0 || w <= avail + 1) { _t.HorizontalAlignment = HorizontalAlignment.Center; return; }
        _t.HorizontalAlignment = HorizontalAlignment.Left;
        double dist = w - avail + 24;
        if (PaceSeconds > 0)
        {
            // sekali jalan, berhenti di ujung (tidak kembali ke awal); selesai sedikit sebelum baris berikutnya
            var once = new DoubleAnimation(0, -dist, TimeSpan.FromSeconds(Math.Max(1, PaceSeconds * 0.85))) { FillBehavior = FillBehavior.HoldEnd };
            Timeline.SetDesiredFrameRate(once, 30);
            _x.BeginAnimation(TranslateTransform.XProperty, once);
            return;
        }
        var run = TimeSpan.FromSeconds(Math.Max(2, dist / 38));   // ~38 px per detik
        var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));              // jeda awal supaya sempat terbaca
        a.KeyFrames.Add(new LinearDoubleKeyFrame(-dist, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2) + run)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(-dist, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.0) + run)));   // jeda di ujung
        Timeline.SetDesiredFrameRate(a, 30);
        _x.BeginAnimation(TranslateTransform.XProperty, a);
    }

    // ukur teks tanpa batas lebar dan beri lebar penuh saat Arrange supaya Grid tidak memotong ujung kalimat sebelum digeser
    protected override Size MeasureOverride(Size constraint)
    {
        _t.Measure(new Size(double.PositiveInfinity, constraint.Height));
        return new Size(
            double.IsInfinity(constraint.Width) ? _t.DesiredSize.Width : Math.Min(_t.DesiredSize.Width, constraint.Width),
            double.IsInfinity(constraint.Height) ? _t.DesiredSize.Height : Math.Min(_t.DesiredSize.Height, constraint.Height));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        _t.Arrange(new Rect(0, 0, Math.Max(arrangeSize.Width, _t.DesiredSize.Width), arrangeSize.Height));
        return arrangeSize;
    }
}
