using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

public sealed class EyeView : TabView
{
    public override string Key => "eye";
    public override string Glyph => "";
    public override string Label => "Mata";
    public override double H => 150;

    readonly TextBlock _time = UI.T("", 32, null, true), _sub = UI.T("", 12, UI.Dim);
    readonly TextBlock _toggleText = UI.T("", 12.5, null, true);
    readonly Border _toggle;
    readonly StackPanel _presets = new() { Orientation = Orientation.Horizontal };
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };

    public EyeView()
    {
        var g = UI.Cols(-1, 0);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(UI.T("Istirahat mata", 15, null, true));
        left.Children.Add(_sub);
        _time.Margin = new Thickness(0, 6, 0, 6);
        left.Children.Add(_time);
        foreach (var min in new[] { 15, 20, 30, 45 })
        {
            var m = min;
            var b = UI.Btn(UI.T($"{min}m", 11.5, UI.Dim), () => { Settings.Cur.EyeMinutes = m; Settings.Save(); EyeService.I.Apply(); Render(); }, UI.Card, 10, new Thickness(8, 3, 8, 3));
            b.Margin = new Thickness(0, 0, 4, 0);
            b.Tag = m;
            _presets.Children.Add(b);
        }
        left.Children.Add(_presets);
        g.Children.Add(left);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        _toggle = UI.Btn(_toggleText, () => { Settings.Cur.EyeOn = !Settings.Cur.EyeOn; Settings.Save(); EyeService.I.Apply(); Render(); }, UI.Card, 12, new Thickness(16, 8, 16, 8));
        right.Children.Add(_toggle);
        var now = UI.Btn(UI.T("Istirahat sekarang", 12, UI.Dim), () => EyeService.I.BreakNow(), UI.Card, 12, new Thickness(12, 6, 12, 6));
        now.Margin = new Thickness(0, 8, 0, 0);
        right.Children.Add(now);
        g.Children.Add(right.At(0, 1));
        Children.Add(g);
        _t.Tick += (_, _) => Render();
    }

    public override void OnShow() { _t.Start(); Render(); }
    public override void OnHide() => _t.Stop();

    void Render()
    {
        var on = Settings.Cur.EyeOn;
        _toggleText.Text = on ? "Aktif" : "Nonaktif";
        _toggle.Background = on ? UI.AccentBg : UI.Card;
        _toggleText.Foreground = on ? UI.Accent : UI.Fg;
        _sub.Text = $"Pengingat tiap {Settings.Cur.EyeMinutes} menit · lihat jauh 20 detik";
        _time.Text = on ? UI.Clock(EyeService.I.Next - DateTime.Now < TimeSpan.Zero ? TimeSpan.Zero : EyeService.I.Next - DateTime.Now) : "--:--";
        foreach (Border b in _presets.Children) b.Background = (int)b.Tag == Settings.Cur.EyeMinutes ? UI.CardOn : UI.Card;
    }
}
