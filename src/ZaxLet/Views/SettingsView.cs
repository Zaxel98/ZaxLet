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
        ("general", "Umum", ""), ("island", "Island", ""), ("music", "Musik", ""), ("phone", "HP", ""),
        ("system", "Usage & Monitor", ""), ("calendar", "Kalender & Sholat", ""), ("remind", "Pengingat", "\U0001F4A7"), ("tools", "Alat", ""),
        ("addons", "Addon", ""),
    };

    readonly StackPanel _cats = new();
    readonly StackPanel _rows = new();
    readonly ScrollViewer _scroll;
    public static string StartPage = "general";
    string _page = Normalize(StartPage);

    /// <summary>Kunci halaman lama (idle, usage, prayer, notif, ...) dipetakan ke halaman baru; dipakai flag --page.</summary>
    static string Normalize(string p) => p switch
    {
        "idle" or "tabs" => "island", "usage" or "monitor" => "system", "prayer" => "calendar", "water" => "remind",
        "apps" => "tools", "notif" => "phone",
        _ => p
    };

    bool _confirmReset;
    readonly TextBox _search = new() { FontSize = 12, Foreground = Brushes.White, CaretBrush = Brushes.White, Background = UI.Card, BorderThickness = new Thickness(0), Padding = new Thickness(9, 6, 9, 6), VerticalContentAlignment = VerticalAlignment.Center };
    string _query = "";

    public SettingsView()
    {
        // kotak cari (Ctrl+F): mencari di semua halaman sekaligus; tetap di atas daftar halaman dan tidak ikut dibangun ulang supaya fokusnya tidak hilang
        var searchHost = new Border { CornerRadius = new CornerRadius(10), Background = UI.Card, Margin = new Thickness(0, 0, 8, 8), ClipToBounds = true };
        var searchGrid = new Grid();
        var ph = new TextBlock { Text = L.T("Cari pengaturan (Ctrl+F)"), Foreground = UI.Faint, FontSize = 12, IsHitTestVisible = false, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        searchGrid.Children.Add(_search); searchGrid.Children.Add(ph);
        searchHost.Child = searchGrid;
        _search.TextChanged += (_, _) => { ph.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; _query = _search.Text; _scroll?.ScrollToTop(); Render(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape && _search.IsKeyboardFocused && _search.Text.Length > 0) { _search.Clear(); e.Handled = true; }
        };
        _cats.Children.Add(searchHost);
        var g = UI.Cols(178, 1, -1);
        g.Children.Add(_cats);
        g.Children.Add(new Border { Width = 1, Background = UI.Track, Margin = new Thickness(0, 2, 12, 2) }.At(0, 1));
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _rows, Margin = new Thickness(0, 0, 0, 0) };
        g.Children.Add(_scroll.At(0, 2));
        Children.Add(g);
    }

    void OnUpdate() => Dispatcher.BeginInvoke(() => { if (_page == "general") Render(); });

    public override void OnShow() { _confirmReset = false; UpdateService.Changed += OnUpdate; ShareServer.I.DevicesChanged += OnDevices; LocationService.Changed += OnDevices; PhoneNotifs.Changed += OnDevices; DiscordRpc.I.Changed += OnDevices; Render(); }
    public override void OnHide() { UpdateService.Changed -= OnUpdate; ShareServer.I.DevicesChanged -= OnDevices; LocationService.Changed -= OnDevices; PhoneNotifs.Changed -= OnDevices; DiscordRpc.I.Changed -= OnDevices; }
    void OnDevices() { if (_page is "phone" or "calendar" or "tools") Render(); }

    void Render()
    {
        while (_cats.Children.Count > 1) _cats.Children.RemoveAt(1);   // indeks 0 = kotak cari
        bool searching = _query.Trim().Length >= 2;
        foreach (var (key, label, glyph) in Pages)
        {
            var k = key;
            bool on = key == _page && !searching;
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = UI.Icon(glyph, 14, on ? UI.Accent : UI.Dim);
            ic.Width = 20;
            item.Children.Add(ic);
            var tx = UI.T(label, 12.5, on ? UI.Fg : UI.Dim, on);
            tx.Margin = new Thickness(6, 0, 0, 0);
            item.Children.Add(tx);
            var b = UI.Btn(item, () => { _page = k; _search.Clear(); _scroll.ScrollToTop(); Render(); },
                           on ? UI.CardOn : Brushes.Transparent, 10, new Thickness(9, 5, 9, 5));
            b.Margin = new Thickness(0, 0, 8, 1);
            _cats.Children.Add(b);
        }
        _rows.Children.Clear();
        if (searching) { RenderSearch(_query.Trim()); return; }
        if (PageHints.TryGetValue(_page, out var hint))
        {
            var h = UI.T(hint, 11.5, UI.Faint);
            h.TextWrapping = TextWrapping.Wrap; h.Margin = new Thickness(0, 0, 0, 6);
            _rows.Children.Add(h);
        }
        bool moreHdr = false;
        foreach (var (raw0, ctl) in PageRows(_page))
        {
            var raw = raw0;
            if (raw.StartsWith('§')) { _rows.Children.Add(SectionHeader(raw[1..])); continue; }
            bool adv = raw.StartsWith('~'), more = raw.StartsWith('¶');
            if ((adv || more) && !Settings.Cur.Advanced) continue;   // opsi lanjutan tersembunyi kecuali Mode lanjutan menyala
            if (more)
            {
                if (!moreHdr) { moreHdr = true; _rows.Children.Add(MoreHeader()); }
                if (!_moreOpen) continue;
            }
            _rows.Children.Add(Row(adv || more ? raw[1..] : raw, ctl));
        }
        // pertama kali halaman HP dibuka: panduan sambung HP tampil sendiri (tombol "Buka panduan" membukanya lagi kapan saja)
        if (_page == "phone" && !_hpGuideShown) { _hpGuideShown = true; PhoneGuide.Show(false); }
    }

    /// <summary>Hasil pencarian: baris dari semua halaman yang labelnya (atau keterangannya, Indonesia atau Inggris) memuat kata yang dicari, dikelompokkan per halaman. Barisnya tetap kontrol asli.</summary>
    void RenderSearch(string q)
    {
        bool any = false;
        foreach (var (key, label, _) in Pages)
        {
            var hits = new List<(string Raw, UIElement Ctl)>();
            foreach (var (raw, ctl) in PageRows(key))
            {
                if (raw.StartsWith('§') || ctl == null) continue;
                var text = string.Join(" ", raw.TrimStart('~', '¶').Split('\n').Select(p => p + " " + L.T(p)));
                if (text.Contains(q, StringComparison.OrdinalIgnoreCase)) hits.Add((raw, ctl));
            }
            if (hits.Count == 0) continue;
            any = true;
            _rows.Children.Add(SectionHeader(label));
            foreach (var (raw, ctl) in hits) _rows.Children.Add(Row(raw.TrimStart('~', '¶'), ctl));
        }
        if (!any)
        {
            var e = UI.T("Tidak ada pengaturan yang cocok", 12, UI.Dim);
            e.Margin = new Thickness(0, 14, 0, 0);
            _rows.Children.Add(e);
        }
    }

    internal static bool _hpGuideShown;

    static readonly Dictionary<string, string> PageHints = new()
    {
        ["general"] = "Bahasa, tampilan, pembaruan, dan cadangan.",
        ["island"] = "Cara island membuka, bersembunyi, dan bergestur, serta apa yang tampil saat diam.",
        ["music"] = "Lirik, tulisan Latin, karaoke, dan pembacaan posisi YouTube.",
        ["phone"] = "Sambungkan HP, notifikasi, dan kamera. Mulai dari Panduan sambung HP.",
        ["system"] = "Kuota Claude dan Codex serta kinerja PC.",
        ["calendar"] = "Kalender, hari libur, jadwal sholat, dan adzan.",
        ["remind"] = "Minum, istirahat mata, dan Pomodoro.",
        ["tools"] = "Tangkap layar, clipboard, Discord, dan Meet.",
        ["addons"] = "Unduhan tambahan yang hanya diambil bila kamu memintanya.",
    };

    // ---------- pembaruan dan cadangan (Umum) ----------
    string _backupMsg = "";

    UIElement UpdateRow()
    {
        var sp = new StackPanel { MaxWidth = 300, Tag = "wide" };
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var st = UI.T(string.IsNullOrEmpty(UpdateService.Status) ? "ZaxLet " + UpdateService.Current : UpdateService.Status, 11.5, UpdateService.Available != null ? UI.Fg : UI.Dim);
        st.TextWrapping = TextWrapping.Wrap; st.MaxWidth = 170; st.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(st);
        var b = UI.Btn(UI.T(UpdateService.Available != null ? L.F("Perbarui ke {0}", UpdateService.Available.Version) : "Periksa", 11.5, UI.Fg, true),
            () => { if (UpdateService.Available != null) _ = UpdateService.ApplyAsync(); else _ = UpdateService.CheckAsync(); },
            UpdateService.Available != null ? UI.AccentBg : UI.Card, 10, new Thickness(10, 4, 10, 4));
        b.Margin = new Thickness(8, 0, 0, 0);
        if (!UpdateService.Busy) line.Children.Add(b);
        sp.Children.Add(line);
        return sp;
    }

    UIElement BackupRow()
    {
        var sp = new StackPanel { MaxWidth = 300, Tag = "wide" };
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        line.Children.Add(Act("Simpan…", DoBackup, UI.Fg));
        var r = (FrameworkElement)Act("Pulihkan…", DoRestore, UI.Fg); r.Margin = new Thickness(6, 0, 0, 0);
        line.Children.Add(r);
        sp.Children.Add(line);
        if (_backupMsg.Length > 0) { var m = UI.T(_backupMsg, 11, UI.Dim); m.TextWrapping = TextWrapping.Wrap; m.HorizontalAlignment = HorizontalAlignment.Right; m.Margin = new Thickness(0, 4, 0, 0); sp.Children.Add(m); }
        return sp;
    }

    void DoBackup()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "ZaxLet-pengaturan.json", Filter = "JSON|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, SettingsBackup.Export(System.Text.Json.JsonSerializer.Serialize(Settings.Cur)));
            _backupMsg = L.T("Cadangan tersimpan.");
        }
        catch (Exception ex) { App.Log(ex); _backupMsg = L.T("Gagal menyimpan: ") + ex.Message; }
        Render();
    }

    void DoRestore()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = SettingsBackup.Import(System.IO.File.ReadAllText(dlg.FileName), System.Text.Json.JsonSerializer.Serialize(Settings.Cur));
            Settings.Cur = System.Text.Json.JsonSerializer.Deserialize<Settings>(json) ?? Settings.Cur;
            Settings.Apply();
            EyeService.I.Apply();
            _backupMsg = L.T("Pengaturan dipulihkan. Bahasa berlaku setelah ZaxLet dimulai ulang.");
        }
        catch (FormatException) { _backupMsg = L.T("File ini bukan cadangan pengaturan ZaxLet."); }
        catch (Exception ex) { App.Log(ex); _backupMsg = L.T("Gagal memulihkan: ") + ex.Message; }
        Render();
    }

    static string PickAudio()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.m4a;*.wma;*.aac;*.ogg;*.flac|*.*|*.*" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    static UIElement NetRow()
    {
        var n = NetInfo.Current();
        var txt = n == null ? L.T("tidak terdeteksi") : $"{n.Name} · {L.T(n.Kind)}";
        var sp = new StackPanel { MaxWidth = 300, Tag = "wide" };
        var t = UI.T(txt, 11.5, n is { Category: 0 } && !NetInfo.FirewallOpenToPublic() ? UI.Warn : UI.Fg);
        t.TextWrapping = TextWrapping.Wrap; t.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(t);
        if (n is { Category: 0 })
        {
            var h = UI.T(NetInfo.FirewallOpenToPublic()
                ? "Jaringan Publik: aturan firewall ZaxLet HP tetap mengizinkan. Wi-Fi tamu sering memblokir antar perangkat; pakai Tailscale bila HP tidak bisa masuk."
                : "Jaringan Publik dan aturan firewall ZaxLet HP belum mencakupnya: HP tidak bisa masuk. Nyalakan ulang server HP atau jadikan jaringan Private.", 11, UI.Dim);
            h.TextWrapping = TextWrapping.Wrap; h.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(h);
        }
        return sp;
    }

    // ---------- isi tiap kategori ----------
    // Konvensi baris: label "§Judul" = subjudul bagian; label diawali "~" = opsi lanjutan (hanya tampil bila Mode lanjutan menyala);
    // "Label\nKeterangan" = label dengan keterangan kecil di bawahnya.
    IEnumerable<(string, UIElement)> PageRows(string page)
    {
        var s = Settings.Cur;
        switch (page)
        {
            case "general":
                yield return ("Gaya notifikasi\nTenang: hanya yang penting. Seimbang: popup umum. Lengkap: semua popup dan widget. Sakelar satu per satu ada di halaman Island dan Fokus", Choice(new[] { ("tenang", "Tenang"), ("seimbang", "Seimbang"), ("lengkap", "Lengkap"), ("custom", "Kustom") }, () => Presets.Detect(s), v => { if (v != "custom") Presets.Apply(s, v); }));
                yield return ("Mode lanjutan\nTampilkan opsi tambahan di semua halaman", Toggle(() => s.Advanced, v => s.Advanced = v, true, Render));
                yield return ("§Bahasa dan tampilan", null);
                yield return ("Bahasa / Language", Choice(new[] { ("auto", "Otomatis (ikut Windows)"), ("id", "Indonesia"), ("en", "English") }, () => s.Lang, v => s.Lang = v, Render));
                if (s.Lang != L.Pref) yield return ("Berlaku setelah ZaxLet dimulai ulang", Act("Mulai ulang sekarang", App.Restart, UI.Warn));
                yield return ("Warna aksen", Choice(UI.Accents.Select(a => (a.Key, a.Label)).ToArray(), () => s.Accent, v => s.Accent = v));
                yield return ("Format jam", Choice(new[] { ("dot", "17.52"), ("colon", "17:52"), ("12h", "5:52 PM") }, () => s.ClockStyle, v => s.ClockStyle = v));
                yield return ("~Tampilkan detik", Toggle(() => s.ClockSeconds, v => s.ClockSeconds = v));
                yield return ("§Windows", null);
                yield return ("Mulai bersama Windows", Toggle(App.IsAutostart, v => App.SetAutostart(v), false));
                yield return ("§Pembaruan dan cadangan", null);
                yield return ("Pembaruan", UpdateRow());
                yield return ("Periksa pembaruan otomatis", Toggle(() => s.AutoUpdate, v => s.AutoUpdate = v));
                yield return ("Cadangan pengaturan\nSimpan ke satu berkas atau pulihkan; token tidak ikut", BackupRow());
                yield return ("§Bantuan", null);
                yield return ("Tur ZaxLet\nPengenalan singkat island", Act("Tonton lagi", () => TourWindow.ShowIt(true), UI.Fg));
                yield return ("Laporkan masalah atau usul fitur\nMembuka halaman Issues di GitHub", Act("Buka", () =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/Zaxel98/ZaxLet/issues/new") { UseShellExecute = true }); }
                    catch (Exception ex) { App.Log(ex); }
                }, UI.Fg));
                yield return ("§Lainnya", null);
                yield return ("~Kembalikan semua pengaturan", Act(_confirmReset ? "Klik lagi untuk yakin" : "Reset", () =>
                {
                    if (!_confirmReset) { _confirmReset = true; Render(); return; }
                    _confirmReset = false;
                    Settings.Reset();
                    EyeService.I.Apply();
                    Render();
                }, UI.Warn));
                yield return ("Keluar dari ZaxLet", Act("Keluar", () => Application.Current.Shutdown(), UI.Bad));
                break;

            case "island":
                var pos = new[] { ("left", "Kiri"), ("center", "Tengah"), ("right", "Kanan") };
                yield return ("§Tampilan island", null);
                yield return ("Tema\nHitam bawaan, atau warna lain; Neon: garis tepi mengikuti warna sampul lagu", Choice(new[] { ("black", "Hitam"), ("aurora", "Aurora"), ("matcha", "Matcha"), ("senja", "Senja"), ("neon", "Neon") }, () => s.Theme, v => s.Theme = v));
                yield return ("Bentuk island\nNotch: bahu cekung di kiri dan kanan seperti takik layar. Dynamic: kapsul bulat", Choice(new[] { ("notch", "Notch"), ("pill", "Dynamic") }, () => s.IslandShape, v => s.IslandShape = v));
                yield return ("Info sesuai waktu di pill\nPagi: cuaca dan sholat berikutnya. Siang: tugas hari ini. Sore: sholat dan sisa minum. Malam: jadwal besok. Hanya tampil bila ada isinya dan tidak ada yang lebih penting", Toggle(() => s.IdleContext, v => s.IdleContext = v));
                yield return ("Kilat tepi saat lagu ganti\nTepi pill menyala sekali dengan warna sampul. Hanya di island, tidak di layar penuh", Toggle(() => s.TrackFlash, v => s.TrackFlash = v));
                yield return ("Ukuran island", Choice(new[] { ("0", "Otomatis"), ("75", "75%"), ("90", "90%"), ("100", "100%"), ("115", "115%"), ("130", "130%") }, () => s.IslandScale.ToString(), v => s.IslandScale = int.Parse(v)));
                yield return ("Kapan island tampil\nSelalu; hanya saat ada aktivitas (kursor ke tepi atas untuk mengintip); atau tersembunyi sampai dibuka dari ikon tray", Choice(new[] { ("always", "Selalu"), ("activity", "Saat ada aktivitas"), ("hidden", "Disembunyikan") }, () => s.ShowMode, v => s.ShowMode = v));
                yield return ("Hemat baterai otomatis\nSaat memakai baterai dan sisanya di bawah angka ini, pembaruan cepat dikurangi (karaoke lirik, tab Monitor). 0 = mati", Num(0, 50, 5, "%", () => s.SaverPct, v => s.SaverPct = v));
                yield return ("Musik tetap tampil di layar penuh\nLagu dan liriknya muncul di atas game borderless; island tidak bisa diklik supaya permainan tidak terganggu", Toggle(() => s.FsMusic, v => s.FsMusic = v));
                var screens = System.Windows.Forms.Screen.AllScreens;
                if (screens.Length > 1)
                    yield return ("Monitor", Choice(screens.Select((sc, i) => (i.ToString(), i == 0 ? "Monitor utama" : $"Monitor {i + 1}")).ToArray(), () => s.MonitorIdx.ToString(), v => s.MonitorIdx = int.Parse(v)));
                yield return ("§Cara membuka", null);
                yield return ("Cara membuka pill", Choice(new[] { ("hover", "Kursor di atas pill"), ("click", "Klik kiri") }, () => s.HoverOpen ? "hover" : "click", v => s.HoverOpen = v == "hover"));
                yield return ("Pintasan buka island\nMis. ctrl+alt+space; tekan lagi untuk menutup. Kosongkan untuk mematikan", Txt(() => s.OpenKey, v => s.OpenKey = v.Trim().ToLowerInvariant(), 150));
                yield return ("Pintasan Aksi cepat\nKolom ketik perintah singkat: t 10m, fokus 25, catat beli susu, w link, minum 250, tema neon. Mis. ctrl+alt+k; kosongkan untuk mematikan", Txt(() => s.QuickKey, v => s.QuickKey = v.Trim().ToLowerInvariant(), 150));
                yield return ("~Jeda sebelum menutup", Dur(() => s.CloseDelayMs / 1000.0, v => s.CloseDelayMs = (int)(v * 1000), 0.05, 600));
                yield return ("§Menyembunyikan island", null);
                yield return ("Sembunyikan saat fullscreen", Toggle(() => s.AutoHideFullscreen, v => s.AutoHideFullscreen = v));
                yield return ("Klik kanan pill = sembunyikan\nKursor ke tepi atas untuk mengintip, klik kanan lagi untuk menampilkan terus", Toggle(() => s.RightClickHide, v => s.RightClickHide = v));
                yield return ("Tahan Alt di atas pill = hilang sementara", Toggle(() => s.AltGhost, v => s.AltGhost = v));
                yield return ("Pintasan sembunyikan pill\nMenu tray juga bisa menyembunyikan island", Txt(() => s.HideKey, v => s.HideKey = v.Trim().ToLowerInvariant(), 150));
                yield return ("~Otomatis tampil lagi setelah\n0 = manual", Num(0, 240, 5, " mnt", () => s.SnoozeMin, v => s.SnoozeMin = v));
                yield return ("Menyingkir otomatis bila menutupi tombol aplikasi lain", Toggle(() => s.YieldOn, v => s.YieldOn = v));
                yield return ("~Pil jam kecil saat menyingkir\nBukan hilang total: tetap tampil sebagai jam kecil (atau jam dan tanggal)", Choice(new[] { ("off", "Mati"), ("clock", "Jam"), ("date", "Jam dan tanggal") }, () => s.MiniClock, v => s.MiniClock = v));
                yield return ("§Gestur dan popup", null);
                yield return ("Gestur island\nScroll di atas island = volume, klik dua kali = putar/jeda, sapuan dua jari = pindah tab", Toggle(() => s.Gestures, v => s.Gestures = v));
                yield return ("Popup OSD (volume, kecerahan, Caps Lock, Bluetooth, mikrofon)", Toggle(() => s.OsdOn, v => s.OsdOn = v));
                yield return ("Tampilkan aplikasi yang memakai mikrofon/kamera", Toggle(() => s.PrivacyOn, v => s.PrivacyOn = v));
                yield return ("Notifikasi unduhan selesai", Toggle(() => s.DownloadAlert, v => s.DownloadAlert = v));
                yield return ("§Isi pill saat diam", null);
                yield return ("Baterai", Toggle(() => s.IdleBattery, v => s.IdleBattery = v));
                yield return ("~Angka persen baterai", Toggle(() => s.IdleBatteryPct, v => s.IdleBatteryPct = v));
                yield return ("Usage AI", Toggle(() => s.IdleUsage, v => s.IdleUsage = v));
                yield return ("~Gaya usage AI", Choice(new[] { ("ringpct", "Cincin + %"), ("ring", "Cincin"), ("pct", "Persen"), ("bar", "Bar + %") }, () => s.IdleUsageStyle, v => s.IdleUsageStyle = v));
                yield return ("~Penyedia usage", Choice(new[] { ("both", "Claude + Codex"), ("claude", "Claude"), ("codex", "Codex") }, () => s.IdleUsageProviders, v => s.IdleUsageProviders = v));
                yield return ("~Jendela kuota", Choice(new[] { ("5h", "5 jam"), ("weekly", "Mingguan") }, () => s.IdleUsageWindow, v => s.IdleUsageWindow = v));
                yield return ("Kecepatan internet", Toggle(() => s.IdleSpeed, v => s.IdleSpeed = v));
                yield return ("~Isi kecepatan", Choice(new[] { ("down", "↓ unduh"), ("both", "↓ ↑ keduanya") }, () => s.IdleSpeedStyle, v => s.IdleSpeedStyle = v));
                yield return ("Waveform saat musik main", Toggle(() => s.IdleWave, v => s.IdleWave = v));
                yield return ("Lirik di baris ke-2", Toggle(() => s.IdleLyrics, v => s.IdleLyrics = v));
                yield return ("Titik status HP\nMenyala saat HP tersambung", Toggle(() => s.IdlePhoneDot, v => s.IdlePhoneDot = v));
                yield return ("~Posisi jam", Choice(pos, () => s.PosClock, v => s.PosClock = v));
                yield return ("~Posisi baterai", Choice(pos, () => s.PosBattery, v => s.PosBattery = v));
                yield return ("~Posisi usage AI", Choice(pos, () => s.PosUsage, v => s.PosUsage = v));
                yield return ("~Posisi kecepatan", Choice(pos, () => s.PosSpeed, v => s.PosSpeed = v));
                yield return ("~Posisi waveform", Choice(pos, () => s.PosWave, v => s.PosWave = v));
                yield return ("~Posisi titik status HP", Choice(pos, () => s.PosPhone, v => s.PosPhone = v));
                yield return ("§Tab", null);
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
                    var up = UI.Btn(UI.Icon("", 11, UI.Dim), () => Move(-1), UI.Card, 8, new Thickness(8, 4, 8, 4));
                    var dn = UI.Btn(UI.Icon("", 11, UI.Dim), () => Move(1), UI.Card, 8, new Thickness(8, 4, 8, 4));
                    up.Margin = new Thickness(0, 0, 4, 0); dn.Margin = new Thickness(0, 0, 10, 0);
                    ctl.Children.Add(up); ctl.Children.Add(dn);
                    ctl.Children.Add(Toggle(() => !s.HiddenTabs.Contains(k), v => { s.HiddenTabs.Remove(k); if (!v) s.HiddenTabs.Add(k); }));
                    yield return (IslandWindow.TabMeta[k].Label, ctl);
                }
                // "¶" = opsi lanjutan yang jarang disentuh: dilipat di bawah satu kepala "Lainnya"
                yield return ("¶Kelengkungan sudut\n0 = otomatis; makin besar makin bulat", Num(0, 40, 4, " px", () => s.PillRadius, v => s.PillRadius = v == 0 || v >= 8 ? v : 8));
                yield return ("¶Lebar tambahan saat idle\nMelebarkan pill saat tertutup supaya lebih lega", Num(0, 160, 10, " px", () => s.IdleExtra, v => s.IdleExtra = v));
                yield return ("¶Kehalusan animasi\nOtomatis: animasi latar 30 fps, buka/tutup/ganti tab 60 fps. Penuh: semua 60 fps. Berlaku setelah ZaxLet dimulai ulang", Choice(new[] { ("auto", "Otomatis"), ("full", "Penuh (60 fps)") }, () => s.AnimFps, v => s.AnimFps = v));
                break;

            case "music":
                yield return ("§Sumber musik", null);
                yield return ("Abaikan musik dari aplikasi\nIsi nama aplikasi dipisah koma, mis. discord, zoom; island tidak akan menampilkannya", Txt(() => s.MediaIgnore, v => s.MediaIgnore = v.Trim(), 190));
                yield return ("Rekap lagu dan artis teratas\nMencatat lagu yang kamu dengarkan sedikitnya 30 detik, hanya di PC ini (playlog.tsv). Dilihat lewat tombol Rekap di tab Musik", Toggle(() => s.PlayLogOn, v => s.PlayLogOn = v));
                yield return ("~Hapus catatan rekap", Act("Hapus", () => { PlayLog.Clear(); }, UI.Bad));
                yield return ("§Lirik", null);
                yield return ("Tampilkan lirik (tab dan idle)", Toggle(() => s.MusicLyrics, v => s.MusicLyrics = v));
                yield return ("Lirik karaoke (isi per kata)", Toggle(() => s.LyricKaraoke, v => s.LyricKaraoke = v));
                yield return ("Karaoke mengikuti vokal\nMembaca suara PC secara lokal hanya saat tab Musik terbuka; tidak direkam atau dikirim", Toggle(() => s.LyricFlow, v => s.LyricFlow = v));
                yield return ("~Tempo isi lirik", Choice(new[] { ("fast", "Cepat"), ("normal", "Normal"), ("slow", "Lambat") }, () => s.LyricPace, v => s.LyricPace = v));
                yield return ("~Offset sinkron bawaan\nDipakai lagu yang belum punya geseran sendiri (atur di tab Musik)", Num(-5000, 5000, 100, " ms", () => s.LyricOffsetMs, v => s.LyricOffsetMs = v));
                yield return ("Warna latar dari sampul", Toggle(() => s.TintCover, v => s.TintCover = v));
                yield return ("§Tulisan Latin untuk lirik berhuruf lain", null);
                yield return ("Tulisan Latin\nRomaji, pinyin, Hangul, Kiril, Yunani, Arab", Choice(new[] { ("off", "Mati"), ("below", "Di bawah lirik"), ("only", "Ganti lirik asli") },
                    () => !s.LyricRomaji ? "off" : s.RomajiOnly ? "only" : "below", v => { s.LyricRomaji = v != "off"; s.RomajiOnly = v == "only"; }));
                yield return ("Lirik di pill idle juga pakai tulisan Latin", Toggle(() => s.IdleRomaji, v => s.IdleRomaji = v));
                yield return ("Paket tulisan Latin", AddonControl(Addons.All[0]));
                yield return ("§YouTube", null);
                yield return ("Baca posisi YouTube dari halaman\nLewat aksesibilitas Windows, hanya saat tab videonya aktif; tidak ada yang dikirim", Toggle(() => s.YtProgress, v => s.YtProgress = v));
                yield return ("Baca daftar lagu video YouTube panjang\nVideo 8 menit ke atas: judul dan kanal dikirim ke youtube.com untuk mencari daftar lagunya", Toggle(() => s.YtChapters, v => s.YtChapters = v));
                yield return ("~Token pengenal lagu (AudD)\nUntuk tombol mikrofon di tab Musik: 12 detik suara PC dikirim ke api.audd.io hanya saat ditekan", Txt(() => s.SongIdToken, v => s.SongIdToken = v.Trim(), 200));
                break;

            case "phone":
                var st = PhoneGuide.State();
                yield return ("§Sambungan", null);
                yield return ("Panduan sambung HP\n" + L.T("Memasang aplikasi, memasangkan, sampai notifikasi masuk") + ": " + st.Text, Act("Buka panduan", () => PhoneGuide.Show(true), UI.Fg));
                yield return ("Server sambungan HP\nHarus menyala agar HP bisa tersambung", Toggle(() => ShareServer.I.On, v => { if (v) VirtualCam.EnsureFirewall(() => { ShareServer.I.Start(); Render(); }); else ShareServer.I.Stop(); }, false, Render));
                yield return ("Aktif otomatis saat ZaxLet mulai", Toggle(() => s.ShareAuto, v => s.ShareAuto = v));
                if (ShareServer.I.On) yield return ("Perangkat", PhoneDevices());
                yield return ("§Notifikasi dari HP", null);
                yield return ("Tampilkan notifikasi HP di pill", Toggle(() => s.NotifOn, v => s.NotifOn = v));
                yield return ("Notifikasi berjalan (Gojek, dll)", Toggle(() => s.NotifOngoing, v => s.NotifOngoing = v));
                yield return ("Salin kode OTP\nTombol Salin muncul di notifikasi berisi kode; kodenya tidak masuk riwayat clipboard", Toggle(() => s.NotifOtp, v => s.NotifOtp = v));
                yield return ("Aplikasi baru langsung tampil", Toggle(() => s.NotifNewDefault, v => s.NotifNewDefault = v));
                yield return ("~Lama tampil", Dur(() => s.NotifShowSec, v => s.NotifShowSec = v, 2, 120));
                yield return ("~Muncul walau fullscreen / disembunyikan", Toggle(() => s.NotifFullscreen, v => s.NotifFullscreen = v));
                yield return ("Uji notifikasi", Act("Tampilkan contoh", () => PhoneNotifs.Test(), UI.Fg));
                string[][] known;
                lock (PhoneNotifs.Apps) known = PhoneNotifs.Apps.OrderBy(a => a.Value).Select(a => new[] { a.Key, a.Value }).ToArray();
                if (known.Length == 0) yield return ("Aplikasi yang tampil\nBelum ada notifikasi masuk. Di aplikasi ZaxLet HP: izinkan akses notifikasi.", new Border());
                else yield return ("§Aplikasi yang tampil", null);
                foreach (var kv in known)
                {
                    var pkg = kv[0];
                    yield return (kv[1], Toggle(() => s.NotifApps.TryGetValue(pkg, out var on) ? on : s.NotifNewDefault, v => s.NotifApps[pkg] = v));
                }
                yield return ("§Kamera HP", null);
                yield return ("Kamera HP sebagai webcam\nJendela kamera dengan tombol cermin dan putar", Act("Buka jendela kamera", () => CameraWindow.ShowIt(), UI.Fg));
                yield return ("Kamera yang dipakai", Choice(new[] { ("back", "Belakang"), ("front", "Depan") }, () => s.CamFront ? "front" : "back", v => s.CamFront = v == "front"));
                yield return ("Kualitas kamera", Choice(new[] { ("480", "480p"), ("720", "720p"), ("1080", "1080p") }, () => s.CamRes.ToString(), v => s.CamRes = int.Parse(v)));
                yield return ("~FPS kamera", Choice(new[] { ("10", "10"), ("15", "15"), ("24", "24"), ("30", "30") }, () => s.CamFps.ToString(), v => s.CamFps = int.Parse(v)));
                yield return ("Kamera virtual (semua aplikasi)\n" + L.T(!VirtualCam.Available ? "softcam.dll tidak ada" : !VirtualCam.Registered ? "Didaftarkan otomatis saat kamera pertama dinyalakan" : VirtualCam.Running ? "Aktif: pilih \"DirectShow Softcam\" di Discord/Zoom" : "Terdaftar, belum aktif"),
                    Toggle(() => VirtualCam.Running, v => { if (v) VirtualCam.Start(); else VirtualCam.Stop(); s.VCamOn = VirtualCam.Running; }, true, Render));
                yield return ("~Rasio kamera virtual", Choice(new[] { ("1:1", "1:1"), ("4:3", "4:3"), ("9:16", "9:16"), ("16:9", "16:9") }, () => s.VCamRatio, v => s.VCamRatio = v));
                yield return ("~Landscape otomatis saat HP dimiringkan", Toggle(() => s.VCamAuto, v => s.VCamAuto = v));
                yield return ("~Bingkai kamera virtual", Choice(new[] { ("crop", "Isi penuh (potong)"), ("fit", "Utuh (bar hitam)") }, () => s.VCamFit, v => s.VCamFit = v));
                if (ShareServer.I.On) { yield return ("§Jaringan dan keamanan", null); yield return ("Jaringan sekarang", NetRow()); }
                yield return ("~Ganti token (putuskan semua HP)", Act("Ganti", () => { ShareServer.I.ResetToken(); Render(); }, UI.Warn));
                break;

            case "system":
                yield return ("§Usage AI", null);
                yield return ("Claude\nTanpa login terminal, angkanya dari catatan aplikasi Claude Desktop", Toggle(() => s.ClaudeOn, v => s.ClaudeOn = v));
                yield return ("Codex", Toggle(() => s.CodexOn, v => s.CodexOn = v));
                yield return ("Tampilkan perkiraan biaya", Toggle(() => s.ShowCost, v => s.ShowCost = v));
                yield return ("~Interval baca kuota", Dur(() => s.UsageRefreshSec, v => s.UsageRefreshSec = v, 1, 86400));
                yield return ("~Kolom Code (data Ponytail)", Toggle(() => s.ShowPonytail, v => s.ShowPonytail = v));
                yield return ("~Peringatan kuning di", Num(30, 95, 5, "%", () => s.WarnAt, v => s.WarnAt = v));
                yield return ("~Peringatan merah di", Num(50, 100, 5, "%", () => s.CritAt, v => s.CritAt = v));
                yield return ("§Monitor", null);
                yield return ("Tampilkan GPU", Toggle(() => s.MonGpu, v => s.MonGpu = v));
                yield return ("Bar per core CPU", Toggle(() => s.MonCores, v => s.MonCores = v));
                yield return ("Daftar proses teratas", Toggle(() => s.MonTop, v => s.MonTop = v));
                yield return ("§Overlay performa saat main", null);
                yield return ("Kapan tampil\nGame: otomatis saat aplikasi layar penuh atau borderless di depan. Selalu: terus tampil. Ketik \"perf\" di Aksi cepat untuk menyalakan atau mematikan sesaat. Mati = tidak membaca sensor sama sekali", Choice(new[] { ("off", "Mati"), ("game", "Game"), ("on", "Selalu") }, () => s.PerfShow, v => s.PerfShow = v));
                yield return ("Tampilkan juga di tab Monitor island\nMenggantikan daftar proses teratas dengan metrik yang dipilih di bawah, satu baris per metrik. Ukuran island tidak berubah", Toggle(() => s.PerfInIsland, v => s.PerfInIsland = v));
                yield return ("Pojok layar", Choice(new[] { ("tl", "Kiri atas"), ("tr", "Kanan atas"), ("bl", "Kiri bawah"), ("br", "Kanan bawah") }, () => s.PerfCorner, v => s.PerfCorner = v));
                yield return ("Kepekatan latar overlay\n0% = hanya teks tanpa kotak. Overlay selalu tembus klik", Num(0, 100, 10, "%", () => s.PerfBgAlpha, v => s.PerfBgAlpha = v));
                foreach (var m in PerfFormat.Metrics)
                {
                    var key = m.Key;
                    string cap = key switch
                    {
                        "fps" => "FPS dibaca dari peristiwa Present DirectX (cara PresentMon), tanpa menyuntik game. Windows mewajibkan izin: lihat dua baris di bawah daftar ini",
                        "cpuvolt" or "gpuvolt" => "Dibaca dari LibreHardwareMonitor (jalankan sebagai admin, nyalakan Options > Remote Web Server). Tanpa itu tampil \"butuh LHM\"; ZaxLet sengaja tidak membawa driver sensor",
                        "cpupwr" => "Dari sensor energi (RAPL) Windows; tidak ada di semua PC",
                        _ => null,
                    };
                    yield return (m.Label + (cap != null ? "\n" + cap : ""), Toggle(() => PerfFormat.Items(s.PerfItems).Contains(key), v => s.PerfItems = PerfFormat.WithItem(s.PerfItems, key, v)));
                }
                yield return ("FPS: izinkan sekali (UAC)\nProses pembantu admin kecil membaca peristiwa Present; berlaku sampai ZaxLet ditutup. Pilih metrik FPS dulu", Act(FpsSource.Available ? "Aktif" : "Izinkan", () => { FpsSource.Ask(); Render(); }, UI.Fg));
                yield return ("~FPS: izin permanen tanpa UAC\nSalin perintah, jalankan di CMD sebagai administrator, lalu sign out dan masuk lagi ke Windows sekali", Act("Salin perintah", () => { try { Clipboard.SetText(FpsSource.GroupCommand); } catch { } }, UI.Fg));
                yield return ("§Bluetooth", null);
                yield return ("Baterai perangkat Bluetooth\nTampil saat tersambung dan peringatan saat rendah, untuk perangkat yang melaporkan baterai ke Windows (headset Hands-Free). Ketik \"bt\" di Aksi cepat untuk melihat sekarang", Toggle(() => s.BtBattery, v => s.BtBattery = v));
                yield return ("~Peringatkan di bawah", Num(5, 50, 5, "%", () => s.BtLowPct, v => s.BtLowPct = v));
                break;

            case "calendar":
                yield return ("§Kalender", null);
                yield return ("Minggu dimulai", Choice(new[] { ("mon", "Senin"), ("sun", "Minggu") }, () => s.WeekMonday ? "mon" : "sun", v => s.WeekMonday = v == "mon"));
                yield return ("Tampilkan hari libur", Toggle(() => s.ShowHolidays, v => s.ShowHolidays = v));
                yield return ("§Cuaca", null);
                yield return ("Tampilkan cuaca\nSuhu di tab Hari ini. Mengirim koordinat lokasimu ke open-meteo.com; mati bawaannya", Toggle(() => s.WeatherOn, v => s.WeatherOn = v));
                yield return ("Peringatan hujan\nBila peluang hujan 2 jam ke depan 60% ke atas", Toggle(() => s.RainAlert, v => s.RainAlert = v));
                yield return ("§Ibadah", null);
                yield return ("Widget ibadah\nSholat: jadwal dan adzan. Lainnya: pengingat dari jadwal mingguanmu sendiri", Choice(new[] { ("sholat", "Sholat"), ("other", "Ibadah lain"), ("off", "Mati") }, () => s.WorshipMode, v => s.WorshipMode = v));
                if (s.WorshipMode == "other")
                    yield return ("Jadwal ibadah lain\nFormat: hari jam nama; pisahkan dengan titik koma. Contoh: min 08:00 Kebaktian; jum,sab 18.30 Latihan koor; tiap 21:00 Doa", Txt(() => s.WorshipText, v => s.WorshipText = v.Trim(), 230));
                yield return ("§Sholat", null);
                yield return ("Sumber lokasi", Choice(new[] { ("auto", "Otomatis (IP)"), ("device", "Perangkat (izin)"), ("manual", "Cari kota") }, () => s.LocMode, v => s.LocMode = v,
                    () => { if (s.LocMode != "manual") _ = LocationService.RefreshAsync(true).ContinueWith(_ => Dispatcher.BeginInvoke(Render)); }));
                yield return ("Kota\n" + (string.IsNullOrEmpty(LocationService.Status) ? "" : LocationService.Status), s.LocMode == "manual"
                    ? Txt(() => s.City, v => { if (!string.IsNullOrWhiteSpace(v) && v.Trim() != s.City) _ = LocationService.SearchAsync(v.Trim()).ContinueWith(_ => Dispatcher.BeginInvoke(Render)); })
                    : UI.T(s.City, 12.5, UI.Fg, true));
                if (s.LocMode != "manual")
                    yield return ("~Deteksi ulang", Act("Deteksi sekarang", () => { _ = LocationService.RefreshAsync(true).ContinueWith(_ => Dispatcher.BeginInvoke(Render)); }, UI.Fg));
                yield return ("Metode hitung", Choice(PrayerAlerts.Methods.Select(m => (m.Key, m.Label)).ToArray(), () => s.PrayerMethod, v => s.PrayerMethod = v));
                yield return ("Mazhab Asar", Choice(new[] { ("shafi", "Syafi'i"), ("hanafi", "Hanafi") }, () => s.AsrHanafi ? "hanafi" : "shafi", v => s.AsrHanafi = v == "hanafi"));
                yield return ("~Ihtiyat", Num(0, 5, 1, " mnt", () => s.Ihtiyat, v => s.Ihtiyat = v));
                yield return ("Notifikasi waktu sholat", Toggle(() => s.PrayerAlert, v => s.PrayerAlert = v));
                yield return ("Jeda musik saat sholat", Toggle(() => s.PrayerPauseMusic, v => s.PrayerPauseMusic = v));
                yield return ("Tawarkan melanjutkan musik\nBeberapa menit setelah dijeda, pill menawarkan memutar lagi", Toggle(() => s.PrayerResumeAsk, v => s.PrayerResumeAsk = v));
                yield return ("Imsak dan buka puasa\nPengingat 10 menit sebelum Subuh dan tanda saat Maghrib", Toggle(() => s.ImsakOn, v => s.ImsakOn = v));
                yield return ("Putar adzan\nPilih berkas suaramu sendiri; klik pill untuk menghentikannya", Toggle(() => s.AdhanOn, v => s.AdhanOn = v));
                yield return ("Berkas adzan (mp3, wav, …)", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        Txt(() => s.AdhanFile, v => s.AdhanFile = v.Trim().Trim('"'), 150),
                        new Border { Width = 6 },
                        Act("Pilih…", () => { var f = PickAudio(); if (f != null) { s.AdhanFile = f; Settings.Apply(); Render(); } }, UI.Fg),
                        new Border { Width = 6 },
                        Act(AdhanPlayer.Playing ? "Hentikan" : "Uji", () => { if (AdhanPlayer.Playing) AdhanPlayer.Stop(); else AdhanPlayer.Play(s.AdhanFile); Render(); }, UI.Fg),
                    }
                });
                break;

            case "remind":
                yield return ("§Minum", null);
                yield return ("Pengingat minum", Toggle(() => s.WaterOn, v => s.WaterOn = v));
                yield return ("Target harian", Txt(() => s.WaterGoalMl.ToString() + " ml", v => { if (int.TryParse(new string(v.Where(char.IsDigit).ToArray()), out var n) && n is >= 100 and <= 20000) s.WaterGoalMl = n; }, 100));
                yield return ("Ukuran satu gelas", Txt(() => s.WaterCupMl.ToString() + " ml", v => { if (int.TryParse(new string(v.Where(char.IsDigit).ToArray()), out var n) && n is >= 20 and <= 3000) s.WaterCupMl = n; }, 100));
                yield return ("Hanya bila tertinggal jadwal", Toggle(() => s.WaterSmart, v => s.WaterSmart = v));
                yield return ("~Jam aktif mulai", Txt(() => s.WaterFrom, v => { if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1)) s.WaterFrom = t.ToString(@"hh\:mm"); }, 100));
                yield return ("~Jam aktif selesai", Txt(() => s.WaterTo, v => { if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1)) s.WaterTo = t.ToString(@"hh\:mm"); }, 100));
                yield return ("~Jarak antar pengingat", Dur(() => s.WaterEverySec, v => s.WaterEverySec = v, 10, 86400, null, 60));
                yield return ("~Lama pengingat tampil", Dur(() => s.WaterShowSec, v => s.WaterShowSec = v, 2, 600));
                yield return ("Uji pengingat", Act("Tampilkan sekarang", () => IslandWindow.Instance?.Alert(L.F("Waktunya minum · {0}/{1} ml", HydrationService.I.Today, HydrationService.I.Goal), s.WaterShowSec), UI.Fg));
                yield return ("§Istirahat mata", null);
                yield return ("Pengingat istirahat mata", Toggle(() => s.EyeOn, v => { s.EyeOn = v; }, true, () => EyeService.I.Apply()));
                yield return ("Interval istirahat mata", Dur(() => s.EyeMinutes * 60, v => s.EyeMinutes = v / 60, 10, 86400, () => EyeService.I.Apply(), 60));
                yield return ("§Pomodoro", null);
                yield return ("Pomodoro: fokus", Dur(() => s.PomoWork * 60, v => s.PomoWork = v / 60, 10, 86400, null, 60));
                yield return ("Pomodoro: istirahat", Dur(() => s.PomoBreak * 60, v => s.PomoBreak = v / 60, 10, 86400, null, 60));
                yield return ("§Mode Fokus", null);
                yield return ("Lama Mode Fokus\nNotifikasi HP ditahan; kode OTP tetap lewat. Tombol ada di tab Timer", Num(5, 240, 5, " mnt", () => s.FocusMin, v => s.FocusMin = v));
                yield return ("Fase kerja Pomodoro = Mode Fokus", Toggle(() => s.FocusPomo, v => s.FocusPomo = v));
                break;

            case "tools":
                yield return ("§Tangkap layar", null);
                yield return ("Folder tangkapan (kosong = Pictures\\ZaxLet)", Txt(() => s.CapDir, v => s.CapDir = v.Trim(), 150));
                yield return ("Salin tangkapan ke clipboard", Toggle(() => s.CapClipboard, v => s.CapClipboard = v));
                yield return ("Pintasan tangkap area\nMis. ctrl+shift+s; kosongkan untuk mematikan", Txt(() => s.CapKey, v => s.CapKey = v.Trim().ToLowerInvariant(), 150));
                yield return ("~FPS rekam layar", Choice(new[] { ("15", "15"), ("30", "30"), ("60", "60") }, () => s.RecFps.ToString(), v => s.RecFps = int.Parse(v)));
                yield return ("§Clipboard", null);
                yield return ("Riwayat clipboard (hanya di memori)", Toggle(() => s.ClipboardOn, v => s.ClipboardOn = v));
                yield return ("§Wishlist otomatis (ekstensi browser)", null);
                yield return ("Panduan pemasangan\n" + (WishBridge.Connected ? L.T("Ekstensi tersambung") : L.T("Belum tersambung: Wishlist tetap bisa diisi manual")), Act("Buka panduan", () => WishGuide.Show(true), UI.Fg));
                yield return ("Terima info produk dari ekstensi\nHanya dari browsermu ke ZaxLet di PC ini (127.0.0.1); tidak ada yang dikirim ke internet", Toggle(() => s.WishBridgeOn, v => s.WishBridgeOn = v));
                yield return ("Sarankan simpan ke Wishlist\nSaat link produk yang baru dikenali disalin, pill menawarkannya sekali", Toggle(() => s.WishSuggest, v => s.WishSuggest = v));
                yield return ("~Token ekstensi", Act("Salin", () => { try { Clipboard.SetText(WishBridge.Token); } catch { } }, UI.Fg));
                yield return ("~Webhook lokal\nSkrip atau alat di PC ini bisa menampilkan pesan di pill; salin contoh perintahnya", Act("Salin contoh", () =>
                {
                    try { Clipboard.SetText("curl -X POST http://127.0.0.1:" + WishBridge.Port + "/notify -H \"X-ZaxLet-Token: " + WishBridge.Token + "\" -d \"{\\\"text\\\":\\\"Build selesai\\\",\\\"seconds\\\":6}\""); } catch { }
                }, UI.Fg));
                yield return ("§Mode rapat", null);
                yield return ("Deteksi rapat\nZoom, Meet, Teams, Webex, Skype, Slack, dan telepon WhatsApp dikenali dari pemakaian mikrofon", Toggle(() => s.MeetOn, v => s.MeetOn = v));
                yield return ("Tombol mikrofon di pill", Toggle(() => s.MeetMic, v => s.MeetMic = v));
                yield return ("Jeda musik selama rapat", Toggle(() => s.MeetPause, v => s.MeetPause = v));
                yield return ("Tahan notifikasi HP selama rapat", Toggle(() => s.MeetHold, v => s.MeetHold = v));
                yield return ("§Discord dan Meet", null);
                yield return ("Panduan Discord\n" + (DiscordRpc.I.Connected ? L.T("Tersambung") + (DiscordRpc.I.InVoice ? " · voice: " + DiscordRpc.I.Channel : "") : DiscordRpc.Configured ? DiscordRpc.I.Status : L.T("Cara cepat siap; cara lengkap belum disambungkan")), Act("Buka panduan", () => DiscordGuide.Show(true), UI.Fg));
                yield return ("~Pintasan Discord: mic\nFormat: ctrl+shift+m, ctrl+d, f9", Txt(() => s.AppKeys.GetValueOrDefault("discord.mic", ""), v => s.AppKeys["discord.mic"] = v.Trim().ToLowerInvariant(), 150));
                yield return ("~Pintasan Discord: deafen", Txt(() => s.AppKeys.GetValueOrDefault("discord.deaf", ""), v => s.AppKeys["discord.deaf"] = v.Trim().ToLowerInvariant(), 150));
                yield return ("~Pintasan Meet: mic", Txt(() => s.AppKeys.GetValueOrDefault("meet.mic", ""), v => s.AppKeys["meet.mic"] = v.Trim().ToLowerInvariant(), 150));
                yield return ("~Pintasan Meet: kamera", Txt(() => s.AppKeys.GetValueOrDefault("meet.cam", ""), v => s.AppKeys["meet.cam"] = v.Trim().ToLowerInvariant(), 150));
                yield return ("~Atur ulang sambungan Discord", Act("Atur ulang", () => { s.DiscordClientId = s.DiscordSecret = ""; s.DiscordSetupAsked = false; DiscordRpc.I.ResetToken(); Render(); }, UI.Warn));
                break;

            case "addons":
                yield return ("Unduhan tambahan\nTidak ada yang diunduh otomatis; unduh hanya bila fiturnya dibutuhkan", new Border());
                foreach (var a in Addons.All) yield return (L.T(a.Name) + " · " + a.Size, AddonControl(a));
                break;
        }
    }

    // ---------- addon (unduhan opsional) ----------
    UIElement AddonControl(Addon a)
    {
        bool on = a.Installed();
        var sp = new StackPanel { MaxWidth = 360, Tag = "wide" };
        var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
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
                    catch (Exception ex) { a.Error = L.T("Gagal: ") + ex.Message; App.Log(ex); }
                    finally { a.Busy = false; _ = Dispatcher.BeginInvoke(() => { if (_page == "addons") Render(); }); }
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

    UIElement PhoneDevices()
    {
        var list = new StackPanel { MinWidth = 280, Tag = "wide" };
        var devs = ShareServer.I.Devices.OrderByDescending(d => d.LastSeen).ToList();
        if (devs.Count == 0) list.Children.Add(UI.T("Belum ada HP yang tersambung", 11.5, UI.Faint));
        foreach (var d in devs)
        {
            var row = UI.Cols(14, -1, 0, 0);
            row.Margin = new Thickness(0, 2, 0, 2);
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = d.Blocked ? UI.Bad : d.Online ? UI.B("#FF22D35A") : UI.Faint, VerticalAlignment = VerticalAlignment.Center });
            var name = UI.T($"{d.Name}  ·  {d.Ip}" + (ShareServer.I.HasApp(d.Id) ? "  · app" : "") + (d.Blocked ? "  " + L.T("(diblokir)") : d.Online ? "" : "  · " + d.LastSeen.ToString("dd/MM HH.mm")), 11.5, d.Online ? UI.Fg : UI.Dim);
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
    // "Label\nKeterangan": keterangan kecil di bawah label; label panjang membungkus, tidak terpotong
    static UIElement Row(string label, UIElement ctl)
    {
        string cap = null;
        int nl = label.IndexOf('\n');
        if (nl >= 0) { cap = label[(nl + 1)..]; label = label[..nl]; }
        var g = UI.Cols(-1, 0);
        g.ColumnDefinitions[0].MinWidth = 160;
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var t = UI.T(label, 12.5);
        t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
        left.Children.Add(t);
        if (!string.IsNullOrWhiteSpace(cap))
        {
            var c = UI.T(cap, 11, UI.Faint);
            c.TextWrapping = TextWrapping.Wrap; c.TextTrimming = TextTrimming.None; c.Margin = new Thickness(0, 2, 0, 0);
            left.Children.Add(c);
        }
        if (ctl is FrameworkElement { Tag: "wide" } wide)
        {
            // kontrol lebar (status, add-on, daftar HP) di bawah label: kolom kanan yang auto-lebar terpotong pada jendela ukuran bawaan
            left.Margin = new Thickness(0);
            wide.HorizontalAlignment = HorizontalAlignment.Left; wide.Margin = new Thickness(0, 6, 0, 0);
            var col = new StackPanel();
            col.Children.Add(left); col.Children.Add(wide);
            return new Border { BorderBrush = UI.Track, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 6, 0, 8), MinHeight = 34, Child = col };
        }
        g.Children.Add(left);
        ctl.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        g.Children.Add(ctl.At(0, 1));
        return new Border { BorderBrush = UI.Track, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 6, 0, 6), MinHeight = 34, Child = g };
    }

    bool _moreOpen;

    /// <summary>Kepala "Lainnya" yang bisa dibuka-tutup untuk baris bertanda "¶".</summary>
    UIElement MoreHeader()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(UI.T("Lainnya", 11.5, UI.Accent, true));
        var chev = UI.Icon(_moreOpen ? "" : "", 9, UI.Accent); chev.Margin = new Thickness(6, 1, 0, 0);
        row.Children.Add(chev);
        var b = UI.Btn(row, () => { _moreOpen = !_moreOpen; Render(); }, Brushes.Transparent, 6, new Thickness(0, 4, 0, 4));
        b.Margin = new Thickness(0, 12, 0, 0); b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    static UIElement SectionHeader(string title)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 16, 0, 2) };
        sp.Children.Add(UI.T(title, 11.5, UI.Accent, true));
        sp.Children.Add(new Border { Height = 1, Background = UI.Track, Margin = new Thickness(0, 4, 0, 0) });
        return sp;
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

    /// <summary>Pilihan 4 opsi atau lebih: daftar turun. Kurang dari itu: pemilih "‹ nilai ›" yang berputar.</summary>
    static UIElement Dropdown((string Key, string Label)[] opts, Func<string> get, Action<string> set, Action extra)
    {
        var txt = UI.T("", 12.5, UI.Fg, true);
        txt.MinWidth = 84; txt.MaxWidth = 190; txt.TextAlignment = TextAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(txt);
        var chev = UI.Icon("", 9, UI.Dim); chev.Margin = new Thickness(8, 1, 0, 0);
        row.Children.Add(chev);
        var list = new StackPanel();
        var pop = new System.Windows.Controls.Primitives.Popup
        {
            StaysOpen = false, AllowsTransparency = true, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            Child = new Border
            {
                Background = UI.B("#FF1C1C20"), BorderBrush = UI.Track, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 8),
                Child = new ScrollViewer { MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list }
            }
        };
        void Paint() { txt.Text = L.T(Array.Find(opts, o => o.Key == get()).Label ?? get()); }
        var b = UI.Btn(row, () =>
        {
            list.Children.Clear();
            foreach (var o in opts)
            {
                var key = o.Key;
                var item = UI.Btn(UI.T(o.Label, 12.5, UI.Fg, key == get()), () => { pop.IsOpen = false; set(key); Chg(extra); Paint(); },
                    key == get() ? UI.CardOn : Brushes.Transparent, 8, new Thickness(12, 6, 12, 6));
                item.MinWidth = 120;
                list.Children.Add(item);
            }
            pop.IsOpen = !pop.IsOpen;
        }, UI.Card, 10, new Thickness(12, 5, 12, 5));
        pop.PlacementTarget = b;
        Paint();
        return b;
    }

    static UIElement Choice((string Key, string Label)[] opts, Func<string> get, Action<string> set, Action extra = null)
    {
        if (opts.Length >= 4) return Dropdown(opts, get, set, extra);
        var txt = UI.T("", 12.5, UI.Fg, true);
        txt.MinWidth = 96; txt.MaxWidth = 190; txt.TextTrimming = TextTrimming.CharacterEllipsis; txt.TextAlignment = TextAlignment.Center;   // pilihan panjang (mis. bahasa) dipotong dengan "…" bukan terpotong di tepi
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(UI.Icon("", 9, UI.Dim));
        row.Children.Add(txt);
        row.Children.Add(UI.Icon("", 9, UI.Dim));
        var b = new Border { Child = row, Background = UI.Card, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand };
        void Paint() { txt.Text = L.T(Array.Find(opts, o => o.Key == get()).Label ?? get()); txt.ToolTip = txt.Text; }
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
        void Paint() => val.Text = get() + L.T(suffix);
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
        var tb = new TextBox { Width = 120, FontSize = 12.5, TextAlignment = TextAlignment.Center, Text = DurText.Fmt(getSec()), ToolTip = L.F("Ketik bebas, contoh: 30dtk, 10m, 1.5 jam, 1j30m. Tanpa satuan = {0}", L.T(bare == 60 ? "menit" : "detik")) };
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
