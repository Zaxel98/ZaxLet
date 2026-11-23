using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZaxLet;

/// <summary>A single notification from the phone (received over the control channel). Key is unique per notification on the phone; updates reuse the same Key.</summary>
public sealed class PhoneNotif
{
    public string Key, Pkg, App, Title, Text, Device;
    public bool Ongoing, CanReply;
    public int Prog, ProgMax;
    public List<(string Sender, string Text)> Msgs = new();   // conversation messages, oldest to newest (chat notifications carry the last few messages)
    public DateTime Time = DateTime.Now;
}

/// <summary>
/// Phone notifications -> idle pill. "Ongoing" notifications (e.g. Gojek delivery status) reuse the same Key, so they update in place
/// and reappear in the pill whenever their content changes. Users choose which apps are allowed to show.
/// </summary>
public static class PhoneNotifs
{
    public static readonly Dictionary<string, PhoneNotif> Active = new();
    public static readonly Dictionary<string, string> Apps = new();            // pkg -> app name (for settings list)
    static readonly Dictionary<string, string> Dismissed = new();                // key -> content when dismissed; reappears if phone sends new content
    static readonly Dictionary<string, ImageSource> Icons = new();
    static bool _inCall, _callPaused, _sawPaused;
    static PhoneNotifs() => MediaService.I.Changed += () => Ui(OnMediaChanged);
    public static int Received;                                                 // diagnostics: count of notification messages from phone
    public static string LastPkg = ""; public static DateTime LastAt;
    public static event Action<PhoneNotif> Posted;                              // needs to be shown in pill (UI thread)
    public static event Action Changed;                                         // list changed (UI thread)

    public static ImageSource IconOf(string pkg) { lock (Icons) return Icons.GetValueOrDefault(pkg); }

    public static bool IsActive(string key) { lock (Active) return Active.ContainsKey(key); }

    public static bool Enabled(string pkg)
    {
        var s = Settings.Cur;
        if (!s.NotifOn) return false;
        return s.NotifApps.TryGetValue(pkg, out var on) ? on : s.NotifNewDefault;
    }

    public static void Handle(PhoneDevice dev, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("t", out var tt) && tt.GetString() == "b")   // phone battery: not a notification, don't count
            {
                dev.BatPct = r.GetProperty("l").GetInt32(); dev.BatCharging = r.GetProperty("c").GetInt32() == 1;
                return;
            }
            Received++; LastAt = DateTime.Now; LastPkg = r.TryGetProperty("pkg", out var lp) ? lp.GetString() : "";
            switch (r.GetProperty("t").GetString())
            {
                case "i":   // app icon (once per package)
                {
                    var pkg = r.GetProperty("pkg").GetString();
                    lock (Apps) { Apps[pkg] = r.GetProperty("app").GetString(); }
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(r.GetProperty("png").GetString()));
                    bi.EndInit();
                    bi.Freeze();
                    lock (Icons) Icons[pkg] = bi;
                    Notify(null, false);
                    break;
                }
                case "n":
                {
                    var n = new PhoneNotif
                    {
                        Key = r.GetProperty("k").GetString(), Pkg = r.GetProperty("pkg").GetString(), App = r.GetProperty("app").GetString(),
                        Title = Str(r, "title"), Text = Str(r, "text"), Device = dev.Name,
                        Ongoing = r.TryGetProperty("og", out var og) && og.GetBoolean(), CanReply = r.TryGetProperty("rp", out var rp) && rp.GetBoolean(),
                        Prog = r.TryGetProperty("pc", out var pc) ? pc.GetInt32() : 0, ProgMax = r.TryGetProperty("pm", out var pm) ? pm.GetInt32() : 0,
                    };
                    if (r.TryGetProperty("ms", out var ma) && ma.ValueKind == JsonValueKind.Array)
                        foreach (var m in ma.EnumerateArray()) n.Msgs.Add((Str(m, "s"), Str(m, "t")));
                    lock (Apps) Apps[n.Pkg] = n.App;
                    bool changed;
                    lock (Active)
                    {
                        if (Dismissed.TryGetValue(n.Key, out var gone))
                        {
                            if (gone == n.Title + "\n" + n.Text) break;   // already dismissed with same content (e.g. phone resent on reconnect)
                            Dismissed.Remove(n.Key);
                        }
                        changed = !Active.TryGetValue(n.Key, out var old) || old.Title != n.Title || old.Text != n.Text || old.Prog != n.Prog;
                        Active[n.Key] = n;
                    }
                    var s = Settings.Cur;
                    bool show = changed && Enabled(n.Pkg) && (!n.Ongoing || s.NotifOngoing) && !string.IsNullOrWhiteSpace(n.Title + n.Text);
                    Notify(show ? n : null, true);
                    break;
                }
                case "x":   // text from phone -> PC clipboard
                    PhoneText.FromPhone(Str(r, "text"), dev.Name);
                    break;
                case "c":   // phone call status (started/ended)
                    if (r.TryGetProperty("on", out var on) && (on.ValueKind == JsonValueKind.True || on.ValueKind == JsonValueKind.False))
                    {
                        var active = on.GetBoolean();
                        Ui(() => OnCall(active));
                    }
                    break;
                case "r":
                    lock (Active) Active.Remove(r.GetProperty("k").GetString());
                    Notify(null, false);
                    break;
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    static string Str(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    static void Ui(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    static void OnCall(bool on)
    {
        if (on)
        {
            IslandWindow.Instance?.Alert(L.T("Panggilan masuk di HP"));
            if (_inCall) return;
            _inCall = true;
            if (MediaService.I.HasSession && MediaService.I.Playing)
            {
                _callPaused = true;
                _sawPaused = false;
                MediaService.I.PlayPause();
            }
            return;
        }
        _inCall = false;
        if (_callPaused)
        {
            _callPaused = false;
            if (MediaService.I.HasSession && !MediaService.I.Playing)
                MediaService.I.PlayPause();
        }
    }

    static void OnMediaChanged()
    {
        if (!_callPaused) return;
        if (!MediaService.I.HasSession) { _callPaused = false; return; }
        if (!MediaService.I.Playing) _sawPaused = true;
        else if (_sawPaused) _callPaused = false;   // user changed playback manually: do not auto-resume
    }

    static void Notify(PhoneNotif show, bool listChanged)
    {
        Ui(() =>
        {
            Changed?.Invoke();
            if (show != null) Posted?.Invoke(show);
        });
    }

    /// <summary>Sample notification for UI testing (long text so marquee scrolling is visible).</summary>
    public static void Test() => Notify(new PhoneNotif { Key = "test", Pkg = "test", App = "Contoh", Title = "Gojek", Text = "Driver sedang menuju lokasi pengantaran, perkiraan tiba 3 menit lagi. Mohon siapkan pesanan Anda.", Device = "Uji" }, false);

    /// <summary>Multi-row display test (--demo-notifs flag): n notifications with distinct keys.</summary>
    public static void Demo(int n)
    {
        for (int i = 1; i <= 2; i++)
        {
            var p = new PhoneNotif { Key = "test" + i, Pkg = "test", App = "WhatsApp", Title = "Kontak " + i, Text = "Pesan terakhir: apakah kamu sudah sampai di kantor? Kabari ya.", Device = "Uji", CanReply = true };
            for (int k = 1; k <= n; k++) p.Msgs.Add((i == 2 ? "Budi" : null, "Pesan nomor " + k + " dari kontak " + i));
            lock (Active) Active[p.Key] = p;
            Notify(p, true);
        }
        var g = new PhoneNotif { Key = "test3", Pkg = "test2", App = "Gmail", Title = "Tagihan Mei", Text = "Tagihan listrik bulan Mei sudah terbit dan jatuh tempo tanggal 20.", Device = "Uji" };
        lock (Active) Active[g.Key] = g;
        Notify(null, true);
    }

    /// <summary>Comms tab inbox, newest first: replyable chats plus regular notifications from allowed apps ("ongoing" ones like delivery status are excluded).</summary>
    public static bool Listed(PhoneNotif n) => n.CanReply || (!n.Ongoing && Enabled(n.Pkg));

    public static List<PhoneNotif> Inbox(string pkg = null)
    {
        lock (Active) return Active.Values.Where(n => Listed(n) && (pkg == null || n.Pkg == pkg)).OrderByDescending(n => n.Time).ToList();
    }

    /// <summary>Remove conversation from list (close button). Reappears only if its message changes.</summary>
    public static void Dismiss(string key)
    {
        lock (Active)
        {
            if (Active.Remove(key, out var n)) Dismissed[key] = n.Title + "\n" + n.Text;
        }
        Notify(null, true);
    }

    /// <summary>Send reply to phone: the phone app runs that notification's reply action (RemoteInput).</summary>
    public static int Reply(string key, string text) =>
        ShareServer.I.Command("reply " + Convert.ToBase64String(Encoding.UTF8.GetBytes(key)) + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));   // key contains "|": encoded
}
