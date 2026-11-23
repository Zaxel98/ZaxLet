using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace ZaxLet;

public partial class App : Application
{
    static Mutex _mutex;
    WF.NotifyIcon _tray;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    protected override void OnStartup(StartupEventArgs e)
    {
        // FPS helper (admin): no window, no single-instance mutex, only sends numbers to the requesting ZaxLet
        if (e.Args.Length == 2 && e.Args[0] == "--fps-helper") { FpsHelper.Run(e.Args[1]); Environment.Exit(0); return; }
        // ZAXLET_NO_SINGLE=1: allow a second instance (testing on a PC already running ZaxLet; used with ZAXLET_DATA_DIR)
        bool isSendPhone = e.Args.Contains("--send-phone");
        if (Environment.GetEnvironmentVariable("ZAXLET_NO_SINGLE") != "1")
        {
            _mutex = new Mutex(true, "ZaxLet.Single.Instance", out bool first);
            if (!first)
            {
                if (isSendPhone) SendTo.ForwardToRunning(e.Args.Where(a => a != "--send-phone" && !a.StartsWith("--")));
                Shutdown();
                return;
            }
        }
        base.OnStartup(e);
        // simple UI: software rendering avoids graphics driver overhead (tens of MB RAM) and keeps GPU free
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);

        MigrateLegacy();
        Settings.Load();
        // background animations (equalizer, scrolling text, ring) 30 fps is smooth enough at half the load; open/close/tab switch use 60 fps (IslandWindow.Fast)
        if (Settings.Cur.AnimFps != "full")
            System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata(30));
        Translit.PinyinFile = RomajiPack.PinyinFile;   // pinyin table is in the Latin Script Pack (optional), not in the installer
        var w = new IslandWindow();
        w.Show();
        // new user tour and update check: not for test runs (flags --… or custom data folder)
        bool dev = e.Args.Any(x => x.StartsWith("--") && x != "--updated") || Environment.GetEnvironmentVariable("ZAXLET_DATA_DIR") != null;
        System.Windows.Threading.DispatcherTimer After(double sec, Action act)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
            t.Tick += (_, _) => { t.Stop(); act(); };
            t.Start();
            return t;
        }
        if (!dev) { After(4, () => TourWindow.ShowIt()); After(25, () => _ = UpdateService.AutoCheckAsync()); }
        if (e.Args.Contains("--updated")) After(3, () => w.Alert(L.F("ZaxLet diperbarui ke {0}", UpdateService.Current), 8));
        // `ZaxLet.exe --tab=prayer` opens and holds the pill on a single tab (used for visual testing)
        foreach (var a in e.Args)
        {
            if (a.StartsWith("--tab=")) { Settings.Cur.LastTab = a[6..]; w.OpenNow(); w.KeepOpen(true); }
            else if (a == "--shot") CaptureService.Full();
            else if (a == "--fps-test")   // FPS test: tries direct session, prompts UAC if denied; 15s reading FPS -> fps-test.txt
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    var o = Environment.GetEnvironmentVariable("ZAXLET_UI_OUT") is { Length: > 0 } d ? d : Settings.Dir;
                    System.IO.Directory.CreateDirectory(o);
                    var log = new System.Text.StringBuilder();
                    FpsSource.TryDirect();
                    log.AppendLine("direct=" + FpsSource.Available);
                    if (!FpsSource.Available) log.AppendLine("elevated started=" + FpsSource.StartElevated());
                    for (int i = 0; i < 15; i++) { System.Threading.Thread.Sleep(1000); log.AppendLine($"{i}s fps={FpsSource.Read()?.ToString("0.0") ?? "-"} fg={Native.ForegroundPid()} avail={FpsSource.Available}"); }
                    System.IO.File.WriteAllText(System.IO.Path.Combine(o, "fps-test.txt"), log.ToString());
                });
            }
            else if (a == "--ui-test") UiTest.Run();   // render each Settings page and guide step -> PNG in ZAXLET_UI_OUT
            else if (a == "--usage-test")   // Claude/Codex quotas successfully read and from which source -> usage-test.txt
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await UsageService.I.RefreshQuotaAsync();
                    // island window runs the same reading: wait until Claude source is clear (max 20s)
                    for (int i = 0; i < 20 && UsageService.I.Claude.Source == ""; i++) { await System.Threading.Tasks.Task.Delay(1000); await UsageService.I.RefreshQuotaAsync(); }
                    string F(ProviderUsage u) => $"{u.Name}: sumber={(u.Source == "" ? "-" : u.Source)} catatan={u.Error ?? "-"} jendela=" + string.Join(", ", u.Windows.Select(x => $"{x.Label}={x.Pct}%"));
                    File.WriteAllText(Path.Combine(Settings.Dir, "usage-test.txt"), F(UsageService.I.Claude) + "\n" + F(UsageService.I.Codex));
                });
            }
            else if (a == "--update-test")   // GitHub release check result -> update-test.txt (ZAXLET_UPDATE_URL overrides address)
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await UpdateService.CheckAsync();
                    File.WriteAllText(Path.Combine(Settings.Dir, "update-test.txt"), $"saat ini={UpdateService.Current} tersedia={UpdateService.Available?.Version.ToString() ?? "-"} status={UpdateService.Status}");
                });
            }
            else if (a == "--feat-test")   // OSD/privacy/network sources available on this PC -> feat-test.txt
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    var n = NetInfo.Current();
                    File.WriteAllText(Path.Combine(Settings.Dir, "feat-test.txt"),
                        $"volume={AudioEndpoint.Volume()?.ToString("0.00") ?? "-"} mute={AudioEndpoint.Muted()?.ToString() ?? "-"} micMute={AudioEndpoint.Muted(AudioEndpoint.Capture)?.ToString() ?? "-"}\n" +
                        $"kecerahan={Brightness.Get()?.ToString() ?? "-"} mikrofonDipakai=[{string.Join(",", PrivacyWatch.InUse("microphone"))}] kameraDipakai=[{string.Join(",", PrivacyWatch.InUse("webcam"))}]\n" +
                        $"jaringan={(n == null ? "-" : n.Name + "/" + n.Kind)} firewallPublik={NetInfo.FirewallOpenToPublic()} unduhan={DownloadWatch.DownloadsDir()}");
                });
            }
            else if (a == "--mon-test")   // readable temp/GPU sources -> mon-test.txt (test Monitor tab on another PC)
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    var sm = new SysMon();
                    for (int i = 0; i < 3; i++) { sm.Sample(); await sm.SampleSlowAsync(); await System.Threading.Tasks.Task.Delay(3200); }
                    File.WriteAllText(Path.Combine(Settings.Dir, "mon-test.txt"), $"cpuTemp={sm.CpuTemp?.ToString("0.0")} gpuUtil={sm.GpuUtil?.ToString("0.00")} gpuTemp={sm.GpuTemp?.ToString("0.0")} gpuPower={sm.GpuPower?.ToString("0.0")}");
                });
            }
            else if (a.StartsWith("--yt-trace="))   // every second: song, position, duration, lyric state -> yt-trace.txt (test YouTube position reading)
            {
                int secs = int.Parse(a[11..]);
                var m = MediaService.I;
                var tsb = new System.Text.StringBuilder();
                var t0 = DateTime.Now;
                var tt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                tt.Tick += (_, _) =>
                {
                    tsb.AppendLine($"{(DateTime.Now - t0).TotalSeconds:0}s | {m.Title} | {m.Artist} | pos {m.Position():h\\:mm\\:ss} / {m.Duration:h\\:mm\\:ss} | lyrics {m.LyricState}/{m.Lyrics.Count}");
                    File.WriteAllText(Path.Combine(Settings.Dir, "yt-trace.txt"), tsb.ToString());
                    if ((DateTime.Now - t0).TotalSeconds >= secs) tt.Stop();
                };
                tt.Start();
            }
            else if (a == "--romaji-test")   // Kawazu test: results written to romaji-test.txt
            {
                var inFile = Path.Combine(Settings.Dir, "romaji-in.txt");   // optional: custom test lines, one per line
                var sample = File.Exists(inFile) ? File.ReadAllLines(inFile) : new[] { "この世界が くるくる回る夢で", "君の名前を呼んだ", "クローバー・クラブ" };
                var sw = Stopwatch.StartNew();
                Romaji.Ready += () => File.WriteAllText(Path.Combine(Settings.Dir, "romaji-test.txt"),
                    string.Join("\n", sample.Select(l => l + " => " + Romaji.Of(l))) + $"\n{sw.ElapsedMilliseconds} ms, WS {Process.GetCurrentProcess().WorkingSet64 >> 20} MB");
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try { if (!RomajiPack.Installed) await RomajiPack.DownloadAsync(() => { }); }
                    catch (Exception ex) { Log(ex); return; }
                    Romaji.Prefetch(sample);
                });
            }
            else if (a.StartsWith("--karaoke-trace="))   // record karaoke every 250 ms for N sec -> karaoke-trace.txt
            {
                var parts = a[16..].Split(',');   // --karaoke-trace=seconds[,songCount]  (default 5 songs with lyrics)
                int secs = int.Parse(parts[0]), maxSongs = parts.Length > 1 ? int.Parse(parts[1]) : 5;
                var m = MediaService.I;
                AudioFlow.I.Labeler = m.VocalLabel;
                AudioFlow.I.Acquire();
                var sb = new System.Text.StringBuilder();
                var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                var t0 = DateTime.Now;
                // Rules: ads (artist "Spotify" / duration < 45s) and songs without lyrics are NOT counted. Lyrics are searched first across all sources; only if
                // found is the song recorded (counted), otherwise skipped. Stops after 5 songs with lyrics finish (6th song starts) or time limit.
                string song = null, skippedKey = null; int songs = 0, skipped = 0, tick = 0; bool recording = false, finished = false;
                void Flush() => File.WriteAllText(Path.Combine(Settings.Dir, "karaoke-trace.txt"), $"songs={songs} skipped={skipped} finished={finished}\n{AudioFlow.I.ModelInfo}\n" + sb);
                tm.Tick += (_, _) =>
                {
                    bool ad = m.Artist == "Spotify" || (m.Duration > TimeSpan.Zero && m.Duration.TotalSeconds < 45) || string.IsNullOrEmpty(m.Title);
                    bool timeUp = (DateTime.Now - t0).TotalSeconds >= secs;
                    if (++tick % 240 == 0 || timeUp) Flush();   // save periodically (every ±60s)
                    if (ad) { if (timeUp) { tm.Stop(); AudioFlow.I.Release(); } return; }
                    var key = m.Title + "|" + m.Artist;
                    if (key != song)
                    {
                        if (recording && songs >= maxSongs) { finished = true; Flush(); tm.Stop(); AudioFlow.I.Release(); return; }   // 6th song starts: 5 songs done
                        song = key; recording = false;
                    }
                    if (!recording)
                    {
                        if (m.LyricState == "ok" && m.Lyrics.Count > 0) { recording = true; songs++; sb.AppendLine($"### {m.Title} | {m.Artist} | dur={m.Duration.TotalSeconds:0}s lyricsLines={m.Lyrics.Count}"); }
                        else if (m.LyricState == "none" && skippedKey != key) { skipped++; skippedKey = key; sb.AppendLine($"### SKIP (tanpa lirik) {m.Title} | {m.Artist}"); }   // skipped, not counted
                        if (!recording) { if (timeUp) { tm.Stop(); AudioFlow.I.Release(); } return; }
                    }
                    var (i, p) = m.Karaoke();
                    var k = m.DbgK;
                    sb.AppendLine($"{(DateTime.Now - t0).TotalSeconds,5:0.0}s pos={m.Position().TotalSeconds,6:0.0} line={i,3} p={p:0.00} time={k.TimeP:0.00} sung={k.Sung:0.0} voiced={k.Voiced:0.0} vr={k.VoicedRatio:0.00} flow={k.Flow} silent={AudioFlow.I.SilentFor:0.0} \"{(k.Text ?? "").Replace("\"", "'")}\"");
                    if ((DateTime.Now - t0).TotalSeconds >= secs)
                    {
                        tm.Stop();
                        Flush();
                        AudioFlow.I.Release();
                    }
                };
                tm.Start();
            }
            else if (a.StartsWith("--flow-test"))   // capture output audio (default 8s, --flow-test=40) -> flow-test.txt (play music first)
            {
                int secs = a.Contains('=') ? int.Parse(a[(a.IndexOf('=') + 1)..]) : 8;
                AudioFlow.I.Labeler = MediaService.I.VocalLabel;
                AudioFlow.I.Acquire();
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(secs * 1000);
                    File.WriteAllText(Path.Combine(Settings.Dir, "flow-test.txt"), AudioFlow.I.DebugStats() + $" fresh={AudioFlow.I.Fresh} voiced8s={AudioFlow.I.VoicedSince(7):0.0}");
                    AudioFlow.I.Release();
                });
            }
            else if (a.StartsWith("--lyrics-test="))   // --lyrics-test=Title|Artist|seconds -> lyrics-test.txt
            {
                var p = a[14..].Split('|');
                _ = System.Threading.Tasks.Task.Run(async () => File.WriteAllText(Path.Combine(Settings.Dir, "lyrics-test.txt"), await MediaService.I.DebugSources(p[0], p[1], int.Parse(p[2]))));
            }
            else if (a.StartsWith("--demo-notifs="))Dispatcher.BeginInvoke(() => PhoneNotifs.Demo(int.Parse(a[14..])), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            else if (a.StartsWith("--page=")) SettingsView.StartPage = a[7..];
            else if (a == "--settings") Dispatcher.BeginInvoke(SettingsWindow.ShowIt);
            else if (a == "--camera") Dispatcher.BeginInvoke(CameraWindow.ShowIt);
            else if (a.StartsWith("--timer=")) { TimerService.I.SetTotal(TimeSpan.FromSeconds(int.Parse(a[8..]))); TimerService.I.Start(); }
        }
        BuildTray();
        void Safe(Action act) { try { act(); } catch (Exception ex) { Log(ex); } }
        Safe(() => _ = MediaService.I.StartAsync());
        Safe(() => PrayerAlerts.Start());
        Safe(() => WeatherService.I.Apply());
        Settings.Applied += WeatherService.I.Apply;   // on/off from Settings takes effect immediately
        Safe(() => HydrationService.I.Start());
        Safe(() => SysAlerts.I.Start());
        Safe(() => BtAutoPause.I.Start());
        Safe(() => AppUsage.I.Apply());
        Settings.Applied += AppUsage.I.Apply;
        Safe(() => DiscordRpc.I.Start());
        Safe(() => _ = LocationService.RefreshAsync());
        Safe(() => UsageService.I.StartAgyLoop());
        Safe(() => { if (Settings.Cur.ShareAuto || Settings.Cur.VCamOn) ShareServer.I.Start(); });
        Safe(() => { if (Settings.Cur.VCamOn) VirtualCam.Start(); });
        Safe(() => { if (Settings.Cur.SendToOn) SendTo.Sync(true); });
        Safe(() => SendTo.StartPipeServer());
        if (isSendPhone)
        {
            var files = e.Args.Where(a => a != "--send-phone" && !a.StartsWith("--")).ToArray();
            if (files.Length > 0) _ = System.Threading.Tasks.Task.Run(() => SendTo.HandleFiles(files));
        }
    }

    /// <summary>Old version was named "Island": migrate its data folder and autostart value once so settings are not lost.</summary>
    static void MigrateLegacy()
    {
        try
        {
            var old = Path.Combine(Path.GetDirectoryName(Settings.Dir), "Island");
            if (!Directory.Exists(Settings.Dir) && Directory.Exists(old)) Directory.Move(old, Settings.Dir);
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k?.GetValue("Island") != null) { k.DeleteValue("Island"); k.SetValue("ZaxLet", $"\"{Environment.ProcessPath}\""); }
        }
        catch (Exception ex) { Log(ex); }
    }

    void BuildTray()
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add(L.T("Buka"), null, (_, _) => IslandWindow.Instance?.OpenNow());
        menu.Items.Add(L.T("Sembunyikan / tampilkan pill"), null, (_, _) => IslandWindow.Instance?.ToggleHidden());
        menu.Items.Add(L.T("Pengaturan"), null, (_, _) => SettingsWindow.ShowIt());
        menu.Items.Add(L.T("Periksa pembaruan"), null, async (_, _) =>
        {
            await UpdateService.CheckAsync();
            var a = UpdateService.Available;
            IslandWindow.Instance?.Alert(a != null ? L.F("ZaxLet {0} tersedia · klik untuk memperbarui", a.Version) : string.IsNullOrEmpty(UpdateService.Status) ? "ZaxLet " + UpdateService.Current : UpdateService.Status,
                a != null ? 20 : 6, a != null ? () => _ = UpdateService.ApplyAsync() : null);
        });
        var auto = new WF.ToolStripMenuItem(L.T("Mulai bersama Windows"));
        auto.Click += (_, _) => SetAutostart(!IsAutostart());
        menu.Opening += (_, _) => auto.Checked = IsAutostart();
        menu.Items.Add(auto);
        menu.Items.Add(L.T("Keluar"), null, (_, _) => { _tray.Visible = false; Shutdown(); });
        SD.Icon icon = null;
        try { if (!string.IsNullOrEmpty(Environment.ProcessPath)) icon = SD.Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
        _tray = new WF.NotifyIcon { Icon = icon ?? SD.SystemIcons.Application, Text = "ZaxLet", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => IslandWindow.Instance?.OpenNow();
    }

    /// <summary>Restart (e.g. after language change): new process waits 2s so this instance can release the single-instance mutex.</summary>
    public static void Restart()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & start \"\" \"{Environment.ProcessPath}\"") { CreateNoWindow = true, UseShellExecute = false });
            Current.Shutdown();
        }
        catch (Exception ex) { Log(ex); }
    }

    static string ExeFromRunValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        if (File.Exists(s)) return s;
        int sp = s.IndexOf(' ');
        return sp > 0 ? s[..sp] : s;
    }

    public static bool IsAutostart()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            var exe = ExeFromRunValue(k?.GetValue("ZaxLet") as string);
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
            using var sa = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
            if (sa?.GetValue("ZaxLet") is byte[] b && b.Length > 0 && (b[0] & 1) != 0) return false;
            return true;
        }
        catch { return false; }
    }

    public static void SetAutostart(bool on)
    {
        if (on)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            k?.SetValue("ZaxLet", $"\"{exe}\"");
        }
        else
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            k?.DeleteValue("ZaxLet", false);
        }
        // clear disabled status from Task Manager > Startup apps so the Run entry is truly active/clean
        using var sa = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, true);
        sa?.DeleteValue("ZaxLet", false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        PlayLog.Flush();
        AppUsage.I.Flush();
        base.OnExit(e);
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            var p = Path.Combine(Settings.Dir, "error.log");
            if (File.Exists(p) && new FileInfo(p).Length > 200_000) File.Move(p, p + ".old", true);   // size limit: keep one old generation
            // network errors (timeout, 429, 403, DNS) need just one line, no stack trace
            var net = ex is System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException or System.Net.Sockets.SocketException or System.Net.WebException;
            File.AppendAllText(p, net ? $"{DateTime.Now:s} {ex.GetType().Name}: {ex.Message}\n" : $"{DateTime.Now:s} {ex}\n\n");
        }
        catch { }
    }
}
