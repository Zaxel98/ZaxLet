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

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "ZaxLet.Single.Instance", out bool first);
        if (!first) { Shutdown(); return; }
        base.OnStartup(e);
        // UI sederhana: render software menghindari driver grafis (puluhan MB RAM) dan GPU tetap bebas
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);

        MigrateLegacy();
        Settings.Load();
        var w = new IslandWindow();
        w.Show();
        // `ZaxLet.exe --tab=prayer` membuka dan menahan pill pada satu tab (dipakai untuk uji visual)
        foreach (var a in e.Args)
        {
            if (a.StartsWith("--tab=")) { Settings.Cur.LastTab = a[6..]; w.OpenNow(); w.KeepOpen(true); }
            else if (a == "--shot") CaptureService.Full();
            else if (a == "--romaji-test")   // uji Kawazu: hasil ditulis ke romaji-test.txt
            {
                var inFile = Path.Combine(Settings.Dir, "romaji-in.txt");   // opsional: baris uji sendiri, satu per baris
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
            else if (a.StartsWith("--karaoke-trace="))   // rekam karaoke tiap 250 ms selama N dtk -> karaoke-trace.txt
            {
                var parts = a[16..].Split(',');   // --karaoke-trace=detik[,jumlahLagu]  (bawaan 5 lagu berlirik)
                int secs = int.Parse(parts[0]), maxSongs = parts.Length > 1 ? int.Parse(parts[1]) : 5;
                var m = MediaService.I;
                AudioFlow.I.Labeler = m.VocalLabel;
                AudioFlow.I.Acquire();
                var sb = new System.Text.StringBuilder();
                var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                var t0 = DateTime.Now;
                // Aturan: iklan (artis "Spotify" / durasi < 45 dtk) dan lagu tanpa lirik TIDAK dihitung. Lirik dicari dulu lewat semua sumber; hanya bila
                // ketemu lagu direkam (dihitung), bila tidak lagu dilewati. Berhenti setelah 5 lagu berlirik selesai (lagu ke-6 mulai) atau batas waktu.
                string song = null, skippedKey = null; int songs = 0, skipped = 0, tick = 0; bool recording = false, finished = false;
                void Flush() => File.WriteAllText(Path.Combine(Settings.Dir, "karaoke-trace.txt"), $"songs={songs} skipped={skipped} finished={finished}\n{AudioFlow.I.ModelInfo}\n" + sb);
                tm.Tick += (_, _) =>
                {
                    bool ad = m.Artist == "Spotify" || (m.Duration > TimeSpan.Zero && m.Duration.TotalSeconds < 45) || string.IsNullOrEmpty(m.Title);
                    bool timeUp = (DateTime.Now - t0).TotalSeconds >= secs;
                    if (++tick % 240 == 0 || timeUp) Flush();   // simpan berkala (tiap ±60 dtk)
                    if (ad) { if (timeUp) { tm.Stop(); AudioFlow.I.Release(); } return; }
                    var key = m.Title + "|" + m.Artist;
                    if (key != song)
                    {
                        if (recording && songs >= maxSongs) { finished = true; Flush(); tm.Stop(); AudioFlow.I.Release(); return; }   // lagu ke-6 mulai: 5 lagu selesai
                        song = key; recording = false;
                    }
                    if (!recording)
                    {
                        if (m.LyricState == "ok" && m.Lyrics.Count > 0) { recording = true; songs++; sb.AppendLine($"### {m.Title} | {m.Artist} | dur={m.Duration.TotalSeconds:0}s lyricsLines={m.Lyrics.Count}"); }
                        else if (m.LyricState == "none" && skippedKey != key) { skipped++; skippedKey = key; sb.AppendLine($"### SKIP (tanpa lirik) {m.Title} | {m.Artist}"); }   // dilewati, tidak dihitung
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
            else if (a.StartsWith("--flow-test"))   // tangkap audio keluaran (bawaan 8 dtk, --flow-test=40) -> flow-test.txt (putar musik dulu)
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
            else if (a.StartsWith("--lyrics-test="))   // --lyrics-test=Judul|Artis|detik -> lyrics-test.txt
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
        _ = MediaService.I.StartAsync();
        EyeService.I.Apply();
        PrayerAlerts.Start();
        HydrationService.I.Start();
        DiscordRpc.I.Start();
        _ = LocationService.RefreshAsync();
        if (Settings.Cur.ShareAuto || Settings.Cur.VCamOn) ShareServer.I.Start();
        if (Settings.Cur.VCamOn) VirtualCam.Start();
    }

    /// <summary>Versi lama bernama "Island": pindahkan folder data dan nilai autostartnya sekali supaya pengaturan tidak hilang.</summary>
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
        menu.Items.Add("Buka", null, (_, _) => IslandWindow.Instance?.OpenNow());
        menu.Items.Add("Sembunyikan / tampilkan pill", null, (_, _) => IslandWindow.Instance?.ToggleHidden());
        menu.Items.Add("Pengaturan", null, (_, _) => SettingsWindow.ShowIt());
        var auto = new WF.ToolStripMenuItem("Mulai bersama Windows") { Checked = IsAutostart(), CheckOnClick = true };
        auto.CheckedChanged += (_, _) => SetAutostart(auto.Checked);
        menu.Items.Add(auto);
        menu.Items.Add("Keluar", null, (_, _) => { _tray.Visible = false; Shutdown(); });
        _tray = new WF.NotifyIcon { Icon = SD.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? SD.SystemIcons.Application, Text = "ZaxLet", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => IslandWindow.Instance?.OpenNow();
    }

    public static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("ZaxLet") != null;
    }

    public static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (on) k.SetValue("ZaxLet", $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue("ZaxLet", false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(Path.Combine(Settings.Dir, "error.log"), $"{DateTime.Now:s} {ex}\n\n");
        }
        catch { }
    }
}
