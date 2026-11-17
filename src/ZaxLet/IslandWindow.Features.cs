using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>Fitur tambahan island: gestur, popup OSD, indikator mikrofon/kamera, unduhan selesai, jaringan, riwayat clipboard, adzan.</summary>
public partial class IslandWindow
{
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);

    static IslandWindow() { TabMeta["ctrl"] = ("", "Control Panel");TabMeta["wish"] = ("", "Wishlist");TabMeta["clip"] = ("", "Clipboard"); }

    // ---------- tema, mode tampil, musik di layar penuh ----------
    static readonly Dictionary<string, Brush> ThemeBrushes = BuildThemes();

    static Dictionary<string, Brush> BuildThemes()
    {
        static Brush G(Point a, Point b, params (string Hex, double At)[] stops)
        {
            var br = new LinearGradientBrush { StartPoint = a, EndPoint = b };
            foreach (var (hex, at) in stops) br.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hex), at));
            br.Freeze();
            return br;
        }
        return new()
        {
            ["black"] = Brushes.Black, ["neon"] = Brushes.Black,
            ["aurora"] = G(new Point(0, 0), new Point(0, 1), ("#FF040A1C", 0), ("#FF0A2552", 1)),     // biru gelap seperti kaca
            ["matcha"] = G(new Point(0, 0), new Point(0, 1), ("#FF0A150D", 0), ("#FF1D3A22", 1)),     // hijau teh
            ["senja"] = G(new Point(0, 0), new Point(1, 1), ("#FF1A0A30", 0), ("#FF4A1532", .6), ("#FF7A3A12", 1)),   // ungu ke oranye
        };
    }

    Brush ThemeFill() => ThemeBrushes.GetValueOrDefault(Settings.Cur.Theme) ?? Brushes.Black;

    /// <summary>Warna dasar tema, dan garis tepi neon yang mengikuti warna sampul lagu (warna aksen bila tidak ada lagu).</summary>
    void ApplyTheme()
    {
        if (!_open) Shape.Fill = ThemeFill();
        if (Settings.Cur.Theme != "neon") { if (Shape.Stroke != null) Shape.Stroke = null; return; }
        var ms = MediaService.I;
        var c = ms.HasSession && ms.CoverColor is { } cc ? cc : ((SolidColorBrush)UI.Accent).Color;
        if (Shape.Stroke is SolidColorBrush cur && cur.Color == c) return;
        var br = new SolidColorBrush(c); br.Freeze();
        Shape.Stroke = br; Shape.StrokeThickness = 1.5;
    }

    bool _fsApp, _hiddenByMode, _clickThru, _wasMini;
    (double W, double H) _idleFull = (200, 36);

    /// <summary>Pil jam kecil: island menyingkir karena menutupi tombol aplikasi lain (bukan layar penuh sungguhan), tapi tetap tampil sebagai jam kecil bila diizinkan.</summary>
    bool MiniActive => _fs && !_fsApp && !_snooze && Settings.Cur.MiniClock != "off" && !FsMusicOn;   // layar penuh sungguhan: tidak ada jam (dicoba dan dibatalkan: tidak berguna)

    /// <summary>Ada yang sedang terjadi di island (musik, notifikasi, timer, rapat, fokus, rekaman): dipakai mode "hanya saat ada aktivitas".</summary>
    bool HasActivity() => _open || MediaService.I.Playing || _rows.Count > 0 || _alert != null && DateTime.Now < _alertUntil
        || TimerService.I.Running || MeetingService.I.Active || FocusService.I.Active || CaptureService.Recording;

    bool FsMusicOn => _fsApp && Settings.Cur.FsMusic && MediaService.I.Playing;

    /// <summary>"Disembunyikan sampai dibuka dari tray" memakai mekanisme sembunyi yang sudah ada; "hanya saat ada aktivitas" memakai intipan kursor di tepi atas.</summary>
    void ApplyShowMode()
    {
        var mode = Settings.Cur.ShowMode;
        if (mode == "hidden" && !_snooze) { SetSnooze(true); _snoozeUntil = DateTime.MaxValue; _hiddenByMode = true; }
        else if (mode != "hidden" && _hiddenByMode) { _hiddenByMode = false; if (_snooze) SetSnooze(false); }
        if (mode == "activity") _peekT.Start(); else if (!_fs && !_snoozePeek) { _peekT.Stop(); _peek = false; }
        SyncVisible();
    }

    /// <summary>Jendela tidak menangkap klik (WS_EX_TRANSPARENT): permainan di bawah tidak terganggu.</summary>
    void SetClickThrough(bool on)
    {
        if (on == _clickThru) return;
        _clickThru = on;
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        Native.SetWindowLong(h, Native.GWL_EXSTYLE, on ? ex | 0x20 : ex & ~0x20);
    }

    /// <summary>Buka pill lalu pilih tab (dari klik pada notifikasi ringkasan).</summary>
    public void OpenTab(string key) { OpenNow(); Select(key); }

    string _flashTitle = "";
    DispatcherTimer _flashT;

    /// <summary>Lagu ganti: tepi pill menyala sekali dengan warna sampul lalu padam. Hanya di island (bukan layar), tidak di layar penuh, dan tidak di tema Neon (garisnya sudah menyala).</summary>
    void TrackFlash()
    {
        var ms = MediaService.I;
        if (ms.Title == _flashTitle) return;
        _flashTitle = ms.Title ?? "";
        if (_flashTitle.Length == 0 || !ms.Playing || !Settings.Cur.TrackFlash || Settings.Cur.Theme == "neon") return;
        _flashT ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };   // warna sampul datang sesaat setelah judul
        _flashT.Tick -= DoFlash; _flashT.Tick += DoFlash;
        _flashT.Stop(); _flashT.Start();
    }

    void DoFlash(object s, EventArgs e)
    {
        _flashT.Stop();
        var ms = MediaService.I;
        if (_fs || _snooze || !ms.Playing) return;
        var br = new SolidColorBrush(ms.CoverColor ?? ((SolidColorBrush)UI.Accent).Color);
        Shape.Stroke = br; Shape.StrokeThickness = 2;
        var a = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1100)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn } };
        a.Completed += (_, _) => { if (Shape.Stroke == br) ApplyTheme(); };
        br.BeginAnimation(Brush.OpacityProperty, a);
    }

    DispatcherTimer _hkT;
    IntPtr _prevFg;   // jendela yang aktif sebelum island dibuka lewat pintasan; fokusnya dikembalikan saat island menutup

    void RestoreFocus()
    {
        var p = _prevFg; _prevFg = IntPtr.Zero;
        if (p != IntPtr.Zero && IsActive) Native.SetForegroundWindow(p);
    }

    /// <summary>Panah kiri/kanan pindah tab, Esc menutup. Tidak aktif saat mengetik di kolom teks.</summary>
    void KeyNav(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (!_open || Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
        if (e.Key is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right)
        {
            var ks = TabOrder.Where(k => k != "settings" && !Settings.Cur.HiddenTabs.Contains(k)).ToList();
            int i = ks.IndexOf(_cur?.Key), n = ks.Count, d = e.Key == System.Windows.Input.Key.Right ? 1 : -1;
            if (n > 0) Select(ks[((i < 0 ? 0 : i) + d + n) % n]);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape) { KeepOpen(false); _closeT.Stop(); _hkT?.Stop(); Close_(); e.Handled = true; }
    }

    /// <summary>Pintasan keyboard: buka island dan biarkan terbuka (tanpa kursor); tekan lagi untuk menutup. Menutup sendiri setelah 12 dtk bila kursor tidak di atasnya dan tidak sedang mengetik.</summary>
    void ToggleOpenByKey()
    {
        if (_open) { KeepOpen(false); _closeT.Stop(); Close_(); _hkT?.Stop(); return; }
        _prevFg = Native.GetForegroundWindow();
        OpenNow(); KeepOpen(true);
        if (_open) Activate();   // pintasan eksplisit: island menerima keyboard (panah ganti tab, Esc menutup)
        _hkT ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _hkT.Tick -= HotkeyIdle; _hkT.Tick += HotkeyIdle;
        _hkT.Stop(); _hkT.Start();
    }

    void HotkeyIdle(object s, EventArgs e)
    {
        if (Pill.IsMouseOver || Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
        _hkT.Stop(); KeepOpen(false); RestoreFocus();
    }

    Action _alertAction;
    bool _dblSkip;
    readonly YieldService _yield = new();
    (int L, int T, int R, int B) _pillPx;   // ditulis thread UI, dibaca thread latar: salinan sesaat yang tidak konsisten tidak berbahaya
    readonly DispatcherTimer _osdT = new() { Interval = TimeSpan.FromMilliseconds(300) };
    Timer _slowT;
    readonly BluetoothWatch _bt = new();
    readonly DownloadWatch _dl = new();
    readonly DispatcherTimer _btT = new() { Interval = TimeSpan.FromMinutes(10) };
    readonly HashSet<string> _btWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tiap 10 menit: perangkat Bluetooth yang tersambung dan baterainya di bawah batas diperingatkan sekali (tanpa suara); pulih di atas batas + 10 baru bisa diperingatkan lagi.</summary>
    void BtLowCheck()
    {
        var s = Settings.Cur;
        if (!s.BtBattery || _bt.Connected().Count == 0) return;
        var on = _bt.Connected();
        foreach (var (name, pct) in BtBattery.Read())
        {
            if (!on.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (pct > s.BtLowPct + 10) _btWarned.Remove(name);
            else if (pct <= s.BtLowPct && _btWarned.Add(name)) Alert(L.F("Baterai {0} tinggal {1}%", name, pct), 6, null, false);
        }
    }

    /// <summary>Baterai HP yang tersambung lewat aplikasi: pola dan batas yang sama dengan Bluetooth (BtBattery, BtLowPct); diam saat mengisi daya.</summary>
    void PhoneLowCheck()
    {
        var s = Settings.Cur;
        if (!s.BtBattery) return;
        foreach (var d in ShareServer.I.Devices.Where(x => x.BatPct >= 0 && ShareServer.I.HasApp(x.Id)).ToList())
        {
            var key = "hp:" + d.Id;
            if (d.BatCharging || d.BatPct > s.BtLowPct + 10) _btWarned.Remove(key);
            else if (d.BatPct <= s.BtLowPct && _btWarned.Add(key)) Alert(L.F("Baterai HP {0} tinggal {1}%", d.Name, d.BatPct), 6, null, false);
        }
    }

    /// <summary>Perangkat Bluetooth tersambung dan persen baterainya (null bila tidak melaporkan): untuk tab Komunikasi.</summary>
    public List<(string Name, int? Pct)> BtList()
    {
        var bat = BtBattery.Read();
        return _bt.Connected().Select(n => (n, bat.TryGetValue(n, out var p) ? (int?)p : null)).ToList();
    }

    /// <summary>Teks untuk perintah "bt": perangkat tersambung yang melaporkan baterai.</summary>
    public string BtSummary()
    {
        var on = _bt.Connected();
        var l = BtBattery.Read().Where(k => on.Contains(k.Key, StringComparer.OrdinalIgnoreCase)).Select(k => $"{k.Key} {k.Value}%").ToList();
        return l.Count > 0 ? string.Join(" · ", l) : L.T("Tidak ada perangkat Bluetooth yang melaporkan baterai");
    }

    // keadaan terakhir untuk mendeteksi perubahan (null = belum dibaca: bacaan pertama tidak memunculkan popup)
    float? _lastVol; bool? _lastMute, _lastMic; bool? _lastCaps; int? _lastBright;
    HashSet<string> _lastPrivacy = new();
    bool _netWarned;

    void InitFeatures()
    {
        Pill.PreviewMouseWheel += OnWheel;
        Pill.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // klik dua kali pada pill idle = putar/jeda; klik kedua tidak boleh ikut membuka pill
            if (Settings.Cur.Gestures && e.ClickCount == 2 && !_open && MediaService.I.HasSession) { MediaService.I.PlayPause(); _dblSkip = true; e.Handled = true; }
        };
        _osdT.Tick += (_, _) => OsdPoll();
        _yield.Start(() => _pillPx);
    }

    bool _slowOn;

    /// <summary>Timer latar hanya jalan bila fiturnya menyala. Dipanggil saat mulai, saat pengaturan berubah, dan saat server HP menyala/mati.</summary>
    void SyncTimers()
    {
        var s = Settings.Cur;
        if (s.AltGhost) _altT.Start();
        else if (_altT.IsEnabled) { _altT.Stop(); AltCheck(); }   // AltCheck terakhir melepas pill yang sedang "hantu"
        if (s.OsdOn || s.YieldOn) _osdT.Start(); else _osdT.Stop();   // YieldOn: poll yang sama mengisi persegi pill untuk YieldService
        _yield.Enable(s.YieldOn);
        bool slow = s.OsdOn || s.PrivacyOn || s.MeetOn || ShareServer.I.On || MeetingService.I.Active;
        if (slow != _slowOn && _slowT != null) { _slowOn = slow; _slowT.Change(slow ? 4000 : Timeout.Infinite, slow ? 2000 : Timeout.Infinite); }
    }

    /// <summary>Dipanggil dari hook jendela: pesan Windows yang dipakai fitur tambahan.</summary>
    void HookFeatures(int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == 0x031D) { ClipboardHistory.Capture(); WishBridge.OnClipboard(); handled = true; }   // WM_CLIPBOARDUPDATE
        else if (msg == 0x020E && Settings.Cur.Gestures && _open && Pill.IsMouseOver)   // WM_MOUSEHWHEEL: sapuan dua jari ke samping
        {
            int delta = (short)((wp.ToInt64() >> 16) & 0xFFFF);
            if (delta != 0) { ShiftTab(delta > 0 ? 1 : -1); handled = true; }
        }
    }

    void StartFeatureListeners(IntPtr hwnd)
    {
        try { AddClipboardFormatListener(hwnd); } catch (Exception ex) { App.Log(ex); }
        _bt.Changed += (name, on) =>
        {
            if (!Settings.Cur.OsdOn) return;
            if (!on || !Settings.Cur.BtBattery) { Osd(L.F(on ? "Bluetooth tersambung: {0}" : "Bluetooth terputus: {0}", name)); return; }
            // baterai baru dilaporkan beberapa detik setelah tersambung: satu popup saja, nama + persen
            Dispatcher.InvokeAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(2500);
                var pct = BtBattery.Read().TryGetValue(name, out var p) ? $" · {p}%" : "";
                Osd(L.F("Bluetooth tersambung: {0}", name) + pct);
            });
        };
        _bt.Start();
        _btT.Tick += (_, _) => { BtLowCheck(); PhoneLowCheck(); };
        _btT.Start();
        _dl.Finished += path =>
        {
            if (!Settings.Cur.DownloadAlert) return;
            Alert(L.F("Unduhan selesai: {0} · klik untuk menampilkan", System.IO.Path.GetFileName(path)), 8,
                () => { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { } });
        };
        _dl.Start();
        // pembacaan yang agak berat (WMI, registri, jaringan) di thread latar tiap 2 dtk
        _slowT = new Timer(_ => SlowPoll(), null, Timeout.Infinite, Timeout.Infinite);
        ShareServer.I.RunChanged += () => Dispatcher.BeginInvoke(SyncTimers);
        SyncTimers();
    }

    // ---------- klik pada pill ----------
    bool HandleClickAction()
    {
        if (_dblSkip) { _dblSkip = false; return true; }
        if (AdhanPlayer.Playing) { AdhanPlayer.Stop(); _alert = null; RefreshIdle(); return true; }
        if (_alert != null && DateTime.Now < _alertUntil && _alertAction != null)
        {
            var a = _alertAction;
            _alertAction = null; _alertUntil = DateTime.MinValue;
            RefreshIdle();
            try { a(); } catch (Exception ex) { App.Log(ex); }
            return true;
        }
        return false;
    }

    // ---------- gestur ----------
    static bool InScrollable(DependencyObject d)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollViewer or TextBox or ComboBox or ListBox) return true;
        return false;
    }

    // scroll di atas island = volume (kecuali di atas daftar/kotak teks yang menggulir sendiri)
    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Settings.Cur.Gestures || OverNotifRows() || e.OriginalSource is DependencyObject d && InScrollable(d)) return;
        var v = AudioEndpoint.Volume();
        if (v == null) return;
        e.Handled = true;
        var nv = Math.Clamp(v.Value + Math.Sign(e.Delta) * 0.02f, 0f, 1f);
        AudioEndpoint.SetVolume(nv);
        _lastVol = nv; _lastMute = nv <= 0 ? (bool?)null : false;
        Osd(VolumeText(nv, false));
    }

    static string VolumeText(float v, bool muted)
    {
        int n = (int)Math.Round(v * 10);
        return $"{L.T("Volume")} {new string('█', n)}{new string('░', 10 - n)} {(muted ? L.T("bisu") : Math.Round(v * 100) + "%")}";
    }

    void ShiftTab(int d)
    {
        var keys = TabOrder.Where(k => k == "settings" || !Settings.Cur.HiddenTabs.Contains(k)).ToList();
        int i = keys.IndexOf(_cur?.Key ?? "");
        if (i < 0) return;
        Select(keys[(i + d + keys.Count) % keys.Count]);
    }

    // ---------- OSD ----------
    void Osd(string text) => Alert(text, 1.8, null, sound: false);

    void OsdPoll()
    {
        {   // persegi pill dalam piksel layar untuk YieldService
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            // ukuran pill TERTUTUP: panel yang terbuka menutupi banyak tombol, tapi itu pilihan pengguna dan tidak boleh membuat island menyingkir
            double w = _idleFull.W * Zoom, h = _idleFull.H * Zoom, x0 = Left + (Width - w) / 2;
            _pillPx = ((int)(x0 * dpi), (int)(Top * dpi), (int)((x0 + w) * dpi), (int)((Top + h) * dpi));
        }
        if (!Settings.Cur.OsdOn || _snooze) return;
        var v = AudioEndpoint.Volume();
        var m = AudioEndpoint.Muted();
        if (v != null && _lastVol != null && (Math.Abs(v.Value - _lastVol.Value) >= 0.005f || m != _lastMute) && m != null)
            Osd(VolumeText(v.Value, m == true));
        _lastVol = v ?? _lastVol; _lastMute = m ?? _lastMute;

        bool caps = (GetKeyState(0x14) & 1) != 0;
        if (_lastCaps != null && caps != _lastCaps) Osd(caps ? "Caps Lock ON" : "Caps Lock off");
        _lastCaps = caps;
    }

    // ---------- pembacaan latar ----------
    void SlowPoll()
    {
        try
        {
            var s = Settings.Cur;
            if (s.OsdOn)
            {
                var mic = AudioEndpoint.Muted(AudioEndpoint.Capture);
                if (mic != null && _lastMic != null && mic != _lastMic) Osd(mic == true ? L.T("Mikrofon dibisukan") : L.T("Mikrofon aktif"));
                if (mic != null) _lastMic = mic;
                var b = Brightness.Get();
                if (b != null && _lastBright != null && b != _lastBright) Osd($"{L.T("Kecerahan")} {b}%");
                if (b != null) _lastBright = b;
            }
            if (s.PrivacyOn)
            {
                var now = new HashSet<string>(PrivacyWatch.InUse("microphone").Select(a => "mic:" + a).Concat(PrivacyWatch.InUse("webcam").Select(a => "cam:" + a)));
                foreach (var n in now.Except(_lastPrivacy))
                    Alert(L.F(n.StartsWith("mic:") ? "Mikrofon dipakai: {0}" : "Kamera dipakai: {0}", n[4..]), 4, null, sound: false);
                _lastPrivacy = now;
            }
            MeetingService.I.Poll();
            if (ShareServer.I.On && !_netWarned)
            {
                var net = NetInfo.Current();
                if (net != null && net.Category == 0 && !NetInfo.FirewallOpenToPublic())
                {
                    _netWarned = true;
                    Alert(L.T("Wi-Fi ini Publik di Windows dan aturan firewall HP belum mencakupnya: HP tidak bisa masuk"), 12);
                }
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ---------- baris pill menurut waktu ----------
    string _ctxLine; DateTime _ctxAt;

    /// <summary>Dihitung ulang paling cepat tiap 20 detik (jadwal sholat dan kalender tidak perlu dihitung tiap detik).</summary>
    string ContextLine()
    {
        if (DateTime.Now - _ctxAt < TimeSpan.FromSeconds(20)) return _ctxLine;
        _ctxAt = DateTime.Now;
        try
        {
            var s = Settings.Cur; var now = DateTime.Now;
            (string, DateTime)? prayer = null;
            if (s.Lat != 0 || s.Lon != 0)
            {
                foreach (var x in PrayerAlerts.For(now.Date).All) if (x.At > now) { prayer = (x.Name, x.At); break; }
                if (prayer == null) { var f = PrayerAlerts.For(now.Date.AddDays(1)).All[0]; prayer = (f.Name, f.At); }
            }
            var cal = CalendarService.I;
            var next = DateOnly.FromDateTime(now.Hour < 5 ? now : now.AddDays(1));   // lewat tengah malam "besok" masih hari ini
            string tomorrow = null;
            if (cal.Holidays.TryGetValue(next, out var hol)) tomorrow = L.T(now.Hour < 5 ? "Hari ini" : "Besok") + ": " + hol;
            else if (cal.On(next).Count(t => !t.Done) is var n and > 0) tomorrow = L.F(now.Hour < 5 ? "{0} tugas hari ini" : "{0} tugas besok", n);
            int tasks = cal.On(DateOnly.FromDateTime(now)).Count(t => !t.Done);
            int water = s.WaterOn ? Math.Max(0, HydrationService.I.Goal - HydrationService.I.Today) : 0;
            _ctxLine = IdleContext.Compose(now, WeatherService.I.Current == null ? null : WeatherService.I.Summary, prayer, water, tasks, tomorrow);
        }
        catch (Exception ex) { App.Log(ex); _ctxLine = null; }
        return _ctxLine;
    }
}
