using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZaxLet;

/// <summary>Pengaturan berkategori (gaya BrimDeck): kategori di kiri, baris pengaturan di kanan. Setiap perubahan langsung disimpan dan diterapkan.</summary>
public sealed class SettingsView : TabView
{
    public override string Key => "settings";
    public override string Glyph => "";
    public override string Label => "Atur";
    public override bool CenterV => false;
    public override double H => 292;

    static readonly (string Key, string Label, string Glyph)[] Pages =
    {
        ("general", "Umum", ""), ("idle", "Pill idle", ""), ("music", "Musik", ""), ("usage", "Usage", ""), ("monitor", "Monitor", ""),
        ("calendar", "Kalender", ""), ("prayer", "Sholat", ""), ("water", "Minum", "\U0001F4A7"), ("apps", "Aplikasi", ""),("notif", "Notifikasi HP", ""),("phone", "Perangkat HP", ""),("tools", "Alat",""), ("tabs", "Tab", ""),
        ("addons", "Addon", ""),
    };

    readonly StackPanel _cats = new();
    readonly StackPanel _rows = new();
    readonly ScrollViewer _scroll;
    public static string StartPage = "general";
    string _page = StartPage;
    bool _confirmReset;

    public SettingsView()
    {
        var g = UI.Cols(142, 1, -1);
        g.Children.Add(_cats);
        g.Children.Add(new Border { Width = 1, Background = UI.Track, Margin = new Thickness(0, 2, 12, 2) }.At(0, 1));
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _rows, Margin = new Thickness(0, 0, 0, 0) };
        g.Children.Add(_scroll.At(0, 2));
        Children.Add(g);
    }

    public override void OnShow() { _confirmReset = false; ShareServer.I.DevicesChanged += OnDevices; LocationService.Changed += OnDevices; PhoneNotifs.Changed += OnDevices; DiscordRpc.I.Changed += OnDevices; Render(); }
    public override void OnHide() { ShareServer.I.DevicesChanged -= OnDevices; LocationService.Changed -= OnDevices; PhoneNotifs.Changed -= OnDevices; DiscordRpc.I.Changed -= OnDevices; }
    void OnDevices() { if (_page is "phone" or "prayer" or "notif" or "apps") Render(); }

    void Render()
    {
        _cats.Children.Clear();
        foreach (var (key, label, glyph) in Pages)
        {
            var k = key;
            bool on = key == _page;
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = UI.Icon(glyph, 14, on ? UI.Accent : UI.Dim);
            ic.Width = 20;
            item.Children.Add(ic);
            var tx = UI.T(label, 12.5, on ? UI.Fg : UI.Dim, on);
            tx.Margin = new Thickness(6, 0, 0, 0);
            item.Children.Add(tx);
            var b = UI.Btn(item, () => { _page = k; _scroll.ScrollToTop(); Render(); },
                           on ? UI.CardOn : Brushes.Transparent, 10, new Thickness(9, 5, 9, 5));
            b.Margin = new Thickness(0, 0, 8, 1);
            _cats.Children.Add(b);
        }
        _rows.Children.Clear();
        foreach (var (label, ctl) in PageRows(_page)) _rows.Children.Add(Row(label, ctl));
    }

    // ---------- isi tiap kategori ----------
    IEnumerable<(string, UIElement)> PageRows(string page)
    {
        var s = Settings.Cur;
        switch (page)
        {
            case "general":
                yield return ("Mulai bersama Windows", Toggle(App.IsAutostart, v => App.SetAutostart(v), false));
                yield return ("Buka saat kursor di atas pill", Toggle(() => s.HoverOpen, v => s.HoverOpen = v));
                yield return ("Jeda sebelum menutup", Dur(() => s.CloseDelayMs / 1000.0, v => s.CloseDelayMs = (int)(v * 1000), 0.05, 600));
                yield return ("Sembunyikan saat fullscreen", Toggle(() => s.AutoHideFullscreen, v => s.AutoHideFullscreen = v));
                yield return ("Tahan Alt di atas pill = hilang sementara", Toggle(() => s.AltGhost, v => s.AltGhost = v));
                yield return ("Sembunyikan lebih lama", UI.T("pintasan di bawah atau menu tray", 11.5, UI.Dim));
                yield return ("Pintasan sembunyikan pill", Txt(() => s.HideKey, v => s.HideKey = v.Trim().ToLowerInvariant(), 150));
                yield return ("Otomatis tampil lagi setelah", Num(0, 240, 5, " mnt (0 = manual)", () => s.SnoozeMin, v => s.SnoozeMin = v));
                yield return ("Warna aksen", Choice(UI.Accents.Select(a => (a.Key, a.Label)).ToArray(), () => s.Accent, v => s.Accent = v));
                yield return ("Format jam", Choice(new[] { ("dot", "17.52"), ("colon", "17:52"), ("12h", "5:52 PM") }, () => s.ClockStyle, v => s.ClockStyle = v));
                yield return ("Tampilkan detik", Toggle(() => s.ClockSeconds, v => s.ClockSeconds = v));
                yield return ("Kembalikan semua pengaturan", Act(_confirmReset ? "Klik lagi untuk yakin" : "Reset", () =>
                {
                    if (!_confirmReset) { _confirmReset = true; Render(); return; }
                    _confirmReset = false;
                    Settings.Reset();
                    EyeService.I.Apply();
                    Render();
                }, UI.Warn));
                yield return ("Keluar dari ZaxLet", Act("Keluar", () => Application.Current.Shutdown(), UI.Bad));
                break;

            case "idle":
                var pos = new[] { ("left", "Kiri"), ("center", "Tengah"), ("right", "Kanan") };
                yield return ("Jarak antar komponen", Num(0, 40, 2, " px", () => s.IdleGap, v => s.IdleGap = v));
                yield return ("Zona tengah tepat di tengah layar", Toggle(() => s.IdleExactCenter, v => s.IdleExactCenter = v));
                yield return ("Titik status HP (kosong = tidak tersambung)", Toggle(() => s.IdlePhoneDot, v => s.IdlePhoneDot = v));
                yield return ("Posisi titik status HP", Choice(pos, () => s.PosPhone, v => s.PosPhone = v));
                yield return ("Posisi jam", Choice(pos, () => s.PosClock, v => s.PosClock = v));
                yield return ("Baterai", Toggle(() => s.IdleBattery, v => s.IdleBattery = v));
                yield return ("Posisi baterai", Choice(pos, () => s.PosBattery, v => s.PosBattery = v));
                yield return ("Angka persen baterai", Toggle(() => s.IdleBatteryPct, v => s.IdleBatteryPct = v));
                yield return ("Usage AI", Toggle(() => s.IdleUsage, v => s.IdleUsage = v));
                yield return ("Posisi usage AI", Choice(pos, () => s.PosUsage, v => s.PosUsage = v));
                yield return ("Gaya usage AI", Choice(new[] { ("ringpct", "Cincin + %"), ("ring", "Cincin"), ("pct", "Persen"), ("bar", "Bar + %") }, () => s.IdleUsageStyle, v => s.IdleUsageStyle = v));
                yield return ("Penyedia usage", Choice(new[] { ("both", "Claude + Codex"), ("claude", "Claude"), ("codex", "Codex") }, () => s.IdleUsageProviders, v => s.IdleUsageProviders = v));
                yield return ("Jendela kuota", Choice(new[] { ("5h", "5 jam"), ("weekly", "Mingguan") }, () => s.IdleUsageWindow, v => s.IdleUsageWindow = v));
                yield return ("Kecepatan internet", Toggle(() => s.IdleSpeed, v => s.IdleSpeed = v));
                yield return ("Posisi kecepatan", Choice(pos, () => s.PosSpeed, v => s.PosSpeed = v));
                yield return ("Isi kecepatan", Choice(new[] { ("down", "↓ unduh"), ("both", "↓ ↑ keduanya") }, () => s.IdleSpeedStyle, v => s.IdleSpeedStyle = v));
                yield return ("Waveform saat musik main", Toggle(() => s.IdleWave, v => s.IdleWave = v));
                yield return ("Posisi waveform", Choice(pos, () => s.PosWave, v => s.PosWave = v));
                yield return ("Lirik di baris ke-2", Toggle(() => s.IdleLyrics, v => s.IdleLyrics = v));
                break;

            case "music":
                yield return ("Tampilkan lirik (tab dan idle)", Toggle(() => s.MusicLyrics, v => s.MusicLyrics = v));
                yield return ("Romaji untuk lirik Jepang", Toggle(() => s.LyricRomaji, v => s.LyricRomaji = v));
                yield return ("Hanya romaji (sembunyikan lirik asli)", Toggle(() => s.RomajiOnly, v => s.RomajiOnly = v));
                yield return ("Lirik pill idle pakai romaji", Toggle(() => s.IdleRomaji, v => s.IdleRomaji = v));
                yield return ("Catatan romaji", UI.T("Hanya berlaku untuk lirik Jepang; lagu lain tampil seperti biasa", 11.5, UI.Dim));
                if (!RomajiPack.Installed) yield return ("Romaji untuk kanji", UI.T("Unduh di Pengaturan > Addon (kana sudah otomatis)", 11.5, UI.Dim));
                yield return ("Lirik karaoke (isi per kata)", Toggle(() => s.LyricKaraoke, v => s.LyricKaraoke = v));
                yield return ("Karaoke mengikuti vokal (baca audio)", Toggle(() => s.LyricFlow, v => s.LyricFlow = v));
                yield return ("Toleransi pembacaan vokal", Choice(new[] { ("0.8", "±0,8 dtk (ketat)"), ("1.5", "±1,5 dtk"), ("2.5", "±2,5 dtk"), ("4", "±4 dtk (longgar)") },
                    () => s.FlowTolSec.ToString(CultureInfo.InvariantCulture), v => s.FlowTolSec = double.Parse(v, CultureInfo.InvariantCulture)));
                yield return ("Soal audio", UI.T("Membaca keluaran suara PC secara lokal hanya saat tab Musik terbuka; tidak direkam atau dikirim", 11.5, UI.Dim));
                yield return ("Tempo isi lirik", Choice(new[] { ("fast", "Cepat"), ("normal", "Normal"), ("slow", "Lambat") }, () => s.LyricPace, v => s.LyricPace = v));
                yield return ("Offset sinkron lirik", Num(-5000, 5000, 100, " ms", () => s.LyricOffsetMs, v => s.LyricOffsetMs = v));
                yield return ("Warna latar dari sampul", Toggle(() => s.TintCover, v => s.TintCover = v));
                break;

            case "usage":
                yield return ("Claude", Toggle(() => s.ClaudeOn, v => s.ClaudeOn = v));
                yield return ("Codex", Toggle(() => s.CodexOn, v => s.CodexOn = v));
                yield return ("Interval baca kuota", Dur(() => s.UsageRefreshSec, v => s.UsageRefreshSec = v, 1, 86400));
                yield return ("Tampilkan perkiraan biaya", Toggle(() => s.ShowCost, v => s.ShowCost = v));
                yield return ("Kolom Code (data Ponytail)", Toggle(() => s.ShowPonytail, v => s.ShowPonytail = v));
                yield return ("Peringatan kuning di", Num(30, 95, 5, "%", () => s.WarnAt, v => s.WarnAt = v));
                yield return ("Peringatan merah di", Num(50, 100, 5, "%", () => s.CritAt, v => s.CritAt = v));
                break;

            case "monitor":
                yield return ("Tampilkan GPU", Toggle(() => s.MonGpu, v => s.MonGpu = v));
                yield return ("Bar per core CPU", Toggle(() => s.MonCores, v => s.MonCores = v));
                yield return ("Daftar proses teratas", Toggle(() => s.MonTop, v => s.MonTop = v));
                break;

            case "calendar":
                yield return ("Minggu dimulai", Choice(new[] { ("mon", "Senin"), ("sun", "Minggu") }, () => s.WeekMonday ? "mon" : "sun", v => s.WeekMonday = v == "mon"));
                yield return ("Tampilkan hari libur", Toggle(() => s.ShowHolidays, v => s.ShowHolidays = v));
                break;

            case "prayer":
                yield return ("Sumber lokasi", Choice(new[] { ("auto", "Otomatis (IP)"), ("device", "Perangkat (izin)"), ("manual", "Cari kota") }, () => s.LocMode, v => s.LocMode = v,
                    () => { if (s.LocMode != "manual") _ = LocationService.RefreshAsync(true).ContinueWith(_ => Dispatcher.BeginInvoke(Render)); }));
                yield return ("Kota", s.LocMode == "manual"
                    ? Txt(() => s.City, v => { if (!string.IsNullOrWhiteSpace(v) && v.Trim() != s.City) _ = LocationService.SearchAsync(v.Trim()).ContinueWith(_ => Dispatcher.BeginInvoke(Render)); })
                    : UI.T(s.City, 12.5, UI.Fg, true));
                yield return ("Status lokasi", UI.T(string.IsNullOrEmpty(LocationService.Status) ? "—" : LocationService.Status, 11.5, UI.Dim));
                if (s.LocMode != "manual")
                    yield return ("Deteksi ulang", Act("Deteksi sekarang", () => { _ = LocationService.RefreshAsync(true).ContinueWith(_ => Dispatcher.BeginInvoke(Render)); }, UI.Fg));
                yield return ("Metode hitung", Choice(PrayerAlerts.Methods.Select(m => (m.Key, m.Label)).ToArray(), () => s.PrayerMethod, v => s.PrayerMethod = v));
                yield return ("Mazhab Asar", Choice(new[] { ("shafi", "Syafi'i"), ("hanafi", "Hanafi") }, () => s.AsrHanafi ? "hanafi" : "shafi", v => s.AsrHanafi = v == "hanafi"));
                yield return ("Ihtiyat", Num(0, 5, 1, " mnt", () => s.Ihtiyat, v => s.Ihtiyat = v));
                yield return ("Notifikasi waktu sholat", Toggle(() => s.PrayerAlert, v => s.PrayerAlert = v));
                yield return ("Jeda musik saat sholat", Toggle(() => s.PrayerPauseMusic, v => s.PrayerPauseMusic = v));
                break;

            case "phone":
                yield return ("Server sambungan HP", Toggle(() => ShareServer.I.On, v => { if (v) ShareServer.I.Start(); else ShareServer.I.Stop(); }, false, Render));
                yield return ("Aktif otomatis saat ZaxLet mulai", Toggle(() => s.ShareAuto, v => s.ShareAuto = v));
                if (ShareServer.I.On)
                {
                    yield return ("Alamat HP", PhoneLinks());
                    yield return ("QR menuju", Choice(new[] { ("lan", "Jaringan sama"), ("tailscale", "Online (Tailscale)") }, () => s.ShareQr, v => s.ShareQr = v, Render));
                    yield return ("Perangkat", PhoneDevices());
                    yield return ("Aplikasi HP", AppQr());
                    yield return ("Aplikasi tersambung", UI.T(ShareServer.I.AppCount == 0 ? "belum ada" : ShareServer.I.AppCount + " HP", 12.5, ShareServer.I.AppCount == 0 ? UI.Dim : UI.Fg, true));
                    yield return ("Kamera yang dipakai", Choice(new[] { ("back", "Belakang"), ("front", "Depan") }, () => s.CamFront ? "front" : "back", v => s.CamFront = v == "front"));
                    yield return ("Kualitas kamera", Choice(new[] { ("480", "480p"), ("720", "720p"), ("1080", "1080p") }, () => s.CamRes.ToString(), v => s.CamRes = int.Parse(v)));
                    yield return ("FPS kamera", Choice(new[] { ("10", "10"), ("15", "15"), ("24", "24"), ("30", "30") }, () => s.CamFps.ToString(), v => s.CamFps = int.Parse(v)));
                    yield return ("Kamera HP (aplikasi)", new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            Act("Nyalakan", () => { CameraWindow.ShowIt(); CameraWindow.CamOn(); }, UI.Fg),
                            new Border { Width = 6 },
                            Act("Matikan", () => ShareServer.I.Command("cam off"), UI.Fg),
                        }
                    });
                    yield return ("Kamera virtual (semua aplikasi)", Toggle(() => VirtualCam.Running, v => { if (v) VirtualCam.Start(); else VirtualCam.Stop(); s.VCamOn = VirtualCam.Running; }, true, Render));
                    yield return ("Status kamera virtual", UI.T(!VirtualCam.Available ? "softcam.dll tidak ada" : !VirtualCam.Registered ? "Didaftarkan otomatis saat kamera pertama dinyalakan" : VirtualCam.Running ? "Aktif: pilih \"DirectShow Softcam\" di Discord/Zoom" : "Terdaftar, belum aktif", 11.5, VirtualCam.Running ? UI.Fg : UI.Dim));
                    yield return ("Rasio kamera virtual", Choice(new[] { ("1:1", "1:1"), ("4:3", "4:3"), ("9:16", "9:16"), ("16:9", "16:9") }, () => s.VCamRatio, v => s.VCamRatio = v));
                    yield return ("Landscape otomatis saat HP dimiringkan", Toggle(() => s.VCamAuto, v => s.VCamAuto = v));
                    yield return ("Bingkai kamera virtual", Choice(new[] { ("crop", "Isi penuh (potong)"), ("fit", "Utuh (bar hitam)") }, () => s.VCamFit, v => s.VCamFit = v));
                    yield return ("Putar kamera virtual", Choice(new[] { ("0", "0°"), ("90", "90°"), ("180", "180°"), ("270", "270°") }, () => s.VCamRot.ToString(), v => s.VCamRot = int.Parse(v)));
                    yield return ("Cermin kamera virtual", Toggle(() => s.VCamMirror, v => s.VCamMirror = v));
                    yield return ("Kamera HP sebagai webcam", Act("Buka jendela kamera", () => CameraWindow.ShowIt(), UI.Fg));
                    yield return ("Alamat halaman kamera", Act("Salin tautan", () => System.Windows.Clipboard.SetText(ShareServer.I.CamPageUrl), UI.Fg));
                }
                yield return ("Ganti token (putuskan semua HP)", Act("Ganti", () => { ShareServer.I.ResetToken(); Render(); }, UI.Warn));
                break;

            case "apps":
                foreach (var (k, label) in new[] { ("discord.mic", "Discord: mic"), ("discord.deaf", "Discord: deafen"), ("meet.mic", "Meet: mic"), ("meet.cam", "Meet: kamera") })
                {
                    var kk = k;
                    yield return (label, Txt(() => s.AppKeys.GetValueOrDefault(kk, ""), v => s.AppKeys[kk] = v.Trim().ToLowerInvariant(), 150));
                }
                yield return ("Format pintasan", UI.T("contoh: ctrl+shift+m, ctrl+d, f9", 11.5, UI.Dim));
                yield return ("Discord", Act("Atur ulang sambungan", () => { s.DiscordClientId = s.DiscordSecret = ""; s.DiscordSetupAsked = false; DiscordRpc.I.ResetToken(); Render(); }, UI.Warn));
                yield return ("Status Discord", UI.T(DiscordRpc.I.Connected ? "Tersambung" + (DiscordRpc.I.InVoice ? " · voice: " + DiscordRpc.I.Channel : "") : DiscordRpc.Configured ? DiscordRpc.I.Status : "Belum disambungkan (tekan tombol Discord di tab Komunikasi)", 11.5, DiscordRpc.I.Connected ? UI.Fg : UI.Dim));
                break;

            case "notif":
                yield return ("Tampilkan notifikasi HP di pill", Toggle(() => s.NotifOn, v => s.NotifOn = v));
                yield return ("Lama tampil", Dur(() => s.NotifShowSec, v => s.NotifShowSec = v, 2, 120));
                yield return ("Muncul walau fullscreen / disembunyikan", Toggle(() => s.NotifFullscreen, v => s.NotifFullscreen = v));
                yield return ("Notifikasi berjalan (Gojek, dll)", Toggle(() => s.NotifOngoing, v => s.NotifOngoing = v));
                yield return ("Aplikasi baru langsung tampil", Toggle(() => s.NotifNewDefault, v => s.NotifNewDefault = v));
                yield return ("Diterima dari HP", UI.T(PhoneNotifs.Received == 0 ? "belum ada" : PhoneNotifs.Received + " pesan, terakhir " + PhoneNotifs.LastPkg + " (" + PhoneNotifs.LastAt.ToString("HH.mm.ss") + ")", 11.5, PhoneNotifs.Received == 0 ? UI.Dim : UI.Fg));
                yield return ("Uji notifikasi", Act("Tampilkan contoh", () => PhoneNotifs.Test(), UI.Fg));
                string[][] known;
                lock (PhoneNotifs.Apps) known = PhoneNotifs.Apps.OrderBy(a => a.Value).Select(a => new[] { a.Key, a.Value }).ToArray();
                if (known.Length == 0)
                    yield return ("Aplikasi", UI.T("Belum ada notifikasi masuk. Di aplikasi ZaxLet HP: izinkan akses notifikasi.", 11.5, UI.Dim));
                foreach (var kv in known)
                {
                    var pkg = kv[0];
                    yield return (kv[1], Toggle(() => s.NotifApps.TryGetValue(pkg, out var on) ? on : s.NotifNewDefault, v => s.NotifApps[pkg] = v));
                }
                break;

            case "tools":
                yield return ("Folder tangkapan (kosong = Pictures\\ZaxLet)", Txt(() => s.CapDir, v => s.CapDir = v.Trim(), 200));
                yield return ("Salin tangkapan ke clipboard", Toggle(() => s.CapClipboard, v => s.CapClipboard = v));
                yield return ("FPS rekam layar", Choice(new[] { ("15", "15"), ("30", "30"), ("60", "60") }, () => s.RecFps.ToString(), v => s.RecFps = int.Parse(v)));
                yield return ("Pomodoro: fokus", Dur(() => s.PomoWork * 60, v => s.PomoWork = v / 60, 10, 86400, null, 60));
                yield return ("Pomodoro: istirahat", Dur(() => s.PomoBreak * 60, v => s.PomoBreak = v / 60, 10, 86400, null, 60));
                yield return ("Pengingat istirahat mata", Toggle(() => s.EyeOn, v => { s.EyeOn = v; }, true, () => EyeService.I.Apply()));
                yield return ("Interval istirahat mata", Dur(() => s.EyeMinutes * 60, v => s.EyeMinutes = v / 60, 10, 86400, () => EyeService.I.Apply(), 60));
                break;

            case "water":
                yield return ("Pengingat minum", Toggle(() => s.WaterOn, v => s.WaterOn = v));
                yield return ("Target harian", Txt(() => s.WaterGoalMl.ToString() + " ml", v => { if (int.TryParse(new string(v.Where(char.IsDigit).ToArray()), out var n) && n is >= 100 and <= 20000) s.WaterGoalMl = n; }, 100));
                yield return ("Ukuran satu gelas", Txt(() => s.WaterCupMl.ToString() + " ml", v => { if (int.TryParse(new string(v.Where(char.IsDigit).ToArray()), out var n) && n is >= 20 and <= 3000) s.WaterCupMl = n; }, 100));
                yield return ("Jam aktif mulai", Txt(() => s.WaterFrom, v => { if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1)) s.WaterFrom = t.ToString(@"hh\:mm"); }, 100));
                yield return ("Jam aktif selesai", Txt(() => s.WaterTo, v => { if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1)) s.WaterTo = t.ToString(@"hh\:mm"); }, 100));
                yield return ("Jarak antar pengingat", Dur(() => s.WaterEverySec, v => s.WaterEverySec = v, 10, 86400, null, 60));
                yield return ("Hanya bila tertinggal jadwal", Toggle(() => s.WaterSmart, v => s.WaterSmart = v));
                yield return ("Lama pengingat tampil", Dur(() => s.WaterShowSec, v => s.WaterShowSec = v, 2, 600));
                yield return ("Uji pengingat", Act("Tampilkan sekarang", () => IslandWindow.Instance?.Alert($"Waktunya minum · {HydrationService.I.Today}/{HydrationService.I.Goal} ml", s.WaterShowSec), UI.Fg));
                break;

            case "addons":
                yield return ("Unduhan tambahan", UI.T("Tidak ada yang diunduh otomatis. Unduh hanya bila fiturnya dibutuhkan.", 11.5, UI.Dim));
                foreach (var a in Addons.All) yield return (a.Name + " · " + a.Size, AddonControl(a));
                break;

            case "tabs":
                yield return ("Tab yang dibuka pertama", Choice(new[] { ("last", "Terakhir dipakai") }.Concat(IslandWindow.TabOrder.Where(k => k != "settings").Select(k => (k, IslandWindow.TabMeta[k].Label))).ToArray(),
                    () => s.OpenTab, v => s.OpenTab = v));
                foreach (var key in IslandWindow.TabOrder.Where(k => k != "settings"))
                {
                    var k = key;
                    var ctl = new StackPanel { Orientation = Orientation.Horizontal };
                    void Move(int d)
                    {
                        var o = IslandWindow.TabOrder.Where(x => x != "settings").ToList();
                        int i = o.IndexOf(k), j = i + d;
                        if (j < 0 || j >= o.Count) return;
                        (o[i], o[j]) = (o[j], o[i]);
                        s.TabOrder = o;
                        Chg(); Render();
                    }
                    var up = UI.Btn(UI.Icon("\uE70E", 11, UI.Dim), () => Move(-1), UI.Card, 8, new Thickness(8, 4, 8, 4));
                    var dn = UI.Btn(UI.Icon("\uE70D", 11, UI.Dim), () => Move(1), UI.Card, 8, new Thickness(8, 4, 8, 4));
                    up.Margin = new Thickness(0, 0, 4, 0); dn.Margin = new Thickness(0, 0, 10, 0);
                    ctl.Children.Add(up); ctl.Children.Add(dn);
                    ctl.Children.Add(Toggle(() => !s.HiddenTabs.Contains(k), v => { s.HiddenTabs.Remove(k); if (!v) s.HiddenTabs.Add(k); }));
                    yield return (IslandWindow.TabMeta[k].Label, ctl);
                }
                break;
        }
    }

    // ---------- addon (unduhan opsional) ----------
    UIElement AddonControl(Addon a)
    {
        bool on = a.Installed();
        var sp = new StackPanel { MaxWidth = 280 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        string status = a.Busy ? (string.IsNullOrEmpty(a.Progress()) ? "Menunggu…" : a.Progress()) : a.Error.Length > 0 ? a.Error : on ? "Terpasang" : "Belum diunduh";
        var st = UI.T(status, 11.5, a.Error.Length > 0 && !a.Busy ? UI.Bad : on && !a.Busy ? UI.Fg : UI.Dim);
        st.VerticalAlignment = VerticalAlignment.Center; st.TextWrapping = TextWrapping.Wrap; st.MaxWidth = 170;
        top.Children.Add(st);
        bool removable = on && (a.Detail == null || a.Detail().Length == 0);   // terdeteksi di PATH = bukan milik kita, tidak dihapus
        if (!a.Busy && (!on || removable))
        {
            var b = UI.Btn(UI.T(on ? "Hapus" : "Unduh", 11.5, on ? UI.Dim : UI.Fg, true), () =>
            {
                if (on) { a.Remove(); a.Error = ""; Render(); return; }
                a.Error = ""; a.Busy = true; Render();
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await a.Install(() => Dispatcher.BeginInvoke(() => { if (_page == "addons") Render(); })); }
                    catch (Exception ex) { a.Error = "Gagal: " + ex.Message; App.Log(ex); }
                    finally { a.Busy = false; Dispatcher.BeginInvoke(() => { if (_page == "addons") Render(); }); }
                });
            }, on ? UI.Card : UI.AccentBg, 10, new Thickness(10, 4, 10, 4));
            b.Margin = new Thickness(8, 0, 0, 0);
            top.Children.Add(b);
        }
        sp.Children.Add(top);
        var d = UI.T(a.Desc + (a.Detail?.Invoke() is { Length: > 0 } det ? " (" + det + ")" : ""), 11, UI.Faint);
        d.TextWrapping = TextWrapping.Wrap; d.Margin = new Thickness(0, 3, 0, 0);
        sp.Children.Add(d);
        return sp;
    }

    // ---------- HP ----------
    static UIElement PhoneLinks()
    {
        var sv = ShareServer.I;
        var g = new StackPanel { Orientation = Orientation.Horizontal };
        g.Children.Add(new Image { Source = ShelfView.Qr(sv.Url), Width = 112, Height = 112, SnapsToDevicePixels = true, Margin = new Thickness(0, 0, 12, 0) });
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        UIElement Link(string label, string url)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(UI.T(label, 11, UI.Dim));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var u = UI.T(url ?? "tidak terdeteksi (pasang/nyalakan Tailscale)", 11.5, url == null ? UI.Faint : UI.Fg);
            u.MaxWidth = 165; u.TextTrimming = TextTrimming.CharacterEllipsis; u.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(u);
            if (url != null) { var b = UI.Btn(UI.T("Salin", 11, UI.Fg), () => System.Windows.Clipboard.SetText(url), UI.Card, 8, new Thickness(8, 2, 8, 2)); b.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(b); }
            sp.Children.Add(row);
            return sp;
        }
        t.Children.Add(Link("Jaringan yang sama", sv.LanUrl));
        t.Children.Add(Link("Dari mana saja (Tailscale)", sv.TailUrl));
        g.Children.Add(t);
        return g;
    }

    // dua QR: pasangan aplikasi (zaxlet://pair…) dan unduhan APK (dibuka di browser HP)
    static UIElement AppQr()
    {
        var sv = ShareServer.I;
        var g = new StackPanel { Orientation = Orientation.Horizontal };
        UIElement One(string url, string cap)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            sp.Children.Add(new Image { Source = ShelfView.Qr(url), Width = 96, Height = 96, SnapsToDevicePixels = true });
            var t = UI.T(cap, 10.5, UI.Dim); t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(t);
            return sp;
        }
        g.Children.Add(One(sv.ApkUrl, "1. Unduh APK"));
        g.Children.Add(One(sv.PairLink, "2. Pasangkan"));
        return g;
    }

    UIElement PhoneDevices()
    {
        var list = new StackPanel { MinWidth = 280 };
        var devs = ShareServer.I.Devices.OrderByDescending(d => d.LastSeen).ToList();
        if (devs.Count == 0) list.Children.Add(UI.T("Belum ada HP yang tersambung", 11.5, UI.Faint));
        foreach (var d in devs)
        {
            var row = UI.Cols(14, -1, 0, 0);
            row.Margin = new Thickness(0, 2, 0, 2);
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = d.Blocked ? UI.Bad : d.Online ? UI.B("#FF22D35A") : UI.Faint, VerticalAlignment = VerticalAlignment.Center });
            var name = UI.T($"{d.Name}  ·  {d.Ip}" + (ShareServer.I.HasApp(d.Id) ? "  · app" : "") + (d.Blocked ? "  (diblokir)" : d.Online ? "" : "  · " + d.LastSeen.ToString("dd/MM HH.mm")), 11.5, d.Online ? UI.Fg : UI.Dim);
            name.TextTrimming = TextTrimming.CharacterEllipsis; name.MaxWidth = 190; name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name.At(0, 1));
            var dd = d;
            var b1 = UI.Btn(UI.T(d.Blocked ? "Buka blokir" : "Blokir", 11, UI.Dim), () => { if (dd.Blocked) { dd.Blocked = false; Store.Save("devices.json", ShareServer.I.Devices); } else ShareServer.I.Forget(dd, true); Render(); }, UI.Card, 8, new Thickness(7, 2, 7, 2));
            var b2 = UI.Btn(UI.T("Lupakan", 11, UI.Dim), () => { ShareServer.I.Forget(dd, false); Render(); }, UI.Card, 8, new Thickness(7, 2, 7, 2));
            b1.Margin = new Thickness(6, 0, 0, 0); b2.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(b1.At(0, 2)); row.Children.Add(b2.At(0, 3));
            list.Children.Add(row);
        }
        return list;
    }


    // ---------- kontrol ----------
    static UIElement Row(string label, UIElement ctl)
    {
        var g = UI.Cols(-1, 0);
        var t = UI.T(label, 12.5);
        t.Margin = new Thickness(0, 0, 10, 0);
        g.Children.Add(t);
        ctl.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        g.Children.Add(ctl.At(0, 1));
        return new Border { BorderBrush = UI.Track, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 5, 0, 5), MinHeight = 34, Child = g };
    }

    static void Chg(Action extra = null) { extra?.Invoke(); Settings.Apply(); }

    static UIElement Toggle(Func<bool> get, Action<bool> set, bool apply = true, Action extra = null)
    {
        var knob = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var track = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(11), Cursor = Cursors.Hand, Padding = new Thickness(3, 0, 3, 0), Child = knob };
        void Paint()
        {
            bool on = get();
            track.Background = on ? UI.Accent : UI.CardOn;
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
        track.MouseLeftButtonUp += (_, _) => { set(!get()); if (apply) Chg(extra); Paint(); };
        Paint();
        return track;
    }

    static UIElement Choice((string Key, string Label)[] opts, Func<string> get, Action<string> set, Action extra = null)
    {
        var txt = UI.T("", 12.5, UI.Fg, true);
        txt.MinWidth = 96; txt.TextAlignment = TextAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(UI.Icon("", 9, UI.Dim));
        row.Children.Add(txt);
        row.Children.Add(UI.Icon("", 9, UI.Dim));
        var b = new Border { Child = row, Background = UI.Card, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand };
        void Paint() => txt.Text = Array.Find(opts, o => o.Key == get()).Label ?? get();
        b.MouseLeftButtonUp += (_, e) =>
        {
            int i = Math.Max(0, Array.FindIndex(opts, o => o.Key == get()));
            bool back = e.GetPosition(b).X < b.ActualWidth / 3;   // sepertiga kiri = mundur
            i = (i + (back ? opts.Length - 1 : 1)) % opts.Length;
            set(opts[i].Key);
            Chg(extra);
            Paint();
        };
        Paint();
        return b;
    }

    static UIElement Num(int min, int max, int step, string suffix, Func<int> get, Action<int> set, Action extra = null)
    {
        var val = UI.T("", 12.5, UI.Fg, true);
        val.MinWidth = 70; val.TextAlignment = TextAlignment.Center;
        void Paint() => val.Text = get() + suffix;
        void Bump(int d) { set(Math.Clamp(get() + d, min, max)); Chg(extra); Paint(); }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(UI.Btn(UI.T("−", 14), () => Bump(-step), UI.Card, 999, new Thickness(9, 0, 9, 2)));
        row.Children.Add(val);
        row.Children.Add(UI.Btn(UI.T("+", 14), () => Bump(step), UI.Card, 999, new Thickness(8, 0, 8, 2)));
        Paint();
        return row;
    }

    static UIElement Dur(Func<double> getSec, Action<double> setSec, double min, double max, Action extra = null, double bare = 1)
    {
        var tb = new TextBox { Width = 120, FontSize = 12.5, TextAlignment = TextAlignment.Center, Text = DurText.Fmt(getSec()), ToolTip = $"Ketik bebas, contoh: 30dtk, 10m, 1.5 jam, 1j30m. Tanpa satuan = {(bare == 60 ? "menit" : "detik")}" };
        void Commit()
        {
            if (DurText.Parse(tb.Text, bare, out var s)) { setSec(Math.Clamp(s, min, max)); Chg(extra); }
            tb.Text = DurText.Fmt(getSec());
        }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(); Keyboard.ClearFocus(); } };
        return tb;
    }

    static UIElement Txt(Func<string> get, Action<string> set, double width = 170)
    {
        var tb = new TextBox { Width = width, FontSize = 12.5, Text = get() };
        void Commit() { set(tb.Text); Chg(); tb.Text = get(); }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(); Keyboard.ClearFocus(); } };
        return tb;
    }

    static UIElement Act(string label, Action click, Brush fg) =>
        UI.Btn(UI.T(label, 12.5, fg, true), click, UI.Card, 10, new Thickness(12, 5, 12, 5));
}
