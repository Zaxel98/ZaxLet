using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>
/// Mini teleprompter di bawah kamera web: tampil 3-4 baris, tidak ikut terekam layar (WDA_EXCLUDEFROMCAPTURE),
/// tembus klik secara bawaan, jeda saat kursor di atasnya, gulir roda mouse untuk kecepatan, klik kanan atau "prompter stop" untuk menutup.
/// </summary>
public sealed class PrompterWindow : Window
{
    static PrompterWindow _cur;

    public static bool IsOpen => _cur != null;

    public static void Stop() => _cur?.Close();

    public static void Start(string script)
    {
        _cur?.Close();
        _cur = new PrompterWindow(script);
        _cur.Show();
    }

    readonly string _script;
    readonly int _wordCount;
    readonly TextBlock _text = new()
    {
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };
    readonly TranslateTransform _shift = new();
    readonly Grid _viewport = new() { ClipToBounds = true };
    readonly Border _card;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    IntPtr _hwnd;
    bool _hover;
    bool _captureBlocked;
    double _offset;
    double _maxOffset;
    double _elapsedShort;
    DateTime _lastTick;

    PrompterWindow(string script)
    {
        _script = script;
        _wordCount = CountWords(script);
        _text.RenderTransform = _shift;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = 0;   // tetap tersembunyi sampai SetWindowDisplayAffinity berhasil

        _viewport.Children.Add(_text);
        _card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(215, 12, 12, 14)),
            BorderBrush = UI.Track,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 10, 18, 10),
            Child = _viewport,
        };
        Content = _card;

        SourceInitialized += OnSourceInitialized;
        MouseWheel += OnWheel;
        MouseRightButtonUp += (_, e) => { e.Handled = true; Close(); };
        Settings.Applied += OnSettingsApplied;
        _timer.Tick += OnTick;
        Closed += OnClosed;
    }

    void OnSourceInitialized(object sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE,
            ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ||
            !Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE))
        {
            _captureBlocked = true;
            _text.Text = "";
            IslandWindow.Instance?.Alert(L.T("Tidak bisa disembunyikan dari rekaman di Windows ini"), 6, null, false);
            Dispatcher.BeginInvoke(Close);
            return;
        }

        _text.Text = _script;
        ApplyLayout(keepProgress: false);
        Opacity = 1;
        _lastTick = DateTime.UtcNow;
        _timer.Start();
    }

    void OnSettingsApplied() => ApplyLayout(keepProgress: true);

    void ApplyLayout(bool keepProgress)
    {
        if (_captureBlocked) return;
        double oldMax = _maxOffset;
        double ratio = oldMax > 0 ? Math.Clamp(_offset / oldMax, 0, 1) : 0;

        int font = Math.Clamp(Settings.Cur.PrompterFont, 16, 48);
        double lh = Math.Round(font * 1.35);
        _text.FontSize = font;
        _text.LineHeight = lh;

        var a = System.Windows.Forms.Screen.AllScreens;
        int idx = Settings.Cur.MonitorIdx;
        var scr = idx > 0 && idx < a.Length ? a[idx] : System.Windows.Forms.Screen.PrimaryScreen;
        var dpi = VisualTreeHelper.GetDpi(this);
        double fx = 1.0 / dpi.DpiScaleX, fy = 1.0 / dpi.DpiScaleY;

        double waLeft = scr.WorkingArea.Left * fx;
        double waWidth = scr.WorkingArea.Width * fx;
        double w = Math.Min(540, Math.Max(280, waWidth - 24));
        double vpH = lh * 3.5;

        Width = w;
        _viewport.Height = vpH;
        Height = vpH + 22;   // padding atas-bawah + border
        _text.Width = Math.Max(100, w - 38);

        double cx = (scr.Bounds.Left + scr.Bounds.Width / 2.0) * fx;
        Left = Math.Clamp(cx - w / 2.0, waLeft + 8, waLeft + waWidth - w - 8);
        Top = scr.Bounds.Top * fy + 44;

        _text.Measure(new Size(_text.Width, double.PositiveInfinity));
        _maxOffset = Math.Max(0, _text.DesiredSize.Height - vpH);
        _offset = keepProgress ? ratio * _maxOffset : 0;
        _shift.Y = -_offset;
    }

    void OnTick(object sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min(0.1, (now - _lastTick).TotalSeconds);
        _lastTick = now;

        UpdateHoverState();
        if (_hover) return;

        int wpm = Math.Clamp(Settings.Cur.PrompterWpm, 60, 300);
        double totalSec = Math.Max(2.0, _wordCount * 60.0 / wpm);

        if (_maxOffset <= 0)
        {
            _elapsedShort += dt;
            if (_elapsedShort >= totalSec) Close();
            return;
        }

        double pxPerSec = _maxOffset / totalSec;
        _offset += pxPerSec * dt;
        if (_offset >= _maxOffset)
        {
            _shift.Y = -_maxOffset;
            Close();
            return;
        }
        _shift.Y = -_offset;
    }

    void UpdateHoverState()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool over = Native.GetCursorPos(out var pt) && Native.GetWindowRect(_hwnd, out var r)
            && pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
        if (over == _hover) return;
        _hover = over;

        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE,
            over ? (ex & ~Native.WS_EX_TRANSPARENT) : (ex | Native.WS_EX_TRANSPARENT));
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        int step = e.Delta > 0 ? 10 : -10;
        Settings.Cur.PrompterWpm = Math.Clamp(Settings.Cur.PrompterWpm + step, 60, 300);
        Settings.Save();
        e.Handled = true;
    }

    void OnClosed(object sender, EventArgs e)
    {
        _timer.Stop();
        Settings.Applied -= OnSettingsApplied;
        if (_cur == this) _cur = null;
    }

    static int CountWords(string s)
    {
        int n = 0;
        bool inWord = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) inWord = false;
            else if (!inWord) { inWord = true; n++; }
        }
        return Math.Max(1, n);
    }
}
