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
/// Bridge to the browser extension (src/BrowserExtension): the extension reads product pages in the owner's browser (their own session, so even stores that
/// block automated clients can be read) and sends the title, price, and image here. Listens only on 127.0.0.1, rejects origins
/// other than browser extensions, and requires a token (Settings.WishToken). Stored in memory only (max 60 pages, 6 hours) and only
/// becomes a Wishlist item when the owner clicks Paste link or accepts the pill suggestion. No polling: only runs when the extension sends data.
/// </summary>
public static class WishBridge
{
    public const int Port = 47892;
    static TcpListener _l;
    static readonly Dictionary<string, (WishBridgeCore.Page P, DateTime At)> Seen = new();
    static string _suggested = "";
    public static DateTime LastContact = DateTime.MinValue;
    public static int Received;
    public static event Action Changed;   // UI thread: new contact from extension

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

    /// <summary>Called on startup and whenever Settings change.</summary>
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
        catch (Exception ex) { _l = null; App.Log(ex); }   // port used by another app: extension not connected, links can still be pasted manually
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
            catch { return; }   // listener stopped
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
                bool hook = path == "/notify";   // local webhook: scripts on this PC (no Origin); web pages (http/https Origin) are still rejected
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
            catch { /* connection dropped or malformed request: ignore */ }
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

    /// <summary>Page info previously sent by the extension for this link (exact match, or the only page with the same host and path).</summary>
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

    /// <summary>From clipboard hook (UI thread): product link newly recognized by the extension is offered once to Wishlist, not auto-saved.</summary>
    public static void OnClipboard()
    {
        if (!Settings.Cur.WishSuggest) return;
        lock (Seen) if (Seen.Count == 0) return;   // nothing recognized: do not touch clipboard
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
