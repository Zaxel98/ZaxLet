using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace ZaxLet;

/// <summary>Daftar file di shelf. Hanya menyimpan path (file tidak disalin); file kiriman HP disimpan di inbox.</summary>
public static class Shelf
{
    public static event Action Changed;
    public static List<string> Items = (Store.Load<List<string>>("shelf.json") ?? new()).Where(File.Exists).ToList();
    public static string Inbox => Path.Combine(Settings.Dir, "inbox");

    public static void Add(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if (Items.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        Items.Add(path);
        Save();
    }

    public static void Remove(string path) { Items.Remove(path); Save(); }

    static void Save()
    {
        Store.Save("shelf.json", Items);
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }
}

/// <summary>Satu perangkat HP yang pernah membuka halaman ZaxLet. Id dibuat HP sendiri (localStorage), jadi tetap sama antar sesi.</summary>
public sealed class PhoneDevice
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Ip { get; set; }
    public DateTime LastSeen { get; set; }
    public bool Blocked { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Online => (DateTime.Now - LastSeen).TotalSeconds < 25;
}

/// <summary>Bingkai kamera terbaru dari HP (JPEG). Dipakai jendela pratinjau dan keluaran MJPEG.</summary>
public static class PhoneCam
{
    public static byte[] Latest;
    public static long Seq;
    public static DateTime At;
    public static string Device = "";
    public static event Action Frame;
    public static bool Active => Latest != null && (DateTime.Now - At).TotalSeconds < 3;

    public static void Push(byte[] jpeg, string device)
    {
        Latest = jpeg; At = DateTime.Now; Device = device;
        System.Threading.Interlocked.Increment(ref Seq);
        Frame?.Invoke();
    }
}

/// <summary>
/// Server mini untuk HP. Menerima alamat privat/loopback dan rentang Tailscale (100.64.0.0/10), jadi HP bisa tersambung dari
/// jaringan mana pun lewat Tailscale; di jaringan yang sama lewat IP LAN. Dijaga token tetap + daftar perangkat yang bisa diblokir.
/// HTTP untuk berkas; HTTPS (sertifikat sendiri) untuk kamera, karena browser HP hanya mengizinkan kamera di halaman aman.
/// TcpListener dipakai agar tidak butuh hak admin (urlacl).
/// </summary>
public sealed class ShareServer
{
    public static readonly ShareServer I = new();
    const long MaxUpload = 4L << 30;

    TcpListener _l, _tls;
    X509Certificate2 _cert;
    public string Token => Settings.Cur.ShareToken;
    public int Port { get; private set; }
    public int TlsPort { get; private set; }
    public bool On => _l != null;
    /// <summary>IP Tailscale saat ini; dibaca ulang tiap dipakai karena berubah bila akun/jaringan Tailscale diganti.</summary>
    public string TailIp => NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsTailscale(a))?.ToString();
    public string TailName { get; private set; }
    public List<PhoneDevice> Devices { get; } = Store.Load<List<PhoneDevice>>("devices.json") ?? new();
    public event Action DevicesChanged;

    public string LanUrl => $"http://{LanIp() ?? "127.0.0.1"}:{Port}/?t={Token}";
    public string TailUrl => TailIp == null ? null : $"http://{TailIp}:{Port}/?t={Token}";   // IP, bukan nama MagicDNS: nama baru terbaca bila MagicDNS aktif di HP
    /// <summary>Tautan untuk QR sesuai pilihan (jaringan sama / Tailscale); jatuh ke LAN bila Tailscale tidak ada.</summary>
    public string Url => Settings.Cur.ShareQr == "tailscale" && TailUrl != null ? TailUrl : LanUrl;
    public string CamUrl(string host) => $"https://{host}:{TlsPort}/cam?t={Token}";
    public string CertFp => _cert?.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant() ?? "";
    /// <summary>Tautan pasangan untuk aplikasi HP: kedua alamat (LAN dulu, lalu Tailscale), token, dan sidik jari sertifikat.</summary>
    public string PairLink => $"zaxlet://pair?lan={LanIp()}&ts={TailIp}&tp={TlsPort}&t={Token}&f={CertFp}";
    public string ApkUrl => $"http://{LanIp() ?? "127.0.0.1"}:{Port}/zaxlet.apk?t={Token}";
    public string MjpegUrl => $"http://127.0.0.1:{Port}/cam.mjpg?t={Token}";
    public string CamPageUrl => CamUrl(Settings.Cur.ShareQr == "tailscale" && TailIp != null ? TailIp : LanIp() ?? "127.0.0.1");

    public void Start()
    {
        if (_l != null) return;
        var s = Settings.Cur;
        if (string.IsNullOrEmpty(s.ShareToken)) { s.ShareToken = NewToken(); Settings.Save(); }
        _l = Listen(s.SharePort); Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Loop(_l, false);
        try
        {
            _cert = LoadCert();
            _tls = Listen(s.SharePort + 1); TlsPort = ((IPEndPoint)_tls.LocalEndpoint).Port;
            _ = Loop(_tls, true);
        }
        catch (Exception ex) { App.Log(ex); _tls = null; TlsPort = 0; }
        _ = Task.Run(DetectTailscale);
        // hook uji: tulis URL ke file yang ditunjuk env var (tidak aktif secara normal)
        if (Environment.GetEnvironmentVariable("ZAXLET_URL_FILE") is { Length: > 0 } f) File.WriteAllText(f, Url + "\n" + CamPageUrl);
    }

    static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    // port tetap bila bisa (aturan firewall dan bookmark di HP tetap berlaku), kalau bentrok pakai port acak
    static TcpListener Listen(int port)
    {
        try { var l = new TcpListener(IPAddress.Any, port); l.Start(); return l; }
        catch (SocketException) { var l = new TcpListener(IPAddress.Any, 0); l.Start(); return l; }
    }

    public void Stop() { _l?.Stop(); _l = null; _tls?.Stop(); _tls = null; }

    public void ResetToken() { Settings.Cur.ShareToken = NewToken(); Settings.Save(); }

    public void Forget(PhoneDevice d, bool block)
    {
        if (block) d.Blocked = true; else Devices.Remove(d);
        SaveDevices();
    }

    void SaveDevices()
    {
        Store.Save("devices.json", Devices);
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => DevicesChanged?.Invoke());
    }

    PhoneDevice Touch(string id, string ua, IPAddress ip, string nameHint = null)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return null;
        lock (Devices)
        {
            var d = Devices.FirstOrDefault(x => x.Id == id);
            bool isNew = d == null;
            if (isNew) Devices.Add(d = new PhoneDevice { Id = id, Name = string.IsNullOrWhiteSpace(nameHint) ? DeviceName(ua) : nameHint.Trim() });
            var wasOnline = d.Online;
            d.Ip = (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
            d.LastSeen = DateTime.Now;
            if (isNew || !wasOnline) SaveDevices();
            if (isNew) IslandWindow.Instance?.Alert(L.F("HP tersambung · {0}", d.Name), 5);
            return d;
        }
    }

    static string DeviceName(string ua)
    {
        ua ??= "";
        var m = System.Text.RegularExpressions.Regex.Match(ua, @"Android [\d.]+; ([^;)]+?)(?: Build|;|\))");
        if (m.Success) return m.Groups[1].Value.Trim();
        if (ua.Contains("iPhone")) return "iPhone";
        if (ua.Contains("iPad")) return "iPad";
        return ua.Length > 0 ? "Perangkat (" + (ua.Contains("Chrome") ? "Chrome" : ua.Contains("Firefox") ? "Firefox" : "browser") + ")" : "Perangkat";
    }

    // ---------- Tailscale: alamat 100.x dan nama MagicDNS ----------
    void DetectTailscale()
    {
        try
        {
            TailName = null;
            var exe = new[] { @"C:\Program Files\Tailscale\tailscale.exe", @"C:\Program Files (x86)\Tailscale\tailscale.exe" }.FirstOrDefault(File.Exists);
            if (exe == null || TailIp == null) return;
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "status --json") { RedirectStandardOutput = true, CreateNoWindow = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi);
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var dns = doc.RootElement.GetProperty("Self").GetProperty("DNSName").GetString();
            if (!string.IsNullOrEmpty(dns)) TailName = dns.TrimEnd('.');
        }
        catch (Exception ex) { App.Log(ex); }
    }

    static bool IsTailscale(IPAddress a) { var b = a.GetAddressBytes(); return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127; }

    // ---------- sertifikat sendiri untuk HTTPS (kamera) ----------
    static X509Certificate2 LoadCert()
    {
        var path = Path.Combine(Settings.Dir, "share.pfx");
        try
        {
            if (File.Exists(path))
            {
                var c = new X509Certificate2(path, (string)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                if (c.NotAfter > DateTime.Now.AddDays(30)) return c;
            }
        }
        catch { /* buat ulang */ }
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=ZaxLet", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost"); san.AddDnsName(Environment.MachineName);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var a in nic.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork) san.AddIpAddress(a.Address);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(800));
        var pfx = self.Export(X509ContentType.Pfx);
        Directory.CreateDirectory(Settings.Dir);
        File.WriteAllBytes(path, pfx);
        // diimpor ulang agar kunci privatnya bisa dipakai SChannel (kunci efemeral dari CreateSelfSigned tidak bisa)
        return new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    async Task Loop(TcpListener l, bool tls)
    {
        try
        {
            while (true)
            {
                var c = await l.AcceptTcpClientAsync();
                _ = Task.Run(() => Handle(c, tls));
            }
        }
        catch { /* listener ditutup */ }
    }

    public static string LanIp()
    {
        string fallback = null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            var ip = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (ip == null || IsTailscale(ip)) continue;
            if (props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) return ip.ToString();
            fallback ??= ip.ToString();
        }
        return fallback;
    }

    static bool IsLocal(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || IsTailscale(a);
    }

    async Task Handle(TcpClient c, bool tls)
    {
        using (c)
        {
            try
            {
                var remote = ((IPEndPoint)c.Client.RemoteEndPoint).Address;
                if (!IsLocal(remote)) return;
                c.ReceiveTimeout = c.SendTimeout = 30000;
                c.NoDelay = true;
                Stream ns = c.GetStream();
                if (tls)
                {
                    var ssl = new SslStream(ns, false);
                    await ssl.AuthenticateAsServerAsync(_cert, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    ns = ssl;
                }
                using var owned = ns;

                var head = new MemoryStream();
                var buf = new byte[8192];
                int end = -1;
                while (end < 0)
                {
                    int n = await ns.ReadAsync(buf);
                    if (n <= 0) return;
                    head.Write(buf, 0, n);
                    end = IndexOfHeaderEnd(head.GetBuffer(), (int)head.Length);
                    if (head.Length > 32768) return;
                }
                var all = head.GetBuffer();
                int total = (int)head.Length;
                var lines = Encoding.ASCII.GetString(all, 0, end).Split("\r\n");
                var rq = lines[0].Split(' ');
                if (rq.Length < 2) return;
                var hdr = new Dictionary<string, string>();
                foreach (var ln in lines.Skip(1)) { var p = ln.Split(':', 2); if (p.Length == 2) hdr[p[0].Trim().ToLowerInvariant()] = p[1].Trim(); }

                var path = rq[1];
                var q = new Dictionary<string, string>();
                int qi = path.IndexOf('?');
                if (qi >= 0)
                {
                    foreach (var kv in path[(qi + 1)..].Split('&'))
                    {
                        var p = kv.Split('=', 2);
                        q[p[0]] = p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "";
                    }
                    path = path[..qi];
                }
                if (!q.TryGetValue("t", out var tok) || string.IsNullOrEmpty(Token) || tok != Token) { await Send(ns, 403, "text/plain", "forbidden"); return; }
                var dev = Touch(q.GetValueOrDefault("d"), hdr.GetValueOrDefault("user-agent"), remote, q.GetValueOrDefault("dn"));
                if (dev?.Blocked == true) { await Send(ns, 403, "text/plain", "perangkat diblokir"); return; }

                if (rq[0] == "GET" && path == "/") await Send(ns, 200, "text/html; charset=utf-8", Page());
                else if (rq[0] == "GET" && path == "/ping") await Send(ns, 200, "text/plain", "ok");
                else if (rq[0] == "GET" && path == "/ctl" && hdr.TryGetValue("sec-websocket-key", out var ck) && dev != null) await CtlSocket(ns, ck, dev);
                else if (rq[0] == "GET" && path == "/shelf.json")
                    await Send(ns, 200, "application/json", System.Text.Json.JsonSerializer.Serialize(Shelf.Items.Select((p, i) => new { i, name = Path.GetFileName(p), size = File.Exists(p) ? new FileInfo(p).Length : 0 })));
                else if (rq[0] == "GET" && path == "/zaxlet.apk" && File.Exists(ApkPath)) await SendFile(ns, ApkPath, "application/vnd.android.package-archive");
                else if (rq[0] == "GET" && path == "/cam") await Send(ns, 200, "text/html; charset=utf-8", CamPage());
                else if (rq[0] == "GET" && path == "/cam/ws" && hdr.TryGetValue("sec-websocket-key", out var wk)) await CamSocket(ns, wk, dev?.Name ?? "HP");
                else if (rq[0] == "GET" && path == "/cam.mjpg") await Mjpeg(ns);
                else if (rq[0] == "GET" && path == "/cam.jpg" && PhoneCam.Latest is { } jpg) await Send(ns, 200, "image/jpeg", jpg);
                else if (rq[0] == "GET" && path.StartsWith("/d/") && int.TryParse(path[3..], out var idx)) await Download(ns, idx);
                else if (rq[0] == "POST" && path == "/u") await Upload(ns, q, hdr, all, end + 4, total);
                else await Send(ns, 404, "text/plain", "not found");
            }
            catch { /* koneksi putus: abaikan */ }
        }
    }

    static int IndexOfHeaderEnd(byte[] b, int len)
    {
        for (int i = 3; i < len; i++) if (b[i] == '\n' && b[i - 1] == '\r' && b[i - 2] == '\n' && b[i - 3] == '\r') return i - 3;
        return -1;
    }

    static Task Send(Stream ns, int code, string type, string body) => Send(ns, code, type, Encoding.UTF8.GetBytes(body));

    static async Task Send(Stream ns, int code, string type, byte[] bytes)
    {
        var h = $"HTTP/1.1 {code} {(code == 200 ? "OK" : code == 403 ? "Forbidden" : "Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(h));
        await ns.WriteAsync(bytes);
    }

    // ---------- kanal kontrol: aplikasi HP tetap tersambung (notifikasi masuk, balasan chat keluar) ----------
    public static string ApkPath => Path.Combine(AppContext.BaseDirectory, "ZaxLet.apk");
    readonly Dictionary<string, (Stream S, System.Threading.SemaphoreSlim Lock)> _ctl = new();
    public int AppCount { get { lock (_ctl) return _ctl.Count; } }
    public bool HasApp(string id) { lock (_ctl) return _ctl.ContainsKey(id); }

    /// <summary>Kirim perintah teks ke semua aplikasi HP yang tersambung. Mengembalikan jumlah penerima.</summary>
    public int Command(string text)
    {
        (Stream S, System.Threading.SemaphoreSlim Lock)[] all;
        lock (_ctl) all = _ctl.Values.ToArray();
        var b = Encoding.UTF8.GetBytes(text);
        foreach (var (s, l) in all)
            _ = Task.Run(async () =>
            {
                await l.WaitAsync();
                try
                {
                    var head = b.Length < 126 ? new byte[] { 0x81, (byte)b.Length } : new byte[] { 0x81, 126, (byte)(b.Length >> 8), (byte)(b.Length & 255) };   // teks hingga 64 KB (balasan chat)
                    await s.WriteAsync(head.Concat(b).ToArray()); await s.FlushAsync();
                }
                catch { /* soket mati: loop baca akan membersihkan */ }
                finally { l.Release(); }
            });
        return all.Length;
    }

    async Task CtlSocket(Stream ns, string key, PhoneDevice dev)
    {
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await ns.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
        var mine = (ns, new System.Threading.SemaphoreSlim(1, 1));
        lock (_ctl) _ctl[dev.Id] = mine;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => DevicesChanged?.Invoke());
        try
        {
            var hdr = new byte[14];
            while (true)
            {
                await ReadExact(ns, hdr, 2);
                int op = hdr[0] & 0x0F; bool masked = (hdr[1] & 0x80) != 0; long len = hdr[1] & 0x7F;
                if (len == 126) { await ReadExact(ns, hdr, 2); len = (hdr[0] << 8) | hdr[1]; }
                else if (len == 127) return;
                var mask = new byte[4];
                if (masked) await ReadExact(ns, mask, 4);
                var data = new byte[len];
                await ReadExact(ns, data, (int)len);
                if (masked) for (int i = 0; i < data.Length; i++) data[i] ^= mask[i & 3];
                if (op == 8) return;
                dev.LastSeen = DateTime.Now;   // setiap ping dari aplikasi = HP online
                if (op == 1 && data.Length > 4) PhoneNotifs.Handle(dev, Encoding.UTF8.GetString(data));   // JSON notifikasi dari HP
            }
        }
        finally
        {
            lock (_ctl) if (_ctl.TryGetValue(dev.Id, out var cur) && cur.Item1 == ns) _ctl.Remove(dev.Id);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => DevicesChanged?.Invoke());
        }
    }

    static async Task SendFile(Stream ns, string path, string type)
    {
        var fi = new FileInfo(path);
        await ns.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {fi.Length}\r\nContent-Disposition: attachment; filename=\"{fi.Name}\"\r\nConnection: close\r\n\r\n"));
        using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await fs.CopyToAsync(ns);
    }

    // ---------- kamera: WebSocket masuk (HP -> PC) dan MJPEG keluar ----------
    static async Task CamSocket(Stream ns, string key, string device)
    {
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await ns.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
        var hdr = new byte[14];
        while (true)
        {
            await ReadExact(ns, hdr, 2);
            int op = hdr[0] & 0x0F; bool masked = (hdr[1] & 0x80) != 0; long len = hdr[1] & 0x7F;
            if (len == 126) { await ReadExact(ns, hdr, 2); len = (hdr[0] << 8) | hdr[1]; }
            else if (len == 127) { var e = new byte[8]; await ReadExact(ns, e, 8); len = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(e); }
            if (len > (8 << 20)) return;   // bingkai tak wajar
            var mask = new byte[4];
            if (masked) await ReadExact(ns, mask, 4);
            var data = new byte[len];
            await ReadExact(ns, data, (int)len);
            if (masked) for (int i = 0; i < data.Length; i++) data[i] ^= mask[i & 3];
            if (op == 8) return;
            if (op == 9) { await ns.WriteAsync(new byte[] { 0x8A, 0 }); continue; }   // ping -> pong
            if (op is 1 or 2 && data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8) PhoneCam.Push(data, device);
        }
    }

    static async Task Mjpeg(Stream ns)
    {
        await ns.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=frame\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"));
        long last = -1;
        while (true)
        {
            var f = PhoneCam.Latest; var seq = PhoneCam.Seq;
            if (f != null && seq != last)
            {
                last = seq;
                await ns.WriteAsync(Encoding.ASCII.GetBytes($"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {f.Length}\r\n\r\n"));
                await ns.WriteAsync(f);
                await ns.WriteAsync(new byte[] { 13, 10 });
            }
            else await Task.Delay(15);
        }
    }

    static async Task ReadExact(Stream s, byte[] buf, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = await s.ReadAsync(buf.AsMemory(got, n - got));
            if (r <= 0) throw new IOException("terputus");
            got += r;
        }
    }

    async Task Download(Stream ns, int idx)
    {
        var items = Shelf.Items.ToList();
        if (idx < 0 || idx >= items.Count || !File.Exists(items[idx])) { await Send(ns, 404, "text/plain", "not found"); return; }
        var fi = new FileInfo(items[idx]);
        var h = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {fi.Length}\r\nContent-Disposition: attachment; filename*=UTF-8''{Uri.EscapeDataString(fi.Name)}\r\nConnection: close\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(h));
        using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await fs.CopyToAsync(ns);
    }

    async Task Upload(Stream ns, Dictionary<string, string> q, Dictionary<string, string> hdr, byte[] all, int bodyStart, int have)
    {
        if (!hdr.TryGetValue("content-length", out var cls) || !long.TryParse(cls, out var len) || len < 0 || len > MaxUpload)
        { await Send(ns, 403, "text/plain", "ukuran tidak valid"); return; }
        var name = Path.GetFileName(q.GetValueOrDefault("n") ?? "file");
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        if (name.Length == 0) name = "file";
        Directory.CreateDirectory(Shelf.Inbox);
        var dest = Path.Combine(Shelf.Inbox, name);
        for (int i = 1; File.Exists(dest); i++)
            dest = Path.Combine(Shelf.Inbox, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");

        long written = 0;
        try
        {
            using (var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
            {
                int first = (int)Math.Min(have - bodyStart, len);
                if (first > 0) { await fs.WriteAsync(all.AsMemory(bodyStart, first)); written = first; }
                var buf = new byte[81920];
                while (written < len)
                {
                    int n = await ns.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, len - written)));
                    if (n <= 0) throw new IOException("terputus");
                    await fs.WriteAsync(buf.AsMemory(0, n));
                    written += n;
                }
            }
        }
        catch { try { File.Delete(dest); } catch { } throw; }
        Shelf.Add(dest);
        IslandWindow.Instance?.Alert(L.F("Diterima dari HP · {0}", Path.GetFileName(dest)), 6);
        await Send(ns, 200, "text/plain", "ok");
    }

    const string Css = @"body{font:16px system-ui;background:#0b0b0d;color:#fff;margin:0;padding:20px;max-width:520px;margin:auto}h1{font-size:20px}
.b{display:block;background:#2dd4bf;color:#04201c;border:0;border-radius:14px;padding:16px;font-weight:600;font-size:16px;width:100%;margin:8px 0;text-align:center;text-decoration:none;box-sizing:border-box}
.b.g{background:#1c1c20;color:#fff}.f{display:block;background:#1c1c20;border-radius:12px;padding:14px;margin:8px 0;color:#fff;text-decoration:none;word-break:break-all}
.m{color:#999}progress{width:100%;margin-top:10px}";

    // id perangkat dibuat sekali di HP dan disertakan di setiap permintaan (daftar perangkat di PC)
    const string IdJs = "const T=Q('t');let D=localStorage.getItem('iid');if(!D){D=(crypto.randomUUID?crypto.randomUUID():String(Math.random()).slice(2)+Date.now());localStorage.setItem('iid',D)}";

    string Page()
    {
        var sb = new StringBuilder();
        var items = Shelf.Items.ToList();
        for (int i = 0; i < items.Count; i++)
            sb.Append($"<a class=f href=\"/d/{i}?t={Token}\">{WebUtility.HtmlEncode(Path.GetFileName(items[i]))}</a>");
        if (items.Count == 0) sb.Append("<p class=m>Shelf kosong</p>");
        return $@"<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>ZaxLet</title><style>{Css}</style>
<h1>ZaxLet · Sambung HP</h1>
<input id=i type=file multiple hidden><button class=b onclick=i.click()>Kirim file ke PC</button><progress id=p value=0 max=1 hidden></progress><p id=s class=m></p>
<a class='b g' id=cam>Pakai kamera HP</a>
<h1>Shelf di PC</h1>{sb}
<script>
function Q(k){{return new URLSearchParams(location.search).get(k)}}{IdJs}
document.querySelectorAll('a.f').forEach(a=>a.href+='&d='+D);
cam.href='https://'+location.hostname+':{(TlsPort > 0 ? TlsPort : Port + 1)}/cam?t='+T+'&d='+D;
setInterval(()=>fetch('/ping?t='+T+'&d='+D).catch(()=>{{}}),10000);fetch('/ping?t='+T+'&d='+D);
i.onchange=async()=>{{for(const f of i.files){{await new Promise(r=>{{const x=new XMLHttpRequest();p.hidden=false;s.textContent=f.name;
x.upload.onprogress=e=>{{if(e.lengthComputable)p.value=e.loaded/e.total}};x.onload=x.onerror=()=>{{s.textContent=x.status==200?'Terkirim: '+f.name:'Gagal: '+f.name;r()}};
x.open('POST','/u?t='+T+'&d='+D+'&n='+encodeURIComponent(f.name));x.send(f)}})}};setTimeout(()=>location.reload(),900)}};
</script>";
    }


    string CamPage() => $@"<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Kamera ZaxLet</title><style>{Css}
video{{width:100%;border-radius:14px;background:#000;margin:8px 0}}select{{background:#1c1c20;color:#fff;border:0;border-radius:10px;padding:10px;font-size:15px;width:49%}}</style>
<h1>Kamera HP → PC</h1>
<video id=v playsinline muted></video><canvas id=c hidden></canvas>
<button class=b id=go>Mulai</button>
<div><select id=res><option value=480>480p</option><option value=720 selected>720p</option><option value=1080>1080p</option></select>
<select id=fps><option value=10>10 fps</option><option value=15 selected>15 fps</option><option value=24>24 fps</option></select></div>
<button class='b g' id=flip>Ganti kamera depan/belakang</button><p id=st class=m>Siap.</p>
<script>
function Q(k){{return new URLSearchParams(location.search).get(k)}}{IdJs}
let ws,stream,facing='user',run=false,lock;
const x=c.getContext('2d');
async function open(){{
  if(!navigator.mediaDevices){{st.textContent='Browser menolak kamera: buka halaman ini lewat https (terima peringatan sertifikat sekali).';return}}
  const h=+res.value;
  stream=await navigator.mediaDevices.getUserMedia({{video:{{facingMode:facing,height:{{ideal:h}}}},audio:false}});
  v.srcObject=stream;await v.play();
  try{{lock=await navigator.wakeLock.request('screen')}}catch(e){{}}
  ws=new WebSocket('wss://'+location.host+'/cam/ws?t='+T+'&d='+D);ws.binaryType='arraybuffer';
  ws.onopen=()=>{{run=true;st.textContent='Mengirim ke PC…';loop()}};
  ws.onclose=()=>{{run=false;st.textContent='Terputus.'}};
}}
function loop(){{
  if(!run)return;
  if(ws.bufferedAmount>400000){{setTimeout(loop,30);return}}
  c.width=v.videoWidth;c.height=v.videoHeight;x.drawImage(v,0,0);
  c.toBlob(b=>{{if(b&&ws.readyState==1)b.arrayBuffer().then(a=>ws.send(a))}},'image/jpeg',0.7);
  setTimeout(loop,1000/(+fps.value));
}}
function stop(){{run=false;try{{ws.close()}}catch(e){{}}if(stream)stream.getTracks().forEach(t=>t.stop());try{{lock.release()}}catch(e){{}}go.textContent='Mulai'}}
go.onclick=async()=>{{if(run){{stop();return}}try{{go.textContent='Berhenti';await open()}}catch(e){{st.textContent='Gagal: '+e.message;stop()}}}};
flip.onclick=async()=>{{facing=facing=='user'?'environment':'user';if(run){{stop();go.textContent='Berhenti';await open()}}}};
</script>";
}
