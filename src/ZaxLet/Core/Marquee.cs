using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZaxLet;

/// <summary>Single line of text: centered when it fits, scrolling like a marquee when longer than its width.</summary>
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

    /// <summary>Line duration (seconds) for lyrics: long text scrolls once over that duration and stops at the end. 0 = scroll back and forth continuously.</summary>
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
            // run once, stop at the end (do not return to start); finish slightly before the next line
            var once = new DoubleAnimation(0, -dist, TimeSpan.FromSeconds(Math.Max(1, PaceSeconds * 0.85 - 0.5))) { BeginTime = TimeSpan.FromMilliseconds(500), FillBehavior = FillBehavior.HoldEnd };   // 500 ms pause so the first word can be read
            Timeline.SetDesiredFrameRate(once, 30);
            _x.BeginAnimation(TranslateTransform.XProperty, once);
            return;
        }
        var run = TimeSpan.FromSeconds(Math.Max(2, dist / 38));   // ~38 px per second
        var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));              // initial pause so it can be read
        a.KeyFrames.Add(new LinearDoubleKeyFrame(-dist, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2) + run)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(-dist, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.0) + run)));   // pause at the end
        Timeline.SetDesiredFrameRate(a, 30);
        _x.BeginAnimation(TranslateTransform.XProperty, a);
    }

    // measure text without width limit and give full width during Arrange so Grid does not clip the end of the sentence before scrolling
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
