using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZaxLet;

/// <summary>Categorized settings (BrimDeck style): categories on the left, setting rows on the right. Every change is saved and applied immediately.</summary>
public sealed class SettingsView : TabView
{
    public override string Key => "settings";
    public override string Glyph => "";
    public override string Label => "Atur";
    public override bool CenterV => false;
    public override double H => 292;

    static readonly (string Key, string Label, string Glyph)[] Pages =
    {
        ("general", "Umum", ""), ("island", "Island", ""), ("content", "Isi & tab", "\uE71D"), ("shortcuts", "Pintasan", "\uE765"),
        ("music", "Musik", ""), ("phone", "HP", ""), ("system", "Usage & Monitor", ""), ("calendar", "Kalender & Sholat", ""),
        ("remind", "Pengingat", "\U0001F4A7"), ("tools", "Alat", ""), ("addons", "Addon", ""),
    };

    readonly StackPanel _cats = new();
    readonly StackPanel _rows = new();
    readonly ScrollViewer _scroll;
    public static string StartPage = "general";
    string _page = Normalize(StartPage);

    /// <summary>Old page keys (idle, usage, prayer, notif, ...) mapped to new pages; used by --page flag.</summary>
    static string Normalize(string p) => p switch
    {
        "idle" or "tabs" => "content", "keys" or "pintasan" => "shortcuts", "usage" or "monitor" => "system", "prayer" => "calendar", "water" => "remind",
        "apps" => "tools", "notif" => "phone",
        _ => p
    };

    static readonly Dictionary<string, bool> _secOpen = new();

    static readonly Brush CardBg = UI.WinCard, Sep = UI.WinSep, Fg = UI.Fg, Dim = UI.Dim;   // window palette unified with UI tokens (formerly #1F1F24/#2C2C33/#E6E6EA/#B4B4BC)
    const double CaptionMaxW = 520;   // captions do not span full width in wide windows: ~70 chars per line

    const double FontLabel = 13.5, FontCaption = 12, FontCat = 13.5, FontSearch = 13, FontCtl = 13, FontSection = 14;

    bool _confirmReset;
    readonly TextBox _search = new() { FontSize = FontSearch, Foreground = Fg, CaretBrush = Fg, Background = CardBg, BorderThickness = new Thickness(0), Padding = new Thickness(9, 6, 9, 6), VerticalContentAlignment = VerticalAlignment.Center };
    string _query = "";

    public SettingsView()
    {
        // search box (Ctrl+F): searches across all pages at once; stays above the page list and isn't rebuilt so it keeps focus
        var searchHost = new Border { CornerRadius = new CornerRadius(10), Background = CardBg, Margin = new Thickness(0, 0, 8, 8), ClipToBounds = true };
        var searchGrid = new Grid();
        var ph = new TextBlock { Text = L.T("Cari pengaturan (Ctrl+F)"), Foreground = Dim, FontSize = FontSearch, IsHitTestVisible = false, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        searchGrid.Children.Add(_search); searchGrid.Children.Add(ph);
        searchHost.Child = searchGrid;
        _search.TextChanged += (_, _) => { ph.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; _query = _search.Text; _scroll?.ScrollToTop(); Render(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape && _search.IsKeyboardFocused && _search.Text.Length > 0) { _search.Clear(); e.Handled = true; }
        };
        _cats.Children.Add(searchHost);
        var g = UI.Cols(200, 1, -1);
        g.Children.Add(_cats);
        g.Children.Add(new Border { Width = 1, Background = Sep, Margin = new Thickness(0, 2, 12, 2) }.At(0, 1));
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _rows, Margin = new Thickness(0, 0, 0, 0) };
        g.Children.Add(_scroll.At(0, 2));
        Children.Add(g);
    }

    public void SetCentered(bool centered)
    {
        _rows.MaxWidth = centered ? 820 : double.PositiveInfinity;
        _rows.HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
    }

    void OnUpdate() => Dispatcher.BeginInvoke(() => { if (_page == "general") Render(); });

    public override void OnShow() { _confirmReset = false; UpdateService.Changed += OnUpdate; ShareServer.I.DevicesChanged += OnDevices; LocationService.Changed += OnDevices; PhoneNotifs.Changed += OnDevices; DiscordRpc.I.Changed += OnDevices; Render(); }
    public override void OnHide() { UpdateService.Changed -= OnUpdate; ShareServer.I.DevicesChanged -= OnDevices; LocationService.Changed -= OnDevices; PhoneNotifs.Changed -= OnDevices; DiscordRpc.I.Changed -= OnDevices; }
    void OnDevices() { if (_page is "phone" or "calendar" or "tools") Render(); }

    void Render()
    {
        while (_cats.Children.Count > 1) _cats.Children.RemoveAt(1);   // index 0 = search box
        bool searching = _query.Trim().Length >= 2;
        foreach (var (key, label, glyph) in Pages)
        {
            var k = key;
            bool on = key == _page && !searching;
            var item = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            item.Children.Add(new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Background = on ? UI.Accent : Brushes.Transparent, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            var ic = UI.Icon(glyph, 15, on ? UI.Accent : Dim);
            ic.Width = 20;
            item.Children.Add(ic);
            var tx = UI.T(label, FontCat, on ? Fg : Dim, on);
            tx.Margin = new Thickness(6, 0, 0, 0);
            item.Children.Add(tx);
            var b = UI.Btn(item, () => { _page = k; _search.Clear(); _scroll.ScrollToTop(); Render(); },
                           on ? UI.CardOn : Brushes.Transparent, 10, new Thickness(8, 7, 9, 7));
            b.MinHeight = 34;
            b.Margin = new Thickness(0, 0, 8, 2);
            _cats.Children.Add(b);
        }
        _rows.Children.Clear();
        if (searching) { RenderSearch(_query.Trim()); return; }
        if (PageHints.TryGetValue(_page, out var hint))
        {
            var h = UI.T(hint, FontCaption, Dim);
            h.TextWrapping = TextWrapping.Wrap; h.Margin = new Thickness(2, 0, 0, 12);
            h.MaxWidth = CaptionMaxW; h.HorizontalAlignment = HorizontalAlignment.Left;
            _rows.Children.Add(h);
        }
        bool moreHdr = false;
        StackPanel group = null;
        void CloseGroup()
        {
            if (group != null && group.Children.Count > 0 && group.Children[^1] is Border last)
                last.BorderThickness = new Thickness(0);
            group = null;
        }
        void AddRow(UIElement row)
        {
            if (group == null)
            {
                group = new StackPanel();
                _rows.Children.Add(new Border { Background = CardBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 0, 4), Child = group });
            }
            group.Children.Add(row);
        }
        var pageRows = PageRows(_page).ToList();
        int totalSections = pageRows.Count(r => r.Item1.StartsWith('§'));
        int secIdx = -1;
        bool secOpen = true, inMore = false;
        foreach (var (raw0, ctl) in pageRows)
        {
            var raw = raw0;
            if (raw.StartsWith('§'))
            {
                CloseGroup();
                inMore = false;
                secIdx++;
                var title = raw[1..];
                var secKey = _page + ":" + title;
                bool defOpen = secIdx == 0 || (title != "Tab" && !(totalSections > 4 && secIdx >= 3));
                secOpen = _secOpen.TryGetValue(secKey, out var saved) ? saved : defOpen;
                _rows.Children.Add(SectionHeader(title, _rows.Children.Count == 0, secOpen, () => { _secOpen[secKey] = !secOpen; Render(); }));
                continue;
            }
            bool adv = raw.StartsWith('~'), more = raw.StartsWith('¶');
            if ((adv || more) && !Settings.Cur.Advanced) continue;   // advanced options hidden unless Advanced mode is on
            if (more)
            {
                inMore = true;
                if (!moreHdr) { moreHdr = true; CloseGroup(); _rows.Children.Add(MoreHeader()); }
                if (!_moreOpen) continue;
            }
            else if (!inMore && !secOpen) continue;
            AddRow(Row(adv || more ? raw[1..] : raw, ctl));
        }
        CloseGroup();
        // first time Phone page is opened: phone link guide opens automatically ("Open guide" button reopens it anytime)
        if (_page == "phone" && !_hpGuideShown) { _hpGuideShown = true; PhoneGuide.Show(false); }
    }

    /// <summary>Search results: rows from all pages whose label (or caption, Indonesian or English) contains the query, grouped by page. Rows remain live controls.</summary>
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
            _rows.Children.Add(SectionHeader(label, _rows.Children.Count == 0));
            var group = new StackPanel();
            foreach (var (raw, ctl) in hits) group.Children.Add(Row(raw.TrimStart('~', '¶'), ctl));
            if (group.Children.Count > 0 && group.Children[^1] is Border last) last.BorderThickness = new Thickness(0);
            _rows.Children.Add(new Border { Background = CardBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 0, 4), Child = group });
        }
        if (!any)
        {
            var e = UI.T("Tidak ada pengaturan yang cocok", FontCtl, Dim);
            e.Margin = new Thickness(0, 14, 0, 0);
            _rows.Children.Add(e);
        }
    }

    internal static bool _hpGuideShown;

    static readonly Dictionary<string, string> PageHints = new()
    {
        ["general"] = "Bahasa, tampilan, pembaruan, dan cadangan.",
        ["island"] = "Tampilan island, cara membuka, menyembunyikan, dan gestur.",
        ["content"] = "Elemen yang tampil di pill saat diam serta urutan dan visibilitas tab.",
        ["shortcuts"] = "Klik kotak pintasan lalu tekan kombinasi tombol (format seperti Ctrl + Alt + Space); kosongkan untuk mematikan.",
        ["music"] = "Lirik, tulisan Latin, karaoke, dan pembacaan posisi YouTube.",
        ["phone"] = "Sambungkan HP, notifikasi, dan kamera. Mulai dari Panduan sambung HP.",
        ["system"] = "Kuota Claude dan Codex serta kinerja PC.",
        ["calendar"] = "Kalender, hari libur, jadwal sholat, dan adzan.",
        ["remind"] = "Pengingat minum.",
        ["tools"] = "Tangkap layar, clipboard, Discord, dan Meet.",
        ["addons"] = "Unduhan tambahan yang hanya diambil bila kamu memintanya.",
    };

    // ---------- updates and backup (General) ----------
    string _backupMsg = "";

    UIElement UpdateRow()
    {
        var sp = new StackPanel { MaxWidth = 300, Tag = "wide" };
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var st = UI.T(string.IsNullOrEmpty(UpdateService.Status) ? "ZaxLet " + UpdateService.Current : UpdateService.Status, 12.5, UpdateService.Available != null ? Fg : Dim);
        st.TextWrapping = TextWrapping.Wrap; st.MaxWidth = 170; st.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(st);
        var b = UI.Btn(UI.T(UpdateService.Available != null ? L.F("Perbarui ke {0}", UpdateService.Available.Version) : "Periksa", 12.5, Fg, true),
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
        if (_backupMsg.Length > 0) { var m = UI.T(_backupMsg, FontCaption, Dim); m.TextWrapping = TextWrapping.Wrap; m.HorizontalAlignment = HorizontalAlignment.Left; m.Margin = new Thickness(0, 4, 0, 0); sp.Children.Add(m); }
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
        var t = UI.T(txt, 12.5, n is { Category: 0 } && !NetInfo.FirewallOpenToPublic() ? UI.Warn : Fg);
        t.TextWrapping = TextWrapping.Wrap; t.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(t);
        if (n is { Category: 0 })
        {
            var h = UI.T(NetInfo.FirewallOpenToPublic()
                ? "Jaringan Publik: aturan firewall ZaxLet HP tetap mengizinkan. Wi-Fi tamu sering memblokir antar perangkat; pakai Tailscale bila HP tidak bisa masuk."
                : "Jaringan Publik dan aturan firewall ZaxLet HP belum mencakupnya: HP tidak bisa masuk. Nyalakan ulang server HP atau jadikan jaringan Private.", FontCaption, Dim);
            h.TextWrapping = TextWrapping.Wrap; h.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(h);
        }
        return sp;
    }

    // ---------- contents of each category ----------
    // Row convention: label "§Title" = section subheading; label starting with "~" = advanced option (only shown when Advanced mode is on);
    // "Label\nCaption" = label with small caption underneath.
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
                    Render();
                }, UI.Warn));
                yield return ("Keluar dari ZaxLet", Act("Keluar", () => Application.Current.Shutdown(), UI.Bad));
                break;

            case "island":
                yield return ("§Tampilan island", null);
                yield return ("Tema\nHitam bawaan, atau warna lain; Neon: garis tepi mengikuti warna sampul lagu", Choice(new[] { ("black", "Hitam"), ("aurora", "Aurora"), ("matcha", "Matcha"), ("senja", "Senja"), ("neon", "Neon") }, () => s.Theme, v => s.Theme = v));
                yield return ("Bentuk island\nNotch: bahu cekung di kiri dan kanan seperti takik layar. Dynamic: kapsul bulat", Choice(new[] { ("notch", "Notch"), ("pill", "Dynamic") }, () => s.IslandShape, v => s.IslandShape = v));
                yield return ("Info sesuai waktu di pill\nPagi: cuaca dan sholat berikutnya. Siang: tugas hari ini. Sore: sholat dan sisa minum. Malam: jadwal besok. Hanya tampil bila ada isinya dan tidak ada yang lebih penting", Toggle(() => s.IdleContext, v => s.IdleContext = v));
                yield return ("Kilat tepi saat lagu ganti\nTepi pill menyala sekali dengan warna sampul. Hanya di island, tidak di layar penuh", Toggle(() => s.TrackFlash, v => s.TrackFlash = v));
                yield return ("Ukuran island", Choice(new[] { ("0", "Otomatis"), ("75", "75%"), ("90", "90%"), ("100", "100%"), ("115", "115%"), ("130", "130%") }, () => s.IslandScale.ToString(), v => s.IslandScale = int.Parse(v)));
                yield return ("Geser posisi horizontal\nGeser kiri (−) atau kanan (+) untuk menghindari kamera atau notch", Num(-600, 600, 20, " px", () => s.IslandOffsetX, v => s.IslandOffsetX = v));
                yield return ("Kapan island tampil\nSelalu; hanya saat ada aktivitas (kursor ke tepi atas untuk mengintip); atau tersembunyi sampai dibuka dari ikon tray", Choice(new[] { ("always", "Selalu"), ("activity", "Saat ada aktivitas"), ("hidden", "Disembunyikan") }, () => s.ShowMode, v => s.ShowMode = v));
                yield return ("Hemat baterai otomatis\nSaat memakai baterai dan sisanya di bawah angka ini, pembaruan cepat dikurangi (karaoke lirik, tab Monitor). 0 = mati", Num(0, 50, 5, "%", () => s.SaverPct, v => s.SaverPct = v));
                yield return ("Musik tetap tampil di layar penuh\nLagu dan liriknya muncul di atas game borderless; island tidak bisa diklik supaya permainan tidak terganggu", Toggle(() => s.FsMusic, v => s.FsMusic = v));
                var screens = System.Windows.Forms.Screen.AllScreens;
                if (screens.Length > 1)
                    yield return ("Monitor", Choice(screens.Select((sc, i) => (i.ToString(), i == 0 ? "Monitor utama" : $"Monitor {i + 1}")).ToArray(), () => s.MonitorIdx.ToString(), v => s.MonitorIdx = int.Parse(v)));
                yield return ("§Cara membuka", null);
                yield return ("Cara membuka pill", Choice(new[] { ("hover", "Kursor di atas pill"), ("click", "Klik kiri") }, () => s.HoverOpen ? "hover" : "click", v => s.HoverOpen = v == "hover"));
                yield return ("~Jeda sebelum menutup", Dur(() => s.CloseDelayMs / 1000.0, v => s.CloseDelayMs = (int)(v * 1000), 0.05, 600));
                yield return ("§Menyembunyikan island", null);
                yield return ("Sembunyikan saat fullscreen", Toggle(() => s.AutoHideFullscreen, v => s.AutoHideFullscreen = v));
                yield return ("Klik kanan pill = sembunyikan\nKursor ke tepi atas untuk mengintip, klik kanan lagi untuk menampilkan terus", Toggle(() => s.RightClickHide, v => s.RightClickHide = v));
                yield return ("Tahan Alt di atas pill = hilang sementara", Toggle(() => s.AltGhost, v => s.AltGhost = v));
                yield return ("~Otomatis tampil lagi setelah\n0 = manual", Num(0, 240, 5, " mnt", () => s.SnoozeMin, v => s.SnoozeMin = v));
                yield return ("Menyingkir otomatis bila menutupi tombol aplikasi lain", Toggle(() => s.YieldOn, v => s.YieldOn = v));
                yield return ("~Pil jam kecil saat menyingkir\nBukan hilang total: tetap tampil sebagai jam kecil (atau jam dan tanggal)", Choice(new[] { ("off", "Mati"), ("clock", "Jam"), ("date", "Jam dan tanggal") }, () => s.MiniClock, v => s.MiniClock = v));
                yield return ("§Gestur dan popup", null);
                yield return ("Gestur island\nScroll di atas island = volume, klik dua kali = putar/jeda, sapuan dua jari = pindah tab", Toggle(() => s.Gestures, v => s.Gestures = v));
                yield return ("Popup OSD (volume, kecerahan, Caps Lock, Bluetooth, mikrofon)", Toggle(() => s.OsdOn, v => s.OsdOn = v));
                yield return ("Tampilkan aplikasi yang memakai mikrofon/kamera", Toggle(() => s.PrivacyOn, v => s.PrivacyOn = v));
                yield return ("Notifikasi unduhan selesai", Toggle(() => s.DownloadAlert, v => s.DownloadAlert = v));
                // "¶" = rarely touched advanced option: collapsed under a single "More" header
                yield return ("¶Kelengkungan sudut\n0 = otomatis; makin besar makin bulat", Num(0, 40, 4, " px", () => s.PillRadius, v => s.PillRadius = v == 0 || v >= 8 ? v : 8));
                yield return ("¶Lebar tambahan saat idle\nMelebarkan pill saat tertutup supaya lebih lega", Num(0, 160, 10, " px", () => s.IdleExtra, v => s.IdleExtra = v));
                yield return ("¶Kehalusan animasi\nOtomatis: animasi latar 30 fps, buka/tutup/ganti tab 60 fps. Penuh: semua 60 fps. Berlaku setelah ZaxLet dimulai ulang", Choice(new[] { ("auto", "Otomatis"), ("full", "Penuh (60 fps)") }, () => s.AnimFps, v => s.AnimFps = v));
                break;

            case "content":
                var pos = new[] { ("left", "Kiri"), ("center", "Tengah"), ("right", "Kanan") };
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
                break;

            case "shortcuts":
                yield return ("§Global", null);
                yield return ("Pintasan buka island\nTekan lagi untuk menutup", KeyRec(() => s.OpenKey, v => s.OpenKey = v));
                yield return ("Pintasan Aksi cepat\nKolom ketik perintah singkat: t 10m, fokus 25, catat beli susu, w link, minum 250, tema neon", KeyRec(() => s.QuickKey, v => s.QuickKey = v));
                yield return ("Pintasan sembunyikan pill\nMenu tray juga bisa menyembunyikan island", KeyRec(() => s.HideKey, v => s.HideKey = v));
                yield return ("Pintasan tangkap area", KeyRec(() => s.CapKey, v => s.CapKey = v));
                if (s.Advanced)
                {
                    yield return ("§Aplikasi rapat", null);
                    yield return ("~Pintasan Discord: mic", KeyRec(() => s.AppKeys.GetValueOrDefault("discord.mic", ""), v => s.AppKeys["discord.mic"] = v));
                    yield return ("~Pintasan Discord: deafen", KeyRec(() => s.AppKeys.GetValueOrDefault("discord.deaf", ""), v => s.AppKeys["discord.deaf"] = v));
                    yield return ("~Pintasan Meet: mic", KeyRec(() => s.AppKeys.GetValueOrDefault("meet.mic", ""), v => s.AppKeys["meet.mic"] = v));
                    yield return ("~Pintasan Meet: kamera", KeyRec(() => s.AppKeys.GetValueOrDefault("meet.cam", ""), v => s.AppKeys["meet.cam"] = v));
                }
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
                yield return ("Kirim ke HP dari klik kanan (SendTo)", Toggle(() => s.SendToOn, v => { s.SendToOn = v; SendTo.Sync(v); }));
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
                yield return ("Kotak kedua (Codex / Antigravity)", Toggle(() => s.CodexOn, v => s.CodexOn = v));
                yield return ("~Isi kotak kedua\nAntigravity dibaca dari aplikasinya yang sedang terbuka", Choice(new[] { ("codex", "Codex"), ("antigravity", "Antigravity") }, () => s.Slot2, v => s.Slot2 = v));
                yield return ("Tampilkan perkiraan biaya", Toggle(() => s.ShowCost, v => s.ShowCost = v));
                yield return ("~Interval baca kuota", Dur(() => s.UsageRefreshSec, v => s.UsageRefreshSec = v, 1, 86400));
                yield return ("~Peringatan kuning di", Num(30, 95, 5, "%", () => s.WarnAt, v => s.WarnAt = v));
                yield return ("~Peringatan merah di", Num(50, 100, 5, "%", () => s.CritAt, v => s.CritAt = v));
                yield return ("§Monitor", null);
                yield return ("Tampilkan GPU", Toggle(() => s.MonGpu, v => s.MonGpu = v));
                yield return ("Daftar proses teratas", Toggle(() => s.MonTop, v => s.MonTop = v));
                yield return ("Waktu layar per aplikasi\nMencatat nama aplikasi dan durasi pemakaian per hari secara lokal (appusage.json), tanpa judul jendela", Toggle(() => s.AppUsageOn, v => s.AppUsageOn = v));
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
                yield return ("Jeda musik saat headset terputus\nMenjeda pemutar saat perangkat Bluetooth terputus, dengan tombol klik untuk lanjut", Toggle(() => s.BtPauseOnLost, v => s.BtPauseOnLost = v));
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
                    : UI.T(s.City, FontCtl, Fg, true));
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
                    Tag = "wide",
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
                break;

            case "tools":
                yield return ("§Tangkap layar", null);
                yield return ("Folder tangkapan (kosong = Pictures\\ZaxLet)", Txt(() => s.CapDir, v => s.CapDir = v.Trim(), 150));
                yield return ("Salin tangkapan ke clipboard", Toggle(() => s.CapClipboard, v => s.CapClipboard = v));
                yield return ("~FPS rekam layar", Choice(new[] { ("15", "15"), ("30", "30"), ("60", "60") }, () => s.RecFps.ToString(), v => s.RecFps = int.Parse(v)));
                yield return ("§Clipboard", null);
                yield return ("Riwayat clipboard (hanya di memori)", Toggle(() => s.ClipboardOn, v => s.ClipboardOn = v));
                yield return ("§Discord dan Meet", null);
                yield return ("Panduan Discord\n" + (DiscordRpc.I.Connected ? L.T("Tersambung") + (DiscordRpc.I.InVoice ? " · voice: " + DiscordRpc.I.Channel : "") : DiscordRpc.Configured ? DiscordRpc.I.Status : L.T("Cara cepat siap; cara lengkap belum disambungkan")), Act("Buka panduan", () => DiscordGuide.Show(true), UI.Fg));
                yield return ("~Atur ulang sambungan Discord", Act("Atur ulang", () => { s.DiscordClientId = s.DiscordSecret = ""; s.DiscordSetupAsked = false; DiscordRpc.I.ResetToken(); Render(); }, UI.Warn));
                break;

            case "addons":
                yield return ("Unduhan tambahan\nTidak ada yang diunduh otomatis; unduh hanya bila fiturnya dibutuhkan", new Border());
                foreach (var a in Addons.All) yield return (L.T(a.Name) + " · " + a.Size, AddonControl(a));
                break;
        }
    }

    // ---------- addons (optional downloads) ----------
    UIElement AddonControl(Addon a)
    {
        bool on = a.Installed();
        var sp = new StackPanel { MaxWidth = 360, Tag = "wide" };
        var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        string status = a.Busy ? (string.IsNullOrEmpty(a.Progress()) ? "Menunggu…" : a.Progress()) : a.Error.Length > 0 ? a.Error : on ? "Terpasang" : "Belum diunduh";
        var st = UI.T(status, 12.5, a.Error.Length > 0 && !a.Busy ? UI.Bad : on && !a.Busy ? Fg : Dim);
        st.VerticalAlignment = VerticalAlignment.Center; st.TextWrapping = TextWrapping.Wrap; st.MaxWidth = 170;
        top.Children.Add(st);
        bool removable = on && (a.Detail == null || a.Detail().Length == 0);   // detected in PATH = not ours, do not delete
        if (!a.Busy && (!on || removable))
        {
            var b = UI.Btn(UI.T(on ? "Hapus" : "Unduh", 12.5, on ? Dim : Fg, true), () =>
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
        var d = UI.T(a.Desc + (a.Detail?.Invoke() is { Length: > 0 } det ? " (" + det + ")" : ""), FontCaption, Dim);
        d.TextWrapping = TextWrapping.Wrap; d.Margin = new Thickness(0, 3, 0, 0);
        sp.Children.Add(d);
        return sp;
    }

    // ---------- Phone ----------

    UIElement PhoneDevices()
    {
        var list = new StackPanel { MinWidth = 280, Tag = "wide" };
        var devs = ShareServer.I.DevicesSnapshot().OrderByDescending(d => d.LastSeen).ToList();
        if (devs.Count == 0) list.Children.Add(UI.T("Belum ada HP yang tersambung", FontCaption, Dim));
        foreach (var d in devs)
        {
            var row = UI.Cols(14, -1, 0, 0);
            row.Margin = new Thickness(0, 2, 0, 2);
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = d.Blocked ? UI.Bad : d.Online ? UI.Good : Dim, VerticalAlignment = VerticalAlignment.Center });
            var name = UI.T($"{d.Name}  ·  {d.Ip}" + (ShareServer.I.HasApp(d.Id) ? "  · app" : "") + (d.Blocked ? "  " + L.T("(diblokir)") : d.Online ? "" : "  · " + d.LastSeen.ToString("dd/MM HH.mm")), 12.5, d.Online ? Fg : Dim);
            name.TextTrimming = TextTrimming.CharacterEllipsis; name.MaxWidth = 175; name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name.At(0, 1));
            var dd = d;
            var b1 = UI.Btn(UI.T(d.Blocked ? "Buka blokir" : "Blokir", FontCaption, Dim), () => { if (dd.Blocked) ShareServer.I.Unblock(dd); else ShareServer.I.Forget(dd, true); Render(); }, UI.Card, 8, new Thickness(7, 3, 7, 3));
            var b2 = UI.Btn(UI.T("Lupakan", FontCaption, Dim), () => { ShareServer.I.Forget(dd, false); Render(); }, UI.Card, 8, new Thickness(7, 3, 7, 3));
            b1.Margin = new Thickness(6, 0, 0, 0); b2.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(b1.At(0, 2)); row.Children.Add(b2.At(0, 3));
            list.Children.Add(row);
        }
        return list;
    }


    // ---------- controls ----------
    // "Label\nCaption": small caption below label; long labels wrap instead of truncating
    static UIElement Row(string label, UIElement ctl)
    {
        string cap = null;
        int nl = label.IndexOf('\n');
        if (nl >= 0) { cap = label[(nl + 1)..]; label = label[..nl]; }
        var g = UI.Cols(-1, 0);
        g.ColumnDefinitions[0].MinWidth = 145;
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var t = UI.T(label, FontLabel, Fg);
        t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
        left.Children.Add(t);
        if (!string.IsNullOrWhiteSpace(cap))
        {
            var c = UI.T(cap, FontCaption, Dim);
            c.TextWrapping = TextWrapping.Wrap; c.TextTrimming = TextTrimming.None; c.Margin = new Thickness(0, 4, 0, 0);
            c.MaxWidth = CaptionMaxW; c.HorizontalAlignment = HorizontalAlignment.Left; c.LineHeight = 17; c.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            left.Children.Add(c);
        }
        bool isWide = ctl is TextBox
            || ctl is FrameworkElement { Tag: "wide" };
        if (isWide)
        {
            // wide controls (long options, text box, long caption, add-ons, phone list) below label
            left.Margin = new Thickness(0);
            if (ctl is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Left; fe.Margin = new Thickness(0, 8, 0, 0); }
            var col = new StackPanel();
            col.Children.Add(left); col.Children.Add(ctl);
            return new Border { BorderBrush = Sep, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 9, 0, 10), MinHeight = 38, Child = col };
        }
        g.Children.Add(left);
        ctl.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        g.Children.Add(ctl.At(0, 1));
        return new Border { BorderBrush = Sep, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 9, 0, 9), MinHeight = 38, Child = g };
    }

    bool _moreOpen;

    /// <summary>Collapsible "More" header for rows marked with "¶".</summary>
    UIElement MoreHeader()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(UI.T("Lainnya", FontSection, UI.Accent, true));
        var chev = UI.Icon(_moreOpen ? "" : "", 10, UI.Accent); chev.Margin = new Thickness(6, 1, 0, 0);
        row.Children.Add(chev);
        var b = UI.Btn(row, () => { _moreOpen = !_moreOpen; Render(); }, Brushes.Transparent, 6, new Thickness(2, 4, 2, 4));
        b.Margin = new Thickness(0, 16, 0, 4); b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    static UIElement SectionHeader(string title, bool first = false, bool open = true, Action toggle = null)
    {
        if (toggle == null)
        {
            var sp = new StackPanel { Margin = new Thickness(2, first ? 6 : 22, 0, 6) };
            sp.Children.Add(UI.T(title, FontSection, UI.Accent, true));
            return sp;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(UI.T(title, FontSection, UI.Accent, true));
        var chev = UI.Icon(open ? "" : "", 10, UI.Accent); chev.Margin = new Thickness(6, 1, 0, 0);
        row.Children.Add(chev);
        var b = UI.Btn(row, toggle, Brushes.Transparent, 6, new Thickness(2, 4, 2, 4));
        b.Margin = new Thickness(0, first ? 4 : 18, 0, 4); b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
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

    /// <summary>4 or more options: dropdown. Fewer than that: cycling "‹ value ›" picker.</summary>
    static UIElement Dropdown((string Key, string Label)[] opts, Func<string> get, Action<string> set, Action extra)
    {
        var txt = UI.T("", FontCtl, Fg, true);
        txt.MinWidth = 84; txt.TextWrapping = TextWrapping.Wrap; txt.TextTrimming = TextTrimming.None; txt.TextAlignment = TextAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(txt);
        var chev = UI.Icon("", 9, Dim); chev.Margin = new Thickness(8, 1, 0, 0);
        row.Children.Add(chev);
        var list = new StackPanel();
        var pop = new System.Windows.Controls.Primitives.Popup
        {
            StaysOpen = false, AllowsTransparency = true, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            Child = new Border
            {
                Background = CardBg, BorderBrush = Sep, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 8),
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
                var item = UI.Btn(UI.T(o.Label, FontCtl, Fg, key == get()), () => { pop.IsOpen = false; set(key); Chg(extra); Paint(); },
                    key == get() ? UI.CardOn : Brushes.Transparent, 8, new Thickness(12, 6, 12, 6));
                item.MinWidth = 120;
                list.Children.Add(item);
            }
            pop.IsOpen = !pop.IsOpen;
        }, UI.Card, 10, new Thickness(12, 5, 12, 5));
        pop.PlacementTarget = b;
        if (opts.Any(o => L.T(o.Label).Length > 14)) b.Tag = "wide";   // long labels ("Automatic (follow Windows)") below title, not truncated on the right
        Paint();
        return b;
    }

    static UIElement Choice((string Key, string Label)[] opts, Func<string> get, Action<string> set, Action extra = null)
        => Dropdown(opts, get, set, extra);

    static UIElement Num(int min, int max, int step, string suffix, Func<int> get, Action<int> set, Action extra = null)
    {
        var val = UI.T("", FontCtl, Fg, true);
        val.MinWidth = 70; val.TextAlignment = TextAlignment.Center;
        void Paint() => val.Text = get() + L.T(suffix);
        void Bump(int d) { set(Math.Clamp(get() + d, min, max)); Chg(extra); Paint(); }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(UI.Btn(UI.T("−", 14.5, Fg), () => Bump(-step), UI.Card, 999, new Thickness(9, 0, 9, 2)));
        row.Children.Add(val);
        row.Children.Add(UI.Btn(UI.T("+", 14.5, Fg), () => Bump(step), UI.Card, 999, new Thickness(8, 0, 8, 2)));
        Paint();
        return row;
    }

    static UIElement Dur(Func<double> getSec, Action<double> setSec, double min, double max, Action extra = null, double bare = 1)
    {
        var tb = new TextBox { Width = 120, FontSize = FontCtl, TextAlignment = TextAlignment.Center, Text = DurText.Fmt(getSec()), ToolTip = L.F("Ketik bebas, contoh: 30dtk, 10m, 1.5 jam, 1j30m. Tanpa satuan = {0}", L.T(bare == 60 ? "menit" : "detik")) };
        void Commit()
        {
            if (DurText.Parse(tb.Text, bare, out var s)) { setSec(Math.Clamp(s, min, max)); Chg(extra); }
            tb.Text = DurText.Fmt(getSec());
        }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(); Keyboard.ClearFocus(); } };
        return tb;
    }

    static UIElement Txt(Func<string> get, Action<string> set, double width = 170, string hint = "Ketik di sini…")
    {
        var tb = new TextBox
        {
            FontSize = FontCtl, Foreground = Fg, CaretBrush = Fg,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(9, 5, 9, 5), VerticalContentAlignment = VerticalAlignment.Center,
            Text = get() ?? ""
        };
        var ph = new TextBlock
        {
            Text = L.T(hint), Foreground = UI.Faint, FontSize = FontCtl,
            IsHitTestVisible = false, Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = string.IsNullOrEmpty(tb.Text) ? Visibility.Visible : Visibility.Collapsed
        };
        void SyncPh() => ph.Visibility = string.IsNullOrEmpty(tb.Text) ? Visibility.Visible : Visibility.Collapsed;
        void Commit() { set(tb.Text); Chg(); tb.Text = get() ?? ""; SyncPh(); }
        tb.TextChanged += (_, _) => SyncPh();
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(); Keyboard.ClearFocus(); } };
        var g = new Grid();
        g.Children.Add(tb);
        g.Children.Add(ph);
        return new Border
        {
            Width = width, Tag = "wide",
            Background = CardBg, BorderBrush = UI.Hairline, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), ClipToBounds = true,
            Child = g
        };
    }

    static string FormatHotkey(string combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return L.T("Kosong");
        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" + ", parts.Select(p => p.ToLowerInvariant() switch
        {
            "ctrl" => "Ctrl", "alt" => "Alt", "shift" => "Shift", "win" => "Win",
            "space" => "Space", "enter" => "Enter", "esc" => "Esc", "tab" => "Tab",
            _ => char.ToUpperInvariant(p[0]) + p[1..]
        }));
    }

    static bool IsValidHotkey(string combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return false;
        int keys = 0;
        foreach (var part in combo.ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "alt" or "ctrl" or "shift" or "win") continue;
            bool ok = part is "space" or "enter" or "esc" or "tab"
                || (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                || (part.StartsWith('f') && int.TryParse(part[1..], out var n) && n is >= 1 and <= 24);
            if (!ok) return false;
            keys++;
        }
        return keys == 1;
    }

    static string CaptureKeyCombo(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin) return null;
        var k = key switch
        {
            System.Windows.Input.Key.Space => "space",
            System.Windows.Input.Key.Enter => "enter",
            System.Windows.Input.Key.Tab => "tab",
            System.Windows.Input.Key.Escape => "esc",
            >= System.Windows.Input.Key.A and <= System.Windows.Input.Key.Z => ((char)('a' + (key - System.Windows.Input.Key.A))).ToString(),
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((char)('0' + (key - System.Windows.Input.Key.D0))).ToString(),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => ((char)('0' + (key - System.Windows.Input.Key.NumPad0))).ToString(),
            >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24 => "f" + (1 + (key - System.Windows.Input.Key.F1)),
            _ => ""
        };
        if (k.Length == 0) return "";
        var mods = Keyboard.Modifiers;
        var parts = new List<string>(5);
        if ((mods & ModifierKeys.Control) != 0) parts.Add("ctrl");
        if ((mods & ModifierKeys.Alt) != 0) parts.Add("alt");
        if ((mods & ModifierKeys.Shift) != 0) parts.Add("shift");
        if ((mods & ModifierKeys.Windows) != 0) parts.Add("win");
        parts.Add(k);
        return string.Join("+", parts);
    }

    static bool IsDuplicateHotkey(string combo)
    {
        var norm = (combo ?? "").Trim().ToLowerInvariant();
        if (norm.Length == 0) return false;
        var s = Settings.Cur;
        string[] all =
        {
            s.OpenKey, s.QuickKey, s.HideKey, s.CapKey,
            s.AppKeys.GetValueOrDefault("discord.mic", ""), s.AppKeys.GetValueOrDefault("discord.deaf", ""),
            s.AppKeys.GetValueOrDefault("meet.mic", ""), s.AppKeys.GetValueOrDefault("meet.cam", ""),
        };
        return all.Count(x => (x ?? "").Trim().ToLowerInvariant() == norm) > 1;
    }

    UIElement KeyRec(Func<string> get, Action<string> set)
    {
        var txt = UI.T("", FontCtl, Fg, true);
        txt.TextAlignment = TextAlignment.Center;
        txt.VerticalAlignment = VerticalAlignment.Center;
        var box = new Border
        {
            Child = txt, Background = UI.Card, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 5, 12, 5),
            MinWidth = 140, Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
            BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent
        };
        var clr = UI.Btn(UI.T("×", 13, Dim), () => { set(""); Chg(); Render(); }, UI.Card, 10, new Thickness(8, 4, 8, 4));
        clr.Margin = new Thickness(6, 0, 0, 0);
        clr.ToolTip = L.T("Hapus pintasan");
        var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        top.Children.Add(box);
        top.Children.Add(clr);
        var warn = UI.T("Pintasan sama dengan pengaturan lain", FontCaption, Dim);
        warn.Margin = new Thickness(0, 4, 0, 0);
        warn.HorizontalAlignment = HorizontalAlignment.Right;
        warn.TextWrapping = TextWrapping.Wrap;
        warn.Visibility = IsDuplicateHotkey(get()) ? Visibility.Visible : Visibility.Collapsed;
        var wrap = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        wrap.Children.Add(top);
        wrap.Children.Add(warn);

        bool recording = false;
        void Paint()
        {
            if (recording)
            {
                txt.Text = L.T("Tekan kombinasi tombol...");
                txt.Foreground = UI.Accent;
                box.BorderBrush = UI.Accent;
            }
            else
            {
                var v = get();
                txt.Text = FormatHotkey(v);
                txt.Foreground = string.IsNullOrWhiteSpace(v) ? Dim : Fg;
                box.BorderBrush = Brushes.Transparent;
            }
        }
        box.MouseLeftButtonDown += (_, e) => { e.Handled = true; recording = true; Paint(); box.Focus(); };
        box.LostKeyboardFocus += (_, _) => { if (recording) { recording = false; Paint(); } };
        box.PreviewKeyDown += (_, e) =>
        {
            if (!recording) return;
            e.Handled = true;
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key == System.Windows.Input.Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                recording = false; Paint(); Keyboard.ClearFocus(); return;
            }
            if ((key == System.Windows.Input.Key.Back || key == System.Windows.Input.Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
            {
                recording = false; set(""); Chg(); Render(); return;
            }
            var combo = CaptureKeyCombo(e);
            if (combo == null) return;
            recording = false;
            if (combo.Length > 0 && IsValidHotkey(combo)) { set(combo); Chg(); Render(); }
            else { Paint(); Keyboard.ClearFocus(); }
        };
        Paint();
        return wrap;
    }

    static UIElement Act(string label, Action click, Brush fg) =>
        UI.Btn(UI.T(label, FontCtl, fg == UI.Fg ? Fg : fg, true), click, UI.Card, 10, new Thickness(12, 5, 12, 5));
}
