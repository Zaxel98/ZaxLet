using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace ZaxLet;

public partial class IslandWindow : Window
{
    public static IslandWindow Instance;
    public bool Dragging;

    public static readonly Dictionary<string, (string Glyph, string Label)> TabMeta = new()
    {
        ["music"] = ("", "Musik"), ["usage"] = ("", "AI"), ["monitor"] = ("", "Monitor"),
        ["calendar"] = ("", "Hari ini"), ["prayer"] = ("", "Sholat"), ["shelf"] = ("", "Shelf"),
        ["tools"] = ("", "Tangkap"), ["timer"] = ("", "Timer"),
        ["settings"] = ("", "Atur"),
        ["water"] = ("\U0001F4A7", "Minum"), ["apps"] = ("", "Komunikasi"),
    };
    public static readonly string[] DefaultOrder = { "music", "usage", "monitor", "calendar", "prayer", "water", "apps", "ctrl", "shelf", "clip", "tools", "timer", "settings" };
    /// <summary>Tab order per user preference; unsaved tabs (e.g. new tabs) follow the default order, "settings" is always last.</summary>
    public static string[] TabOrder
    {
        get
        {
            var saved = Settings.Cur.TabOrder.Where(k => DefaultOrder.Contains(k) && k != "settings").Distinct().ToList();
            saved.AddRange(DefaultOrder.Where(k => k != "settings" && !saved.Contains(k)));
            saved.Add("settings");
            return saved.ToArray();
        }
    }
    public bool Fullscreen => _fs;
    public FrameworkElement PillElement => Pill;

    const double OpenW = 640, TabH = 200, TabBarH = 46, Flare = 12, IdleH = 36, IdleH2 = 60;   // TabH is the same for all tabs
    static readonly Brush BattGreen = UI.B("#FF22D35A"), ArrowBlue = UI.B("#FF3B82F6"), UpTeal = UI.B("#FF2DD4BF"), ClaudeCol = UI.B("#FFE8967A"), CodexCol = UI.B("#FF86EFAC"), AgyCol = UI.B("#FF8AB4F8");

    readonly Dictionary<string, Func<TabView>> _make = new();
    readonly Dictionary<string, TabView> _views = new();
    readonly Dictionary<string, (Border Box, TextBlock Label, TextBlock Icon)> _tabBtns = new();
    readonly DispatcherTimer _closeT = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _peekT = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _altT = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly ScaleTransform _pillScale = new(1, 1), _userScale = new(1, 1);   // _userScale: user-chosen island size
    readonly TranslateTransform _pillMove = new();
    bool _ghost;   // Alt held over pill: pill hides temporarily so whatever is behind it can be clicked
    readonly DispatcherTimer _trimOpenT = new() { Interval = TimeSpan.FromSeconds(4) };
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmFlush();

    TabView _cur;
    bool _open, _waveOn, _fs, _peek, _capturing;
    readonly Queue<(string Text, double Seconds, Action OnClick, bool Sound)> _pendingAlerts = new();
    string _alert, _idleKey = "";
    DateTime _alertUntil, _quotaAt = DateTime.MinValue;
    (double W, double H) _idle = (200, IdleH);
    int _batTick = 999;

    // idle widgets
    readonly TextBlock _batText = new() { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    readonly Canvas _batIcon = new() { Width = 27, Height = 14, VerticalAlignment = VerticalAlignment.Center };
    readonly System.Windows.Shapes.Ellipse _phoneDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly System.Windows.Shapes.Ellipse _voiceDot = new() { Width = 9, Height = 9, Fill = UI.B("#FF22D35A"), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, ToolTip = L.T("Sedang di voice channel Discord") };
    readonly Marquee Row2 = new() { Foreground = UI.B("#A6FFFFFF") };
    // phone notifications currently shown in pill: one row per notification (unique key), newest on top
    sealed class NotifRow { public PhoneNotif N; public DateTime Until; public int Count = 1; }   // Count: consecutive distinct notifications from the same app
    readonly List<NotifRow> _rows = new();
    string _rowsSig = "";
    bool _snooze, _shown = true;      // _snooze: temporarily hidden by user; _shown: window is currently shown
    DateTime _snoozeUntil = DateTime.MaxValue;
    const double RowH = 22;
    const int HideHotkeyId = 0x151A, CapHotkeyId = 0x151B, OpenHotkeyId = 0x151C, QuickHotkeyId = 0x151D;
    readonly TextBlock TimeText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(7, 0, 7, 0) };
    readonly StackPanel _batBox = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly UsageChip _chipC = new("C", ClaudeCol), _chipX = new("X", CodexCol), _chipA = new("A", AgyCol);
    readonly StackPanel _usageBox = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _speed = Pct();
    readonly StackPanel _wave = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 0, 7, 0), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    readonly List<Border> _bars = new();

    static TextBlock Pct() => new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };

    public IslandWindow()
    {
        InitializeComponent();
        Instance = this;
        PreviewKeyDown += KeyNav;

        _make["music"] = () => new MusicView();
        _make["usage"] = () => new UsageView();
        _make["monitor"] = () => new MonitorView();
        _make["calendar"] = () => new CalendarView();
        _make["prayer"] = () => new PrayerView();
        _make["shelf"] = () => new ShelfView();
        _make["tools"] = () => new ToolsView();
        _make["timer"] = () => new TimerView();
        _make["water"] = () => new WaterView();
        _make["apps"] = () => new AppsView();
        _make["ctrl"] = () => new ControlView();
        _make["clip"] = () => new ClipboardView();
        BuildTabBar();
        BuildIdleWidgets();
        _closeT.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.Cur.CloseDelayMs, 50, 600000));

        Pill.SizeChanged += (_, _) => BuildShape();
        // cursor entering over the notification list does not open the pill, so the list can be scrolled with the mouse wheel
        Pill.MouseEnter += (_, _) => { if (_capturing) return; _closeT.Stop(); if (Settings.Cur.HoverOpen && !OverNotifRows() && !(Settings.Cur.AltGhost && Native.AltDown())) Open(); };
        Pill.MouseLeave += (_, _) => { if (_open) _closeT.Start(); };
        Pill.MouseLeftButtonUp += (_, _) => { if (HandleClickAction()) return; Open(); };
        // right-click = hide mode like in fullscreen (restored via shortcut, tray menu, or automatically after a delay); text boxes and items handling their own right-click are skipped
        Pill.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled || !Settings.Cur.RightClickHide || e.OriginalSource is DependencyObject src && InTextBox(src)) return;
            e.Handled = true;
            // alternating right-click: normal -> hidden (with peek); currently peeking (hidden or fullscreen auto-hide) -> always show
            if (_snooze) SetSnooze(false);
            else if (_fs) { _fsForceShow = true; CheckFullscreen(); }
            else SetSnooze(true, peek: true);
        };
        Pill.RenderTransformOrigin = new Point(.5, 0);
        Pill.RenderTransform = new TransformGroup { Children = { _userScale, _pillScale, _pillMove } };
        _altT.Tick += (_, _) => AltCheck();   // run by SyncTimers only when AltGhost is enabled
        InitFeatures();
        _closeT.Tick += (_, _) =>
        {
            // stay open while typing in a text box (to-do, settings) or while cursor is still over the pill
            if (Dragging || Pill.IsMouseOver || (Keyboard.FocusedElement is TextBox tb && Pill.IsAncestorOf(tb))) return;
            _closeT.Stop();
            Close_();
        };
        Deactivated += (_, _) => { if (_open && !Dragging) Close_(); };

        Pill.DragEnter += (_, e) => { _closeT.Stop(); Open(); Select("shelf"); e.Effects = DragDropEffects.Copy; e.Handled = true; };
        Pill.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Pill.DragLeave += (_, _) => { if (_open) _closeT.Start(); };
        Pill.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) foreach (var f in files) Shelf.Add(f);
            e.Handled = true;
        };

        _tick.Tick += (_, _) => Tick();
        _peekT.Tick += (_, _) => Peek();
        // a few seconds after a tab opens, return unused pages (shared graphics driver DLLs) to the OS
        _trimOpenT.Tick += (_, _) => { _trimOpenT.Stop(); Native.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); };
        MediaService.I.Changed += () => { ApplyTint(); ApplyTheme(); RefreshIdle(); TrackFlash(); PlayLog.Update(MediaService.I.HasSession && !MediaService.I.IsAd ? MediaService.I.Title : "",MediaService.I.Artist, MediaService.I.Playing); };
        TimerService.I.Tick += RefreshIdle;
        UsageService.I.Changed += () => Dispatcher.BeginInvoke(RefreshIdle);
        Settings.Applied += OnSettingsApplied;
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(ApplyScale);

        // while closed, return unused memory pages to the OS (RAM shown in Task Manager drops accordingly)
        var trim = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };   // full GC + EmptyWorkingSet forces pages to reload: don't run too often
        trim.Tick += (_, _) =>
        {
            if (_open) return;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            Native.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
        };
        trim.Start();
        Loaded += (_, _) =>
        {
            ApplyScale(); ApplyTheme(); ApplyShowMode();
            Tick(); _tick.Start();
            var warm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            warm.Tick += (_, _) => { warm.Stop(); Prewarm(); };
            warm.Start();
            // WPF loads d3d9 and graphics drivers (Intel igc64, media_bin, etc.: ~100 MB shared pages) at startup even with software rendering;
            // first trim 12 s after startup instead of waiting 45 s, so initial RAM doesn't stay at 180 MB for long
            var early = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            early.Tick += (_, _) => { early.Stop(); GC.Collect(2, GCCollectionMode.Forced, true, true); Native.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); };
            early.Start();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
        PositionTop();
        HwndSource.FromHwnd(h)?.AddHook((IntPtr _, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == 0x0312 && wp.ToInt32() == HideHotkeyId) { ToggleHidden(); handled = true; }   // WM_HOTKEY
            else if (msg == 0x0312 && wp.ToInt32() == CapHotkeyId) { CaptureService.Area(); handled = true; }
            else if (msg == 0x0312 && wp.ToInt32() == OpenHotkeyId) { ToggleOpenByKey(); handled = true; }
            else if (msg == 0x0312 && wp.ToInt32() == QuickHotkeyId) { QuickWindow.Toggle(); handled = true; }
            else HookFeatures(msg, wp, lp, ref handled);
            return IntPtr.Zero;
        });
        RegisterKeys(h);
        StartFeatureListeners(h);
    }

    /// <summary>Register all global shortcuts; empty ones are unregistered, failed ones (conflict / typo) show a warning in the pill.</summary>
    void RegisterKeys(IntPtr h)
    {
        var s = Settings.Cur;
        var bad = new List<string>();
        foreach (var (id, key) in new[] { (HideHotkeyId, s.HideKey), (CapHotkeyId, s.CapKey), (OpenHotkeyId, s.OpenKey), (QuickHotkeyId, s.QuickKey) })
        {
            if (string.IsNullOrWhiteSpace(key)) Hotkey.Unregister(h, id);
            else if (!Hotkey.Register(h, id, key)) bad.Add(key);
        }
        if (bad.Count > 0) Dispatcher.BeginInvoke(() => Alert(L.F("Pintasan tidak bisa dipakai (bentrok atau salah ketik): {0}", string.Join(", ", bad)), 8, null, false));
    }

    static bool InTextBox(DependencyObject d)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.TextBoxBase) return true;
        return false;
    }

    /// <summary>Temporarily hide the pill (or show it again). New notifications still show it briefly.</summary>
    public void ToggleHidden() => SetSnooze(!_snooze);

    /// <param name="peek">Hidden via right-click: pill reappears (clock and battery only) when cursor moves to the top edge, just like in fullscreen.</param>
    void SetSnooze(bool on, bool peek = false)
    {
        _snooze = on;
        _snoozePeek = on && peek;
        _snoozeUntil = on && Settings.Cur.SnoozeMin > 0 ? DateTime.Now.AddMinutes(Settings.Cur.SnoozeMin) : DateTime.MaxValue;
        if (on) { _closeT.Stop(); Close_(); }
        if (_snoozePeek) { _peek = false; _peekT.Start(); }
        else if (!_fs) { _peekT.Stop(); _peek = false; }
        _idleKey = "";
        RefreshIdle();
    }

    bool _snoozePeek, _fsForceShow;

    // Window is visible unless: fullscreen or hidden by user (both without cursor peek); active notifications force it visible.
    void SyncVisible()
    {
        if (_capturing) return;
        bool fsMusic = FsMusicOn;
        bool want = PillVisibility.Want(_fs, _snooze, _snoozePeek, _peek, MiniActive, _ghost,
            Settings.Cur.NotifFullscreen && _rows.Count > 0, fsMusic, Settings.Cur.ShowMode == "activity" && !HasActivity());
        SetClickThrough(fsMusic && want);
        if (want == _shown) return;
        _shown = want;
        if (want) Show();
        Pop(want);
    }

    // minimize-like animation: pill shrinks to the top edge while fading, then window is hidden; shown again with the reverse motion
    void Pop(bool show)
    {
        var ease = new QuinticEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };
        var d = TimeSpan.FromMilliseconds(show ? 280 : 220);
        _pillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(show ? 1 : .45, d) { EasingFunction = ease });
        _pillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(show ? 1 : .1, d) { EasingFunction = ease });
        _pillMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(show ? 0 : -6, d) { EasingFunction = ease });
        var fade = new DoubleAnimation(show ? 1 : 0, d);
        if (!show) fade.Completed += (_, _) => { if (!_shown) Hide(); };
        Pill.BeginAnimation(OpacityProperty, fade);
    }

    // Alt held + cursor over pill area (including its shoulders) = pill hidden; Alt released or cursor leaves = shown again as usual
    void AltCheck()
    {
        bool want = false;
        if (Settings.Cur.AltGhost && Native.AltDown() && Native.GetCursorPos(out var pt))
        {
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double x = pt.X / dpi, y = pt.Y / dpi, x0 = Left + (Width - Pill.ActualWidth * Zoom) / 2 - Flare * Zoom;
            want = x >= x0 && x <= x0 + (Pill.ActualWidth + 2 * Flare) * Zoom && y >= Top && y <= Top + Pill.ActualHeight * Zoom;
        }
        if (want == _ghost) return;
        _ghost = want;
        if (want && _open) { _closeT.Stop(); Close_(); }   // an already-open pill also collapses
        SyncVisible();
    }

    bool OverNotifRows() => NotifHost.IsVisible && new Rect(0, 0, NotifHost.ActualWidth, NotifHost.ActualHeight).Contains(Mouse.GetPosition(NotifHost));

    /// <summary>Monitor where the island is placed: user choice, or primary monitor if that index no longer exists.</summary>
    WF.Screen Scr { get { var a = WF.Screen.AllScreens; var i = Settings.Cur.MonitorIdx; return i > 0 && i < a.Length ? a[i] : WF.Screen.PrimaryScreen; } }

    double Zoom => _userScale.ScaleX;

    /// <summary>Island size: user-chosen percent, or automatic by screen width (1536 DIP = 100%). Window is widened enough so the open panel isn't clipped.</summary>
    void ApplyScale()
    {
        int p = Settings.Cur.IslandScale;
        double z = p <= 0 ? Math.Clamp(Scr.Bounds.Width / VisualTreeHelper.GetDpi(this).DpiScaleX / 1536.0, .85, 1.25) : Math.Clamp(p, 75, 130) / 100.0;
        _userScale.ScaleX = _userScale.ScaleY = z;
        Width = Math.Max(720, (OpenW + 2 * Flare) * z + 40);
        Height = Math.Max(460, (TabH + TabBarH + 80) * z);
        PositionTop();
    }

    void PositionTop(double? pillW = null)
    {
        var b = Scr.Bounds;
        var wa = Scr.WorkingArea;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double w = (pillW ?? (_open ? OpenW : (_idle.W > 0 ? _idle.W : Pill.Width))) * Zoom;
        if (Settings.Cur.IslandShape != "pill") w += 2 * Flare * Zoom;
        double half = w / 2.0;
        double minCx = wa.Left / dpi + half, maxCx = wa.Right / dpi - half;
        double cx = (b.Left + b.Width / 2.0 + Math.Clamp(Settings.Cur.IslandOffsetX, -600, 600)) / dpi;
        cx = minCx <= maxCx ? Math.Clamp(cx, minCx, maxCx) : (minCx + maxCx) / 2.0;
        Left = cx - Width / 2.0;   // center of chosen monitor + offset (pixels to DIP), clamped to work area
        Top = b.Top / dpi;
    }

    // ---------- pill shape: concave shoulders at top like a notch ----------
    void BuildShape()
    {
        double w = Pill.ActualWidth, h = Pill.ActualHeight;
        if (w < 10 || h < 10) return;
        double R = Flare, r = Settings.Cur.PillRadius >= 8 ? Math.Min(Settings.Cur.PillRadius, h / 2) : Math.Clamp(h / 2, 14, 28);
        if (Settings.Cur.IslandShape == "pill")   // Dynamic style: rounded capsule without concave shoulders
        {
            Shape.Margin = new Thickness(0);
            var rg = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
            rg.Freeze();
            Shape.Data = rg;
            return;
        }
        Shape.Margin = new Thickness(-12, 0, -12, 0);   // Notch style: concave shoulders flare out on both sides
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0), true, true);
            c.LineTo(new Point(w + 2 * R, 0), true, false);
            c.ArcTo(new Point(w + R, R), new Size(R, R), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(w + R, h - r), true, false);
            c.ArcTo(new Point(w + R - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(R + r, h), true, false);
            c.ArcTo(new Point(R, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(R, R), true, false);
            c.ArcTo(new Point(0, 0), new Size(R, R), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        Shape.Data = g;
    }

    // ---------- tab bar ----------
    void BuildTabBar()
    {
        TabBar.Children.Clear();
        _tabBtns.Clear();
        foreach (var key in TabOrder)
        {
            if (key != "settings" && Settings.Cur.HiddenTabs.Contains(key)) continue;
            var k = key;
            var label = UI.T(TabMeta[key].Label, 12.5, UI.Fg, true);
            label.Margin = new Thickness(6, 0, 2, 0);
            label.Visibility = Visibility.Collapsed;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = UI.Icon(TabMeta[key].Glyph, 16, UI.Dim);
            row.Children.Add(icon);
            row.Children.Add(label);
            var box = UI.Btn(row, () => Select(k), Brushes.Transparent, 14, new Thickness(10, 6, 10, 6));
            box.Margin = new Thickness(1, 0, 1, 0);
            box.ToolTip = L.T(TabMeta[key].Label);   // icon-only without label: tooltip also serves as screen reader name
            TabBar.Children.Add(box);
            _tabBtns[key] = (box, label, icon);
        }
        Highlight();
    }

    void Highlight()
    {
        foreach (var (k, b) in _tabBtns)
        {
            bool on = k == _cur?.Key;
            b.Box.Background = on ? UI.CardOn : Brushes.Transparent;
            b.Label.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            b.Icon.Foreground = on ? TabAccent() : UI.Dim;
        }
    }

    /// <summary>Active tab icon follows the playing song's album art color (same as neon border); without a song it uses the accent color.</summary>
    Brush TabAccent()
    {
        var ms = MediaService.I;
        if (!Settings.Cur.TintCover || !ms.HasSession || ms.CoverColor is not { } c) return UI.Accent;
        var br = new SolidColorBrush(c); br.Freeze();
        return br;
    }

    // ---------- open / close ----------
    public void OpenNow() { if (_capturing) return; if (_snooze) SetSnooze(false); Open(); _closeT.Start(); }

    /// <summary>Open island directly on a specific tab ("open monitor" command in Quick Actions).</summary>
    public void GoTab(string key) { OpenNow(); if (_open) Select(key); }

    void Open()
    {
        if (_capturing || _open || _fs || _snooze) return;   // in fullscreen the pill only shows clock and battery
        _open = true;
        OpenLayer.Visibility = Visibility.Visible;
        Fade(IdleLayer, 0, 90, () => { if (_open) IdleLayer.Visibility = Visibility.Collapsed; });
        Fade(OpenLayer, 1, 200, null, 70);
        var key = Settings.Cur.OpenTab != "last" && Settings.Cur.OpenTab != "settings" && TabMeta.ContainsKey(Settings.Cur.OpenTab)
            ? Settings.Cur.OpenTab
            : Settings.Cur.LastTab == "settings" ? "music" : Settings.Cur.LastTab;
        try { Select(key); }
        catch (Exception ex) { App.Log(ex); Close_(); }
    }

    void Close_()
    {
        if (!_open) return;
        _open = false;
        _cur?.OnHide();
        Keyboard.ClearFocus();
        Settings.Save();
        Shape.Fill = ThemeFill();
        IdleLayer.Visibility = Visibility.Visible;
        Fade(OpenLayer, 0, 90, () => { if (!_open) OpenLayer.Visibility = Visibility.Collapsed; });
        Fade(IdleLayer, 1, 180, null, 60);
        RefreshIdle();
        SetSize(_idle.W, _idle.H);
        RestoreFocus();
    }

    /// <summary>New tab (not the one in the pill); used for preview in the settings window.</summary>
    public TabView MakeView(string key)
    {
        var v = _make[key]();
        // all tabs have the same height: short tabs are centered, others fill the height
        foreach (UIElement c in v.Children)
            if (c is FrameworkElement fe) fe.VerticalAlignment = v.CenterV ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        return v;
    }

    /// <summary>Build the monitor tab early and take its first sample so it is already populated when opened (one-time startup spike).</summary>
    public void Prewarm()
    {
        if (!_views.TryGetValue("monitor", out var v)) _views["monitor"] = v = MakeView("monitor");
        ((MonitorView)v).Warm();
    }

    void Select(string key)
    {
        if (key == "settings") { SettingsWindow.ShowIt(); return; }
        if (!_make.ContainsKey(key) || Settings.Cur.HiddenTabs.Contains(key))
            key = TabOrder.First(k => k != "settings" && !Settings.Cur.HiddenTabs.Contains(k));
        var prevKey = _cur?.Key;
        if (_cur != null) { _cur.OnHide(); Host.Children.Clear(); }
        if (!_views.TryGetValue(key, out var v)) _views[key] = v = MakeView(key);
        _cur = v;
        Settings.Cur.LastTab = key;
        Host.Children.Add(v);
        // transition: new tab fades in while sliding from the target tab's direction (right if index is larger)
        int dir = prevKey == null || prevKey == key ? 0 : Array.IndexOf(TabOrder, key) > Array.IndexOf(TabOrder, prevKey) ? 1 : -1;
        var slide = new TranslateTransform(dir * 22, dir == 0 ? 8 : 0);
        v.RenderTransform = slide;
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        slide.BeginAnimation(TranslateTransform.XProperty, Fast(new DoubleAnimation(dir * 22, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease }));
        slide.BeginAnimation(TranslateTransform.YProperty, Fast(new DoubleAnimation(dir == 0 ? 8 : 0, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease }));
        v.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        Highlight();
        try { v.OnShow(); }
        catch (Exception ex) { App.Log(ex); }
        SetSize(OpenW, TabH + TabBarH);
        ApplyTint();
        _trimOpenT.Stop();
        _trimOpenT.Start();
    }

    void SetSize(double w, double h)
    {
        PositionTop(w);
        var e = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(300);
        Pill.BeginAnimation(WidthProperty, Fast(new DoubleAnimation(w, d) { EasingFunction = e }));
        Pill.BeginAnimation(HeightProperty, Fast(new DoubleAnimation(h, d) { EasingFunction = e }));
    }

    /// <summary>Interactive animations (open/close, switch tab) stay at 60 fps; the rest follow the 30 fps default (App.OnStartup).</summary>
    static T Fast<T>(T a) where T : Timeline { Timeline.SetDesiredFrameRate(a, 60); return a; }

    static void Fade(UIElement el, double to, int ms, Action done, int delay = 0)
    {
        var a = Fast(new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay) });
        if (done != null) a.Completed += (_, _) => done();
        el.BeginAnimation(OpacityProperty, a);
    }

    void ApplyTint()
    {
        if (!_open) return;
        Highlight();
        var c = MediaService.I.CoverColor;
        if (_cur?.Key != "music" || c == null || !MediaService.I.HasSession || !Settings.Cur.TintCover || Settings.Cur.Theme is not ("black" or "neon")) { Shape.Fill = ThemeFill(); return; }
        var t = Color.FromRgb((byte)(c.Value.R * .6), (byte)(c.Value.G * .6), (byte)(c.Value.B * .6));
        var br = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        br.GradientStops.Add(new GradientStop(Colors.Black, .45));
        br.GradientStops.Add(new GradientStop(t, 1));
        Shape.Fill = br;
    }

    void OnSettingsApplied()
    {
        _closeT.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.Cur.CloseDelayMs, 50, 600000));
        BuildTabBar();
        _idleKey = "";
        PowerSaver.Reset();
        _quotaAt = DateTime.MinValue;
        ApplyScale(); ApplyTheme(); ApplyShowMode(); BuildShape(); PerfOverlay.Refresh();
        RegisterKeys(new WindowInteropHelper(this).Handle);
        SyncTimers();
        Arrange();
        FixWidths();
        UpdateBattery();
        RefreshIdle();
        ApplyTint();
    }

    // ---------- auto-hide in fullscreen ----------
    void CheckFullscreen()
    {
        // "real": there is a reason to hide the pill (fullscreen app, or foreground window has buttons covered by the island)
        bool fsRaw = Native.ForegroundFullscreenOnPrimary(Scr.Bounds);
        PerfOverlay.Sync(fsRaw);   // performance overlay follows fullscreen even if "hide in fullscreen" is off
        bool fsApp = Settings.Cur.AutoHideFullscreen && fsRaw;
        bool real = fsApp || (Settings.Cur.YieldOn && _yield.Blocked && !_open);   // pill currently opened by user does not yield
        if (!real) _fsForceShow = false;
        _fsApp = fsApp && !_fsForceShow;   // "always show" choice lasts while the same fullscreen app is running
        bool fs = real && !_fsForceShow;
        if (fs == _fs) return;
        _fs = fs;
        if (fs)
        {
            _closeT.Stop();
            Close_();
            _peek = false;
            _peekT.Start();
        }
        else
        {
            if (!_snoozePeek) _peekT.Stop();   // right-click hide still uses cursor peek
            _peek = false;
        }
        _idleKey = "";
        RefreshIdle();   // calls SyncVisible
    }

    void Peek()
    {
        if (_capturing) return;
        Native.GetCursorPos(out var pt);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var sb = Scr.Bounds;
        double cx = (Left + Width / 2.0) * dpi, top = sb.Top;
        double halfW = (_peek ? Pill.ActualWidth * Zoom / 2 + 8 : 100) * dpi;
        bool inX = Math.Abs(pt.X - cx) <= halfW;
        if (!_peek && pt.Y <= top + 2 && pt.Y >= top - 2 && inX) { _peek = true; SyncVisible(); RefreshIdle(); }
        else if (_peek && !(inX && pt.Y <= top + (Pill.ActualHeight * Zoom + 8) * dpi)) { _peek = false; SyncVisible(); }
    }

    // ---------- idle ----------
    /// <param name="onClick">Runs if the pill is clicked while the message is shown (e.g. open downloaded file, update).</param>
    /// <param name="sound">False for OSD popups that do not need a sound.</param>
    public void Alert(string text, double seconds = 7, Action onClick = null, bool sound = true)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_capturing)
            {
                _pendingAlerts.Enqueue((text, seconds, onClick, sound));
                return;
            }
            _alert = text;
            _alertAction = onClick;
            _alertUntil = DateTime.Now.AddSeconds(seconds);
            RefreshIdle();
            if (sound) SystemSounds.Asterisk.Play();
        });
    }

    void Tick()
    {
        if (_snooze && DateTime.Now >= _snoozeUntil) SetSnooze(false);
        CheckFullscreen();
        var s = Settings.Cur;
        var ms = MediaService.I;
        var fast = !_fs && ms.Playing && ms.Lyrics.Count > 0 && Settings.Cur.MusicLyrics && Settings.Cur.IdleLyrics;
        var want = TimeSpan.FromMilliseconds(fast ? 250 : 1000);
        if (_tick.Interval != want) _tick.Interval = want;
        if (++_batTick * _tick.Interval.TotalSeconds > 30) { _batTick = 0; UpdateBattery(); }
        // no _fs check: quota is also read while a fullscreen app is active (usage cache is used by Middleman worker); just one lightweight GET per UsageRefreshSec
        if (s.IdleUsage && DateTime.Now >= _quotaAt)
        {
            _quotaAt = DateTime.Now.AddSeconds(Math.Max(1, s.UsageRefreshSec));
            _ = System.Threading.Tasks.Task.Run(() => UsageService.I.RefreshQuotaAsync());
        }
        RefreshIdle();
    }

    string ClockText()
    {
        var s = Settings.Cur;
        var now = DateTime.Now;
        var inv = CultureInfo.InvariantCulture;
        return s.ClockStyle switch
        {
            "colon" => now.ToString(s.ClockSeconds ? "HH:mm:ss" : "HH:mm", inv),
            "12h" => now.ToString(s.ClockSeconds ? "h:mm:ss tt" : "h:mm tt", inv),
            _ => now.ToString(s.ClockSeconds ? "HH.mm.ss" : "HH.mm", inv),
        };
    }

    void RefreshIdle()
    {
        var s = Settings.Cur;
        var ms = MediaService.I;
        bool full = !_fs;
        bool mini = MiniActive;
        if (mini != _wasMini) { _wasMini = mini; FixWidths(); UpdateBattery(); }   // entering/leaving mini clock pill: adjust clock and battery widths
        if (mini) { TimeText.Width = double.NaN; _batBox.Visibility = Visibility.Collapsed; }

        var clock = ClockText();
        if (mini && Settings.Cur.MiniClock == "date") clock += "  " + DateTime.Now.ToString("ddd d MMM", CultureInfo.CurrentUICulture);
        if (TimeText.Text != clock) TimeText.Text = clock;

        // center: AI usage, downloads, waveform
        bool c = _chipC.Update(UsageService.I.Claude, full && s.IdleUsage && s.ClaudeOn && s.IdleUsageProviders != "codex");
        bool x = _chipX.Update(UsageService.I.Codex, full && s.IdleUsage && s.CodexOn && s.Slot2 == "codex" && s.IdleUsageProviders != "claude");
        bool a = _chipA.Update(UsageService.I.Agy, full && s.IdleUsage && s.CodexOn && s.Slot2 == "antigravity" && s.IdleUsageProviders != "claude");
        _usageBox.Visibility = c || x || a ? Visibility.Visible : Visibility.Collapsed;
        _voiceDot.Visibility = full && DiscordRpc.I.InVoice ? Visibility.Visible : Visibility.Collapsed;   // green dot: currently in voice channel
        UpdatePhoneDot(full);
        bool sp = full && s.IdleSpeed;
        if (sp) { NetMeter.Sample(); SetSpeed(NetMeter.Fmt(NetMeter.DownKb), s.IdleSpeedStyle == "both" ? NetMeter.Fmt(NetMeter.UpKb) : null); }
        _speed.Visibility = sp ? Visibility.Visible : Visibility.Collapsed;
        SetWave(full && s.IdleWave && ms.HasSession && ms.Playing);

        // phone notification list: per-row expiry (paused while hovered so user has time to scroll/click)
        if (!OverNotifRows()) _rows.RemoveAll(r => DateTime.Now >= r.Until);
        const int maxRows = 1;
        int shownRows = Math.Min(_rows.Count, maxRows);
        UpdateNotifRows(maxRows);

        // line below notifications: warning > timer > lyrics (remains even when notifications stack)
        string text = null;
        double pace = 0;   // >0 for lyrics only: marquee follows line duration and stops at the end
        Brush br = UI.Dim;
        if (_alert != null)
        {
            if (DateTime.Now < _alertUntil) { text = _alert; br = UI.Accent; }
            else _alert = null;
        }
        if (_fs && !FsMusicOn || (text == null && shownRows > 0)) text = null;   // notifications replace timer/lyrics: idle stays single-line
        else if (text == null && TimerService.I.Running)
            text = (TimerService.I.Mode == TMode.Stopwatch ? "Stopwatch " : "Timer ") + UI.Clock(TimerService.I.Value);
        if ((!_fs || FsMusicOn) && text == null && shownRows == 0 && s.MusicLyrics && s.IdleLyrics && ms.Playing && ms.Lyrics.Count > 0)   // lyrics turned off = idle lyrics off too
        {
            var l = ms.CurrentLine();
            text = string.IsNullOrWhiteSpace(l) ? "♪" : s.IdleRomaji ? Translit.Of(l) ?? l : l;
            pace = ms.CurrentLineSpan();
        }
        if (FsMusicOn && text == null && shownRows == 0 && !string.IsNullOrWhiteSpace(ms.Title)) text = "♪ " + ms.Title;   // fullscreen without lyrics: song title only
        if (!_fs && !mini && text == null && shownRows == 0 && s.IdleContext) text = ContextLine();   // lowest priority: yields to warning, timer, lyrics, and notifications
        Row2.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        Row2.PaceSeconds = pace;
        if (Row2.Text != (text ?? "")) Row2.Text = text ?? "";
        Row2.Foreground = br;

        var key = $"{clock.Length}|{c}|{x}|{a}|{sp}|{_chipC.Key}|{_chipX.Key}|{_chipA.Key}|{_speed.Tag}|{_batText.Text}|{_batText.Visibility}|{_waveOn}|{text != null}|{shownRows}|{_phoneDot.Visibility}|{_voiceDot.Visibility}|{mini}|{clock}";
        if (key != _idleKey)
        {
            _idleKey = key;
            double w = IdleWidth();
            int lines = shownRows + (text != null ? 1 : 0);
            _idle = lines > 0 ? (Math.Max(w, 340), IdleH + 2 + RowH * lines) : (w, IdleH);
            if (!_open) SetSize(_idle.W, _idle.H);
        }
        if (full) _idleFull = _idle;   // stable full pill size for YieldService (size shrinks while yielding and must not shift the decision)
        SyncVisible();
    }

    // rebuild rows only when contents change (Marquee runs its own animation)
    void UpdateNotifRows(int maxRows)
    {
        var sig = string.Join("¦", _rows.Select(r => r.N.Key + "|" + r.N.Title + "|" + r.N.Text + "|" + r.Count)) + "#" + maxRows;
        if (sig == _rowsSig) return;
        _rowsSig = sig;
        NotifRows.Children.Clear();
        NotifHost.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifScroll.MaxHeight = RowH * maxRows;
        bool more = _rows.Count > maxRows;
        NotifMore.Text = more ? "↕ " + _rows.Count : "";
        NotifScroll.Margin = new Thickness(0, 0, more ? 28 : 0, 0);
        foreach (var r in _rows)
        {
            var n = r.N;
            var g = new Grid { Height = RowH, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new Image { Width = 16, Height = 16, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Top, Source = PhoneNotifs.IconOf(n.Pkg) });
            var m = new Marquee { Foreground = Brushes.White, Text = (string.IsNullOrWhiteSpace(n.Title) ? "" : n.Title + ": ") + n.Text + (r.Count > 1 ? "   (+" + (r.Count - 1) + ")" : "") };
            Grid.SetColumn(m, 1);
            g.Children.Add(m);
            if (Settings.Cur.NotifOtp && Otp.Find(n.Title, n.Text) is { } code)
            {
                // OTP code: Copy button at the right end of the row; the code does not enter clipboard history
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var chip = UI.Btn(UI.T(L.T("Salin") + " " + code, 11.5, UI.Fg, true), () =>
                {
                    _rows.Remove(r); RefreshIdle();
                    Alert(L.T(ClipboardHistory.CopySecret(code) ? "Kode disalin" : "Gagal menyalin, coba lagi"), 2, null, false);
                }, UI.AccentBg, 9, new Thickness(9, 1, 9, 2));
                chip.Margin = new Thickness(8, 0, 0, 0); chip.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(chip, 2);
                g.Children.Add(chip);
            }
            g.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenRow(r); };   // click: open conversation (can reply) or close row
            g.MouseRightButtonUp += (_, e) => { e.Handled = true; _rows.Remove(r); RefreshIdle(); };
            NotifRows.Children.Add(g);
        }
        if (_rows.Count > 0) NotifScroll.ScrollToTop();
    }

    void OpenRow(NotifRow r)
    {
        _rows.Remove(r);
        if (PhoneNotifs.Listed(r.N) && !_fs && !_snooze) { AppsView.WantPkg = r.N.Pkg; Open(); Select("apps"); }
        RefreshIdle();
    }

    // Idle pill width follows content. When center zone is non-empty, left and right match the wider side so center content
    // is truly centered; when empty, a 56 px gap (camera area) remains and left/right share width per their content.
    double IdleWidth()
    {
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        ZoneL.Measure(inf); ZoneC.Measure(inf); ZoneR.Measure(inf);
        double l = ZoneL.DesiredSize.Width, c = ZoneC.DesiredSize.Width, r = ZoneR.DesiredSize.Width;
        var s = Settings.Cur;
        bool exact = s.IdleExactCenter && c > 0;   // center component dead-center in pill (= screen center); shorter side pads the difference
        var cols = IdleRow.ColumnDefinitions;
        cols[0].Width = cols[2].Width = exact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        IdleRow.HorizontalAlignment = exact ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        double ex = Math.Clamp(s.IdleExtra, 0, 160);
        return ex + (exact ? Math.Max(120, c + 2 * Math.Max(l, r) + 28) : Math.Max(120, l + c + r + 28));   // spacing between components is fixed: Settings.IdleGap
    }

    // phone status dot, only two states: lit (filled) = phone connected; empty (ring only) = not connected, regardless of reason
    void UpdatePhoneDot(bool full)
    {
        if (!full || !Settings.Cur.IdlePhoneDot) { _phoneDot.Visibility = Visibility.Collapsed; return; }
        bool on = ShareServer.I.On && ShareServer.I.AppCount > 0;
        var c = UI.B("#FF38BDF8");
        _phoneDot.Fill = on ? c : Brushes.Transparent;
        _phoneDot.Stroke = on ? c : UI.B("#99FFFFFF");
        _phoneDot.StrokeThickness = 1.4;
        var bat = on ? ShareServer.I.DevicesSnapshot().FirstOrDefault(x => x.BatPct >= 0 && ShareServer.I.HasApp(x.Id)) : null;
        var tip = L.T(on ? "HP tersambung" : "HP tidak tersambung") + (bat == null ? "" : $" · {bat.BatPct}%" + (bat.BatCharging ? " ⚡" : ""));
        if ((string)_phoneDot.ToolTip != tip) _phoneDot.ToolTip = tip;
        _phoneDot.Visibility = Visibility.Visible;
    }

    void SetSpeed(string down, string up)
    {
        var tag = up == null ? down : down + "|" + up;
        if ((string)_speed.Tag == tag) return;
        _speed.Tag = tag;
        _speed.Inlines.Clear();
        _speed.Inlines.Add(new System.Windows.Documents.Run("↓ ") { Foreground = ArrowBlue, FontWeight = FontWeights.Bold });
        _speed.Inlines.Add(new System.Windows.Documents.Run(down));
        if (up == null) return;
        _speed.Inlines.Add(new System.Windows.Documents.Run("  ↑ ") { Foreground = UpTeal, FontWeight = FontWeights.Bold });
        _speed.Inlines.Add(new System.Windows.Documents.Run(up));
    }

    // place each item in the user's chosen zone; order within a zone is fixed
    void Arrange()
    {
        var s = Settings.Cur;
        ZoneL.Children.Clear(); ZoneC.Children.Clear(); ZoneR.Children.Clear();
        void Put(string pos, UIElement e) => (pos switch { "left" => ZoneL, "right" => ZoneR, _ => ZoneC }).Children.Add(e);
        Put(s.PosWave, _wave); Put(s.PosUsage, _usageBox); Put(s.PosSpeed, _speed); Put(s.PosClock, TimeText); Put(s.PosBattery, _batBox); Put(s.PosVoice, _voiceDot); Put(s.PosPhone, _phoneDot);
    }

    // All components have equal margin: gap between any two components = IdleGap, no extra empty space.
    void ApplyGap()
    {
        var m = new Thickness(Settings.Cur.IdleGap / 2.0, 0, Settings.Cur.IdleGap / 2.0, 0);
        TimeText.Margin = _batBox.Margin = _speed.Margin = _wave.Margin = _chipC.Margin = _chipX.Margin = _chipA.Margin = _voiceDot.Margin = _phoneDot.Margin = m;
        _usageBox.Margin = new Thickness(0);
    }

    // Fixed width for changing text (clock, speed, percent) so other components don't shift.
    void FixWidths()
    {
        ApplyGap();
        var s = Settings.Cur;
        string[] clocks = s.ClockStyle switch
        {
            "colon" => s.ClockSeconds ? new[] { "88:88:88", "00:00:00" } : new[] { "88:88", "00:00" },
            "12h" => s.ClockSeconds ? new[] { "88:88:88 PM", "00:00:00 PM" } : new[] { "88:88 PM", "00:00 PM" },
            _ => s.ClockSeconds ? new[] { "88.88.88", "00.00.00" } : new[] { "88.88", "00.00" },
        };
        TimeText.TextAlignment = TextAlignment.Center;
        TimeText.Width = clocks.Max(c => UI.TextWidth(c, 17, FontWeights.Bold)) + 2;
        _speed.MinWidth = UI.TextWidth(s.IdleSpeedStyle == "both" ? "↓ 88K  ↑ 88K" : "↓ 88K", 13, FontWeights.SemiBold);
        _batText.MinWidth = UI.TextWidth("88%", 14, FontWeights.Bold);   // 2 digits: fixed width without extra space; "100%" only shifts once
        _batText.TextAlignment = TextAlignment.Right;
        _chipC.Fix(); _chipX.Fix(); _chipA.Fix();
    }

    void BuildIdleWidgets()
    {
        Grid.SetColumn(Row2, 1);
        Row2Host.Children.Add(Row2);
        DiscordRpc.I.Changed += () => { _idleKey = ""; RefreshIdle(); };
        ShareServer.I.DevicesChanged += () => { _idleKey = ""; RefreshIdle(); };
        PhoneNotifs.Posted += n =>
        {
            // idle is single-line: newest notification replaces the old one (full conversations are in the Communication tab)
            // another notification from the same app while the old one is still shown is merged: one row with "(+n)" badge, not a constantly flipping row
            int count = _rows.Count > 0 && _rows[0].N.Pkg == n.Pkg && _rows[0].N.Key != n.Key && DateTime.Now < _rows[0].Until ? _rows[0].Count + 1 : _rows.Count > 0 && _rows[0].N.Key == n.Key ? _rows[0].Count : 1;
            _rows.Clear();
            _rows.Add(new NotifRow { N = n, Count = count, Until = DateTime.Now.AddSeconds(Math.Max(2, Settings.Cur.NotifShowSec)) });
            _idleKey = ""; RefreshIdle();
        };
        PhoneNotifs.Changed += () => { _rows.RemoveAll(r => !r.N.Key.StartsWith("test") && !PhoneNotifs.IsActive(r.N.Key)); RefreshIdle(); };   // replied/dismissed on phone or closed = row removed
        _usageBox.Children.Add(_chipC); _usageBox.Children.Add(_chipX); _usageBox.Children.Add(_chipA);
        _speed.Margin = new Thickness(7, 0, 7, 0);
        for (int i = 0; i < 4; i++)
        {
            var b = new Border { Width = 3, Height = 4, Margin = new Thickness(1, 0, 0, 0), CornerRadius = new CornerRadius(1.5), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            _bars.Add(b);
            _wave.Children.Add(b);
        }
        _batBox.Children.Add(_batText);
        _batBox.Children.Add(_batIcon);
        Arrange();
        FixWidths();
        UpdateBattery();
    }

    void SetWave(bool on)
    {
        if (on == _waveOn) return;
        _waveOn = on;
        _wave.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _bars.Count; i++)
        {
            if (on)
            {
                var a = new DoubleAnimation(4, 14, TimeSpan.FromMilliseconds(380 + i * 110)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(a, 20);
                _bars[i].BeginAnimation(HeightProperty, a);
            }
            else { _bars[i].BeginAnimation(HeightProperty, null); _bars[i].Height = 4; }
        }
    }

    // ReyLand-style battery icon: "80%" + accented body with green fill that scales with percentage
    void UpdateBattery()
    {
        _batIcon.Children.Clear();
        var ps = WF.SystemInformation.PowerStatus;
        bool none = (ps.BatteryChargeStatus & WF.BatteryChargeStatus.NoSystemBattery) != 0;
        var vis = none || !Settings.Cur.IdleBattery ? Visibility.Collapsed : Visibility.Visible;
        _batBox.Visibility = _batText.Visibility = _batIcon.Visibility = vis;
        if (vis != Visibility.Visible) return;
        double p = Math.Clamp(ps.BatteryLifePercent, 0, 1);
        _batText.Text = Math.Round(p * 100) + "%";
        _batText.Visibility = Settings.Cur.IdleBatteryPct ? Visibility.Visible : Visibility.Collapsed;
        Brush fill = p <= .2 && ps.PowerLineStatus != WF.PowerLineStatus.Online ? UI.Bad : BattGreen;
        var body = new Border { Width = 24, Height = 14, CornerRadius = new CornerRadius(4), BorderBrush = UI.B("#D9FFFFFF"), BorderThickness = new Thickness(1.4) };
        var bar = new Border { Width = Math.Max(2.5, 17.5 * p), Height = 8, CornerRadius = new CornerRadius(2), Background = fill };
        Canvas.SetLeft(bar, 3); Canvas.SetTop(bar, 3);
        var nub = new Border { Width = 2.4, Height = 6, CornerRadius = new CornerRadius(0, 2, 2, 0), Background = UI.B("#D9FFFFFF") };
        Canvas.SetLeft(nub, 24.6); Canvas.SetTop(nub, 4);
        _batIcon.Children.Add(body);
        _batIcon.Children.Add(bar);
        _batIcon.Children.Add(nub);
    }

    // used by ShelfView for drag-out so the pill does not close
    public void KeepOpen(bool on) { Dragging = on; if (!on && _open) _closeT.Start(); }

    // used by ToolsView: hide pill before capturing screen
    public void HideForCapture(Action capture, int delayMs = 200)
    {
        if (_capturing) return;
        _capturing = true;
        _closeT.Stop();
        Close_();
        OpenLayer.BeginAnimation(OpacityProperty, null);
        OpenLayer.Visibility = Visibility.Collapsed;
        IdleLayer.BeginAnimation(OpacityProperty, null);
        Pill.BeginAnimation(OpacityProperty, null);
        _pillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _pillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _pillMove.BeginAnimation(TranslateTransform.YProperty, null);
        _shown = false;
        Hide(); // Windows activates the window underneath, so "Window" mode captures the last app
        try { DwmFlush(); } catch { }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try { capture(); }
                catch (Exception ex) { App.Log(ex); }
                finally
                {
                    _capturing = false;
                    SyncVisible();
                    while (_pendingAlerts.Count > 0)
                    {
                        var a = _pendingAlerts.Dequeue();
                        Alert(a.Text, a.Seconds, a.OnClick, a.Sound);
                    }
                }
            };
            t.Start();
        });
    }
}
