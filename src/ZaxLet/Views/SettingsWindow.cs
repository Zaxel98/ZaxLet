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
    static readonly Brush Bg = UI.B("#FF16161A"), CardBg = UI.B("#FF1F1F24"), Sep = UI.B("#FF2C2C33"),
        Fg = UI.B("#FFE6E6EA"), Dim = UI.B("#FFB4B4BC");

    static SettingsWindow _i;
    static bool _previewVisible;
    static bool _previewInit;
    const double StageW = 470, TabW = 640, TabH = 200 + 46;

    readonly SettingsView _sv = new();
    readonly Border _stage = new() { Background = Brushes.Black, CornerRadius = new CornerRadius(26), ClipToBounds = true };
    readonly StackPanel _bar = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Grid _host = new() { ClipToBounds = true };
    readonly Border _idleBox = new() { Background = Brushes.Black, CornerRadius = new CornerRadius(18), Height = 78, Margin = new Thickness(0, 0, 0, 12) };
    readonly Rectangle _mirror = new();
    readonly ColumnDefinition _sepCol;
    readonly ColumnDefinition _previewCol;
    readonly Border _sep;
    readonly StackPanel _right;
    readonly Border _previewBtn;
    readonly TextBlock _previewBtnIcon;
    FrameworkElement _pill;
    SizeChangedEventHandler _onPillSize;
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
        Title = L.T("ZaxLet · Pengaturan");
        Width = 1120; Height = 720; MinWidth = 860; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        Foreground = Fg;
        Icon = IslandWindow.Instance?.Icon;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _previewBtnIcon = UI.Icon("\uE890", 13, Dim);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        btnRow.Children.Add(_previewBtnIcon);
        var btnTxt = UI.T("Pratinjau", 12.5, Fg, true);
        btnTxt.Margin = new Thickness(6, 0, 0, 0);
        btnRow.Children.Add(btnTxt);
        _previewBtn = UI.Btn(btnRow, () => { _previewVisible = !_previewVisible; ApplyPreviewState(); }, CardBg, 10, new Thickness(10, 5, 10, 5));
        _previewBtn.HorizontalAlignment = HorizontalAlignment.Right;
        _previewBtn.Margin = new Thickness(0, 10, 16, 4);
        root.Children.Add(_previewBtn);

        var g = UI.Cols(-1, 1, StageW + 40);
        Grid.SetRow(g, 1);
        _sepCol = g.ColumnDefinitions[1];
        _previewCol = g.ColumnDefinitions[2];
        g.Children.Add(_sv);
        _sep = new Border { Width = 1, Background = Sep, Margin = new Thickness(0, 12, 0, 12) };
        g.Children.Add(_sep.At(0, 1));

        _right = new StackPanel { Margin = new Thickness(20, 8, 20, 16) };
        _right.Children.Add(UI.T("Pratinjau langsung", 12, Dim));
        _idleBox.Margin = new Thickness(0, 8, 0, 14);
        _idleBox.Child = new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(14, 10, 14, 10), Child = _mirror };
        _right.Children.Add(_idleBox);

        var inner = new Grid { Width = TabW, Height = TabH };
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(200) });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        inner.Children.Add(_host);
        var barBox = new Border { BorderBrush = UI.B("#1FFFFFFF"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(14, 0, 14, 0), Child = _bar };
        Grid.SetRow(barBox, 1);
        inner.Children.Add(barBox);
        _stage.Child = new Viewbox { Stretch = Stretch.Uniform, Child = inner };
        _stage.Height = StageW * TabH / TabW;
        _right.Children.Add(_stage);
        _right.Children.Add(new TextBlock { Text = L.T("Klik ikon tab untuk melihat tampilannya. Pill idle di atas mengikuti pill yang sebenarnya."), Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        g.Children.Add(_right.At(0, 2));
        root.Children.Add(g);
        Content = root;

        Loaded += (_, _) =>
        {
            if (!_previewInit) { _previewVisible = root.ActualWidth >= 1280; _previewInit = true; }
            _sv.OnShow();
            ApplyPreviewState();
        };
        Settings.Applied += OnApplied;
        Closed += (_, _) =>
        {
            Settings.Applied -= OnApplied;
            _sv.OnHide();
            DetachPreview();
            _i = null;
        };
        SourceInitialized += (_, _) => DarkTitle();
    }

    void ApplyPreviewState()
    {
        _sv.SetCentered(!_previewVisible);
        if (_previewVisible)
        {
            _sepCol.Width = new GridLength(1);
            _sep.Visibility = Visibility.Visible;
            _previewCol.Width = new GridLength(StageW + 40);
            _right.Visibility = Visibility.Visible;
            _previewBtn.Background = UI.CardOn;
            _previewBtnIcon.Foreground = UI.Accent;
            AttachMirror();
            BuildBar();
            ShowTab(_key);
        }
        else
        {
            _sepCol.Width = new GridLength(0);
            _sep.Visibility = Visibility.Collapsed;
            _previewCol.Width = new GridLength(0);
            _right.Visibility = Visibility.Collapsed;
            _previewBtn.Background = CardBg;
            _previewBtnIcon.Foreground = Dim;
            DetachPreview();
        }
    }

    void DetachMirror()
    {
        if (_pill != null && _onPillSize != null) { _pill.SizeChanged -= _onPillSize; _pill = null; _onPillSize = null; }
        _mirror.Fill = null;
    }

    void DetachPreview()
    {
        DetachMirror();
        _pv?.OnHide();
        _pv = null;
        _host.Children.Clear();
    }

    void OnApplied() { Dispatcher.BeginInvoke(() => { if (!_previewVisible) return; BuildBar(); ShowTab(_key); }); }

    void AttachMirror()
    {
        var pill = IslandWindow.Instance?.PillElement;
        if (pill == null) return;
        _mirror.Fill = new VisualBrush(pill) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        void Fit() { _mirror.Width = Math.Max(10, pill.ActualWidth); _mirror.Height = Math.Max(10, pill.ActualHeight); }
        if (_pill != null && _onPillSize != null) _pill.SizeChanged -= _onPillSize;
        _pill = pill;
        _onPillSize = (_, _) => Fit();
        _pill.SizeChanged += _onPillSize;
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
