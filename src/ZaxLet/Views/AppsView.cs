using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>
/// Satu tab untuk banyak aplikasi; pilih aplikasinya di kiri atas. Aplikasi HP (WhatsApp, Telegram, dll): balas chat langsung dari pill
/// lewat aksi balas milik notifikasinya. Discord: mic/deafen/kamera lewat pintasan bawaan; status voice dan Keluar VC lewat RPC
/// yang disiapkan sekali oleh panduan di tab ini (muncul saat tombol Discord pertama kali ditekan). Meet: pintasan keyboard.
/// </summary>
public sealed class AppsView : TabView
{
    public override string Key => "apps";
    public override string Glyph => "";
    public override string Label => "Komunikasi";
    public override bool CenterV => false;
    public override double H => 188;

    readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    readonly Grid _body = new();
    readonly Dictionary<string, string> _draft = new();
    readonly DispatcherTimer _clip = new() { Interval = TimeSpan.FromMilliseconds(700) };
    string _sel, _lastClip = "";
    bool _picked;   // pengguna sudah memilih aplikasi sendiri; sebelum itu tab mengikuti aplikasi pertama (chat HP bila ada)
    bool _wizard;

    public AppsView()
    {
        var root = UI.Rows(0, -1);
        var sv = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _chips, Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(sv);
        root.Children.Add(_body.At(1));
        Children.Add(root);
        _clip.Tick += (_, _) => ClipTick();
    }

    public override void OnShow() { PhoneNotifs.Changed += OnChanged; DiscordRpc.I.Changed += OnChanged; Render(); }
    public override void OnHide() { PhoneNotifs.Changed -= OnChanged; DiscordRpc.I.Changed -= OnChanged; if (!_wizard) _clip.Stop(); }   // panduan: clipboard tetap dipantau walau pill menutup (pengguna sedang di browser)

    // jangan membangun ulang saat pengguna sedang mengetik balasan
    void OnChanged() { if (Keyboard.FocusedElement is TextBox) return; Render(); }

    void Render()
    {
        var apps = new List<(string Id, string Name, string Pkg)>();
        foreach (var g in PhoneNotifs.Replyable().GroupBy(n => n.Pkg)) apps.Add((g.Key, g.First().App, g.Key));
        apps.Add(("pc:discord", "Discord", null));
        apps.Add(("pc:meet", "Google Meet", null));
        if (!_picked || _sel == null || apps.All(a => a.Id != _sel)) _sel = apps[0].Id;
        if (_wizard && DiscordRpc.I.Connected) { _wizard = false; _clip.Stop(); }   // selesai: Discord sudah tersambung

        _chips.Children.Clear();
        foreach (var a in apps)
        {
            var id = a.Id;
            bool on = id == _sel;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (a.Pkg != null && PhoneNotifs.IconOf(a.Pkg) is { } ic)
                row.Children.Add(new Image { Source = ic, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(UI.T(a.Name, 12.5, on ? UI.Fg : UI.Dim, on));
            var b = UI.Btn(row, () => { _sel = id; _picked = true; Render(); }, on ? UI.CardOn : UI.Card, 12, new Thickness(10, 5, 10, 5));
            b.Margin = new Thickness(0, 0, 6, 0);
            _chips.Children.Add(b);
        }
        if (_sel != "pc:meet")   // tombol info: panduan sambung Discord / sambung HP kapan pun dibutuhkan
        {
            var info = UI.IconBtn("", () => { if (_sel == "pc:discord") DiscordGuide.Show(true); else PhoneGuide.Show(true); }, 13);
            info.ToolTip = L.T("Panduan");
            _chips.Children.Add(info);
        }

        _body.Children.Clear();
        _body.Children.Add(_sel.StartsWith("pc:") ? PcPanel(_sel[3..]) : ChatPanel(_sel));
    }

    // ---------- balas chat dari HP ----------
    UIElement ChatPanel(string pkg)
    {
        var list = new StackPanel();
        var items = PhoneNotifs.Replyable(pkg);
        if (items.Count == 0)
            list.Children.Add(UI.T(ShareServer.I.AppCount == 0 ? "Aplikasi ZaxLet HP belum tersambung." : "Belum ada percakapan yang bisa dibalas.", 12, UI.Dim));
        foreach (var n in items.Take(8))
        {
            var key = n.Key;
            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
            var head = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 };
            head.Inlines.Add(new System.Windows.Documents.Run(n.Title) { FontWeight = FontWeights.SemiBold, Foreground = UI.Fg });
            var headRow = UI.Cols(-1, 0);
            headRow.Children.Add(head);
            headRow.Children.Add(UI.IconBtn("\uE711", () => { PhoneNotifs.Dismiss(key); _draft.Remove(key); }, 10).At(0, 1));   // silang: hapus dari daftar bila sudah dibalas
            card.Children.Add(headRow);

            // pesan dalam percakapan ini: satu baris per pesan (lama di atas, terbaru di bawah); lebih dari 3 baris bisa digulir
            var msgs = new StackPanel();
            if (n.Msgs.Count == 0) n.Msgs.Add((null, n.Text));
            foreach (var (who, text) in n.Msgs)
            {
                var line = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = UI.Dim, Margin = new Thickness(0, 1, 0, 1), ToolTip = text };
                if (!string.IsNullOrWhiteSpace(who) && who != n.Title) line.Inlines.Add(new System.Windows.Documents.Run(who + ": ") { Foreground = UI.Fg });
                line.Inlines.Add(new System.Windows.Documents.Run(text));
                msgs.Children.Add(line);
            }
            var msgScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, MaxHeight = 3 * 18, Content = msgs };
            msgScroll.Loaded += (_, _) => msgScroll.ScrollToEnd();
            card.Children.Add(msgScroll);

            var box = new TextBox { FontSize = 12, Padding = new Thickness(8, 3, 8, 3), Text = _draft.GetValueOrDefault(key, "") };
            var ph = new TextBlock { Text = L.T("Balas…"), Foreground = UI.Faint, FontSize = 12, IsHitTestVisible = false, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ph.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var sendText = UI.T("Kirim", 12, UI.Fg, true);
            void Send()
            {
                var t = box.Text.Trim();
                if (t.Length == 0) return;
                if (PhoneNotifs.Reply(key, t) == 0) { sendText.Text = L.T("HP offline"); return; }
                box.Text = ""; _draft.Remove(key);
                sendText.Text = L.T("Terkirim ✓");
            }
            box.TextChanged += (_, _) => { _draft[key] = box.Text; ph.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; if (box.Text.Length > 0) sendText.Text = L.T("Kirim"); };
            box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Send(); };
            var inRow = UI.Cols(-1, 0);
            var wrap = new Grid();
            wrap.Children.Add(box); wrap.Children.Add(ph);
            inRow.Children.Add(wrap);
            var btn = UI.Btn(sendText, Send, UI.AccentBg, 10, new Thickness(10, 5, 10, 5));
            btn.Margin = new Thickness(6, 0, 0, 0);
            inRow.Children.Add(btn.At(0, 1));
            inRow.Margin = new Thickness(0, 3, 0, 0);
            card.Children.Add(inRow);
            list.Children.Add(card);
        }
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = list };
    }

    // ---------- kontrol aplikasi PC ----------
    UIElement PcPanel(string app)
    {
        if (app == "discord" && _wizard) return DiscordWizard();

        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void Btn(string label, string glyph, string keyName, Func<string, bool> send)
        {
            var inner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            inner.Children.Add(UI.Icon(glyph, 20));
            var t = UI.T(label, 12, UI.Dim); t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 6, 0, 0);
            inner.Children.Add(t);
            var b = UI.Btn(inner, () =>
            {
                var combo = Settings.Cur.AppKeys.GetValueOrDefault(keyName, "");
                t.Text = send(combo) ? L.T(label) + " ✓" : L.T("Gagal");
            }, UI.Card, 14, new Thickness(18, 12, 18, 10));
            b.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(b);
        }
        void RBtn(string label, string glyph, bool alert, Action act, Brush bg = null)
        {
            var inner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            inner.Children.Add(UI.Icon(glyph, 20, alert ? UI.Bad : null));
            var tt = UI.T(label, 12, alert ? UI.Bad : UI.Dim); tt.HorizontalAlignment = HorizontalAlignment.Center; tt.Margin = new Thickness(0, 6, 0, 0);
            inner.Children.Add(tt);
            var b = UI.Btn(inner, act, bg ?? UI.Card, 14, new Thickness(16, 12, 16, 10));
            b.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(b);
        }

        if (app == "discord" && DiscordRpc.Configured)
        {
            var rpc = DiscordRpc.I;
            // status voice channel + tombol bergantung keadaan (RPC lokal Discord)
            var st = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            st.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = rpc.InVoice ? UI.B("#FF22D35A") : UI.Faint, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            st.Children.Add(UI.T(!rpc.Connected ? rpc.Status : rpc.InVoice ? "Voice: " + rpc.Channel : "Tidak sedang di voice channel", 12.5, rpc.InVoice ? UI.Fg : UI.Dim, rpc.InVoice));
            sp.Children.Add(st);
            RBtn(rpc.Muted ? "Mic mati" : "Mic", "", rpc.Muted, () => _ = rpc.SetMute(!rpc.Muted));
            RBtn(rpc.Deaf ? "Deafen aktif" : "Deafen", "", rpc.Deaf, () => _ = rpc.SetDeaf(!rpc.Deaf));
            if (rpc.InVoice) RBtn("Keluar VC", "", false, () => _ = rpc.Leave(), UI.B("#CC9B1C24"));
        }
        else if (app == "discord")
        {
            // Penekanan tombol pertama: panduan sambung RPC (sekali). Setelahnya tombol memfokuskan Discord sebentar dan
            // menekan pintasan bawaannya (Ctrl+Shift+M mic, Ctrl+Shift+D deafen) tanpa mengganggu.
            bool D(string combo)
            {
                if (!Settings.Cur.DiscordSetupAsked) { StartWizard(); return true; }
                return Hotkey.SendToWindow(" - Discord", combo);
            }
            Btn("Mic", "", "discord.mic", D);
            Btn("Deafen", "", "discord.deaf", D);
            RBtn("Keluar VC", "", false, StartWizard, UI.B("#CC9B1C24"));   // butuh RPC: langsung ke panduan
        }
        else
        {
            bool Meet(string combo) => Hotkey.SendToWindow("Meet - ", combo) || Hotkey.SendToWindow("Google Meet", combo);
            Btn("Mic", "", "meet.mic", Meet);
            Btn("Kamera", "\uE714", "meet.cam", Meet);
        }
        sp.Children.Add(row);
        var hint = UI.T(app == "discord"
            ? (DiscordRpc.Configured ? "Terhubung lewat RPC lokal Discord." : "Tombol memfokuskan Discord sebentar lalu menekan pintasan bawaannya. Keluar VC dan status voice butuh sambungan sekali (ketuk Keluar VC).")
            : "Tab Meet harus menjadi tab aktif di jendela Chrome/Edge; ZaxLet memindahkan fokus sebentar lalu mengembalikannya.", 11, UI.Faint);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 10, 0, 0);
        sp.Children.Add(hint);
        return sp;
    }

    // ---------- panduan sambung Discord (sekali) ----------
    void StartWizard()
    {
        Settings.Cur.DiscordSetupAsked = true;
        Settings.Save();
        DiscordGuide.Show(true);   // panduan bertahap di jendela sendiri (menggantikan panel kecil di dalam tab)
    }

    /// <summary>Client ID (angka 17-20 digit) dan Secret (32 karakter) dikenali otomatis dari clipboard saat disalin di portal Discord.</summary>
    void ClipTick()
    {
        string txt;
        try { if (!Clipboard.ContainsText()) return; txt = Clipboard.GetText().Trim(); }
        catch { return; }   // clipboard sedang dipakai aplikasi lain
        if (txt == _lastClip || txt.Length > 64) return;
        _lastClip = txt;
        var s = Settings.Cur;
        if (Regex.IsMatch(txt, @"^\d{17,20}$")) s.DiscordClientId = txt;
        else if (Regex.IsMatch(txt, @"^[A-Za-z0-9_\-]{32}$")) s.DiscordSecret = txt;
        else return;
        Settings.Apply();
        Render();
    }

    UIElement DiscordWizard()
    {
        var s = Settings.Cur;
        bool haveId = !string.IsNullOrWhiteSpace(s.DiscordClientId), haveSecret = !string.IsNullOrWhiteSpace(s.DiscordSecret);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var top = UI.Cols(-1, 0);
        top.Children.Add(UI.T("Hubungkan Discord (sekali)", 14, null, true));
        top.Children.Add(UI.Btn(UI.T("Lewati", 11.5, UI.Dim), () => { _wizard = false; _clip.Stop(); Render(); }, UI.Card, 9, new Thickness(9, 3, 9, 3)).At(0, 1));
        sp.Children.Add(top);

        var steps = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = UI.Dim, Margin = new Thickness(0, 6, 0, 8) };
        steps.Inlines.Add(new System.Windows.Documents.Run(L.T("1. Buka portal, ")));
        steps.Inlines.Add(new System.Windows.Documents.Run("New Application") { Foreground = UI.Fg, FontWeight = FontWeights.SemiBold });
        steps.Inlines.Add(new System.Windows.Documents.Run(L.T(" → menu OAuth2 → tambah Redirect ")));
        steps.Inlines.Add(new System.Windows.Documents.Run("http://localhost") { Foreground = UI.Fg, FontWeight = FontWeights.SemiBold });
        steps.Inlines.Add(new System.Windows.Documents.Run(L.T(" → Save.  2. Salin Client ID, lalu Reset Secret → salin. ZaxLet membacanya otomatis.")));
        sp.Children.Add(steps);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        Border B(string text, Action a) { var b = UI.Btn(UI.T(text, 12, UI.Fg, true), a, UI.Card, 10, new Thickness(10, 5, 10, 5)); b.Margin = new Thickness(0, 0, 6, 0); return b; }
        row.Children.Add(B("Buka portal Discord", () => Open("https://discord.com/developers/applications")));
        row.Children.Add(B("Salin redirect", () => { try { Clipboard.SetText("http://localhost"); } catch { } }));
        sp.Children.Add(row);

        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        status.Children.Add(UI.T((haveId ? "✓ Client ID  " : "○ Client ID  "), 12, haveId ? UI.B("#FF22D35A") : UI.Faint));
        status.Children.Add(UI.T((haveSecret ? "✓ Secret  " : "○ Secret  "), 12, haveSecret ? UI.B("#FF22D35A") : UI.Faint));
        if (haveId && haveSecret) status.Children.Add(UI.T(DiscordRpc.I.Connected ? "· Tersambung" : "· " + DiscordRpc.I.Status, 12, UI.Dim));
        sp.Children.Add(status);
        return sp;
    }

    static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }
}
