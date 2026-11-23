using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZaxLet;

/// <summary>
/// DirectShow virtual camera ("DirectShow Softcam", softcam project, MIT) visible in Discord, Zoom, Chrome, etc.
/// softcam.dll is registered once (regsvr32, requires admin); ZaxLet sends phone camera frames into it via shared memory.
/// When an app opens the virtual camera, the phone camera is turned on automatically; when closed, it turns off again.
/// </summary>
public static class VirtualCam
{
    const string Dll = "softcam.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr scCreateCamera(int width, int height, float framerate);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void scDeleteCamera(IntPtr cam);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void scSendFrame(IntPtr cam, IntPtr bits);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] static extern bool scIsConnected(IntPtr cam);

    public static int W = 1280, H = 720;
    public const int Fps = 30;
    static string _ratio = "16:9";
    public static (int W, int H) SizeFor(string ratio) => ratio switch { "1:1" => (720, 720), "4:3" => (960, 720), "9:16" => (404, 720), _ => (1280, 720) };
    public static string DllPath => Path.Combine(AppContext.BaseDirectory, Dll);
    public static bool Available => File.Exists(DllPath);
    public static string Status = "";

    static CancellationTokenSource _cts;
    static Thread _t;
    static volatile bool _run;
    static volatile bool _paused;
    static bool _phoneOn;
    static int _idleTicks;
    static long _seq = -1;

    public static bool Running => _run;
    public static bool Paused { get => _paused; set => _paused = value; }

    /// <summary>Is softcam.dll registered in Windows (appears as a camera)?</summary>
    public static bool Registered
    {
        get
        {
            using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{AEF3B972-5FA5-4647-9571-358EB472BC9E}");
            return k != null;
        }
    }

    static bool? _fw;
    static bool _tried;   // permission already requested once this session (cancel = don't repeat until ZaxLet restarts)

    /// <summary>Does the "ZaxLet HP" firewall rule for the phone server port already exist? (netsh show rule doesn't require admin)</summary>
    public static bool FirewallOk
    {
        get
        {
            if (_fw == true) return true;
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"ZaxLet HP\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
                p.StandardOutput.ReadToEnd(); p.WaitForExit(4000);
                _fw = p.ExitCode == 0;
            }
            catch { _fw = false; }
            return _fw == true;
        }
    }

    public static bool NeedsSetup => !_tried && (!FirewallOk || (Available && !Registered));

    /// <summary>
    /// One-time system permission when the camera is first turned on: register virtual camera and open firewall port, in a single UAC prompt.
    /// Called only from user action (turning on camera).
    /// </summary>
    public static void EnsureSetup(Action done)
    {
        if (!NeedsSetup) { done?.Invoke(); return; }
        _tried = true;
        Elevate(Available && !Registered, !FirewallOk, ok =>
        {
            if (ok) { done?.Invoke(); return; }
            _tried = false;   // UAC canceled or registration failed: next button press asks again, and camera isn't "active" without registration
            IslandWindow.Instance?.Alert(L.T("Kamera virtual butuh izin sekali. Nyalakan lagi dan pilih Ya di jendela Windows."), 8, null, false);
        });
    }

    static bool _fwAsked;

    /// <summary>Open firewall port for the phone server with a single UAC prompt, from user action (starting server). Cancel = retried on next start; server still starts (done is invoked).</summary>
    public static void EnsureFirewall(Action done)
    {
        if (_fwAsked || FirewallOk) { done?.Invoke(); return; }
        _fwAsked = true;
        Elevate(false, true, _ =>
        {
            if (!FirewallOk)
            {
                _fwAsked = false;
                IslandWindow.Instance?.Alert(L.T("Tanpa izin firewall, HP bisa gagal tersambung. Matikan lalu nyalakan lagi untuk mencoba, atau pilih Izinkan saat Windows bertanya."), 10, null, false);
            }
            done?.Invoke();
        });
    }

    /// <summary>Single UAC for camera registration and/or firewall rule; callback on UI thread, ok = requested camera is registered.</summary>
    static void Elevate(bool cam, bool fw, Action<bool> done)
    {
        var cmds = new System.Collections.Generic.List<string>();
        if (cam) cmds.Add($"regsvr32 /s \"{DllPath}\"");
        if (fw) { int p = Settings.Cur.SharePort; cmds.Add($"netsh advfirewall firewall add rule name=\"ZaxLet HP\" dir=in action=allow protocol=TCP localport={p}-{p + 1} profile=any"); }
        if (cmds.Count == 0) { done(true); return; }
        void Finish() { _fw = null; var ok = !cam || Registered; System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { if (!ok) Status = "Izin sistem dibatalkan"; done(ok); }); }
        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", cmds)) { Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            System.Threading.Tasks.Task.Run(() => { proc?.WaitForExit(); Finish(); });
        }
        catch (Exception ex) { if (ex is not System.ComponentModel.Win32Exception) App.Log(ex); Finish(); }   // 1223 = UAC declined
    }

    public static void Start()
    {
        if (_run) return;
        if (NeedsSetup) { EnsureSetup(Start); return; }
        if (!Available) { Status = "softcam.dll tidak ada"; return; }
        if (_t?.IsAlive == true && !_t.Join(1500)) { Status = "Kamera virtual masih dalam proses berhenti"; return; }
        IntPtr cam;
        try
        {
            _ratio = Settings.Cur.VCamRatio; (W, H) = SizeFor(_ratio);
            cam = scCreateCamera(W, H, Fps);
            if (cam == IntPtr.Zero) { Status = "Kamera virtual gagal dibuat (mungkin sudah dipakai proses lain)"; return; }
        }
        catch (Exception ex) { App.Log(ex); Status = "softcam.dll tidak bisa dimuat: " + ex.Message; return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _paused = false;
        _run = true;
        Status = "Aktif";
        _t = new Thread(() => Loop(cam, ct)) { IsBackground = true, Name = "vcam" };
        _t.Start();
    }

    public static void Stop()
    {
        _run = false;
        _paused = false;
        _cts?.Cancel();
        _t?.Join(1500);   // camera is deleted by Loop on exit to avoid use-after-free if Join times out
        if (_phoneOn) { ShareServer.I.Command("cam off"); _phoneOn = false; }
        Status = "Mati";
    }

    // Single thread: composite latest frame onto canvas (size follows aspect ratio) and send at 30 fps. scSendFrame paces itself.
    // Aspect ratio switches (settings, or phone rotated = landscape) if held >1.5s: camera is recreated with the new size.
    static void Loop(IntPtr cam, CancellationToken ct)
    {
        Bitmap canvas = null; Graphics g = null;
        void Make()
        {
            g?.Dispose(); canvas?.Dispose();
            canvas = new Bitmap(W, H, PixelFormat.Format24bppRgb);
            g = Graphics.FromImage(canvas);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        }
        Image src = null;
        try
        {
            Make();
            int tick = 0;
            var diffSince = DateTime.MaxValue;
            bool wasPaused = false;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_paused)
                    {
                        if (!wasPaused)
                        {
                            Placeholder(g, L.T("Sebentar ya"));
                            wasPaused = true;
                        }
                    }
                    else
                    {
                        var s = Settings.Cur;
                        bool fresh = false;
                        if (PhoneCam.Seq != _seq && PhoneCam.Latest is { } jpg)
                        {
                            _seq = PhoneCam.Seq;
                            src?.Dispose();
                            src = Image.FromStream(new MemoryStream(jpg));
                            fresh = true;
                        }
                        else if (src == null || !PhoneCam.Active) { Placeholder(g, PhoneCam.Active ? "" : (ShareServer.I.AppCount == 0 ? "Buka aplikasi ZaxLet HP di HP" : "Menyalakan kamera HP…")); src?.Dispose(); src = null; }
                        else if (wasPaused) fresh = true;
                        wasPaused = false;

                        var want = s.VCamAuto && src != null && src.Width > src.Height ? "16:9" : s.VCamRatio;
                        if (want != _ratio)
                        {
                            if (diffSince == DateTime.MaxValue) diffSince = DateTime.Now;
                            else if ((DateTime.Now - diffSince).TotalSeconds > 1.5)
                            {
                                var (nw, nh) = SizeFor(want);
                                scDeleteCamera(cam);
                                W = nw; H = nh; _ratio = want;
                                cam = scCreateCamera(W, H, Fps);
                                if (cam == IntPtr.Zero) { Status = "Kamera virtual gagal dibuat ulang"; _run = false; break; }
                                Make();
                                fresh = true; diffSince = DateTime.MaxValue;
                            }
                        }
                        else diffSince = DateTime.MaxValue;
                        if (fresh && src != null) Compose(g, src, s);
                    }

                    var bd = canvas.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try { scSendFrame(cam, bd.Scan0); } finally { canvas.UnlockBits(bd); }

                    // every ~0.5s: check if any app is using the virtual camera, toggle phone camera
                    if (++tick % (Fps / 2) == 0) Follow(scIsConnected(cam));
                }
                catch (Exception ex) { App.Log(ex); Thread.Sleep(200); }
            }
        }
        finally
        {
            src?.Dispose(); g?.Dispose(); canvas?.Dispose();
            if (cam != IntPtr.Zero) scDeleteCamera(cam);
        }
    }

    static void Follow(bool consumer)
    {
        if (consumer)
        {
            _idleTicks = 0;
            if (!PhoneCam.Active && ShareServer.I.AppCount > 0 && (!_phoneOn || (++_retry % 10 == 0))) { CamOn(); _phoneOn = true; }
        }
        else if (_phoneOn && ++_idleTicks > 8)   // ~4s with no consumer: turn off phone camera (save battery)
        {
            ShareServer.I.Command("cam off"); _phoneOn = false; _idleTicks = 0;
        }
    }
    static int _retry;

    static void CamOn()
    {
        var s = Settings.Cur;
        ShareServer.I.Command($"cam on {s.CamRes} {s.CamFps} {(s.CamFront ? "front" : "back")}");
    }

    static void Compose(Graphics g, Image src, Settings s)
    {
        g.Clear(Color.Black);
        var img = (Image)src.Clone();
        try
        {
            var rf = s.VCamRot switch { 90 => RotateFlipType.Rotate90FlipNone, 180 => RotateFlipType.Rotate180FlipNone, 270 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone };
            if (s.VCamMirror) rf = rf switch { RotateFlipType.RotateNoneFlipNone => RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate90FlipNone => RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate180FlipNone => RotateFlipType.Rotate180FlipX, _ => RotateFlipType.Rotate270FlipX };
            if (rf != RotateFlipType.RotateNoneFlipNone) img.RotateFlip(rf);
            double sx = (double)W / img.Width, sy = (double)H / img.Height;
            double k = s.VCamFit == "crop" ? Math.Max(sx, sy) : Math.Min(sx, sy);   // crop = fill, fit = full image with black bars
            float w = (float)(img.Width * k), h = (float)(img.Height * k);
            g.DrawImage(img, (W - w) / 2, (H - h) / 2, w, h);
        }
        finally { img.Dispose(); }
    }

    static void Placeholder(Graphics g, string text)
    {
        g.Clear(Color.FromArgb(14, 14, 17));
        if (text.Length == 0) return;
        using var f = new Font("Segoe UI", 28, FontStyle.Bold);
        var sz = g.MeasureString(text, f);
        g.DrawString(text, f, Brushes.Gray, (W - sz.Width) / 2, (H - sz.Height) / 2);
    }
}
