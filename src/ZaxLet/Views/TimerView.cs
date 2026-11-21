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
    readonly StackPanel _remList = new();
    readonly TextBlock _go = UI.Icon("", 20);

    public TimerView()
    {
        var cols = UI.Cols(-1, 200);
        var root = UI.Rows(0, -1, 0);
        foreach (var (m, name) in new[] { (TMode.Timer, "Timer"), (TMode.Stopwatch, "Stopwatch") })
        {
            var mode = m;
            var b = UI.Btn(UI.T(name, 12.5), () => { _t.SetMode(mode); Render(); }, UI.Card, 12, new Thickness(12, 4, 12, 4));
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Tag = m;
            _modes.Children.Add(b);
        }
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

        var remPanel = new StackPanel { Margin = new Thickness(12, 2, 0, 0) };
        remPanel.Children.Add(UI.T("Pengingat", 11.5, UI.Dim, true));
        _remList.Margin = new Thickness(0, 6, 0, 0);
        remPanel.Children.Add(_remList);

        cols.Children.Add(root.At(0, 0));
        cols.Children.Add(remPanel.At(0, 1));
        Children.Add(cols);

        foreach (var min in new[] { 5, 10, 15, 25, 45, 60 })
        {
            var mm = min;
            var b = UI.Btn(UI.T($"{min}m", 11.5, UI.Dim), () => { _t.SetMode(TMode.Timer); _t.SetTotal(TimeSpan.FromMinutes(mm)); Render(); }, UI.Card, 10, new Thickness(8, 3, 8, 3));
            b.Margin = new Thickness(2, 0, 2, 0);
            _presets.Children.Add(b);
        }
    }

    public override void OnShow() { _t.Tick += OnTick; ReminderService.I.Changed += OnTick; Render(); }
    public override void OnHide() { _t.Tick -= OnTick; ReminderService.I.Changed -= OnTick; }
    void OnTick() => Render();

    void Render()
    {
        _time.Text = UI.Clock(_t.Value);
        _go.Text = _t.Running ? "" : "";
        _sub.Text = _t.Mode switch
        {
            TMode.Timer => L.T("Hitung mundur"),
            _ => L.T("Hitung maju")
        };
        _presets.Visibility = _t.Mode == TMode.Timer ? Visibility.Visible : Visibility.Hidden;
        foreach (Border b in _modes.Children) if (b.Tag is TMode tm) b.Background = tm == _t.Mode ? UI.CardOn : UI.Card;
        RenderReminders();
    }

    void RenderReminders()
    {
        _remList.Children.Clear();
        var items = ReminderService.I.Pending;
        if (items.Count == 0)
        {
            _remList.Children.Add(UI.T("Kosong · ingat 15:30 rapat", 11, UI.Faint));
            return;
        }
        const int cap = 3;
        for (int i = 0; i < Math.Min(items.Count, cap); i++)
        {
            var r = items[i];
            var row = UI.Cols(0, -1, 0);
            row.Margin = new Thickness(0, 0, 0, 4);
            var at = UI.T(r.DueAt.ToString("HH.mm") + " ", 11.5, UI.Accent, true);
            var lbl = UI.T(r.Label, 11.5);
            lbl.Margin = new Thickness(0, 0, 4, 0);
            var del = UI.Btn(UI.T("×", 11.5, UI.Dim), () => ReminderService.I.Remove(r.Id), UI.Card, 6, new Thickness(5, 1, 5, 1));
            del.ToolTip = L.T("Hapus pengingat");
            row.Children.Add(at.At(0, 0));
            row.Children.Add(lbl.At(0, 1));
            row.Children.Add(del.At(0, 2));
            _remList.Children.Add(row);
        }
        if (items.Count > cap)
            _remList.Children.Add(UI.T(L.F("+{0} lagi", items.Count - cap), 11, UI.Faint));
    }
}
