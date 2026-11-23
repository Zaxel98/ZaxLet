using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>
/// Phone screen viewer window with mouse and keyboard control back to the phone via /scr/ws.
/// </summary>
public sealed class ScreenWindow : Window
{
    static ScreenWindow _cur;

    readonly Border _viewport;
    readonly Image _img = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _status = UI.T("", 12, UI.Dim);
    readonly DispatcherTimer _textTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly StringBuilder _textBuf = new();

    long _shownConnId = -1;
    bool _closingFromDisconnect;
    bool _dragging;
    Point _downPos;
    double _downNx, _downNy;
    long _downTicks;

    public static void OnUpdate()
    {
        if (!PhoneScreen.Connected) return;
        var frame = PhoneScreen.LatestFrame;
        if (frame == null)
        {
            _cur?.RefreshStatus();
            return;
        }

        if (_cur == null)
        {
            _cur = new ScreenWindow();
            _cur.Show();
        }
        if (_cur._shownConnId != PhoneScreen.ConnId)
        {
            _cur._shownConnId = PhoneScreen.ConnId;
            if (_cur.WindowState == WindowState.Minimized) _cur.WindowState = WindowState.Normal;
            _cur.Activate();
        }
        _cur._img.Source = frame;
        _cur.RefreshStatus();
    }

    public static void OnDisconnected()
    {
        if (_cur == null || PhoneScreen.Connected) return;
        _cur._closingFromDisconnect = true;
        _cur.Close();
    }

    ScreenWindow()
    {
        Title = "HP · layar";
        Width = 420; Height = 820; MinWidth = 280; MinHeight = 360;
        Background = UI.WinBg;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _viewport = new Border
        {
            Background = Brushes.Black,
            ClipToBounds = true,
            Child = _img
        };
        _viewport.MouseLeftButtonDown += OnLeftDown;
        _viewport.MouseLeftButtonUp += OnLeftUp;
        _viewport.MouseRightButtonDown += (_, e) => { e.Handled = true; SendKey("back"); };
        _viewport.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; SendKey("home"); } };
        _viewport.MouseWheel += OnWheel;
        _viewport.LostMouseCapture += (_, _) => _dragging = false;

        var bar = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        void AddBtn(string label, string key)
        {
            var b = UI.Btn(UI.T(label, 12, UI.Fg), () => SendKey(key), UI.Card, 8, new Thickness(10, 4, 10, 4));
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Focusable = false;
            btns.Children.Add(b);
        }
        AddBtn("Kembali", "back");
        AddBtn("Beranda", "home");
        AddBtn("Terbaru", "recents");
        DockPanel.SetDock(btns, Dock.Left);
        bar.Children.Add(btns);

        _status.Margin = new Thickness(6, 0, 0, 0);
        bar.Children.Add(_status);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_viewport);
        var barBox = new Border
        {
            Background = UI.WinCard,
            BorderBrush = UI.WinSep,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = bar
        };
        Grid.SetRow(barBox, 1);
        root.Children.Add(barBox);
        Content = root;

        _textTimer.Tick += (_, _) => FlushText();
        TextInput += OnTextInput;
        SourceInitialized += (_, _) => { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int)); };
        Closed += (_, _) =>
        {
            _textTimer.Stop();
            _cur = null;
            if (!_closingFromDisconnect) PhoneScreen.Disconnect();
        };
    }

    void RefreshStatus()
    {
        var err = PhoneScreen.Error;
        if (!string.IsNullOrEmpty(err))
        {
            _status.Text = err;
            _status.Foreground = UI.Bad;
            return;
        }
        _status.Foreground = UI.Dim;
        int w = PhoneScreen.Width, h = PhoneScreen.Height;
        if (w <= 0 && _img.Source is BitmapSource bmp) { w = bmp.PixelWidth; h = bmp.PixelHeight; }
        _status.Text = w > 0 ? $"{PhoneScreen.Fps:0} fps · {w}×{h}" : $"{PhoneScreen.Fps:0} fps";
    }

    bool TryGetImageRect(out double ox, out double oy, out double rw, out double rh)
    {
        ox = oy = rw = rh = 0;
        if (_img.Source is not BitmapSource bmp || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0) return false;
        double vw = _viewport.ActualWidth, vh = _viewport.ActualHeight;
        if (vw <= 0 || vh <= 0) return false;
        double imgAspect = (double)bmp.PixelWidth / bmp.PixelHeight;
        double viewAspect = vw / vh;
        if (viewAspect > imgAspect)
        {
            rh = vh;
            rw = vh * imgAspect;
            ox = (vw - rw) / 2.0;
        }
        else
        {
            rw = vw;
            rh = vw / imgAspect;
            oy = (vh - rh) / 2.0;
        }
        return rw > 0 && rh > 0;
    }

    bool TryMapPoint(Point p, out double nx, out double ny)
    {
        nx = ny = 0;
        if (!TryGetImageRect(out var ox, out var oy, out var rw, out var rh)) return false;
        double rx = p.X - ox, ry = p.Y - oy;
        if (rx < 0 || ry < 0 || rx > rw || ry > rh) return false;
        nx = Math.Clamp(rx / rw, 0, 1);
        ny = Math.Clamp(ry / rh, 0, 1);
        return true;
    }

    void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(_viewport);
        if (!TryMapPoint(pos, out _downNx, out _downNy)) return;
        _downPos = pos;
        _downTicks = Environment.TickCount64;
        _dragging = true;
        _viewport.CaptureMouse();
        e.Handled = true;
    }

    void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _viewport.ReleaseMouseCapture();
        e.Handled = true;

        var upPos = e.GetPosition(_viewport);
        double dist = (upPos - _downPos).Length;
        long elapsed = Environment.TickCount64 - _downTicks;

        if (dist < 8)
        {
            if (elapsed < 600)
                PhoneScreen.Send(string.Create(CultureInfo.InvariantCulture, $"{{\"t\":\"tap\",\"x\":{_downNx:0.####},\"y\":{_downNy:0.####}}}"));
            else
                PhoneScreen.Send(string.Create(CultureInfo.InvariantCulture, $"{{\"t\":\"press\",\"x\":{_downNx:0.####},\"y\":{_downNy:0.####},\"ms\":{elapsed}}}"));
        }
        else if (TryGetImageRect(out var ox, out var oy, out var rw, out var rh))
        {
            double nx2 = Math.Clamp((upPos.X - ox) / rw, 0, 1);
            double ny2 = Math.Clamp((upPos.Y - oy) / rh, 0, 1);
            long ms = Math.Clamp(elapsed, 100, 1500);
            PhoneScreen.Send(string.Create(CultureInfo.InvariantCulture,
                $"{{\"t\":\"swipe\",\"x1\":{_downNx:0.####},\"y1\":{_downNy:0.####},\"x2\":{nx2:0.####},\"y2\":{ny2:0.####},\"ms\":{ms}}}"));
        }
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || !TryMapPoint(e.GetPosition(_viewport), out var nx, out var ny)) return;
        int dy = e.Delta > 0 ? 1 : -1;
        PhoneScreen.Send(string.Create(CultureInfo.InvariantCulture, $"{{\"t\":\"scroll\",\"x\":{nx:0.####},\"y\":{ny:0.####},\"dy\":{dy}}}"));
        e.Handled = true;
    }

    void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        bool added = false;
        foreach (var ch in e.Text)
        {
            if (!char.IsControl(ch)) { _textBuf.Append(ch); added = true; }
        }
        if (added && !_textTimer.IsEnabled) _textTimer.Start();
    }

    void FlushText()
    {
        _textTimer.Stop();
        if (_textBuf.Length == 0) return;
        var s = _textBuf.ToString();
        _textBuf.Clear();
        PhoneScreen.Send(JsonSerializer.Serialize(new { t = "text", s }));
    }

    static void SendKey(string k) => PhoneScreen.Send($"{{\"t\":\"key\",\"k\":\"{k}\"}}");

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
