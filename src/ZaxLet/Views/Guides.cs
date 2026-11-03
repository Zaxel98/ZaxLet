using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>Satu langkah panduan: judul, penjelasan, dan isi tambahan (kode QR, status langsung) yang dibangun sekali; Tick memperbarui isinya tiap detik.</summary>
public sealed class GuideStep
{
    public string Title, Body;
    public Func<UIElement> Build;
    public Action Tick;
}

/// <summary>
/// Jendela panduan bertahap yang dipakai semua fitur yang rumit (sambung HP, Discord). Tampil otomatis sekali saat fitur pertama dipakai
/// (<see cref="Settings.GuidesSeen"/>), dan bisa dibuka lagi kapan saja dari tombol info. Tiap langkah boleh menampilkan status langsung
/// (mis. "HP tersambung") supaya pengguna tahu langkahnya berhasil tanpa menebak.
/// </summary>
public sealed class GuideWindow : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    readonly string _id;
    readonly List<GuideStep> _steps;
    readonly TextBlock _count = UI.T("", 11, UI.Faint), _title = UI.T("", 18, null, true), _body = UI.T("", 13, UI.Dim);
    readonly ContentControl _extra = new();
    readonly TextBlock _nextText = UI.T("", 12.5, UI.Fg, true);
    readonly Border _back;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Dictionary<GuideStep, UIElement> _built = new();
    int _i;

    static readonly Dictionary<string, GuideWindow> Open = new();

    /// <param name="force">True = tombol info (selalu tampil); false = hanya bila panduan ini belum pernah tampil otomatis.</param>
    public static void Show(string id, List<GuideStep> steps, bool force)
    {
        if (!force && Settings.Cur.GuidesSeen.Contains(id)) return;
        if (Open.TryGetValue(id, out var w)) { w.Activate(); return; }
        w = new GuideWindow(id, steps);
        Open[id] = w;
        w.Show();
    }

    GuideWindow(string id, List<GuideStep> steps)
    {
        _id = id; _steps = steps;
        Title = "ZaxLet";
        Width = 520; Height = 540;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = Brushes.Black;
        _body.TextWrapping = TextWrapping.Wrap;
        _title.Margin = new Thickness(0, 4, 0, 8);
        _title.TextWrapping = TextWrapping.Wrap;

        var root = UI.Rows(0, -1, 0);
        root.Margin = new Thickness(26, 20, 26, 20);
        var top = new StackPanel();
        top.Children.Add(_count); top.Children.Add(_title); top.Children.Add(_body);
        _body.Margin = new Thickness(0, 0, 0, 10);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { top, _extra } } };
        root.Children.Add(scroll.At(0));
        root.Children.Add(new Border().At(1));
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _back = UI.Btn(UI.T("Kembali", 12.5, UI.Dim), () => { if (_i > 0) { _i--; Render(); } }, UI.Card, 10, new Thickness(14, 6, 14, 6));
        _back.Margin = new Thickness(0, 0, 8, 0);
        btns.Children.Add(_back);
        btns.Children.Add(UI.Btn(_nextText, Next, UI.AccentBg, 10, new Thickness(18, 6, 18, 6)));
        root.Children.Add(btns.At(2));
        Content = root;

        _tick.Tick += (_, _) => { try { _steps[_i].Tick?.Invoke(); } catch (Exception ex) { App.Log(ex); } };
        _tick.Start();
        Closed += (_, _) =>
        {
            _tick.Stop(); Open.Remove(_id);
            if (!Settings.Cur.GuidesSeen.Contains(_id)) { Settings.Cur.GuidesSeen.Add(_id); Settings.Save(); }
        };
        SourceInitialized += (_, _) => { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4); };
        Render();
    }

    void Next() { if (_i + 1 >= _steps.Count) Close(); else { _i++; Render(); } }

    void Render()
    {
        var s = _steps[_i];
        _count.Text = L.F("Langkah {0} dari {1}", _i + 1, _steps.Count);
        _title.Text = L.T(s.Title);
        _body.Text = L.T(s.Body);
        _back.Visibility = _i > 0 ? Visibility.Visible : Visibility.Collapsed;
        _nextText.Text = L.T(_i + 1 >= _steps.Count ? "Selesai" : "Berikutnya");
        if (s.Build != null && !_built.ContainsKey(s)) _built[s] = s.Build();
        _extra.Content = s.Build != null ? _built[s] : null;
        try { s.Tick?.Invoke(); } catch (Exception ex) { App.Log(ex); }
    }

    // ---------- bahan bersama ----------
    /// <summary>Baris status berwarna: "✓ teks" hijau bila ok, "○ teks" redup bila belum.</summary>
    public static TextBlock Status(bool ok, string text)
    {
        var t = UI.T((ok ? "✓ " : "○ ") + text, 12.5, ok ? UI.Good : UI.Dim, ok);
        t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 4, 0, 0);
        return t;
    }

    public static void SetStatus(TextBlock t, bool ok, string text)
    {
        t.Text = (ok ? "✓ " : "○ ") + text;
        t.Foreground = ok ? UI.Good : UI.Dim;
        t.FontWeight = ok ? FontWeights.SemiBold : FontWeights.Normal;
    }

    public static Border QrCard(string url, string caption)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image { Source = ShelfView.Qr(url), Width = 170, Height = 170, SnapsToDevicePixels = true }
        });
        var c = UI.T(caption, 11.5, UI.Dim); c.HorizontalAlignment = HorizontalAlignment.Center; c.Margin = new Thickness(0, 6, 0, 0); c.TextWrapping = TextWrapping.Wrap; c.TextAlignment = TextAlignment.Center;
        sp.Children.Add(c);
        return new Border { Child = sp, Margin = new Thickness(0, 4, 0, 4) };
    }
}

/// <summary>Panduan sambung HP: pasang aplikasi, pasangkan, izinkan notifikasi, dan bila gagal cek jaringan, dengan status langsung di tiap langkah.</summary>
public static class PhoneGuide
{
    public static void Show(bool force = true) => GuideWindow.Show("hp", Steps(), force);

    /// <summary>Ringkasan satu baris keadaan sambungan HP, dipakai di Pengaturan dan Shelf.</summary>
    public static (bool Ok, string Text) State() =>
        !ShareServer.I.On ? (false, L.T("Server HP mati"))
        : ShareServer.I.AppCount > 0 ? (true, L.F("{0} HP tersambung", ShareServer.I.AppCount))
        : (false, L.T("Menunggu aplikasi HP"));

    internal static List<GuideStep> Steps()
    {
        var sv = ShareServer.I;

        // satu isi yang dibangun ulang saat server dinyalakan: QR hanya ada bila server hidup
        UIElement QrOrSwitch(Func<string> url, string caption)
        {
            var host = new ContentControl();
            void Fill()
            {
                if (sv.On) { host.Content = GuideWindow.QrCard(url(), caption); return; }
                var sp = new StackPanel();
                sp.Children.Add(GuideWindow.Status(false, L.T("Server sambungan HP belum menyala")));
                var b = UI.Btn(UI.T("Nyalakan server HP", 12.5, UI.Fg, true), () => { sv.Start(); Settings.Cur.ShareAuto = true; Settings.Save(); Fill(); }, UI.AccentBg, 10, new Thickness(14, 6, 14, 6));
                b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(b);
                host.Content = sp;
            }
            Fill();
            return host;
        }

        TextBlock appLine = null, notifLine = null, netLine = null;
        var steps = new List<GuideStep>
        {
            new()
            {
                Title = "Pasang aplikasi ZaxLet HP",
                Body = "Pindai kode ini dengan kamera HP (HP dan laptop harus di Wi-Fi yang sama), buka berkas yang terunduh, lalu izinkan pemasangan dari sumber ini bila Android bertanya.",
                Build = () => QrOrSwitch(() => sv.ApkUrl, L.T("1. Unduh aplikasi (APK)")),
            },
            new()
            {
                Title = "Pasangkan HP ke laptop",
                Body = "Buka aplikasi ZaxLet HP lalu pindai kode ini (atau pindai dengan kamera HP, aplikasinya terbuka sendiri). Cukup sekali; setelahnya tersambung otomatis.",
                Build = () =>
                {
                    var sp = new StackPanel();
                    sp.Children.Add(QrOrSwitch(() => sv.PairLink, L.T("2. Pasangkan")));
                    appLine = GuideWindow.Status(false, "");
                    sp.Children.Add(appLine);
                    return sp;
                },
                Tick = () => { var (ok, t) = State(); if (appLine != null) GuideWindow.SetStatus(appLine, ok, t); },
            },
            new()
            {
                Title = "Izinkan notifikasi",
                Body = "Di aplikasi ZaxLet HP, ketuk Izinkan akses notifikasi lalu aktifkan ZaxLet HP di daftar Android. Matikan juga penghemat baterai untuk aplikasi ini supaya sambungan tidak terputus. Pilih aplikasi mana saja yang ditampilkan di Pengaturan > HP.",
                Build = () =>
                {
                    var sp = new StackPanel();
                    notifLine = GuideWindow.Status(false, "");
                    sp.Children.Add(notifLine);
                    var test = UI.Btn(UI.T("Kirim notifikasi contoh", 12.5, UI.Fg, true), PhoneNotifs.Test, UI.Card, 10, new Thickness(14, 6, 14, 6));
                    test.HorizontalAlignment = HorizontalAlignment.Left; test.Margin = new Thickness(0, 8, 0, 0);
                    sp.Children.Add(test);
                    return sp;
                },
                Tick = () => { if (notifLine != null) GuideWindow.SetStatus(notifLine, PhoneNotifs.Received > 0, PhoneNotifs.Received > 0 ? L.F("{0} notifikasi diterima dari HP", PhoneNotifs.Received) : L.T("Belum ada notifikasi dari HP")); },
            },
            new()
            {
                Title = "Kalau tidak tersambung",
                Body = "Periksa berurutan: (1) HP dan laptop di Wi-Fi yang sama. Wi-Fi tamu atau publik sering memblokir antar perangkat; pakai hotspot HP atau Tailscale. (2) Saat Windows menanyakan firewall, pilih Izinkan. (3) Setelah ganti jaringan, pindai ulang kode pasangan.",
                Build = () =>
                {
                    var sp = new StackPanel();
                    netLine = GuideWindow.Status(false, "");
                    sp.Children.Add(netLine);
                    var b = UI.Btn(UI.T("Nyalakan ulang server HP", 12.5, UI.Fg, true), () => { sv.Stop(); sv.Start(); }, UI.Card, 10, new Thickness(14, 6, 14, 6));
                    b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 8, 0, 0);
                    sp.Children.Add(b);
                    return sp;
                },
                Tick = () =>
                {
                    if (netLine == null) return;
                    var n = NetInfo.Current();
                    bool risky = n is { Category: 0 } && !NetInfo.FirewallOpenToPublic();
                    GuideWindow.SetStatus(netLine, n != null && !risky, n == null ? L.T("Jaringan tidak terdeteksi") : $"{n.Name} · {L.T(n.Kind)}" + (risky ? " · " + L.T("firewall HP belum mencakup jaringan Publik") : ""));
                },
            },
        };
        return steps;
    }
}

/// <summary>Panduan Discord: cara cepat lewat pintasan, cara lengkap lewat RPC (status voice dan keluar VC), dengan deteksi Client ID/Secret dari clipboard.</summary>
public static class DiscordGuide
{
    static readonly Regex IdRx = new(@"^\d{17,20}$", RegexOptions.Compiled), SecretRx = new(@"^[A-Za-z0-9_\-]{32}$", RegexOptions.Compiled);

    public static void Show(bool force = true)
    {
        Settings.Cur.DiscordSetupAsked = true;
        GuideWindow.Show("discord", Steps(), force);
    }

    static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    internal static List<GuideStep> Steps()
    {
        TextBlock quick = null, idLine = null, secretLine = null, rpcLine = null;
        string last = "";
        return new List<GuideStep>
        {
            new()
            {
                Title = "Mic dan Deafen di island",
                Body = "Ada dua cara. Cara cepat tidak perlu pengaturan: ZaxLet memfokuskan Discord sebentar lalu menekan pintasan bawaannya (Ctrl+Shift+M mic, Ctrl+Shift+D deafen); Discord harus terbuka. Cara lengkap menyambungkan ZaxLet ke Discord sekali supaya status voice channel dan tombol Keluar VC ikut bekerja, tanpa berpindah jendela.",
                Build = () =>
                {
                    quick = GuideWindow.Status(false, "");
                    return quick;
                },
                Tick = () => { if (quick != null) GuideWindow.SetStatus(quick, DiscordRpc.I.Connected, DiscordRpc.I.Connected ? L.T("Sudah tersambung lengkap") : L.T("Cara cepat siap dipakai; cara lengkap belum disiapkan")); },
            },
            new()
            {
                Title = "Cara cepat: cek pintasan",
                Body = "Buka Discord (aplikasi desktop), masuk ke voice channel, lalu tekan tombol Mic atau Deafen di tab Komunikasi. Kalau tidak bereaksi, di Discord buka Pengaturan Pengguna > Keybind dan pastikan pintasannya sama dengan di ZaxLet (Pengaturan > Alat > Pintasan Discord dan Meet). Selesai di sini bila itu cukup.",
            },
            new()
            {
                Title = "Cara lengkap 1: buat aplikasi Discord",
                Body = "Buka portal developer Discord (gratis, tidak ada yang dipublikasikan). Klik New Application dan beri nama bebas, lalu di menu OAuth2 klik Add Redirect, tempel http://localhost, dan Save. Tombol di bawah membantu.",
                Build = () =>
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                    sp.Children.Add(UI.Btn(UI.T("Buka portal Discord", 12.5, UI.Fg, true), () => OpenUrl("https://discord.com/developers/applications"), UI.AccentBg, 10, new Thickness(14, 6, 14, 6)));
                    var copy = UI.Btn(UI.T("Salin http://localhost", 12.5, UI.Fg, true), () => { try { Clipboard.SetText("http://localhost"); } catch { } }, UI.Card, 10, new Thickness(14, 6, 14, 6));
                    copy.Margin = new Thickness(8, 0, 0, 0);
                    sp.Children.Add(copy);
                    return sp;
                },
            },
            new()
            {
                Title = "Cara lengkap 2: salin Client ID dan Secret",
                Body = "Di halaman aplikasimu klik Copy pada Client ID, lalu klik Reset Secret dan Copy pada Secret. ZaxLet membaca clipboard otomatis dan mencentang di bawah; tidak perlu menempelkan apa pun.",
                Build = () =>
                {
                    var sp = new StackPanel();
                    idLine = GuideWindow.Status(false, "Client ID"); secretLine = GuideWindow.Status(false, "Secret");
                    sp.Children.Add(idLine); sp.Children.Add(secretLine);
                    return sp;
                },
                Tick = () =>
                {
                    string txt = null;
                    try { if (Clipboard.ContainsText()) txt = Clipboard.GetText().Trim(); } catch { }
                    if (txt != null && txt != last && txt.Length <= 64)
                    {
                        last = txt;
                        var s = Settings.Cur;
                        if (IdRx.IsMatch(txt)) { s.DiscordClientId = txt; Settings.Apply(); }
                        else if (SecretRx.IsMatch(txt)) { s.DiscordSecret = txt; Settings.Apply(); }
                    }
                    if (idLine != null) GuideWindow.SetStatus(idLine, !string.IsNullOrWhiteSpace(Settings.Cur.DiscordClientId), "Client ID");
                    if (secretLine != null) GuideWindow.SetStatus(secretLine, !string.IsNullOrWhiteSpace(Settings.Cur.DiscordSecret), "Secret");
                },
            },
            new()
            {
                Title = "Cara lengkap 3: izinkan di Discord",
                Body = "Setelah keduanya tercentang, Discord menampilkan dialog Authorize. Klik Authorize. Status di bawah berubah hijau saat tersambung; sesudahnya Mic, Deafen, dan Keluar VC di tab Komunikasi bekerja langsung tanpa berpindah jendela.",
                Build = () => { rpcLine = GuideWindow.Status(false, ""); return rpcLine; },
                Tick = () =>
                {
                    if (rpcLine == null) return;
                    var r = DiscordRpc.I;
                    GuideWindow.SetStatus(rpcLine, r.Connected, r.Connected ? (r.InVoice ? L.F("Tersambung · voice: {0}", r.Channel) : L.T("Tersambung ke Discord")) : r.Status);
                },
            },
        };
    }
}
