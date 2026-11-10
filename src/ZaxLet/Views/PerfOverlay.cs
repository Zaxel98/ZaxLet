using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>
/// Overlay performa ala RivaTuner: kotak kecil di pojok layar, tidak bisa diklik dan tidak mengambil fokus, hanya berisi metrik yang dipilih di Pengaturan.
/// Hanya ada (dan hanya membaca sensor) selama tampil: mati = nol biaya. Tampil di game borderless/windowed; game "exclusive fullscreen" menimpa jendela apa pun.
/// </summary>
public sealed class PerfOverlay : Window
{
    static PerfOverlay _cur;
    static bool? _force;   // pintasan/perintah "perf": paksa tampil atau sembunyi sampai Pengaturan diterapkan lagi
    static bool _lastFs;

    readonly PerfProbe _probe;
    readonly TextBlock _text = new() { FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = Brushes.White, LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly HashSet<string> _items = PerfFormat.Items(Settings.Cur.PerfItems);

    /// <summary>Dipanggil tiap pemeriksaan layar penuh dan saat Pengaturan diterapkan: membuka atau menutup overlay sesuai mode.</summary>
    public static void Sync(bool fullscreen)
    {
        _lastFs = fullscreen;
        var s = Settings.Cur;
        bool want = _force ?? (s.PerfShow == "on" || s.PerfShow == "game" && fullscreen);
        if (want && _cur == null) { _cur = new PerfOverlay(); _cur.Show(); }
        else if (!want && _cur != null) _cur.Close();
    }

    public static bool IsOn => _cur != null;

    public static void Toggle() { _force = _cur == null; Sync(_lastFs); if (_cur != null) AskAccess(); }

    /// <summary>Dipanggil saat pengguna menyalakan overlay (tombol, perintah "perf"): izin yang dibutuhkan metrik terpilih diminta saat itu juga, tanpa membuka Pengaturan.</summary>
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

    /// <summary>Pengaturan berubah: bangun ulang agar pilihan metrik dan pojok baru berlaku.</summary>
    public static void Refresh() { _force = null; _cur?.Close(); Sync(_lastFs); }

    PerfOverlay()
    {
        _probe = new PerfProbe(_items);
        // AllowsTransparency: hanya latar yang tembus (alfa per piksel), teks tetap tajam. Pengaturan lama memakai alfa Win32 seluruh jendela (teks ikut pudar).
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; MaxWidth = 460; _text.TextWrapping = TextWrapping.Wrap;
        var bg = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(Settings.Cur.PerfBgAlpha, 0, 100) * 255 / 100), 12, 12, 12));
        Content = new Border { Background = bg, CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 6, 9, 6), Child = _text };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | 0x20 | 0x08000000 | Native.WS_EX_TOOLWINDOW);   // klik tembus, tanpa fokus, tidak di Alt+Tab
        };
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeChanged += (_, e) => Place(e.NewSize.Width, e.NewSize.Height);
        _t.Tick += (_, _) => Render();
        Closed += (_, _) => { _t.Stop(); _probe.Dispose(); if (_cur == this) _cur = null; };
        Render();
        _t.Start();
    }

    /// <summary>Menempel di pojok pilihan pada area kerja (pojok bawah tidak tertutup taskbar); WinForms memberi piksel, WPF butuh DIP.</summary>
    void Place(double width, double height)
    {
        var c = Settings.Cur.PerfCorner;
        var scr = System.Windows.Forms.Screen.PrimaryScreen;
        double f = SystemParameters.PrimaryScreenWidth / scr.Bounds.Width, m = 12;
        var wa = new Rect(scr.WorkingArea.Left * f, scr.WorkingArea.Top * f, scr.WorkingArea.Width * f, scr.WorkingArea.Height * f);
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
