using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZaxLet;

/// <summary>
/// Jembatan ke ekstensi browser (src/BrowserExtension): ekstensi membaca halaman produk di browser pemilik (sesinya sendiri, jadi toko yang
/// memblokir klien otomatis pun terbaca) lalu mengirim judul, harga, dan gambarnya ke sini. Mendengarkan hanya di 127.0.0.1, menolak asal
/// selain ekstensi browser, dan memerlukan token (Settings.WishToken). Isinya disimpan di memori saja (maks. 60 halaman, 6 jam) dan baru
/// menjadi item Wishlist bila pemilik menekan Tempel link atau menyetujui saran di pill. Tidak ada polling: hanya bekerja bila ekstensi mengirim.
/// </summary>
public static class WishBridge
{
    public const int Port = 47892;
    static TcpListener _l;
    static readonly Dictionary<string, (WishBridgeCore.Page P, DateTime At)> Seen = new();
    static string _suggested = "";
    public static DateTime LastContact = DateTime.MinValue;
    public static int Received;
    public static event Action Changed;   // UI thread: kontak baru dari ekstensi

    public static string Token
    {
        get
        {
            var s = Settings.Cur;
            if (string.IsNullOrEmpty(s.WishToken)) { s.WishToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); Settings.Save(); }
            return s.WishToken;
        }
    }

    public static bool Listening => _l != null;
    public static bool Connected => DateTime.Now - LastContact < TimeSpan.FromMinutes(10);

    /// <summary>Dipanggil saat mulai dan tiap Pengaturan berubah.</summary>
    public static void Apply()
    {
        if (Settings.Cur.WishBridgeOn && _l == null) Start();
        else if (!Settings.Cur.WishBridgeOn && _l != null) Stop();
    }

    static void Start()
    {
        try
        {
            _l = new TcpListener(IPAddress.Loopback, Port);
            _l.Start();
            _ = Task.Run(AcceptLoop);
        }
        catch (Exception ex) { _l = null; App.Log(ex); }   // port dipakai aplikasi lain: ekstensi tidak tersambung, link tetap bisa ditempel manual
    }

    static void Stop()
    {
        try { _l?.Stop(); } catch { }
        _l = null;
        lock (Seen) Seen.Clear();
    }

    static async Task AcceptLoop()
    {
        var l = _l;
        while (l != null && ReferenceEquals(l, _l))
        {
            try { var c = await l.AcceptTcpClientAsync(); _ = Task.Run(() => Handle(c)); }
            catch { return; }   // pendengar dihentikan
        }
    }

    static async Task Handle(TcpClient c)
    {
        using (c)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var ns = c.GetStream();
                var (method, path, headers, body) = await ReadRequest(ns, cts.Token);
                headers.TryGetValue("origin", out var origin);
                bool allowed = WishBridgeCore.OriginAllowed(origin);
                if (method == "OPTIONS") { await Reply(ns, allowed ? 204 : 403, "", origin, allowed); return; }
                bool hook = path == "/notify";   // webhook lokal: skrip di PC ini (tanpa Origin); halaman web (Origin http/https) tetap ditolak
                if (method != "POST" || !(allowed || hook && origin == null)) { await Reply(ns, 403, "{\"ok\":false,\"error\":\"origin\"}", origin, false); return; }
                headers.TryGetValue("x-zaxlet-token", out var tok);
                if (!WishBridgeCore.TokenOk(tok, Token)) { await Reply(ns, 401, "{\"ok\":false,\"error\":\"token\"}", origin, true); return; }
                if (hook)
                {
                    var n = WishBridgeCore.ParseNotify(body);
                    if (n == null) { await Reply(ns, 400, "{\"ok\":false,\"error\":\"invalid\"}", origin, true); return; }
                    var (text, secs) = n.Value;
                    Ui(() => IslandWindow.Instance?.Alert(text, secs, null, false));
                    await Reply(ns, 200, "{\"ok\":true}", origin, true);
                    return;
                }
                LastContact = DateTime.Now;
                if (path == "/ping") { Ui(() => Changed?.Invoke()); await Reply(ns, 200, "{\"ok\":true,\"v\":1}", origin, true); return; }
                if (path == "/product")
                {
                    var page = WishBridgeCore.ParsePage(body);
                    if (page == null) { await Reply(ns, 400, "{\"ok\":false,\"error\":\"invalid\"}", origin, true); return; }
                    Put(page);
                    await Reply(ns, 200, "{\"ok\":true}", origin, true);
                    return;
                }
                await Reply(ns, 404, "{\"ok\":false}", origin, true);
            }
            catch { /* sambungan terputus atau permintaan rusak: abaikan */ }
        }
    }

    static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadRequest(NetworkStream ns, CancellationToken ct)
    {
        var buf = new byte[16384]; int len = 0, end = -1;
        while (end < 0)
        {
            if (len == buf.Length) throw new InvalidOperationException("header terlalu besar");
            int n = await ns.ReadAsync(buf.AsMemory(len), ct);
            if (n == 0) throw new InvalidOperationException("ditutup");
            len += n;
            end = Encoding.ASCII.GetString(buf, 0, len).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }
        var head = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
        var rl = head[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in head.Skip(1)) { int i = h.IndexOf(':'); if (i > 0) headers[h[..i].Trim()] = h[(i + 1)..].Trim(); }
        int cl = headers.TryGetValue("content-length", out var cls) && int.TryParse(cls, out var v) ? v : 0;
        if (cl < 0 || cl > WishBridgeCore.MaxBody) throw new InvalidOperationException("badan terlalu besar");
        var body = new byte[cl];
        int have = Math.Min(cl, len - (end + 4));
        Array.Copy(buf, end + 4, body, 0, have);
        while (have < cl) { int n = await ns.ReadAsync(body.AsMemory(have, cl - have), ct); if (n == 0) throw new InvalidOperationException("ditutup"); have += n; }
        return (rl[0], rl.Length > 1 ? rl[1] : "", headers, Encoding.UTF8.GetString(body));
    }

    static async Task Reply(NetworkStream ns, int code, string json, string origin, bool cors)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {code} {(code == 200 ? "OK" : code == 204 ? "No Content" : code == 401 ? "Unauthorized" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : "Bad Request")}\r\n");
        if (cors && WishBridgeCore.OriginAllowed(origin))
            sb.Append($"Access-Control-Allow-Origin: {origin}\r\nVary: Origin\r\nAccess-Control-Allow-Headers: Content-Type, X-ZaxLet-Token\r\nAccess-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Private-Network: true\r\nAccess-Control-Max-Age: 600\r\n");
        var b = Encoding.UTF8.GetBytes(json);
        sb.Append($"Content-Type: application/json\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n");
        await ns.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
        if (b.Length > 0) await ns.WriteAsync(b);
        await ns.FlushAsync();
    }

    static void Ui(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    static void Put(WishBridgeCore.Page p)
    {
        var key = WishBridgeCore.Key(p.Url);
        lock (Seen)
        {
            Seen[key] = (p, DateTime.Now);
            foreach (var old in Seen.Where(x => DateTime.Now - x.Value.At > TimeSpan.FromHours(6)).Select(x => x.Key).ToList()) Seen.Remove(old);
            while (Seen.Count > 60) Seen.Remove(Seen.OrderBy(x => x.Value.At).First().Key);
        }
        Received++;
        Ui(() => { Wishlist.OnPageSeen(p); Changed?.Invoke(); });
    }

    /// <summary>Info halaman yang pernah dikirim ekstensi untuk link ini (cocok persis, atau satu-satunya halaman dengan host dan path yang sama).</summary>
    public static WishBridgeCore.Page TryGet(string url)
    {
        var key = WishBridgeCore.Key(url);
        if (key == null) return null;
        lock (Seen)
        {
            if (Seen.TryGetValue(key, out var e) && DateTime.Now - e.At < TimeSpan.FromHours(6)) return e.P;
            var pk = WishBridgeCore.PathKey(url);
            var same = Seen.Where(x => WishBridgeCore.PathKey(x.Value.P.Url) == pk && DateTime.Now - x.Value.At < TimeSpan.FromHours(6)).ToList();
            return same.Count == 1 ? same[0].Value.P : null;
        }
    }

    /// <summary>Dari hook clipboard (UI thread): link produk yang baru dikenali ekstensi ditawarkan sekali ke Wishlist, tidak disimpan sendiri.</summary>
    public static void OnClipboard()
    {
        if (!Settings.Cur.WishSuggest) return;
        lock (Seen) if (Seen.Count == 0) return;   // tidak ada yang dikenali: jangan menyentuh clipboard
        string t = null;
        try { if (System.Windows.Clipboard.ContainsText()) t = System.Windows.Clipboard.GetText()?.Trim(); } catch { return; }
        if (string.IsNullOrEmpty(t) || t.Length > 2000 || t.Any(char.IsWhiteSpace) || TryGet(t) is not { } p) return;
        var key = WishBridgeCore.Key(t);
        if (key == _suggested || Wishlist.Items.Any(i => WishBridgeCore.Key(i.Url) == key)) return;
        _suggested = key;
        var title = p.Title.Length > 48 ? p.Title[..48] + "…" : p.Title;
        IslandWindow.Instance?.Alert(L.F("Produk dikenali: {0} · klik untuk simpan ke Wishlist", title), 10, () => Wishlist.Add(t), false);
    }
}
