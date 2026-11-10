using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZaxLet;

public sealed class TimerView : TabView
{
    public override string Key => "timer";
    public override string Glyph => "";
    public override string Label => "Timer";
    public override bool CenterV => false;
    public override double H => 206;

    readonly TimerService _t = TimerService.I;
    readonly TextBlock _time = UI.T("25:00", 34, null, true), _sub = UI.T("", 12, UI.Dim);
    readonly StackPanel _modes = new() { Orientation = Orientation.Horizontal }, _presets = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _go = UI.Icon("", 20);

    readonly TextBlock _focusT = UI.T("", 12.5);
    Border _focus;

    public TimerView()
    {
        var root = UI.Rows(0, -1, 0);
        foreach (var (m, name) in new[] { (TMode.Timer, "Timer"), (TMode.Pomodoro, "Pomodoro"), (TMode.Stopwatch, "Stopwatch") })
        {
            var mode = m;
            var b = UI.Btn(UI.T(name, 12.5), () => { _t.SetMode(mode); Render(); }, UI.Card, 12, new Thickness(12, 4, 12, 4));
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Tag = m;
            _modes.Children.Add(b);
        }
        _focus = UI.Btn(_focusT, () => { var f = FocusService.I; if (f.Manual) f.Stop(); else f.Start(Settings.Cur.FocusMin); }, UI.Card, 12, new Thickness(12, 4, 12, 4));
        _focus.Margin = new Thickness(10, 0, 0, 0);
        _focus.ToolTip = L.T("Mode Fokus: notifikasi HP ditahan, kode OTP tetap lewat. Lamanya diatur di Pengaturan > Pengingat");
        _modes.Children.Add(_focus);
        root.Children.Add(_modes);

        var mid = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _time.HorizontalAlignment = HorizontalAlignment.Center;
        _sub.HorizontalAlignment = HorizontalAlignment.Center;
        mid.Children.Add(_time);
        mid.Children.Add(_sub);
        root.Children.Add(mid.At(1));

        var ctl = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        ctl.Children.Add(UI.IconBtn("", () => { _t.Reset(); }, 16));
        ctl.Children.Add(UI.Btn(_go, () => { if (_t.Running) _t.Pause(); else _t.Start(); }, UI.CardOn, 999, new Thickness(11)));
        ctl.Margin = new Thickness(0, 0, 0, 2);
        var bottom = new StackPanel();
        _presets.Margin = new Thickness(0, 0, 0, 6);
        bottom.Children.Add(_presets);
        bottom.Children.Add(ctl);
        root.Children.Add(bottom.At(2));
        Children.Add(root);

        foreach (var min in new[] { 5, 10, 15, 25, 45, 60 })
        {
            var mm = min;
            var b = UI.Btn(UI.T($"{min}m", 11.5, UI.Dim), () => { _t.SetMode(TMode.Timer); _t.SetTotal(TimeSpan.FromMinutes(mm)); Render(); }, UI.Card, 10, new Thickness(8, 3, 8, 3));
            b.Margin = new Thickness(2, 0, 2, 0);
            _presets.Children.Add(b);
        }
    }

    public override void OnShow() { _t.Tick += OnTick; FocusService.I.Changed += OnTick; Render(); }
    public override void OnHide() { _t.Tick -= OnTick; FocusService.I.Changed -= OnTick; }
    void OnTick() => Render();

    void Render()
    {
        _time.Text = UI.Clock(_t.Value);
        _go.Text = _t.Running ? "" : "";
        _sub.Text = _t.Mode switch
        {
            TMode.Pomodoro => L.T(_t.PomoBreak ? "Istirahat " : "Fokus ") + L.F("· {0}/{1} menit", Settings.Cur.PomoWork, Settings.Cur.PomoBreak),
            TMode.Timer => L.T("Hitung mundur"),
            _ => L.T("Hitung maju")
        };
        _presets.Visibility = _t.Mode == TMode.Timer ? Visibility.Visible : Visibility.Hidden;
        foreach (Border b in _modes.Children) if (b.Tag is TMode tm) b.Background = tm == _t.Mode ? UI.CardOn : UI.Card;
        var f = FocusService.I;
        _focusT.Text = f.Active
            ? L.T("Fokus") + " " + UI.Clock(f.Left) + (f.HeldCount > 0 ? " · " + L.F("{0} tertahan", f.HeldCount) : "")
            : L.T("Mode Fokus");
        _focus.Background = f.Active ? UI.AccentBg : UI.Card;
    }
}
