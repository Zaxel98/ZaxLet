using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ZaxLet;

/// <summary>
/// Pengaturan di jendela sendiri (seperti BrimDeck): daftar kategori di kiri, pratinjau langsung di kanan.
/// Pratinjau idle mencerminkan pill asli; pratinjau tab memakai salinan tab yang dibangun ulang tiap pengaturan berubah.
/// </summary>
public sealed class SettingsWindow : Window
{
    static SettingsWindow _i;
    const double StageW = 470, TabW = 640, TabH = 200 + 46;

    readonly SettingsView _sv = new();
    readonly Border _stage = new() { Background = Brushes.Black, CornerRadius = new CornerRadius(26), ClipToBounds = true };
    readonly StackPanel _bar = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Grid _host = new() { ClipToBounds = true };
    readonly Border _idleBox = new() { Background = Brushes.Black, CornerRadius = new CornerRadius(18), Height = 78, Margin = new Thickness(0, 0, 0, 12) };
    readonly Rectangle _mirror = new();
    TabView _pv;
    string _key = "music";

    public static void ShowIt()
    {
        _i ??= new SettingsWindow();
        _i.Show();
        if (_i.WindowState == WindowState.Minimized) _i.WindowState = WindowState.Normal;
        _i.Activate();
    }

    public SettingsWindow()
    {
        Title = "ZaxLet · Pengaturan";
        Width = 1080; Height = 680; MinWidth = 860; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = UI.B("#FF0E0E11");
        Foreground = Brushes.White;
        Icon = IslandWindow.Instance?.Icon;

        var g = UI.Cols(-1, 1, StageW + 40);
        g.Children.Add(_sv);
        g.Children.Add(new Border { Width = 1, Background = UI.Track, Margin = new Thickness(0, 12, 0, 12) }.At(0, 1));

        var right = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        right.Children.Add(UI.T("Pratinjau langsung", 12, UI.Dim));
        _idleBox.Margin = new Thickness(0, 8, 0, 14);
        _idleBox.Child = new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(14, 10, 14, 10), Child = _mirror };
        right.Children.Add(_idleBox);

        var inner = new Grid { Width = TabW, Height = TabH };
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(200) });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        inner.Children.Add(_host);
        var barBox = new Border { BorderBrush = UI.B("#1FFFFFFF"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(14, 0, 14, 0), Child = _bar };
        Grid.SetRow(barBox, 1);
        inner.Children.Add(barBox);
        _stage.Child = new Viewbox { Stretch = Stretch.Uniform, Child = inner };
        _stage.Height = StageW * TabH / TabW;
        right.Children.Add(_stage);
        right.Children.Add(new TextBlock { Text = "Klik ikon tab untuk melihat tampilannya. Pill idle di atas mengikuti pill yang sebenarnya.", Foreground = UI.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        g.Children.Add(right.At(0, 2));
        Content = g;

        Loaded += (_, _) => { _sv.OnShow(); AttachMirror(); BuildBar(); ShowTab(_key); };
        Settings.Applied += OnApplied;
        Closed += (_, _) => { Settings.Applied -= OnApplied; _pv?.OnHide(); _pv = null; _i = null; };
        SourceInitialized += (_, _) => DarkTitle();
    }

    void OnApplied() { Dispatcher.BeginInvoke(() => { BuildBar(); ShowTab(_key); }); }

    void AttachMirror()
    {
        var pill = IslandWindow.Instance?.PillElement;
        if (pill == null) return;
        _mirror.Fill = new VisualBrush(pill) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        void Fit() { _mirror.Width = Math.Max(10, pill.ActualWidth); _mirror.Height = Math.Max(10, pill.ActualHeight); }
        pill.SizeChanged += (_, _) => Fit();
        Fit();
    }

    void BuildBar()
    {
        _bar.Children.Clear();
        foreach (var key in IslandWindow.TabOrder)
        {
            if (key == "settings" || Settings.Cur.HiddenTabs.Contains(key)) continue;
            var k = key;
            bool on = key == _key;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(UI.Icon(IslandWindow.TabMeta[key].Glyph, 16, on ? UI.Accent : UI.Dim));
            if (on) { var t = UI.T(IslandWindow.TabMeta[key].Label, 12.5, UI.Fg, true); t.Margin = new Thickness(6, 0, 2, 0); row.Children.Add(t); }
            var b = UI.Btn(row, () => { _key = k; BuildBar(); ShowTab(k); }, on ? UI.CardOn : Brushes.Transparent, 14, new Thickness(10, 6, 10, 6));
            b.Margin = new Thickness(1, 0, 1, 0);
            _bar.Children.Add(b);
        }
    }

    void ShowTab(string key)
    {
        _pv?.OnHide();
        _host.Children.Clear();
        if (!IslandWindow.TabMeta.ContainsKey(key) || key == "settings") key = "music";
        _pv = IslandWindow.Instance?.MakeView(key);
        if (_pv == null) return;
        _host.Children.Add(_pv);
        _pv.OnShow();
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    void DarkTitle()
    {
        int on = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
    }
}
