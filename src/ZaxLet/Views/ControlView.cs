using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>Panel kontrol: wadah semua jenis kontrol. Sekarang berisi mixer volume per aplikasi (Spotify, Discord, game terpisah).</summary>
public sealed class ControlView : TabView
{
    public override string Key => "ctrl";
    public override string Glyph => "";
    public override string Label => "Control Panel";
    public override bool CenterV => false;

    readonly StackPanel _list = new();
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    bool _drag;

    public ControlView()
    {
        var root = UI.Rows(0, -1);
        root.Children.Add(UI.T("Volume aplikasi", 15, null, true));
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list, Margin = new Thickness(0, 8, 0, 0) }.At(1));
        Children.Add(root);
        _t.Tick += (_, _) => { if (!_drag) Render(); };   // baca ulang berkala: aplikasi muncul/hilang, volume diubah dari tempat lain
    }

    public override void OnShow() { Render(); _t.Start(); }
    public override void OnHide() => _t.Stop();

    void Render()
    {
        _list.Children.Clear();
        var rows = AppMixer.List();
        if (rows.Count == 0) { _list.Children.Add(UI.T("Belum ada aplikasi yang mengeluarkan suara.", 12, UI.Dim)); return; }
        foreach (var r in rows)
        {
            var name = r.Name;
            var (title, icon) = AppInfo.Get(name, r.Path);
            var row = UI.Cols(26, 130, -1, 40);
            row.Margin = new Thickness(0, 3, 0, 3);
            if (icon != null) row.Children.Add(new Image { Source = icon, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left });
            row.Children.Add(UI.T(title, 12.5).At(0, 1));
            var bar = new Bar(4) { Margin = new Thickness(0, 8, 0, 8) };
            bar.Set(r.Mute ? 0 : r.Vol);
            var pct = UI.T(r.Mute ? L.T("Bisu") : Math.Round(r.Vol * 100) + "%", 12, UI.Dim);
            pct.TextAlignment = TextAlignment.Right;
            var hit = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 8, 0) };
            hit.Children.Add(bar);
            var last = DateTime.MinValue;
            void From(MouseEventArgs e, bool force)
            {
                if (bar.ActualWidth <= 0 || !force && (DateTime.Now - last).TotalMilliseconds < 60) return;
                last = DateTime.Now;
                double f = Math.Clamp(e.GetPosition(bar).X / bar.ActualWidth, 0, 1);
                AppMixer.Set(name, (float)f);
                bar.Set(f); pct.Text = Math.Round(f * 100) + "%";
            }
            hit.MouseLeftButtonDown += (_, e) => { _drag = true; hit.CaptureMouse(); From(e, true); };
            hit.MouseMove += (_, e) => { if (hit.IsMouseCaptured) From(e, false); };
            hit.MouseLeftButtonUp += (_, e) => { if (hit.IsMouseCaptured) { From(e, true); hit.ReleaseMouseCapture(); } _drag = false; };
            row.Children.Add(hit.At(0, 2));
            row.Children.Add(pct.At(0, 3));
            _list.Children.Add(row);
        }
    }
}
