using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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

    static readonly Style FocusRing = MakeFocusRing();
    static Style MakeFocusRing()
    {
        var r = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
        r.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.White);
        r.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5);
        r.SetValue(System.Windows.Shapes.Rectangle.RadiusXProperty, 5.0);
        r.SetValue(System.Windows.Shapes.Rectangle.RadiusYProperty, 5.0);
        var st = new Style();
        st.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate { VisualTree = r }));
        return st;
    }

    sealed class VolumeSlider : Grid
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);
        sealed class Peer : FrameworkElementAutomationPeer
        {
            public Peer(VolumeSlider o) : base(o) { }
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
            protected override string GetClassNameCore() => "Slider";
        }
    }

    sealed class RowUI
    {
        public Bar Bar;
        public TextBlock Pct;
        public float Vol;
        public bool Mute;

        public void Update(float vol, bool mute)
        {
            Vol = vol;
            Mute = mute;
            Bar.Set(mute ? 0 : vol);
            Pct.Text = mute ? L.T("Bisu") : Math.Round(vol * 100) + "%";
        }
    }

    readonly StackPanel _list = new();
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    readonly List<RowUI> _rowUIs = new();
    List<AppMixer.Row> _lastRows;
    AppMixer.DragHandle _dragHandle;
    bool _drag;

    public ControlView()
    {
        var root = UI.Rows(0, 0, -1);
        root.Children.Add(UI.T("Volume aplikasi", 15, null, true));
        root.Children.Add(UI.T(L.T("Hanya perangkat keluaran suara utama"), 11, UI.Dim).At(1));
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list, Margin = new Thickness(0, 8, 0, 0) }.At(2));
        Children.Add(root);
        _t.Tick += (_, _) => { if (!_drag) Render(); };   // baca ulang berkala: aplikasi muncul/hilang, volume diubah dari tempat lain
    }

    public override void OnShow() { Render(); _t.Start(); }
    public override void OnHide() { _t.Stop(); EndDrag(); }

    void EndDrag()
    {
        _drag = false;
        _dragHandle?.Dispose();
        _dragHandle = null;
    }

    void Render()
    {
        var rows = AppMixer.List();
        if (AppMixer.SameApps(_lastRows, rows))
        {
            for (int i = 0; i < _rowUIs.Count; i++)
                _rowUIs[i].Update(rows[i].Vol, rows[i].Mute);
            _lastRows = rows;
            return;
        }
        _lastRows = rows;
        _rowUIs.Clear();
        _list.Children.Clear();
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
            var pct = UI.T("", 12, UI.Dim);
            pct.TextAlignment = TextAlignment.Right;
            pct.Cursor = Cursors.Hand;
            pct.Background = Brushes.Transparent;
            var rui = new RowUI { Bar = bar, Pct = pct };
            rui.Update(r.Vol, r.Mute);
            _rowUIs.Add(rui);

            pct.MouseLeftButtonDown += (_, e) =>
            {
                bool nextMute = !rui.Mute;
                AppMixer.SetMute(name, nextMute);
                rui.Update(rui.Vol, nextMute);
                e.Handled = true;
            };

            var hit = new VolumeSlider
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(4, 0, 8, 0),
                Focusable = true,
                FocusVisualStyle = FocusRing
            };
            AutomationProperties.SetName(hit, title);
            hit.Children.Add(bar);

            var last = DateTime.MinValue;
            void From(MouseEventArgs e, bool force)
            {
                if (bar.ActualWidth <= 0 || !force && (DateTime.Now - last).TotalMilliseconds < 60) return;
                last = DateTime.Now;
                float f = (float)Math.Clamp(e.GetPosition(bar).X / bar.ActualWidth, 0, 1);
                if (_dragHandle != null) _dragHandle.Set(f);
                else AppMixer.Set(name, f);
                rui.Update(f, f <= 0 && rui.Mute);
            }
            hit.MouseLeftButtonDown += (_, e) =>
            {
                _drag = true;
                _dragHandle?.Dispose();
                _dragHandle = AppMixer.BeginDrag(name);
                hit.Focus();
                hit.CaptureMouse();
                From(e, true);
            };
            hit.MouseMove += (_, e) => { if (hit.IsMouseCaptured) From(e, false); };
            hit.MouseLeftButtonUp += (_, e) => { if (hit.IsMouseCaptured) { From(e, true); hit.ReleaseMouseCapture(); } EndDrag(); };
            hit.LostMouseCapture += (_, _) => EndDrag();
            hit.KeyDown += (_, e) =>
            {
                if (e.Key is not (System.Windows.Input.Key.Left or System.Windows.Input.Key.Right)) return;
                float next = Math.Clamp(rui.Vol + (e.Key == System.Windows.Input.Key.Right ? 0.05f : -0.05f), 0f, 1f);
                AppMixer.Set(name, next);
                rui.Update(next, next <= 0 && rui.Mute);
                e.Handled = true;
            };
            row.Children.Add(hit.At(0, 2));
            row.Children.Add(pct.At(0, 3));
            _list.Children.Add(row);
        }
    }
}
