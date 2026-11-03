using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>
/// Pratinjau kamera HP di PC. Bingkai datang lewat WebSocket (ShareServer). Untuk dipakai di Zoom/Discord, nyalakan kamera virtual
/// (Pengaturan > Perangkat HP) atau arahkan OBS ke jendela ini / tautan MJPEG.
/// </summary>
public sealed class CameraWindow : Window
{
    static CameraWindow _i;
    readonly Image _img = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _status = UI.T("Menunggu kamera HP…", 12, UI.Dim);
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly ScaleTransform _flip = new(1, 1);
    readonly RotateTransform _rot = new(0);
    long _seq = -1;

    public static void ShowIt()
    {
        ShareServer.I.Start();
        _i ??= new CameraWindow();
        _i.Show();
        if (_i.WindowState == WindowState.Minimized) _i.WindowState = WindowState.Normal;
        _i.Activate();
    }

    /// <summary>Minta aplikasi HP yang tersambung menyalakan kamera sesuai pengaturan (jumlah penerima ditampilkan di status).</summary>
    public static void CamOn()
    {
        if (VirtualCam.NeedsSetup) { VirtualCam.EnsureSetup(CamOn); return; }   // izin sistem diminta sekali, saat kamera pertama dinyalakan
        var s = Settings.Cur;
        int n = ShareServer.I.Command($"cam on {s.CamRes} {s.CamFps} {(s.CamFront ? "front" : "back")}");
        if (n == 0) IslandWindow.Instance?.Alert(L.T("Tidak ada aplikasi HP yang tersambung"), 5);
    }

    public CameraWindow()
    {
        Title = L.T("Kamera HP · ZaxLet");
        Width = 760; Height = 520; MinWidth = 420; MinHeight = 320;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var tg = new TransformGroup();
        tg.Children.Add(_flip); tg.Children.Add(_rot);
        _img.LayoutTransform = tg;
        _img.RenderTransformOrigin = new Point(.5, .5);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 8) };
        void Add(string text, Action a) { var b = UI.Btn(UI.T(text, 12, UI.Fg), a, UI.Card, 10, new Thickness(10, 4, 10, 4)); b.Margin = new Thickness(0, 0, 6, 0); bar.Children.Add(b); }
        Add(L.T("Nyalakan kamera HP"), CamOn);
        Add(L.T("Matikan"), () => ShareServer.I.Command("cam off"));
        Add(L.T("Cermin"), () => _flip.ScaleX = -_flip.ScaleX);
        Add(L.T("Putar 90°"), () => _rot.Angle = (_rot.Angle + 90) % 360);
        Add(L.T("Salin tautan MJPEG (OBS/VLC)"), () => Clipboard.SetText(ShareServer.I.MjpegUrl));
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(6, 0, 0, 0);
        bar.Children.Add(_status);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_img);
        var barBox = new Border { Background = UI.B("#FF111114"), Child = bar };
        Grid.SetRow(barBox, 1);
        root.Children.Add(barBox);
        Content = root;

        _t.Tick += (_, _) => Tick();
        Loaded += (_, _) => _t.Start();
        Closed += (_, _) => { _t.Stop(); _i = null; };
        SourceInitialized += (_, _) => { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int)); };
    }

    void Tick()
    {
        _status.Text = PhoneCam.Active ? L.F("{0} · langsung", PhoneCam.Device) : L.T("Menunggu kamera HP… buka halaman ZaxLet di HP → Pakai kamera HP");
        var seq = PhoneCam.Seq;
        if (seq == _seq || PhoneCam.Latest is not { } jpg) return;
        _seq = seq;
        try
        {
            using var ms = new MemoryStream(jpg);
            var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            _img.Source = frame;
        }
        catch { /* bingkai rusak: lewati */ }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
