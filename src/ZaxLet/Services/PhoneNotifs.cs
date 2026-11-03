using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZaxLet;

/// <summary>Satu notifikasi dari HP (diterima lewat kanal kontrol). Key unik per notifikasi di HP; pembaruan memakai Key yang sama.</summary>
public sealed class PhoneNotif
{
    public string Key, Pkg, App, Title, Text, Device;
    public bool Ongoing, CanReply;
    public int Prog, ProgMax;
    public List<(string Sender, string Text)> Msgs = new();   // pesan percakapan, lama ke baru (notifikasi chat memuat beberapa pesan terakhir)
    public DateTime Time = DateTime.Now;
}

/// <summary>
/// Notifikasi HP -> pill idle. Notifikasi "berjalan" (mis. Gojek: status pengiriman) memakai Key yang sama, jadi diperbarui di tempat
/// dan tampil lagi di pill setiap isinya berubah. Pengguna memilih aplikasi mana yang boleh tampil.
/// </summary>
public static class PhoneNotifs
{
    public static readonly Dictionary<string, PhoneNotif> Active = new();
    public static readonly Dictionary<string, string> Apps = new();            // pkg -> nama aplikasi (untuk daftar pengaturan)
    static readonly Dictionary<string, string> Dismissed = new();                // key -> isi saat disilang; tampil lagi bila HP mengirim isi baru
    static readonly Dictionary<string, ImageSource> Icons = new();
    public static int Received;                                                 // diagnosis: jumlah pesan notifikasi dari HP
    public static string LastPkg = ""; public static DateTime LastAt;
    public static event Action<PhoneNotif> Posted;                              // perlu ditampilkan di pill (UI thread)
    public static event Action Changed;                                         // daftar berubah (UI thread)

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
            Received++; LastAt = DateTime.Now; LastPkg = r.TryGetProperty("pkg", out var lp) ? lp.GetString() : "";
            switch (r.GetProperty("t").GetString())
            {
                case "i":   // ikon aplikasi (sekali per paket)
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
                            if (gone == n.Title + "\n" + n.Text) break;   // sudah disilang dan isinya sama (mis. HP mengirim ulang saat tersambung kembali)
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
                case "r":
                    lock (Active) Active.Remove(r.GetProperty("k").GetString());
                    Notify(null, false);
                    break;
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    static string Str(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    static void Notify(PhoneNotif show, bool listChanged)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Changed?.Invoke();
            if (show != null) Posted?.Invoke(show);
        });
    }

    /// <summary>Contoh notifikasi untuk uji tampilan (teks panjang agar running text terlihat).</summary>
    public static void Test() => Notify(new PhoneNotif { Key = "test", Pkg = "test", App = "Contoh", Title = "Gojek", Text = "Driver sedang menuju lokasi pengantaran, perkiraan tiba 3 menit lagi. Mohon siapkan pesanan Anda.", Device = "Uji" }, false);

    /// <summary>Uji tampilan banyak baris (flag --demo-notifs): n notifikasi dengan key berbeda.</summary>
    public static void Demo(int n)
    {
        for (int i = 1; i <= 2; i++)
        {
            var p = new PhoneNotif { Key = "test" + i, Pkg = "test", App = "WhatsApp", Title = "Kontak " + i, Text = "Pesan terakhir: apakah kamu sudah sampai di kantor? Kabari ya.", Device = "Uji", CanReply = true };
            for (int k = 1; k <= n; k++) p.Msgs.Add((i == 2 ? "Budi" : null, "Pesan nomor " + k + " dari kontak " + i));
            lock (Active) Active[p.Key] = p;
            Notify(p, true);
        }
    }

    /// <summary>Notifikasi yang bisa dibalas (WhatsApp, Telegram, dll), terbaru dulu.</summary>
    public static List<PhoneNotif> Replyable(string pkg = null)
    {
        lock (Active) return Active.Values.Where(n => n.CanReply && (pkg == null || n.Pkg == pkg)).OrderByDescending(n => n.Time).ToList();
    }

    /// <summary>Hapus percakapan dari daftar (tombol silang). Muncul lagi hanya bila pesannya berubah.</summary>
    public static void Dismiss(string key)
    {
        lock (Active)
        {
            if (Active.Remove(key, out var n)) Dismissed[key] = n.Title + "\n" + n.Text;
        }
        Notify(null, true);
    }

    /// <summary>Kirim balasan ke HP: aplikasi HP menjalankan aksi balas (RemoteInput) milik notifikasi itu.</summary>
    public static int Reply(string key, string text) =>
        ShareServer.I.Command("reply " + Convert.ToBase64String(Encoding.UTF8.GetBytes(key)) + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));   // key memuat "|": dikodekan
}
