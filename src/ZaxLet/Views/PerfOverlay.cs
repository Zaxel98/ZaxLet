using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>
/// RivaTuner-style performance overlay: small box in the corner of the screen, click-through and non-focusing, showing only metrics selected in Settings.
/// Only exists (and only reads sensors) while visible: off = zero cost. Shows in borderless/windowed games; "exclusive fullscreen" games cover any window.
/// </summary>
public sealed class PerfOverlay : Window
{
    static PerfOverlay _cur;
    static bool? _force;   // shortcut/"perf" command: force show or hide until Settings are applied again
    static bool _lastFs;

    readonly PerfProbe _probe;
    readonly TextBlock _text = new() { FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = Brushes.White, LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly HashSet<string> _items = PerfFormat.Items(Settings.Cur.PerfItems);

    /// <summary>Called on every fullscreen check and when Settings are applied: opens or closes the overlay according to mode.</summary>
    public static void Sync(bool fullscreen)
    {
        _lastFs = fullscreen;
        var s = Settings.Cur;
        bool want = _force ?? (s.PerfShow == "on" || s.PerfShow == "game" && fullscreen);
        if (want && _cur == null) { _cur = new PerfOverlay(); _cur.Show(); }
        else if (!want && _cur != null) _cur.Close();
        // FPS feature off (overlay "off" without shortcut, or FPS metric removed): stop ETW session and admin helper (pipe broken -> helper exits)
        if (!(PerfFormat.Items(s.PerfItems).Contains("fps") && (want || s.PerfShow != "off"))) FpsSource.Stop();
    }

    public static bool IsOn => _cur != null;

    public static void Toggle() { _force = _cur == null; Sync(_lastFs); if (_cur != null) AskAccess(); }

    /// <summary>Called when the user enables the overlay (button, "perf" command): permissions needed by selected metrics are requested right away, without opening Settings.</summary>
    public static async void AskAccess()
    {
        try
        {
            var items = PerfFormat.Items(Settings.Cur.PerfItems);
            if (items.Contains("fps")) FpsSource.Ask();
            if ((items.Contains("cpuvolt") || items.Contains("gpuvolt")) && !await PerfProbe.LhmUp())
                IslandWindow.Instance?.Alert(L.T("Tegangan butuh LibreHardwareMonitor: jalankan sebagai admin, lalu Options > Remote Web Server."), 10, null, false);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Settings changed: rebuild so new metric selection and corner take effect.</summary>
    public static void Refresh() { _force = null; _cur?.Close(); Sync(_lastFs); }

    PerfOverlay()
    {
        _probe = new PerfProbe(_items);
        // AllowsTransparency: only the background is transparent (per-pixel alpha), text stays sharp. Old settings used Win32 whole-window alpha (text faded too).
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; MaxWidth = 460; _text.TextWrapping = TextWrapping.Wrap;
        var bg = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(Settings.Cur.PerfBgAlpha, 0, 100) * 255 / 100), 12, 12, 12));
        Content = new Border { Background = bg, CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 6, 9, 6), Child = _text };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | 0x20 | 0x08000000 | Native.WS_EX_TOOLWINDOW);   // click-through, no focus, hidden from Alt+Tab
        };
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeChanged += (_, e) => Place(e.NewSize.Width, e.NewSize.Height);
        _t.Tick += (_, _) => Render();
        Closed += (_, _) => { _t.Stop(); _probe.Dispose(); if (_cur == this) _cur = null; };
        Render();
        _t.Start();
    }

    /// <summary>Snaps to the chosen corner of the work area (bottom corners not covered by taskbar); WinForms gives pixels, WPF needs DIP.</summary>
    void Place(double width, double height)
    {
        var c = Settings.Cur.PerfCorner;
        var a = System.Windows.Forms.Screen.AllScreens;
        var i = Settings.Cur.MonitorIdx;
        var scr = i > 0 && i < a.Length ? a[i] : System.Windows.Forms.Screen.PrimaryScreen;
        var dpi = VisualTreeHelper.GetDpi(this);
        double fx = 1.0 / dpi.DpiScaleX, fy = 1.0 / dpi.DpiScaleY, m = 12;
        var wa = new Rect(scr.WorkingArea.Left * fx, scr.WorkingArea.Top * fy, scr.WorkingArea.Width * fx, scr.WorkingArea.Height * fy);
        Left = c.EndsWith('r') ? wa.Right - width - m : wa.Left + m;
        Top = c.StartsWith('b') ? wa.Bottom - height - m : wa.Top + m;
    }

    void Render()
    {
        var lines = PerfFormat.Compose(_items, _probe.Read());
        _text.Inlines.Clear();
        if (lines.Count == 0) { _text.Inlines.Add(new System.Windows.Documents.Run(L.T("Belum ada metrik dipilih")) { Foreground = Brushes.Gray }); return; }
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) _text.Inlines.Add(new System.Windows.Documents.LineBreak());
            _text.Inlines.Add(new System.Windows.Documents.Run(lines[i].Group.PadRight(5)) { Foreground = new SolidColorBrush(Color.FromRgb(120, 200, 255)), FontWeight = FontWeights.Bold });
            _text.Inlines.Add(new System.Windows.Documents.Run(lines[i].Text));
        }
    }
}
