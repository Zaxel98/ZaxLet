using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZaxLet;

public sealed class QuotaWindow
{
    public string Label { get; set; }
    public double Pct { get; set; }
    public DateTimeOffset? Reset { get; set; }
}

public sealed class ProviderUsage
{
    public string Name, Plan, Error;
    public string Source = "";   // Claude: "api" (login terminal, real-time) | "desktop" (catatan Claude Desktop) | "" (belum ada sumber)
    public List<QuotaWindow> Windows = new();
    public long TodayTok, WeekTok;
    public double TodayCost, WeekCost;
    public DateTime Updated;
}

/// <summary>
/// Kuota: endpoint tidak resmi yang dipakai alat sejenis (BrimDeck, CodexIsland). Token dibaca dari file login lokal,
/// hanya dikirim ke host asalnya, tidak disimpan. Token/biaya: dihitung dari catatan sesi lokal, inkremental.
/// </summary>
public sealed class UsageService
{
    public static readonly UsageService I = new();
    public readonly ProviderUsage Claude = new() { Name = "Claude" }, Codex = new() { Name = "Codex" };
    public event Action Changed;

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    readonly SemaphoreSlim _gate = new(1, 1);
    DateTime _claudeNext = DateTime.MinValue, _codexNext = DateTime.MinValue;
    TimeSpan _claudeBackoff = TimeSpan.FromMinutes(2);

    // agregat harian dari catatan lokal
    readonly Dictionary<string, long> _off = new();
    readonly Dictionary<DateOnly, (long Tok, double Cost)> _cDay = new(), _xDay = new();
    readonly HashSet<ulong> _seen = new();
    readonly Dictionary<string, long> _codexLast = new();

    sealed class Cache
    {
        public List<QuotaWindow> Claude { get; set; } = new();
        public List<QuotaWindow> Codex { get; set; } = new();
        public string ClaudePlan { get; set; }
        public string CodexPlan { get; set; }
    }

    UsageService()
    {
        // kuota terakhir yang berhasil dibaca: tetap tampil bila endpoint sedang membatasi (429) atau offline
        if (Store.Load<Cache>("usage-cache.json") is { } c)
        {
            Claude.Windows = c.Claude; Claude.Plan = c.ClaudePlan;
            Codex.Windows = c.Codex; Codex.Plan = c.CodexPlan;
            if (c.Claude.Count > 0) Claude.Error = L.T("data lama");
            if (c.Codex.Count > 0) Codex.Error = L.T("data lama");
        }
    }

    void SaveCache() => Store.Save("usage-cache.json", new Cache
    {
        Claude = Claude.Windows, Codex = Codex.Windows, ClaudePlan = Claude.Plan, CodexPlan = Codex.Plan
    });

    /// <summary>Hanya kuota (ringan), untuk pill idle; pemindaian log berat hanya saat tab Usage dibuka.</summary>
    public async Task RefreshQuotaAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            await Task.WhenAll(ClaudeQuota(), CodexQuota());
            Changed?.Invoke();
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _gate.Release(); }
    }

    public async Task RefreshAsync()
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(20))) return;
        try
        {
            await Task.WhenAll(ClaudeQuota(), CodexQuota(), Task.Run(ScanAll));
            Fill(Claude, _cDay);
            Fill(Codex, _xDay);
            Changed?.Invoke();
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _gate.Release(); }
    }

    static void Fill(ProviderUsage p, Dictionary<DateOnly, (long Tok, double Cost)> days)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        p.TodayTok = p.WeekTok = 0; p.TodayCost = p.WeekCost = 0;
        foreach (var (d, v) in days)
        {
            if (d == today) { p.TodayTok += v.Tok; p.TodayCost += v.Cost; }
            if (d > today.AddDays(-7)) { p.WeekTok += v.Tok; p.WeekCost += v.Cost; }
        }
        p.Updated = DateTime.Now;
    }

    // ---------- kuota Claude ----------
    DateTime _renewAt = DateTime.MinValue;

    sealed class UsageException : Exception { public UsageException(string m) : base(m) { } }

    // Alasan yang bisa dipahami pengguna di perangkat lain (tanpa Claude Code / tanpa internet / login berbeda), bukan "gagal" yang buntu
    static string Why(Exception ex) => ex switch
    {
        UsageException u => u.Message,
        HttpRequestException or TaskCanceledException => L.T("tidak bisa terhubung ke server, cek internet atau proxy"),
        JsonException => L.T("file login tidak terbaca (formatnya berbeda)"),
        _ => L.T("gagal membaca kuota") + ": " + ex.GetType().Name,
    };

    // Claude Code dan Codex mengizinkan folder konfigurasi lain lewat variabel lingkungan
    static string ClaudeDir => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(Home, ".claude");
    static string CodexDir => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } d ? d : Path.Combine(Home, ".codex");

    (string token, bool expired) ReadClaudeToken(string path)
    {
        using var cred = JsonDocument.Parse(File.ReadAllText(path));
        if (!cred.RootElement.TryGetProperty("claudeAiOauth", out var o) || !o.TryGetProperty("accessToken", out var at) || at.ValueKind != JsonValueKind.String)
            throw new UsageException(L.T("login terminal: claude auth login (login aplikasi Claude Desktop tidak terbaca)"));
        Claude.Plan = o.TryGetProperty("subscriptionType", out var st) && st.ValueKind == JsonValueKind.String ? Cap(st.GetString()) : null;
        bool expired = o.TryGetProperty("expiresAt", out var ex) && ex.TryGetInt64(out var ms)
                       && DateTimeOffset.FromUnixTimeMilliseconds(ms) < DateTimeOffset.UtcNow.AddMinutes(1);
        return (at.GetString(), expired);
    }

    Task<HttpResponseMessage> SendUsage(string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Authorization = new("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.UserAgent.ParseAdd("claude-code/2.1.121");
        return Http.SendAsync(req);
    }

    // Claude Code memperbarui token OAuth-nya sendiri saat dipakai, jadi jalankan sekali secara tersembunyi
    // (satu prompt Haiku sangat kecil). Jeda 10 menit antar percobaan agar tidak terus memakai kuota.
    async Task<bool> RenewClaudeToken()
    {
        if (DateTime.UtcNow < _renewAt) return false;
        _renewAt = DateTime.UtcNow.AddMinutes(10);
        try
        {
            var exe = Path.Combine(Home, ".local", "bin", "claude.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(File.Exists(exe) ? exe : "claude", "-p \".\" --model haiku")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = Home };
            using var p = System.Diagnostics.Process.Start(psi);
            p.StandardInput.Close();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch (Exception ex) { App.Log(ex); return false; }
    }
    async Task ClaudeQuota()
    {
        if (!Settings.Cur.ClaudeOn || DateTime.UtcNow < _claudeNext) return;
        try
        {
            var path = Path.Combine(ClaudeDir, ".credentials.json");
            if (!File.Exists(path))
            {
                if (!ClaudeFromDesktop()) Claude.Error = L.T("login terminal: claude auth login (login aplikasi Claude Desktop tidak terbaca)");
                return;
            }
            var (token, expired) = ReadClaudeToken(path);
            if (expired && await RenewClaudeToken()) (token, expired) = ReadClaudeToken(path);

            var res = await SendUsage(token);
            if (res.StatusCode == HttpStatusCode.Unauthorized && await RenewClaudeToken())
            {
                res.Dispose();
                res = await SendUsage(ReadClaudeToken(path).token);
            }
            using var resHold = res;
            if (res.StatusCode == (HttpStatusCode)429)
            {
                // hormati Retry-After dari server; tanpa itu mundur eksponensial 2→30 menit. Data lama tetap ditampilkan.
                var wait = res.Headers.RetryAfter?.Delta ?? _claudeBackoff;
                _claudeNext = DateTime.UtcNow + wait;
                _claudeBackoff = TimeSpan.FromMinutes(Math.Min(30, _claudeBackoff.TotalMinutes * 2));
                Claude.Error = L.F("dibatasi (429), coba lagi {0} lagi", UI.Span(wait));
                return;
            }
            if (!res.IsSuccessStatusCode)
            {
                if (res.StatusCode == HttpStatusCode.Unauthorized && ClaudeFromDesktop()) return;   // token kedaluwarsa: angka dari aplikasi Claude Desktop
                _claudeNext = DateTime.UtcNow.AddMinutes(5);
                Claude.Error = res.StatusCode == HttpStatusCode.Unauthorized ? L.T("token kedaluwarsa, buka Claude Code sekali") : $"HTTP {(int)res.StatusCode}";
                return;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var wins = new List<QuotaWindow>();
            Add(wins, doc.RootElement, "five_hour", "5-hour");
            Add(wins, doc.RootElement, "seven_day", "Weekly");
            Claude.Windows = wins;
            Claude.Source = "api";
            Claude.Error = null;
            _claudeNext = DateTime.UtcNow.AddSeconds(Math.Max(1, Settings.Cur.UsageRefreshSec));
            _claudeBackoff = TimeSpan.FromMinutes(2);
            SaveCache();
        }
        catch (Exception ex)
        {
            if (ex is UsageException && ClaudeFromDesktop()) return;   // file login tanpa akun: pakai angka dari aplikasi Claude Desktop
            Claude.Error = Why(ex); _claudeNext = DateTime.UtcNow.AddMinutes(2); App.Log(ex);
        }
    }

    // ---------- login Claude Code sekali jalan (untuk kuota real-time) ----------
    public string LoginStatus = "";
    bool _loginRunning;

    // claude.exe: pemasangan biasa (~/.local/bin), PATH, atau salinan bawaan aplikasi Claude Desktop (terbaru)
    static string FindClaudeExe()
    {
        var own = Path.Combine(Home, ".local", "bin", "claude.exe");
        if (File.Exists(own)) return own;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { var p = Path.Combine(dir.Trim(), "claude.exe"); if (File.Exists(p)) return p; } catch { }
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data");
            return Directory.EnumerateDirectories(data).SelectMany(d => Directory.EnumerateDirectories(Path.Combine(d, "claude-code")))
                .SelectMany(v => Directory.EnumerateDirectories(v)).Select(h => Path.Combine(h, "claude.exe")).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Jalankan "claude auth login --claudeai" di jendela terlihat: browser terbuka otomatis, pengguna tinggal masuk. Jendela terlihat supaya bila
    /// browser gagal terbuka, alamat dan petunjuknya tetap tampak. Setelah berhasil, kuota real-time langsung dibaca.
    /// </summary>
    public async Task LoginClaudeAsync()
    {
        if (_loginRunning) return;
        _loginRunning = true;
        try
        {
            var exe = FindClaudeExe();
            if (exe == null) { LoginStatus = L.T("Claude Code belum terpasang: pasang dari claude.ai/code lalu klik lagi"); return; }
            LoginStatus = L.T("Browser dibuka: masuk dengan akun Claude-mu, lalu kembali ke sini");
            Changed?.Invoke();
            var msg = "ZaxLet: browser akan terbuka. Masuk dengan akun Claude-mu; jendela ini menutup sendiri setelah selesai.";
            var cmd = "Write-Host '" + msg + "'; & '" + exe.Replace("'", "''") + "' auth login --claudeai; exit $LASTEXITCODE";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -Command \"" + cmd + "\"") { UseShellExecute = false, WorkingDirectory = Home };
            using var p = System.Diagnostics.Process.Start(psi);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } LoginStatus = L.T("Login dibatalkan (waktu habis)"); return; }
            if (p.ExitCode == 0)
            {
                LoginStatus = "";
                _claudeNext = DateTime.MinValue; _claudeBackoff = TimeSpan.FromMinutes(2);
                await RefreshQuotaAsync();
            }
            else LoginStatus = L.T("Login belum selesai atau dibatalkan");
        }
        catch (Exception ex) { App.Log(ex); LoginStatus = L.T("Login gagal: ") + ex.Message; }
        finally { _loginRunning = false; Changed?.Invoke(); }
    }

    // Tanpa login Claude Code di terminal: angka kuota yang dicatat aplikasi Claude Desktop sendiri (persen saja, tanpa waktu reset)
    bool ClaudeFromDesktop()
    {
        try
        {
            var f = DesktopUsage.FindFile();
            if (f == null || DesktopUsage.ParseLatest(DesktopUsage.ReadAll(f)) is not { } s) return false;
            var age = DateTimeOffset.Now - DateTimeOffset.FromUnixTimeMilliseconds(s.T);
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            var wins = new List<QuotaWindow>();
            if (s.FiveHour >= 0 && age < TimeSpan.FromHours(5)) wins.Add(new QuotaWindow { Label = "5-hour", Pct = s.FiveHour });   // jendela 5 jam yang lebih tua dari 5 jam sudah pasti berganti
            if (s.Weekly >= 0 && age < TimeSpan.FromDays(7)) wins.Add(new QuotaWindow { Label = "Weekly", Pct = s.Weekly });
            if (wins.Count == 0) return false;
            Claude.Windows = wins;
            Claude.Source = "desktop";
            Claude.Error = L.F("dari aplikasi Claude Desktop, {0} lalu", UI.Span(age));
            _claudeNext = DateTime.UtcNow.AddSeconds(30);   // berkasnya lokal dan murah: begitu Claude Desktop menulis catatan baru, langsung tampil
            SaveCache();
            return true;
        }
        catch (Exception ex) { App.Log(ex); return false; }
    }

    static void Add(List<QuotaWindow> into, JsonElement root, string prop, string label)
    {
        if (!root.TryGetProperty(prop, out var w) || w.ValueKind != JsonValueKind.Object) return;
        if (!w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return;
        DateTimeOffset? reset = null;
        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var d)) reset = d;
        into.Add(new QuotaWindow { Label = label, Pct = u.GetDouble(), Reset = reset });
    }

    // ---------- kuota Codex ----------
    async Task CodexQuota()
    {
        if (!Settings.Cur.CodexOn || DateTime.UtcNow < _codexNext) return;
        try
        {
            var path = Path.Combine(CodexDir, "auth.json");
            if (!File.Exists(path)) { Codex.Error = L.T("belum login Codex"); return; }
            using var cred = JsonDocument.Parse(File.ReadAllText(path));
            if (!cred.RootElement.TryGetProperty("tokens", out var tk) || !tk.TryGetProperty("access_token", out var at))
            { Codex.Error = L.T("login Codex dengan akun ChatGPT"); return; }

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            req.Headers.Authorization = new("Bearer", at.GetString());
            if (tk.TryGetProperty("account_id", out var acc) && acc.ValueKind == JsonValueKind.String) req.Headers.Add("ChatGPT-Account-Id", acc.GetString());
            req.Headers.UserAgent.ParseAdd("codex-cli");
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                _codexNext = DateTime.UtcNow.AddMinutes(5);
                Codex.Error = res.StatusCode == HttpStatusCode.Unauthorized ? L.T("token kedaluwarsa, buka Codex sekali") : $"HTTP {(int)res.StatusCode}";
                return;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Codex.Plan = root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String ? Cap(pt.GetString()) : null;
            var wins = new List<QuotaWindow>();
            if (root.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object)
            {
                CodexWin(wins, rl, "primary_window");
                CodexWin(wins, rl, "secondary_window");
            }
            Codex.Windows = wins;
            Codex.Error = null;
            _codexNext = DateTime.UtcNow.AddSeconds(Math.Max(1, Settings.Cur.UsageRefreshSec));
            SaveCache();
        }
        catch (Exception ex) { Codex.Error = Why(ex); _codexNext = DateTime.UtcNow.AddMinutes(2); App.Log(ex); }
    }

    static void CodexWin(List<QuotaWindow> into, JsonElement rl, string prop)
    {
        if (!rl.TryGetProperty(prop, out var w) || w.ValueKind != JsonValueKind.Object) return;
        if (!w.TryGetProperty("used_percent", out var u) || u.ValueKind != JsonValueKind.Number) return;
        long secs = w.TryGetProperty("limit_window_seconds", out var ls) && ls.ValueKind == JsonValueKind.Number ? ls.GetInt64() : 0;
        DateTimeOffset? reset = w.TryGetProperty("reset_after_seconds", out var ra) && ra.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.Now.AddSeconds(ra.GetInt64()) : null;
        into.Add(new QuotaWindow { Label = WindowName(secs), Pct = u.GetDouble(), Reset = reset });
    }

    static string WindowName(long secs) => secs switch
    {
        18000 => "5-hour",
        604800 => "Weekly",
        2592000 => "30-day",
        > 0 when secs % 86400 == 0 => $"{secs / 86400}-day",
        > 0 when secs % 3600 == 0 => $"{secs / 3600}-hour",
        > 0 => $"{secs / 60}-minute",
        _ => "Limit"
    };

    static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    // ---------- token & biaya dari catatan lokal (inkremental) ----------
    void ScanAll()
    {
        try { ScanDir(Path.Combine(ClaudeDir, "projects"), true); } catch (Exception ex) { App.Log(ex); }
        try { ScanDir(Path.Combine(CodexDir, "sessions"), false); } catch (Exception ex) { App.Log(ex); }
        var cutoff = DateOnly.FromDateTime(DateTime.Now.AddDays(-8));
        foreach (var d in _cDay.Keys.Where(k => k < cutoff).ToList()) _cDay.Remove(d);
        foreach (var d in _xDay.Keys.Where(k => k < cutoff).ToList()) _xDay.Remove(d);
        // pindaian awal menyisakan banyak buffer besar: kembalikan segera
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        Native.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }

    void ScanDir(string root, bool claude)
    {
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.Now.AddDays(-8);
        foreach (var f in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(f);
            if (fi.LastWriteTime < cutoff) continue;
            long off = _off.GetValueOrDefault(f);
            if (fi.Length < off) { off = 0; _codexLast.Remove(f); }
            if (fi.Length == off) continue;
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(off, SeekOrigin.Begin);
            var buf = new byte[1 << 20];
            var carry = new List<byte>();
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                int s = 0;
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] != 10) continue;
                    // bekerja pada byte: baris besar (output tool) tidak pernah dijadikan string, hanya baris usage yang di-parse
                    ReadOnlyMemory<byte> line;
                    if (carry.Count == 0) line = new ReadOnlyMemory<byte>(buf, s, i - s);
                    else { carry.AddRange(new ArraySegment<byte>(buf, s, i - s)); line = carry.ToArray(); carry.Clear(); }
                    s = i + 1;
                    if (claude) ClaudeLine(line); else CodexLine(f, line);
                }
                if (s < n) carry.AddRange(new ArraySegment<byte>(buf, s, n - s));
            }
            _off[f] = fs.Position - carry.Count;
        }
    }

    void ClaudeLine(ReadOnlyMemory<byte> line)
    {
        var sp = line.Span;
        if (sp.IndexOf("\"usage\""u8) < 0 || sp.IndexOf("\"assistant\""u8) < 0) return;
        try
        {
            // pembaca streaming: isi pesan yang besar dilewati tanpa dialokasikan (JsonDocument membuat puluhan MB)
            if (!TryClaude(sp, out var tsText, out var id, out var model, out var inp, out var outp, out var cw, out var cr)) return;
            if (model.StartsWith("<")) return;
            if (id.Length > 0 && !_seen.Add(Fnv(id))) return;
            if (!DateTimeOffset.TryParse(tsText, out var when)) return;
            var (pi, po, pr, pw) = ClaudePrice(model);
            double cost = (inp * pi + outp * po + cw * pw + cr * pr) / 1e6;
            Bump(_cDay, DateOnly.FromDateTime(when.LocalDateTime), inp + outp + cw + cr, cost);
        }
        catch { /* baris rusak: lewati */ }
    }

    static bool TryClaude(ReadOnlySpan<byte> json, out string ts, out string key, out string model,
                          out long inp, out long outp, out long cw, out long cr)
    {
        ts = null; key = ""; model = ""; inp = outp = cw = cr = 0;
        string rid = null, mid = null;
        bool usage = false;
        var rd = new Utf8JsonReader(json);
        if (!rd.Read() || rd.TokenType != JsonTokenType.StartObject) return false;
        while (rd.Read() && rd.TokenType == JsonTokenType.PropertyName)
        {
            if (rd.ValueTextEquals("timestamp"u8)) { rd.Read(); ts = Str(ref rd); }
            else if (rd.ValueTextEquals("requestId"u8)) { rd.Read(); rid = Str(ref rd); }
            else if (rd.ValueTextEquals("message"u8))
            {
                rd.Read();
                if (rd.TokenType != JsonTokenType.StartObject) { rd.Skip(); continue; }
                while (rd.Read() && rd.TokenType == JsonTokenType.PropertyName)
                {
                    if (rd.ValueTextEquals("id"u8)) { rd.Read(); mid = Str(ref rd); }
                    else if (rd.ValueTextEquals("model"u8)) { rd.Read(); model = Str(ref rd) ?? ""; }
                    else if (rd.ValueTextEquals("usage"u8))
                    {
                        rd.Read();
                        if (rd.TokenType != JsonTokenType.StartObject) { rd.Skip(); continue; }
                        usage = true;
                        while (rd.Read() && rd.TokenType == JsonTokenType.PropertyName)
                        {
                            if (rd.ValueTextEquals("input_tokens"u8)) { rd.Read(); inp = Num(ref rd); }
                            else if (rd.ValueTextEquals("output_tokens"u8)) { rd.Read(); outp = Num(ref rd); }
                            else if (rd.ValueTextEquals("cache_creation_input_tokens"u8)) { rd.Read(); cw = Num(ref rd); }
                            else if (rd.ValueTextEquals("cache_read_input_tokens"u8)) { rd.Read(); cr = Num(ref rd); }
                            else { rd.Read(); rd.Skip(); }
                        }
                    }
                    else { rd.Read(); rd.Skip(); }
                }
            }
            else { rd.Read(); rd.Skip(); }
        }
        key = (mid ?? "") + (rid ?? "");
        return usage && ts != null;
    }

    static string Str(ref Utf8JsonReader rd) { if (rd.TokenType == JsonTokenType.String) return rd.GetString(); rd.Skip(); return null; }
    static long Num(ref Utf8JsonReader rd) { if (rd.TokenType == JsonTokenType.Number && rd.TryGetInt64(out var v)) return v; rd.Skip(); return 0; }

    void CodexLine(string file, ReadOnlyMemory<byte> line)
    {
        if (line.Span.IndexOf("token_count"u8) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (!r.TryGetProperty("payload", out var p) || !p.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return;
            if (!info.TryGetProperty("total_token_usage", out var tot) || !info.TryGetProperty("last_token_usage", out var last)) return;
            long total = Lng(tot, "total_tokens");
            if (total <= _codexLast.GetValueOrDefault(file)) return; // event duplikat
            _codexLast[file] = total;
            if (!r.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), out var when)) return;
            long inp = Lng(last, "input_tokens"), cached = Lng(last, "cached_input_tokens"), outp = Lng(last, "output_tokens");
            double cost = (Math.Max(0, inp - cached) * 1.25 + cached * 0.125 + outp * 10.0) / 1e6;
            Bump(_xDay, DateOnly.FromDateTime(when.LocalDateTime), Lng(last, "total_tokens"), cost);
        }
        catch { }
    }

    static void Bump(Dictionary<DateOnly, (long, double)> d, DateOnly day, long tok, double cost)
    {
        d.TryGetValue(day, out var cur);
        d[day] = (cur.Item1 + tok, cur.Item2 + cost);
    }

    static long Lng(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    // harga USD per juta token (masuk, keluar, baca cache, tulis cache) dari daftar harga LiteLLM; model baru perlu ditambah di sini.
    static (double In, double Out, double CacheRead, double CacheWrite) ClaudePrice(string model)
    {
        model = model.ToLowerInvariant();
        if (model.Contains("fable")) return (10, 50, 0.25, 12.5);
        if (model.Contains("opus-5-5")) return (4, 20, 0.2, 5);
        if (model.Contains("opus-5") || model.Contains("opus-4-5")) return (5, 25, 0.5, 6.25);
        if (model.Contains("opus")) return (15, 75, 1.5, 18.75);
        if (model.Contains("haiku-5")) return (0.1, 0.5, 0.01, 0.125);
        if (model.Contains("haiku-4-5")) return (1, 5, 0.1, 1.25);
        if (model.Contains("haiku")) return (0.8, 4, 0.08, 1);
        if (model.Contains("sonnet-5")) return (2, 10, 0.1, 2.5);
        return (3, 15, 0.3, 3.75);
    }

    static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }
}
