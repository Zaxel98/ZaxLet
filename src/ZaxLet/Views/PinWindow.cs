using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace ZaxLet;

/// <summary>Frameless floating window for pinning an area screenshot in RAM.</summary>
public sealed class PinWindow : Window
{
    const int MaxPins = 5;
    static readonly List<PinWindow> _open = new();
    static bool _exitHooked;

    readonly Image _img = new() { Stretch = Stretch.Fill };
    readonly double _baseW, _baseH;
    SD.Bitmap _bmp;
    double _scale = 1.0;
    double _opacity = 1.0;

    PinWindow(SD.Bitmap bmp, SD.Rectangle screenPx)
    {
        _bmp = bmp;
        _img.Source = ToBitmapSource(bmp);
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);

        double dipPerPx = SystemParameters.PrimaryScreenWidth / WF.Screen.PrimaryScreen.Bounds.Width;
        _baseW = Math.Max(20, screenPx.Width * dipPerPx);
        _baseH = Math.Max(20, screenPx.Height * dipPerPx);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Width = _baseW;
        Height = _baseH;
        Left = screenPx.Left * dipPerPx;
        Top = screenPx.Top * dipPerPx;
        ClampToWorkArea(screenPx);

        var closeBtn = UI.Btn(UI.Icon("\uE711", 10), Close, UI.B("#CC141414"), 10, new Thickness(0));
        closeBtn.Width = 20;
        closeBtn.Height = 20;
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.VerticalAlignment = VerticalAlignment.Top;
        closeBtn.Margin = new Thickness(5);
        closeBtn.Visibility = Visibility.Collapsed;
        closeBtn.ToolTip = L.T("Tutup (klik kanan / Esc)");
        closeBtn.MouseLeftButtonDown += (_, e) => e.Handled = true;

        var grid = new Grid();
        grid.Children.Add(_img);
        grid.Children.Add(closeBtn);

        Content = new Border
        {
            BorderBrush = UI.B("#8CFFFFFF"),
            BorderThickness = new Thickness(1),
            Background = Brushes.Black,
            Child = grid
        };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
        };

        MouseEnter += (_, _) => closeBtn.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => closeBtn.Visibility = Visibility.Collapsed;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch (Exception ex) { App.Log(ex); }
            }
        };
        MouseRightButtonUp += (_, e) => { e.Handled = true; Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        MouseWheel += OnWheel;

        Closed += (_, _) =>
        {
            _open.Remove(this);
            _img.Source = null;
            _bmp?.Dispose();
            _bmp = null;
        };
    }

    public static void Show(SD.Bitmap bmp, SD.Rectangle screenPx)
    {
        if (!_exitHooked && Application.Current != null)
        {
            _exitHooked = true;
            Application.Current.Exit += (_, _) => CloseAll();
        }
        while (_open.Count >= MaxPins)
        {
            var oldest = _open[0];
            _open.RemoveAt(0);
            oldest.Close();
        }
        var w = new PinWindow(bmp, screenPx);
        _open.Add(w);
        w.Show();
    }

    public static void SetAllVisible(bool visible)
    {
        foreach (var w in _open.ToArray())
        {
            try
            {
                if (visible) w.Show();
                else w.Hide();
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    public static void CloseAll()
    {
        foreach (var w in _open.ToArray())
        {
            try { w.Close(); }
            catch (Exception ex) { App.Log(ex); }
        }
        _open.Clear();
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _opacity = Math.Clamp(_opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.3, 1.0);
            Opacity = _opacity;
            return;
        }
        double next = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1.0 / 1.1), 0.2, 3.0);
        if (Math.Abs(next - _scale) < 0.001) return;
        _scale = next;
        double cx = Left + Width / 2, cy = Top + Height / 2;
        Width = Math.Max(10, _baseW * _scale);
        Height = Math.Max(10, _baseH * _scale);
        Left = cx - Width / 2;
        Top = cy - Height / 2;
        ClampToWorkArea();
    }

    void ClampToWorkArea(SD.Rectangle? hintPx = null)
    {
        double dipPerPx = SystemParameters.PrimaryScreenWidth / WF.Screen.PrimaryScreen.Bounds.Width;
        var scr = hintPx is { } r
            ? WF.Screen.FromRectangle(r)
            : WF.Screen.FromPoint(new SD.Point((int)((Left + Width / 2) / dipPerPx), (int)((Top + Height / 2) / dipPerPx)));
        var wa = scr.WorkingArea;
        double waL = wa.Left * dipPerPx, waT = wa.Top * dipPerPx;
        double waR = wa.Right * dipPerPx, waB = wa.Bottom * dipPerPx;
        if (Left + Width > waR) Left = waR - Width;
        if (Top + Height > waB) Top = waB - Height;
        if (Left < waL) Left = waL;
        if (Top < waT) Top = waT;
    }

    static BitmapSource ToBitmapSource(SD.Bitmap bmp)
    {
        var hBmp = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(hBmp);
        }
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeleteObject(IntPtr hObject);
}
