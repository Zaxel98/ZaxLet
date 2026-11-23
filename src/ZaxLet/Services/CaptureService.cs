using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WF = System.Windows.Forms;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace ZaxLet;

/// <summary>Screen capture via GDI (fullscreen / area / last window) + screen recording via ffmpeg if installed.</summary>
public static class CaptureService
{
    const int FfmpegListTimeoutMs = 3000, FfmpegStartCheckMs = 150, FfmpegStopTimeoutMs = 4000;
    const string AudioBitrate = "128k";

    static Process _rec;
    public static bool Recording => _rec is { HasExited: false };
    public static bool RecMic;

    static string Dir(string special, string sub)
    {
        if (special == "p" && !string.IsNullOrWhiteSpace(Settings.Cur.CapDir))
        {
            try { Directory.CreateDirectory(Settings.Cur.CapDir); return Settings.Cur.CapDir; }
            catch { /* invalid folder: use default */ }
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
        var file = Path.Combine(Dir("p", "ZaxLet"), $"ZaxLet_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        bmp.Save(file, SDI.ImageFormat.Png);
        if (Settings.Cur.CapClipboard) try { WF.Clipboard.SetImage(bmp); } catch { }
        bmp.Dispose();
        IslandWindow.Instance?.Alert(L.F("Tangkapan tersimpan · {0}", Path.GetFileName(file)), 5);
    }

    internal static void HideAndCapture(Action capture)
    {
        PinWindow.SetAllVisible(false);
        void Run()
        {
            try { capture(); }
            finally { PinWindow.SetAllVisible(true); }
        }
        if (IslandWindow.Instance is { } isl) isl.HideForCapture(Run);
        else Run();
    }

    public static void PickArea(Action<SD.Bitmap> onPicked) => HideAndCapture(() =>
    {
        var shot = Grab(Screen);
        try
        {
            var picker = new AreaPicker(shot);
            picker.ShowDialog();
            if (picker.Result is { } r)
            {
                using var crop = shot.Clone(r, SDI.PixelFormat.Format32bppArgb);
                onPicked(crop);
            }
        }
        finally { shot.Dispose(); }
    });

    public static void Full() => HideAndCapture(() => Finish(Grab(Screen)));

    public static void Window() => HideAndCapture(() =>
    {
        var r = Native.ForegroundBounds();
        if (r == null) { Finish(Grab(Screen)); return; }
        var b = System.Drawing.Rectangle.Intersect(Screen, System.Drawing.Rectangle.FromLTRB(r.Value.Left, r.Value.Top, r.Value.Right, r.Value.Bottom));
        Finish(Grab(b.Width > 10 && b.Height > 10 ? b : Screen));
    });

    public static void Area() => HideAndCapture(() =>
    {
        var scr = Screen;
        var shot = Grab(scr);
        var picker = new AreaPicker(shot);
        picker.ShowDialog();
        if (picker.Result is { } r)
        {
            var crop = shot.Clone(r, SDI.PixelFormat.Format32bppArgb);
            shot.Dispose();
            var win = new AnnotateWindow(crop, scr);
            win.ShowDialog();
            if (win.Result is { } annotated)
            {
                crop.Dispose();
                Finish(annotated);
            }
            else
            {
                Finish(crop);
            }
        }
        else
        {
            shot.Dispose();
        }
    });

    public static void PinArea() => HideAndCapture(() =>
    {
        var scr = Screen;
        var shot = Grab(scr);
        try
        {
            var picker = new AreaPicker(shot);
            picker.ShowDialog();
            if (picker.Result is { } r)
            {
                var crop = shot.Clone(r, SDI.PixelFormat.Format32bppArgb);
                PinWindow.Show(crop, new System.Drawing.Rectangle(scr.X + r.X, scr.Y + r.Y, r.Width, r.Height));
            }
        }
        finally
        {
            shot.Dispose();
        }
    });

    public static void ToggleRecord()
    {
        if (Recording) { StopRecord(); return; }
        HideAndCapture(() => StartRecord(null));
    }

    public static void ToggleRecordArea()
    {
        if (Recording) { StopRecord(); return; }
        if (FindFfmpeg() == null)
        {
            IslandWindow.Instance?.Alert(L.T("Rekam layar butuh ffmpeg (belum ditemukan)"), 8);
            return;
        }
        HideAndCapture(() =>
        {
            var shot = Grab(Screen);
            try
            {
                var picker = new AreaPicker(shot);
                picker.ShowDialog();
                if (picker.Result is { } r && ClampEven(r) is { } area)
                    StartRecord(area);
            }
            finally
            {
                shot.Dispose();
            }
        });
    }

    static System.Drawing.Rectangle? ClampEven(System.Drawing.Rectangle r)
    {
        var scr = Screen;
        int x = Math.Clamp(scr.X + r.X, scr.Left, scr.Right - 2);
        int y = Math.Clamp(scr.Y + r.Y, scr.Top, scr.Bottom - 2);
        int w = Math.Min(r.Width, scr.Right - x) & ~1;
        int h = Math.Min(r.Height, scr.Bottom - y) & ~1;
        return w >= 2 && h >= 2 ? new System.Drawing.Rectangle(x, y, w, h) : null;
    }

    static void StopRecord()
    {
        try
        {
            if (_rec != null)
            {
                _rec.StandardInput.Write("q");
                _rec.StandardInput.Flush();
                if (!_rec.WaitForExit(FfmpegStopTimeoutMs))
                    _rec.Kill(true);
            }
        }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            _rec?.Dispose();
            _rec = null;
        }
        IslandWindow.Instance?.Alert(L.T("Rekaman tersimpan di Videos\\ZaxLet"), 6);
    }

    static void StartRecord(System.Drawing.Rectangle? area)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
        {
            IslandWindow.Instance?.Alert(L.T("Rekam layar butuh ffmpeg (belum ditemukan)"), 8);
            return;
        }
        var file = Path.Combine(Dir("v", "ZaxLet"), $"ZaxLet_{DateTime.Now:yyyyMMdd_HHmmss_fff}.mp4");
        int fps = Math.Clamp(Settings.Cur.RecFps, 5, 60);
        string region = area is { } a ? $"-offset_x {a.X} -offset_y {a.Y} -video_size {a.Width}x{a.Height} " : "";

        string mic = RecMic ? FindFirstMic(ffmpeg) : null;
        bool micMissing = RecMic && mic == null;
        bool withMic = mic != null;

        if (!LaunchFfmpeg(ffmpeg, fps, region, mic, file))
        {
            if (!withMic || !LaunchFfmpeg(ffmpeg, fps, region, null, file))
            {
                IslandWindow.Instance?.Alert(L.T("Gagal memulai perekaman layar"), 6);
                return;
            }
            withMic = false;
            micMissing = true;
        }

        var msg = micMissing
            ? "Mikrofon tidak ditemukan atau gagal, merekam tanpa audio…"
            : withMic
                ? (area != null ? "Merekam area (REC + mic)…" : "Merekam layar (REC + mic)…")
                : (area != null ? "Merekam area…" : "Merekam layar…");
        IslandWindow.Instance?.Alert(L.T(msg), micMissing ? 5 : 4);
    }

    static bool LaunchFfmpeg(string ffmpeg, int fps, string region, string mic, string file)
    {
        try
        {
            string audioIn = mic != null ? $"-f dshow -i audio=\"{mic.Replace("\"", "\\\"")}\" " : "";
            string audioOut = mic != null ? $"-c:a aac -b:a {AudioBitrate} " : "";
            var args = $"-y -f gdigrab -framerate {fps} {region}-i desktop {audioIn}-c:v libx264 -preset ultrafast -pix_fmt yuv420p {audioOut}\"{file}\"";
            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true
            };
            _rec?.Dispose();
            _rec = null;
            _rec = Process.Start(psi);
            if (_rec == null) return false;
            _rec.ErrorDataReceived += (_, _) => { };
            _rec.BeginErrorReadLine();
            if (_rec.WaitForExit(FfmpegStartCheckMs) && _rec.ExitCode != 0)
            {
                _rec.Dispose();
                _rec = null;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }

    static string FindFirstMic(string ffmpeg)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-list_devices true -f dshow -i dummy")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(FfmpegListTimeoutMs);
            bool inAudio = false;
            foreach (var raw in err.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase)) { inAudio = true; continue; }
                if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase)) { inAudio = false; continue; }
                if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase)) continue;
                int q1 = line.IndexOf('"');
                int q2 = q1 >= 0 ? line.IndexOf('"', q1 + 1) : -1;
                if (q2 <= q1 + 1) continue;
                var tail = line[(q2 + 1)..];
                bool isAudio = tail.Contains("(audio", StringComparison.OrdinalIgnoreCase)
                    || (inAudio && !tail.Contains("(video", StringComparison.OrdinalIgnoreCase));
                if (isAudio) return line.Substring(q1 + 1, q2 - q1 - 1);
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return null;
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

/// <summary>Fullscreen overlay: drag to select area, Esc to cancel.</summary>
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
