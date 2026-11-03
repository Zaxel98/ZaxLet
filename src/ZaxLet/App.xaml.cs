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
        // ZAXLET_NO_SINGLE=1: izinkan instans kedua (uji di PC yang sudah menjalankan ZaxLet; dipakai bersama ZAXLET_DATA_DIR)
        if (Environment.GetEnvironmentVariable("ZAXLET_NO_SINGLE") != "1")
        {
            _mutex = new Mutex(true, "ZaxLet.Single.Instance", out bool first);
            if (!first) { Shutdown(); return; }
        }
        base.OnStartup(e);
        // UI sederhana: render software menghindari driver grafis (puluhan MB RAM) dan GPU tetap bebas
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);

        MigrateLegacy();
        Settings.Load();
        Translit.PinyinFile = RomajiPack.PinyinFile;   // tabel pinyin ada di Paket tulisan Latin (opsional), bukan di installer
        var w = new IslandWindow();
        w.Show();
        // tur pengguna baru dan pemeriksaan pembaruan: tidak untuk pemakaian uji (flag --… atau folder data lain)
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
        // `ZaxLet.exe --tab=prayer` membuka dan menahan pill pada satu tab (dipakai untuk uji visual)
        foreach (var a in e.Args)
        {
            if (a.StartsWith("--tab=")) { Settings.Cur.LastTab = a[6..]; w.OpenNow(); w.KeepOpen(true); }
            else if (a == "--shot") CaptureService.Full();
            else if (a == "--ui-test") UiTest.Run();   // gambar tiap halaman Pengaturan dan langkah panduan -> PNG di ZAXLET_UI_OUT
            else if (a == "--usage-test")   // kuota Claude/Codex yang berhasil dibaca dan dari sumber mana -> usage-test.txt
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await UsageService.I.RefreshQuotaAsync();
                    // jendela island menjalankan pembacaan yang sama: tunggu sampai sumber Claude jelas (maks. 20 dtk)
                    for (int i = 0; i < 20 && UsageService.I.Claude.Source == ""; i++) { await System.Threading.Tasks.Task.Delay(1000); await UsageService.I.RefreshQuotaAsync(); }
                    string F(ProviderUsage u) => $"{u.Name}: sumber={(u.Source == "" ? "-" : u.Source)} catatan={u.Error ?? "-"} jendela=" + string.Join(", ", u.Windows.Select(x => $"{x.Label}={x.Pct}%"));
                    File.WriteAllText(Path.Combine(Settings.Dir, "usage-test.txt"), F(UsageService.I.Claude) + "\n" + F(UsageService.I.Codex));
                });
            }
            else if (a == "--update-test")   // hasil cek rilis GitHub -> update-test.txt (ZAXLET_UPDATE_URL mengganti alamat)
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await UpdateService.CheckAsync();
                    File.WriteAllText(Path.Combine(Settings.Dir, "update-test.txt"), $"saat ini={UpdateService.Current} tersedia={UpdateService.Available?.Version.ToString() ?? "-"} status={UpdateService.Status}");
                });
            }
            else if (a == "--feat-test")   // sumber OSD/privasi/jaringan yang tersedia di PC ini -> feat-test.txt
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
            else if (a == "--mon-test")   // sumber suhu/GPU yang terbaca -> mon-test.txt (uji tab Monitor di PC lain)
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    var sm = new SysMon();
                    for (int i = 0; i < 3; i++) { sm.Sample(); await sm.SampleSlowAsync(); await System.Threading.Tasks.Task.Delay(3200); }
                    File.WriteAllText(Path.Combine(Settings.Dir, "mon-test.txt"), $"cpuTemp={sm.CpuTemp?.ToString("0.0")} gpuUtil={sm.GpuUtil?.ToString("0.00")} gpuTemp={sm.GpuTemp?.ToString("0.0")} gpuPower={sm.GpuPower?.ToString("0.0")}");
                });
            }
            else if (a.StartsWith("--yt-trace="))   // tiap detik: lagu, posisi, durasi, keadaan lirik -> yt-trace.txt (uji pembacaan posisi YouTube)
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
        menu.Items.Add(L.T("Buka"), null, (_, _) => IslandWindow.Instance?.OpenNow());
        menu.Items.Add(L.T("Sembunyikan / tampilkan pill"), null, (_, _) => IslandWindow.Instance?.ToggleHidden());
        menu.Items.Add(L.T("Pengaturan"), null, (_, _) => SettingsWindow.ShowIt());
        var auto = new WF.ToolStripMenuItem(L.T("Mulai bersama Windows")) { Checked = IsAutostart(), CheckOnClick = true };
        auto.CheckedChanged += (_, _) => SetAutostart(auto.Checked);
        menu.Items.Add(auto);
        menu.Items.Add(L.T("Keluar"), null, (_, _) => { _tray.Visible = false; Shutdown(); });
        _tray = new WF.NotifyIcon { Icon = SD.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? SD.SystemIcons.Application, Text = "ZaxLet", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => IslandWindow.Instance?.OpenNow();
    }

    /// <summary>Mulai ulang (mis. setelah ganti bahasa): proses baru menunggu 2 dtk agar instans ini sempat melepas mutex single-instance.</summary>
    public static void Restart()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & start \"\" \"{Environment.ProcessPath}\"") { CreateNoWindow = true, UseShellExecute = false });
            Current.Shutdown();
        }
        catch (Exception ex) { Log(ex); }
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
