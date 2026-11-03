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
        ["music"] = ("", "Musik"), ["usage"] = ("", "Usage"), ["monitor"] = ("", "Monitor"),
        ["calendar"] = ("", "Hari ini"), ["prayer"] = ("", "Sholat"), ["shelf"] = ("", "Shelf"),
        ["tools"] = ("", "Tangkap"), ["timer"] = ("", "Timer"), ["eye"] = ("", "Mata"),
        ["settings"] = ("", "Atur"),
        ["water"] = ("\U0001F4A7", "Minum"), ["apps"] = ("", "Komunikasi"),
    };
    public static readonly string[] DefaultOrder = { "music", "usage", "monitor", "calendar", "prayer", "water", "apps", "shelf", "tools", "timer", "eye", "settings" };
    /// <summary>Urutan tab sesuai pilihan pengguna; tab yang belum tersimpan (mis. tab baru) ditaruh sesuai urutan bawaan, "settings" selalu terakhir.</summary>
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

    const double OpenW = 640, TabH = 200, TabBarH = 46, Flare = 12, IdleH = 36, IdleH2 = 60;   // TabH sama untuk semua tab
    static readonly Brush BattGreen = UI.B("#FF22D35A"), ArrowBlue = UI.B("#FF3B82F6"), UpTeal = UI.B("#FF2DD4BF"), ClaudeCol = UI.B("#FFE8967A"), CodexCol = UI.B("#FF86EFAC");

    readonly Dictionary<string, Func<TabView>> _make = new();
    readonly Dictionary<string, TabView> _views = new();
    readonly Dictionary<string, (Border Box, TextBlock Label, TextBlock Icon)> _tabBtns = new();
    readonly DispatcherTimer _closeT = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _peekT = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _altT = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly ScaleTransform _pillScale = new(1, 1);
    readonly TranslateTransform _pillMove = new();
    bool _ghost;   // Alt ditahan di atas pill: pill menghilang sementara supaya yang di belakangnya bisa diklik
    readonly DispatcherTimer _trimOpenT = new() { Interval = TimeSpan.FromSeconds(4) };
    TabView _cur;
    bool _open, _waveOn, _fs, _peek;
    string _alert, _idleKey = "";
    DateTime _alertUntil, _quotaAt = DateTime.MinValue;
    (double W, double H) _idle = (200, IdleH);
    int _batTick = 999;

    // widget idle
    readonly TextBlock _batText = new() { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    readonly Canvas _batIcon = new() { Width = 27, Height = 14, VerticalAlignment = VerticalAlignment.Center };
    readonly System.Windows.Shapes.Ellipse _phoneDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly System.Windows.Shapes.Ellipse _voiceDot = new() { Width = 9, Height = 9, Fill = UI.B("#FF22D35A"), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, ToolTip = "Sedang di voice channel Discord" };
    readonly Marquee Row2 = new() { Foreground = UI.B("#A6FFFFFF") };
    // notifikasi HP yang sedang tampil di pill: satu baris per notifikasi (key unik), terbaru di atas
    sealed class NotifRow { public PhoneNotif N; public DateTime Until; }
    readonly List<NotifRow> _rows = new();
    string _rowsSig = "";
    bool _snooze, _shown = true;      // _snooze: disembunyikan sementara oleh pengguna; _shown: jendela sedang tampil
    DateTime _snoozeUntil = DateTime.MaxValue;
    const double RowH = 22;
    const int HideHotkeyId = 0x151A;
    readonly TextBlock TimeText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(7, 0, 7, 0) };
    readonly StackPanel _batBox = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly UsageChip _chipC = new("C", ClaudeCol), _chipX = new("X", CodexCol);
    readonly StackPanel _usageBox = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _speed = Pct();
    readonly StackPanel _wave = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 0, 7, 0), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    readonly List<Border> _bars = new();

    static TextBlock Pct() => new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };

    public IslandWindow()
    {
        InitializeComponent();
        Instance = this;

        _make["music"] = () => new MusicView();
        _make["usage"] = () => new UsageView();
        _make["monitor"] = () => new MonitorView();
        _make["calendar"] = () => new CalendarView();
        _make["prayer"] = () => new PrayerView();
        _make["shelf"] = () => new ShelfView();
        _make["tools"] = () => new ToolsView();
        _make["timer"] = () => new TimerView();
        _make["eye"] = () => new EyeView();
        _make["water"] = () => new WaterView();
        _make["apps"] = () => new AppsView();
        BuildTabBar();
        BuildIdleWidgets();
        _closeT.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.Cur.CloseDelayMs, 50, 600000));

        Pill.SizeChanged += (_, _) => BuildShape();
        // kursor yang masuk lewat daftar notifikasi tidak membuka pill, supaya daftar bisa digulir dengan roda mouse
        Pill.MouseEnter += (_, _) => { _closeT.Stop(); if (Settings.Cur.HoverOpen && !OverNotifRows() && !(Settings.Cur.AltGhost && Native.AltDown())) Open(); };
        Pill.MouseLeave += (_, _) => { if (_open) _closeT.Start(); };
        Pill.MouseLeftButtonUp += (_, _) => Open();
        Pill.RenderTransformOrigin = new Point(.5, 0);
        Pill.RenderTransform = new TransformGroup { Children = { _pillScale, _pillMove } };
        _altT.Tick += (_, _) => AltCheck();
        _altT.Start();
        _closeT.Tick += (_, _) =>
        {
            // tetap terbuka bila sedang mengetik di kotak teks (to-do, pengaturan) atau kursor masih di atas pill
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
        // beberapa detik setelah tab terbuka, kembalikan halaman yang tidak terpakai (DLL driver grafis dibagi pakai) ke OS
        _trimOpenT.Tick += (_, _) => { _trimOpenT.Stop(); Native.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); };
        MediaService.I.Changed += () => { ApplyTint(); RefreshIdle(); };
        TimerService.I.Tick += RefreshIdle;
        UsageService.I.Changed += () => Dispatcher.BeginInvoke(RefreshIdle);
        Settings.Applied += OnSettingsApplied;
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PositionTop);

        // saat tertutup, kembalikan halaman memori yang tidak terpakai ke OS (RAM yang tampak di Task Manager ikut turun)
        var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        trim.Tick += (_, _) =>
        {
            if (_open) return;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            Native.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
        };
        trim.Start();
        Loaded += (_, _) =>
        {
            Tick(); _tick.Start();
            var warm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            warm.Tick += (_, _) => { warm.Stop(); Prewarm(); };
            warm.Start();
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
            return IntPtr.Zero;
        });
        Hotkey.Register(h, HideHotkeyId, Settings.Cur.HideKey);
    }

    /// <summary>Sembunyikan pill sementara (atau tampilkan lagi). Notifikasi baru tetap memunculkannya sebentar.</summary>
    public void ToggleHidden() => SetSnooze(!_snooze);

    void SetSnooze(bool on)
    {
        _snooze = on;
        _snoozeUntil = on && Settings.Cur.SnoozeMin > 0 ? DateTime.Now.AddMinutes(Settings.Cur.SnoozeMin) : DateTime.MaxValue;
        if (on) { _closeT.Stop(); Close_(); }
        _idleKey = "";
        RefreshIdle();
    }

    // Jendela tampil kecuali: fullscreen (tanpa intipan kursor) atau disembunyikan pengguna; notifikasi yang sedang tampil memaksanya muncul.
    void SyncVisible()
    {
        bool forced = (_fs || _snooze) && Settings.Cur.NotifFullscreen && _rows.Count > 0;
        bool want = !_ghost && (forced || !((_fs && !_peek) || _snooze));
        if (want == _shown) return;
        _shown = want;
        if (want) Show();
        Pop(want);
    }

    // animasi seperti minimize: pill menciut ke tepi atas sambil memudar lalu jendela disembunyikan; tampil kembali dengan gerak sebaliknya
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

    // Alt ditahan + kursor di area pill (termasuk bahunya) = pill disembunyikan; Alt dilepas atau kursor keluar = tampil lagi seperti biasa
    void AltCheck()
    {
        bool want = false;
        if (Settings.Cur.AltGhost && Native.AltDown() && Native.GetCursorPos(out var pt))
        {
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double x = pt.X / dpi, y = pt.Y / dpi, x0 = Left + (Width - Pill.ActualWidth) / 2 - Flare;
            want = x >= x0 && x <= x0 + Pill.ActualWidth + 2 * Flare && y >= Top && y <= Top + Pill.ActualHeight;
        }
        if (want == _ghost) return;
        _ghost = want;
        if (want && _open) { _closeT.Stop(); Close_(); }   // pill yang sudah terbuka ikut diciutkan
        SyncVisible();
    }

    bool OverNotifRows() => NotifHost.IsVisible && new Rect(0, 0, NotifHost.ActualWidth, NotifHost.ActualHeight).Contains(Mouse.GetPosition(NotifHost));

    void PositionTop()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    // ---------- bentuk pill: bahu cekung di atas seperti notch ----------
    void BuildShape()
    {
        double w = Pill.ActualWidth, h = Pill.ActualHeight;
        if (w < 10 || h < 10) return;
        double R = Flare, r = Math.Clamp(h / 2, 14, 28);
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

    // ---------- bar tab ----------
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
            b.Icon.Foreground = on ? UI.Accent : UI.Dim;
        }
    }

    // ---------- buka / tutup ----------
    public void OpenNow() { if (_snooze) SetSnooze(false); Open(); _closeT.Start(); }

    void Open()
    {
        if (_open || _fs || _snooze) return;   // saat fullscreen pill hanya menampilkan jam dan baterai
        _open = true;
        OpenLayer.Visibility = Visibility.Visible;
        Fade(IdleLayer, 0, 90, () => { if (_open) IdleLayer.Visibility = Visibility.Collapsed; });
        Fade(OpenLayer, 1, 200, null, 70);
        Select(Settings.Cur.OpenTab != "last" && TabMeta.ContainsKey(Settings.Cur.OpenTab) ? Settings.Cur.OpenTab : Settings.Cur.LastTab == "settings" ? "music" : Settings.Cur.LastTab);
    }

    void Close_()
    {
        if (!_open) return;
        _open = false;
        _cur?.OnHide();
        Keyboard.ClearFocus();
        Settings.Save();
        Shape.Fill = Brushes.Black;
        IdleLayer.Visibility = Visibility.Visible;
        Fade(OpenLayer, 0, 90, () => { if (!_open) OpenLayer.Visibility = Visibility.Collapsed; });
        Fade(IdleLayer, 1, 180, null, 60);
        RefreshIdle();
        SetSize(_idle.W, _idle.H);
    }

    /// <summary>Tab baru (bukan yang di pill); dipakai pratinjau di jendela pengaturan.</summary>
    public TabView MakeView(string key)
    {
        var v = _make[key]();
        // semua tab setinggi sama: tab pendek dipusatkan, tab lain mengisi tinggi
        foreach (UIElement c in v.Children)
            if (c is FrameworkElement fe) fe.VerticalAlignment = v.CenterV ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        return v;
    }

    /// <summary>Bangun tab monitor lebih awal dan ambil sampel pertamanya agar langsung terisi saat dibuka (lonjakan sekali di awal).</summary>
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
        // transisi: tab baru memudar masuk sambil bergeser dari arah tab yang dituju (kanan bila indeks lebih besar)
        int dir = prevKey == null || prevKey == key ? 0 : Array.IndexOf(TabOrder, key) > Array.IndexOf(TabOrder, prevKey) ? 1 : -1;
        var slide = new TranslateTransform(dir * 22, dir == 0 ? 8 : 0);
        v.RenderTransform = slide;
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dir * 22, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dir == 0 ? 8 : 0, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        v.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        Highlight();
        v.OnShow();
        SetSize(OpenW, TabH + TabBarH);
        ApplyTint();
        _trimOpenT.Stop();
        _trimOpenT.Start();
    }

    void SetSize(double w, double h)
    {
        var e = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(300);
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = e });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, d) { EasingFunction = e });
    }

    static void Fade(UIElement el, double to, int ms, Action done, int delay = 0)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay) };
        if (done != null) a.Completed += (_, _) => done();
        el.BeginAnimation(OpacityProperty, a);
    }

    void ApplyTint()
    {
        if (!_open) return;
        var c = MediaService.I.CoverColor;
        if (_cur?.Key != "music" || c == null || !MediaService.I.HasSession || !Settings.Cur.TintCover) { Shape.Fill = Brushes.Black; return; }
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
        _quotaAt = DateTime.MinValue;
        Hotkey.Register(new WindowInteropHelper(this).Handle, HideHotkeyId, Settings.Cur.HideKey);
        Arrange();
        FixWidths();
        UpdateBattery();
        RefreshIdle();
        ApplyTint();
    }

    // ---------- auto-hide saat fullscreen ----------
    void CheckFullscreen()
    {
        bool fs = Settings.Cur.AutoHideFullscreen && Native.ForegroundFullscreenOnPrimary();
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
            _peekT.Stop();
            _peek = false;
        }
        _idleKey = "";
        RefreshIdle();   // memanggil SyncVisible
    }

    void Peek()
    {
        Native.GetCursorPos(out var pt);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double cx = WF.Screen.PrimaryScreen.Bounds.Left + WF.Screen.PrimaryScreen.Bounds.Width / 2.0;
        double halfW = (_peek ? Pill.ActualWidth / 2 + 8 : 100) * dpi;
        bool inX = Math.Abs(pt.X - cx) <= halfW;
        if (!_peek && pt.Y <= 2 && inX) { _peek = true; SyncVisible(); RefreshIdle(); }
        else if (_peek && !(inX && pt.Y <= (Pill.ActualHeight + 8) * dpi)) { _peek = false; SyncVisible(); }
    }

    // ---------- idle ----------
    public void Alert(string text, double seconds = 7)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _alert = text;
            _alertUntil = DateTime.Now.AddSeconds(seconds);
            RefreshIdle();
        });
        SystemSounds.Asterisk.Play();
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
        if (!_fs && s.IdleUsage && DateTime.Now >= _quotaAt)
        {
            _quotaAt = DateTime.Now.AddSeconds(Math.Max(1, s.UsageRefreshSec));
            _ = UsageService.I.RefreshQuotaAsync();
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

        var clock = ClockText();
        if (TimeText.Text != clock) TimeText.Text = clock;

        // tengah: usage AI, unduh, waveform
        bool c = _chipC.Update(UsageService.I.Claude, full && s.IdleUsage && s.ClaudeOn && s.IdleUsageProviders != "codex");
        bool x = _chipX.Update(UsageService.I.Codex, full && s.IdleUsage && s.CodexOn && s.IdleUsageProviders != "claude");
        _usageBox.Visibility = c || x ? Visibility.Visible : Visibility.Collapsed;
        _voiceDot.Visibility = full && DiscordRpc.I.InVoice ? Visibility.Visible : Visibility.Collapsed;   // titik hijau: sedang di voice channel
        UpdatePhoneDot(full);
        bool sp = full && s.IdleSpeed;
        if (sp) { NetMeter.Sample(); SetSpeed(NetMeter.Fmt(NetMeter.DownKb), s.IdleSpeedStyle == "both" ? NetMeter.Fmt(NetMeter.UpKb) : null); }
        _speed.Visibility = sp ? Visibility.Visible : Visibility.Collapsed;
        SetWave(full && s.IdleWave && ms.HasSession && ms.Playing);

        // daftar notifikasi HP: kedaluwarsa per baris (tidak selagi kursor di atasnya, supaya sempat digulir/diklik)
        if (!OverNotifRows()) _rows.RemoveAll(r => DateTime.Now >= r.Until);
        const int maxRows = 1;
        int shownRows = Math.Min(_rows.Count, maxRows);
        UpdateNotifRows(maxRows);

        // baris di bawah notifikasi: peringatan > timer > lirik (tetap ada walau notifikasi menumpuk)
        string text = null;
        Brush br = UI.Dim;
        if (_alert != null)
        {
            if (DateTime.Now < _alertUntil) { text = _alert; br = UI.Accent; }
            else _alert = null;
        }
        if (_fs || (text == null && shownRows > 0)) text = null;   // notifikasi menggantikan timer/lirik: idle tetap satu baris
        else if (text == null && TimerService.I.Running)
            text = (TimerService.I.Mode == TMode.Stopwatch ? "Stopwatch " : TimerService.I.PomoBreak ? "Istirahat " : "Timer ") + UI.Clock(TimerService.I.Value);
        if (!_fs && text == null && shownRows == 0 && s.MusicLyrics && s.IdleLyrics && ms.Playing && ms.Lyrics.Count > 0)   // lirik dimatikan = idle ikut mati
        {
            var l = ms.CurrentLine();
            text = string.IsNullOrWhiteSpace(l) ? "♪" : s.IdleRomaji ? Romaji.Of(l) ?? l : l;
        }
        Row2.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        if (Row2.Text != (text ?? "")) Row2.Text = text ?? "";
        Row2.Foreground = br;

        var key = $"{clock.Length}|{c}|{x}|{sp}|{_chipC.Key}|{_chipX.Key}|{_speed.Tag}|{_batText.Text}|{_batText.Visibility}|{_waveOn}|{text != null}|{shownRows}|{_phoneDot.Visibility}|{_voiceDot.Visibility}";
        if (key != _idleKey)
        {
            _idleKey = key;
            double w = IdleWidth();
            int lines = shownRows + (text != null ? 1 : 0);
            _idle = lines > 0 ? (Math.Max(w, 340), IdleH + 2 + RowH * lines) : (w, IdleH);
            if (!_open) SetSize(_idle.W, _idle.H);
        }
        SyncVisible();
    }

    // bangun ulang baris hanya bila isinya berubah (Marquee menjalankan animasi sendiri)
    void UpdateNotifRows(int maxRows)
    {
        var sig = string.Join("¦", _rows.Select(r => r.N.Key + "|" + r.N.Title + "|" + r.N.Text)) + "#" + maxRows;
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
            var m = new Marquee { Foreground = Brushes.White, Text = (string.IsNullOrWhiteSpace(n.Title) ? "" : n.Title + ": ") + n.Text };
            Grid.SetColumn(m, 1);
            g.Children.Add(m);
            g.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenRow(r); };   // klik: buka percakapan (bisa dibalas) atau tutup baris
            g.MouseRightButtonUp += (_, e) => { e.Handled = true; _rows.Remove(r); RefreshIdle(); };
            NotifRows.Children.Add(g);
        }
        if (_rows.Count > 0) NotifScroll.ScrollToTop();
    }

    void OpenRow(NotifRow r)
    {
        _rows.Remove(r);
        if (r.N.CanReply && !_fs && !_snooze) { Open(); Select("apps"); }
        RefreshIdle();
    }

    // Lebar pill idle mengikuti isi. Bila zona tengah berisi, kiri dan kanan dibuat selebar yang terbesar agar isi tengah
    // tepat di tengah; bila kosong, tersisa celah 56 px (area kamera) dan kiri/kanan berbagi lebar sesuai isinya.
    double IdleWidth()
    {
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        ZoneL.Measure(inf); ZoneC.Measure(inf); ZoneR.Measure(inf);
        double l = ZoneL.DesiredSize.Width, c = ZoneC.DesiredSize.Width, r = ZoneR.DesiredSize.Width;
        var s = Settings.Cur;
        bool exact = s.IdleExactCenter && c > 0;   // komponen tengah tepat di tengah pill (= tengah layar); sisi yang lebih pendek mengisi selisihnya
        var cols = IdleRow.ColumnDefinitions;
        cols[0].Width = cols[2].Width = exact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        IdleRow.HorizontalAlignment = exact ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        return exact ? Math.Max(120, c + 2 * Math.Max(l, r) + 28) : Math.Max(120, l + c + r + 28);   // jarak antar komponen tetap: Settings.IdleGap
    }

    // titik status HP, dua keadaan saja: menyala (terisi) = HP tersambung; kosong (hanya cincin) = tidak tersambung, apa pun sebabnya
    void UpdatePhoneDot(bool full)
    {
        if (!full || !Settings.Cur.IdlePhoneDot) { _phoneDot.Visibility = Visibility.Collapsed; return; }
        bool on = ShareServer.I.On && ShareServer.I.AppCount > 0;
        var c = UI.B("#FF38BDF8");
        _phoneDot.Fill = on ? c : Brushes.Transparent;
        _phoneDot.Stroke = on ? c : UI.B("#99FFFFFF");
        _phoneDot.StrokeThickness = 1.4;
        _phoneDot.ToolTip = on ? "HP tersambung" : "HP tidak tersambung";
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

    // tempatkan tiap item di zona pilihan pengguna; urutan di dalam zona tetap
    void Arrange()
    {
        var s = Settings.Cur;
        ZoneL.Children.Clear(); ZoneC.Children.Clear(); ZoneR.Children.Clear();
        void Put(string pos, UIElement e) => (pos switch { "left" => ZoneL, "right" => ZoneR, _ => ZoneC }).Children.Add(e);
        Put(s.PosWave, _wave); Put(s.PosUsage, _usageBox); Put(s.PosSpeed, _speed); Put(s.PosClock, TimeText); Put(s.PosBattery, _batBox); Put(s.PosVoice, _voiceDot); Put(s.PosPhone, _phoneDot);
    }

    // Semua komponen punya margin sama: jarak antar dua komponen = IdleGap, tidak ada ruang kosong tambahan.
    void ApplyGap()
    {
        var m = new Thickness(Settings.Cur.IdleGap / 2.0, 0, Settings.Cur.IdleGap / 2.0, 0);
        TimeText.Margin = _batBox.Margin = _speed.Margin = _wave.Margin = _chipC.Margin = _chipX.Margin = _voiceDot.Margin = _phoneDot.Margin = m;
        _usageBox.Margin = new Thickness(0);
    }

    // Lebar tetap untuk teks yang berubah-ubah (jam, kecepatan, persen) agar komponen lain tidak bergeser.
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
        _batText.MinWidth = UI.TextWidth("88%", 14, FontWeights.Bold);   // 2 digit: lebar tetap tanpa ruang kosong; "100%" hanya menggeser sekali
        _batText.TextAlignment = TextAlignment.Right;
        _chipC.Fix(); _chipX.Fix();
    }

    void BuildIdleWidgets()
    {
        Grid.SetColumn(Row2, 1);
        Row2Host.Children.Add(Row2);
        DiscordRpc.I.Changed += () => { _idleKey = ""; RefreshIdle(); };
        ShareServer.I.DevicesChanged += () => { _idleKey = ""; RefreshIdle(); };
        PhoneNotifs.Posted += n =>
        {
            // idle hanya satu baris: notifikasi terbaru menggantikan yang lama (percakapan lengkap ada di tab Komunikasi)
            _rows.Clear();
            _rows.Add(new NotifRow { N = n, Until = DateTime.Now.AddSeconds(Math.Max(2, Settings.Cur.NotifShowSec)) });
            _idleKey = ""; RefreshIdle();
        };
        PhoneNotifs.Changed += () => { _rows.RemoveAll(r => !r.N.Key.StartsWith("test") && !PhoneNotifs.IsActive(r.N.Key)); RefreshIdle(); };   // dibalas/dihapus di HP atau disilang = baris hilang
        _usageBox.Children.Add(_chipC); _usageBox.Children.Add(_chipX);
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

    // ikon baterai gaya ReyLand: "80%" + badan beraksen isi hijau yang melebar sesuai persen
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

    // dipakai ShelfView untuk drag keluar agar pill tidak menutup
    public void KeepOpen(bool on) { Dragging = on; if (!on && _open) _closeT.Start(); }

    // dipakai ToolsView: sembunyikan pill sebelum menangkap layar
    public void HideForCapture(Action capture, int delayMs = 350)
    {
        _closeT.Stop();
        Close_();
        Hide(); // Windows mengaktifkan jendela di bawahnya, jadi mode "Jendela" menangkap aplikasi terakhir
        _shown = false;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            try { capture(); }
            catch (Exception ex) { App.Log(ex); }
            finally { SyncVisible(); }
        };
        t.Start();
    }
}
