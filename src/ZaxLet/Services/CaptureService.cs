using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WF = System.Windows.Forms;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace ZaxLet;

/// <summary>Tangkap layar via GDI (layar penuh / area / jendela terakhir) + rekam layar lewat ffmpeg bila terpasang.</summary>
public static class CaptureService
{
    static Process _rec;
    public static bool Recording => _rec is { HasExited: false };

    static string Dir(string special, string sub)
    {
        if (special == "p" && !string.IsNullOrWhiteSpace(Settings.Cur.CapDir))
        {
            try { Directory.CreateDirectory(Settings.Cur.CapDir); return Settings.Cur.CapDir; }
            catch { /* folder tidak valid: pakai bawaan */ }
        }
        var d = Path.Combine(Environment.GetFolderPath(special == "v" ? Environment.SpecialFolder.MyVideos : Environment.SpecialFolder.MyPictures), sub);
        Directory.CreateDirectory(d);
        return d;
    }

    static System.Drawing.Rectangle Screen => WF.Screen.PrimaryScreen.Bounds;

    static SD.Bitmap Grab(System.Drawing.Rectangle r)
    {
        var bmp = new SD.Bitmap(r.Width, r.Height, SDI.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, System.Drawing.Point.Empty, r.Size);
        return bmp;
    }

    static void Finish(SD.Bitmap bmp)
    {
        var file = Path.Combine(Dir("p", "ZaxLet"), $"ZaxLet_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        bmp.Save(file, SDI.ImageFormat.Png);
        if (Settings.Cur.CapClipboard) try { WF.Clipboard.SetImage(bmp); } catch { }
        bmp.Dispose();
        IslandWindow.Instance?.Alert("Tangkapan tersimpan · " + Path.GetFileName(file), 5);
    }

    public static void Full() => IslandWindow.Instance.HideForCapture(() => Finish(Grab(Screen)));

    public static void Window() => IslandWindow.Instance.HideForCapture(() =>
    {
        var r = Native.ForegroundBounds();
        if (r == null) { Finish(Grab(Screen)); return; }
        var b = System.Drawing.Rectangle.Intersect(Screen, System.Drawing.Rectangle.FromLTRB(r.Value.Left, r.Value.Top, r.Value.Right, r.Value.Bottom));
        Finish(Grab(b.Width > 10 && b.Height > 10 ? b : Screen));
    });

    public static void Area() => IslandWindow.Instance.HideForCapture(() =>
    {
        var shot = Grab(Screen);
        var picker = new AreaPicker(shot);
        picker.ShowDialog();
        if (picker.Result is { } r)
        {
            using var crop = shot.Clone(r, SDI.PixelFormat.Format32bppArgb);
            Finish(new SD.Bitmap(crop));
        }
        shot.Dispose();
    });

    public static void ToggleRecord()
    {
        if (Recording)
        {
            try { _rec.StandardInput.Write("q"); _rec.StandardInput.Flush(); _rec.WaitForExit(4000); } catch { }
            IslandWindow.Instance?.Alert("Rekaman tersimpan di Videos\\ZaxLet", 6);
            return;
        }
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
        {
            IslandWindow.Instance?.Alert("Rekam layar butuh ffmpeg (belum ditemukan)", 8);
            return;
        }
        var file = Path.Combine(Dir("v", "ZaxLet"), $"ZaxLet_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        var psi = new ProcessStartInfo(ffmpeg, $"-y -f gdigrab -framerate {Math.Clamp(Settings.Cur.RecFps, 5, 60)} -i desktop -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{file}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        _rec = Process.Start(psi);
        _rec.ErrorDataReceived += (_, _) => { };
        _rec.BeginErrorReadLine();
        IslandWindow.Instance?.Alert("Merekam layar…", 4);
    }

    public static string FindFfmpeg()
    {
        var local = Path.Combine(Settings.Dir, "ffmpeg.exe");
        if (File.Exists(local)) return local;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(';').Select(p => { try { return Path.Combine(p.Trim(), "ffmpeg.exe"); } catch { return null; } })
                   .FirstOrDefault(p => p != null && File.Exists(p));
    }
}

/// <summary>Overlay layar penuh: seret untuk memilih area, Esc membatalkan.</summary>
sealed class AreaPicker : System.Windows.Window
{
    public System.Drawing.Rectangle? Result;
    readonly Canvas _c = new();
    readonly System.Windows.Shapes.Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)) };
    readonly System.Windows.Shapes.Rectangle _box = new() { Stroke = Brushes.White, StrokeThickness = 1.5 };
    System.Windows.Point? _a;

    public AreaPicker(SD.Bitmap shot)
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false;
        Left = 0; Top = 0; WindowState = WindowState.Maximized; Cursor = Cursors.Cross;
        var ms = new MemoryStream();
        shot.Save(ms, SDI.ImageFormat.Bmp);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.EndInit(); bi.Freeze();
        var img = new System.Windows.Controls.Image { Source = bi, Stretch = Stretch.Fill };
        _c.Children.Add(img);
        _c.Children.Add(_dim);
        _c.Children.Add(_box);
        Content = _c;
        SizeChanged += (_, _) => { img.Width = _c.ActualWidth; img.Height = _c.ActualHeight; Redraw(new Rect()); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { _a = e.GetPosition(_c); CaptureMouse(); };
        MouseMove += (_, e) => { if (_a != null) Redraw(new Rect(_a.Value, e.GetPosition(_c))); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_a == null) return;
            var r = new Rect(_a.Value, e.GetPosition(_c));
            ReleaseMouseCapture();
            var s = VisualTreeHelper.GetDpi(this);
            var px = new System.Drawing.Rectangle((int)(r.X * s.DpiScaleX), (int)(r.Y * s.DpiScaleY), (int)(r.Width * s.DpiScaleX), (int)(r.Height * s.DpiScaleY));
            px.Intersect(new System.Drawing.Rectangle(0, 0, shot.Width, shot.Height));
            if (px.Width > 4 && px.Height > 4) Result = px;
            Close();
        };
    }

    void Redraw(Rect sel)
    {
        var full = new RectangleGeometry(new Rect(0, 0, _c.ActualWidth, _c.ActualHeight));
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(sel));
        _box.Width = sel.Width; _box.Height = sel.Height;
        Canvas.SetLeft(_box, sel.X); Canvas.SetTop(_box, sel.Y);
    }
}
