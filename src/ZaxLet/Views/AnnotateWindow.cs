using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;
using WF = System.Windows.Forms;

namespace ZaxLet;

/// <summary>Quick annotation window after an area screenshot: Enter/Save saves with annotations, Esc saves the original.</summary>
public sealed class AnnotateWindow : Window
{
    enum Tool { Pen, Arrow, Box, Highlighter }

    static readonly Color[] Palette =
    {
        Color.FromRgb(0xEF, 0x44, 0x44), // Red
        Color.FromRgb(0xFA, 0xCC, 0x15), // Yellow
        Color.FromRgb(0x34, 0xD3, 0x99), // Green
        Color.FromRgb(0x60, 0xA5, 0xFA), // Blue
        Color.FromRgb(0xFF, 0xFF, 0xFF), // White
    };

    public SD.Bitmap Result { get; private set; }

    readonly int _pxW, _pxH;
    readonly float _dpiX, _dpiY;
    readonly Canvas _canvas = new() { Background = Brushes.Transparent, ClipToBounds = true, Cursor = Cursors.Cross };
    readonly Image _img = new() { Stretch = Stretch.Fill };
    readonly List<Border> _toolBtns = new();
    readonly List<Border> _colorBtns = new();

    BitmapSource _src;
    Tool _tool = Tool.Pen;
    Color _color = Palette[0];
    UIElement _active;
    Point _start;

    public AnnotateWindow(SD.Bitmap bmp, SD.Rectangle screenBounds)
    {
        _pxW = bmp.Width;
        _pxH = bmp.Height;
        _dpiX = bmp.HorizontalResolution > 0 ? bmp.HorizontalResolution : 96f;
        _dpiY = bmp.VerticalResolution > 0 ? bmp.VerticalResolution : 96f;
        _src = ToBitmapSource(bmp);
        _img.Source = _src;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        var scr = WF.Screen.FromRectangle(screenBounds);
        var wa = scr.WorkingArea;
        // initial DPI scale estimate; refined on SourceInitialized
        double initScale = _dpiX / 96.0;
        double maxW = Math.Max(320, wa.Width / initScale * 0.85 - 24);
        double maxH = Math.Max(240, wa.Height / initScale * 0.78 - 72);
        double scale = Math.Min(1.0, Math.Min(maxW / _pxW, maxH / _pxH));
        double dispW = Math.Max(1, Math.Round(_pxW * scale));
        double dispH = Math.Max(1, Math.Round(_pxH * scale));

        _img.Width = dispW;
        _img.Height = dispH;
        _canvas.Width = dispW;
        _canvas.Height = dispH;

        var stage = new Grid { Width = dispW, Height = dispH, HorizontalAlignment = HorizontalAlignment.Center };
        stage.Children.Add(_img);
        stage.Children.Add(_canvas);

        var stageFrame = new Border
        {
            Background = Brushes.Black,
            BorderBrush = UI.Track,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = stage
        };

        var toolbar = BuildToolbar();
        toolbar.Margin = new Thickness(0, 10, 0, 0);

        var root = new StackPanel();
        root.Children.Add(stageFrame);
        root.Children.Add(toolbar);

        Content = new Border
        {
            Background = UI.B("#FF111114"),
            BorderBrush = UI.Track,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12),
            Child = root
        };

        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Save(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
            else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                Undo();
            }
        };

        Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double waLeft = wa.Left / dpi.DpiScaleX;
            double waTop = wa.Top / dpi.DpiScaleY;
            double waW = wa.Width / dpi.DpiScaleX;
            double waH = wa.Height / dpi.DpiScaleY;
            Left = waLeft + Math.Max(0, (waW - ActualWidth) / 2);
            Top = waTop + Math.Max(0, (waH - ActualHeight) / 2);
            Activate();
        };

        Closed += (_, _) =>
        {
            _img.Source = null;
            _canvas.Children.Clear();
            _src = null;
        };
    }

    FrameworkElement BuildToolbar()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        AddTool(bar, Tool.Pen, UI.Icon("\uE70F", 14), L.T("Pena bebas"));
        AddTool(bar, Tool.Arrow, MakeArrowIcon(), L.T("Panah"));
        AddTool(bar, Tool.Box, MakeBoxIcon(), L.T("Kotak"));
        AddTool(bar, Tool.Highlighter, UI.Icon("\uE7E6", 14), L.T("Penyorot"));

        bar.Children.Add(Sep());

        for (int i = 0; i < Palette.Length; i++)
        {
            var c = Palette[i];
            int idx = i;
            var dot = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(2),
                BorderBrush = i == 0 ? UI.Fg : Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            dot.MouseLeftButtonUp += (_, _) => SelectColor(idx);
            _colorBtns.Add(dot);
            bar.Children.Add(dot);
        }

        bar.Children.Add(Sep());

        var undoBtn = UI.Btn(UI.Icon("\uE7A7", 14), Undo, UI.Card, 8, new Thickness(8, 6, 8, 6));
        undoBtn.ToolTip = L.T("Urungkan (Ctrl+Z)");
        undoBtn.Margin = new Thickness(0, 0, 4, 0);
        bar.Children.Add(undoBtn);

        var clearBtn = UI.Btn(UI.Icon("\uE74D", 14), Clear, UI.Card, 8, new Thickness(8, 6, 8, 6));
        clearBtn.ToolTip = L.T("Bersihkan");
        clearBtn.Margin = new Thickness(0, 0, 8, 0);
        bar.Children.Add(clearBtn);

        var saveRow = new StackPanel { Orientation = Orientation.Horizontal };
        var okIcon = UI.Icon("\uE73E", 13, Brushes.Black);
        okIcon.Margin = new Thickness(0, 0, 5, 0);
        saveRow.Children.Add(okIcon);
        saveRow.Children.Add(UI.T("Simpan", 12, Brushes.Black, true));
        var saveBtn = UI.Btn(saveRow, Save, UI.Accent, 8, new Thickness(12, 6, 12, 6));
        saveBtn.ToolTip = L.T("Simpan (Enter)");
        bar.Children.Add(saveBtn);

        UpdateToolHighlight();
        return bar;
    }

    void AddTool(Panel bar, Tool tool, UIElement icon, string tip)
    {
        var b = UI.Btn(icon, () => { _tool = tool; UpdateToolHighlight(); }, UI.Card, 8, new Thickness(8, 6, 8, 6));
        b.ToolTip = tip;
        b.Margin = new Thickness(0, 0, 4, 0);
        b.Tag = tool;
        _toolBtns.Add(b);
        bar.Children.Add(b);
    }

    void UpdateToolHighlight()
    {
        foreach (var b in _toolBtns)
            b.Background = (Tool)b.Tag == _tool ? UI.AccentBg : UI.Card;
    }

    void SelectColor(int idx)
    {
        _color = Palette[idx];
        for (int i = 0; i < _colorBtns.Count; i++)
            _colorBtns[i].BorderBrush = i == idx ? UI.Fg : Brushes.Transparent;
    }

    static UIElement Sep() => new Border
    {
        Width = 1,
        Height = 18,
        Background = UI.Track,
        Margin = new Thickness(6, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    static UIElement MakeArrowIcon()
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(2, 12), false, false);
            ctx.LineTo(new Point(12, 2), true, false);
            ctx.BeginFigure(new Point(6, 2), false, false);
            ctx.LineTo(new Point(12, 2), true, false);
            ctx.LineTo(new Point(12, 8), true, false);
        }
        g.Freeze();
        return new Path { Data = g, Stroke = UI.Fg, StrokeThickness = 1.6, Width = 14, Height = 14, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
    }

    static UIElement MakeBoxIcon() => new Rectangle
    {
        Width = 13,
        Height = 11,
        Stroke = UI.Fg,
        StrokeThickness = 1.6,
        RadiusX = 2,
        RadiusY = 2
    };

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = Clamp(e.GetPosition(_canvas));
        _canvas.CaptureMouse();

        var brush = new SolidColorBrush(_tool == Tool.Highlighter
            ? Color.FromArgb(110, _color.R, _color.G, _color.B)
            : _color);
        brush.Freeze();

        switch (_tool)
        {
            case Tool.Pen:
            case Tool.Highlighter:
                var pl = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = _tool == Tool.Highlighter ? 16 : 3,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                pl.Points.Add(_start);
                _active = pl;
                break;

            case Tool.Box:
                var rect = new Rectangle
                {
                    Stroke = brush,
                    StrokeThickness = 3,
                    Fill = Brushes.Transparent,
                    Width = 0,
                    Height = 0
                };
                Canvas.SetLeft(rect, _start.X);
                Canvas.SetTop(rect, _start.Y);
                _active = rect;
                break;

            case Tool.Arrow:
                _active = new Path
                {
                    Stroke = brush,
                    StrokeThickness = 3,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };
                break;
        }

        if (_active != null) _canvas.Children.Add(_active);
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_active == null) return;
        var p = Clamp(e.GetPosition(_canvas));

        switch (_active)
        {
            case Polyline pl:
                pl.Points.Add(p);
                break;

            case Rectangle rect:
                Canvas.SetLeft(rect, Math.Min(_start.X, p.X));
                Canvas.SetTop(rect, Math.Min(_start.Y, p.Y));
                rect.Width = Math.Abs(p.X - _start.X);
                rect.Height = Math.Abs(p.Y - _start.Y);
                break;

            case Path path:
                path.Data = BuildArrow(_start, p);
                break;
        }
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_active == null) return;
        var p = Clamp(e.GetPosition(_canvas));
        _canvas.ReleaseMouseCapture();

        bool tiny = (_active is Rectangle or Path) && Math.Abs(p.X - _start.X) < 3 && Math.Abs(p.Y - _start.Y) < 3;
        if (tiny) _canvas.Children.Remove(_active);
        _active = null;
    }

    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _canvas.ActualWidth), Math.Clamp(p.Y, 0, _canvas.ActualHeight));

    static Geometry BuildArrow(Point a, Point b)
    {
        double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        const double headLen = 14, headAngle = Math.PI / 6;
        var p1 = new Point(b.X - headLen * Math.Cos(angle - headAngle), b.Y - headLen * Math.Sin(angle - headAngle));
        var p2 = new Point(b.X - headLen * Math.Cos(angle + headAngle), b.Y - headLen * Math.Sin(angle + headAngle));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, false, false);
            ctx.LineTo(b, true, false);
            ctx.BeginFigure(p1, false, false);
            ctx.LineTo(b, true, false);
            ctx.LineTo(p2, true, false);
        }
        g.Freeze();
        return g;
    }

    void Undo()
    {
        int n = _canvas.Children.Count;
        if (n > 0) _canvas.Children.RemoveAt(n - 1);
    }

    void Clear() => _canvas.Children.Clear();

    void Cancel()
    {
        Result = null;
        Close();
    }

    void Save()
    {
        if (_canvas.Children.Count > 0 && _src != null)
        {
            try { Result = ToDrawingBitmap(RenderAnnotated()); }
            catch (Exception ex) { App.Log(ex); Result = null; }
        }
        Close();
    }

    BitmapSource RenderAnnotated()
    {
        double logicalW = _pxW * 96.0 / _dpiX;
        double logicalH = _pxH * 96.0 / _dpiY;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(_src, new Rect(0, 0, logicalW, logicalH));
            var vb = new VisualBrush(_canvas)
            {
                Stretch = Stretch.Fill,
                Viewbox = new Rect(0, 0, _canvas.Width, _canvas.Height),
                ViewboxUnits = BrushMappingMode.Absolute
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, logicalW, logicalH));
        }
        var rtb = new RenderTargetBitmap(_pxW, _pxH, _dpiX, _dpiY, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
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

    static SD.Bitmap ToDrawingBitmap(BitmapSource src)
    {
        var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var bmp = new SD.Bitmap(w, h, SDI.PixelFormat.Format32bppArgb);
        if (src.DpiX > 0 && src.DpiY > 0) bmp.SetResolution((float)src.DpiX, (float)src.DpiY);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, w, h), SDI.ImageLockMode.WriteOnly, SDI.PixelFormat.Format32bppArgb);
        try
        {
            bgra.CopyPixels(Int32Rect.Empty, data.Scan0, data.Stride * h, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeleteObject(IntPtr hObject);
}
