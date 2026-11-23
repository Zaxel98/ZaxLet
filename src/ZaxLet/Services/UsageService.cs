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
    public string Source = "";   // Claude: "api" (terminal login, real-time) | "desktop" (Claude Desktop logs) | "" (no source yet)
    public List<QuotaWindow> Windows = new();
    public long TodayTok, WeekTok, MonthTok;
    public double TodayCost, WeekCost, MonthCost;
    public DateTime Updated;
}

/// <summary>Usage table period boundaries: week starts Monday, month starts on the 1st (local time).</summary>
public static class UsagePeriod
{
    public static DateTime WeekStart(DateTime now) => now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
    public static DateTime MonthStart(DateTime now) => new(now.Year, now.Month, 1);
    /// <summary>Oldest date still counted (minus one day as timezone buffer).</summary>
    public static DateTime Oldest(DateTime now) { var a = WeekStart(now); var b = MonthStart(now); return (a < b ? a : b).AddDays(-1); }
}

/// <summary>
/// Quota: unofficial endpoints used by similar tools (BrimDeck, CodexIsland). Tokens are read from local login files,
/// sent only to their origin host, never stored. Tokens/cost: calculated from local session logs, incrementally.
/// </summary>
public sealed class UsageService
{
    public static readonly UsageService I = new();
    public readonly ProviderUsage Claude = new() { Name = "Claude" }, Codex = new() { Name = "Codex" };
    public event Action Changed;

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    const double AlertSec = 8;
    const double ResetSlackMin = 2;   // a few seconds of jitter tolerance on Codex reset_after_seconds
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<string, (int Level, DateTimeOffset? Reset)> _alerted = new();
    readonly Dictionary<string, BurnState> _burn = new();
    sealed class BurnState { public readonly List<BurnSample> Samples = new(); public DateTimeOffset? Reset; }
    DateTime _claudeNext = DateTime.MinValue, _codexNext = DateTime.MinValue;
    TimeSpan _claudeBackoff = TimeSpan.FromMinutes(2);

    // daily aggregates from local logs
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
        // also read by Middleman (hydrogen-worker skill) to pick a stage: last read time per source, so stale data is detected
        public List<QuotaWindow> Agy { get; set; } = new();
        public string ClaudeSource { get; set; }
        public DateTimeOffset? ClaudeAt { get; set; }
        public DateTimeOffset? AgyAt { get; set; }
    }
    DateTimeOffset? _claudeAt, _agyAt;
    readonly object _cacheLock = new();

    UsageService()
    {
        // last successfully read quota: kept visible if the endpoint is rate-limiting (429) or offline
        if (Store.Load<Cache>("usage-cache.json") is { } c)
        {
            Claude.Windows = c.Claude ?? new(); Claude.Plan = c.ClaudePlan; _claudeAt = c.ClaudeAt;
            Codex.Windows = c.Codex ?? new(); Codex.Plan = c.CodexPlan;
            if (Claude.Windows.Count > 0) Claude.Error = L.T("data lama");
            if (Codex.Windows.Count > 0) Codex.Error = L.T("data lama");
        }
    }

    // locked: Claude, Codex and Antigravity quotas are saved from parallel tasks, while Store.Save uses a single .tmp file
    void SaveCache() { lock (_cacheLock) Store.Save("usage-cache.json", new Cache
    {
        Claude = Claude.Windows, Codex = Codex.Windows, ClaudePlan = Claude.Plan, CodexPlan = Codex.Plan,
        Agy = Agy.Windows, ClaudeSource = Claude.Source, ClaudeAt = _claudeAt, AgyAt = _agyAt
    }); }

    /// <summary>Quota only (lightweight), for idle pill; heavy log scanning only when the Usage tab is opened.</summary>
    public async Task RefreshQuotaAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            await Task.WhenAll(ClaudeQuota(), CodexQuota(), AgyQuota());
            Changed?.Invoke();
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _gate.Release(); }
    }

    /// <summary>Antigravity quota is read every minute in the background (localhost only, cheap), without waiting for the AI tab or idle pill. Claude/Codex are excluded: their servers rate-limit with 429.</summary>
    public void StartAgyLoop() => _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));   // AgyQuota itself enforces a 60s interval (_agyNext); short tick so it doesn't slip to 2 minutes
            if (!Settings.Cur.CodexOn || Settings.Cur.Slot2 != "antigravity" || !await _gate.WaitAsync(0)) continue;
            try { await AgyQuota(); Changed?.Invoke(); }
            catch (Exception ex) { App.Log(ex); }
            finally { _gate.Release(); }
        }
    });

    public async Task RefreshAsync()
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(20))) return;
        try
        {
            await Task.WhenAll(ClaudeQuota(), CodexQuota(), AgyQuota(), Task.Run(ScanAll), Task.Run(ScanAgy));
            Fill(Claude, _cDay);
            Fill(Codex, _xDay);
            Changed?.Invoke();
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _gate.Release(); }
    }

    void ScanAgy()
    {
        if (!Settings.Cur.CodexOn || Settings.Cur.Slot2 != "antigravity") return;
        (Agy.TodayTok, Agy.WeekTok, Agy.MonthTok) = AgyLog.Scan();
        Agy.Updated = DateTime.Now;
    }

    static void Fill(ProviderUsage p, Dictionary<DateOnly, (long Tok, double Cost)> days)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        p.TodayTok = p.WeekTok = p.MonthTok = 0; p.TodayCost = p.WeekCost = p.MonthCost = 0;
        var wk = DateOnly.FromDateTime(UsagePeriod.WeekStart(DateTime.Now)); var mo = DateOnly.FromDateTime(UsagePeriod.MonthStart(DateTime.Now));
        foreach (var (d, v) in days)
        {
            if (d == today) { p.TodayTok += v.Tok; p.TodayCost += v.Cost; }
            if (d >= wk) { p.WeekTok += v.Tok; p.WeekCost += v.Cost; }
            if (d >= mo) { p.MonthTok += v.Tok; p.MonthCost += v.Cost; }
        }
        p.Updated = DateTime.Now;
    }

    static void Ui(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    static string WinLabel(string label) => label switch
    {
        "5-hour" => L.T("5 jam"),
        "Weekly" => L.T("Mingguan"),
        _ => label
    };

    public BurnProjection Burn(string providerName, QuotaWindow w)
    {
        if (w == null || string.IsNullOrEmpty(w.Label) || w.Reset is not { } reset) return default;
        lock (_burn)
        {
            if (!_burn.TryGetValue(providerName + "|" + w.Label, out var st)) return default;
            return BurnRate.Project(st.Samples, reset - DateTimeOffset.Now);
        }
    }

    void RecordBurn(ProviderUsage p)
    {
        var now = DateTimeOffset.Now;
        lock (_burn)
        {
            foreach (var w in p.Windows)
            {
                if (w == null || string.IsNullOrEmpty(w.Label) || double.IsNaN(w.Pct) || w.Pct < 0) continue;
                if (w.Reset is { } r && r < now) continue;
                var key = p.Name + "|" + w.Label;
                if (!_burn.TryGetValue(key, out var st)) _burn[key] = st = new BurnState();
                BurnRate.Record(st.Samples, now, w.Pct, w.Reset, ref st.Reset);
            }
        }
    }

    void CheckAlerts(ProviderUsage p)
    {
        if (p.Windows.Count == 0) return;
        RecordBurn(p);
        var s = Settings.Cur;
        var now = DateTimeOffset.Now;
        string msg = null;
        int bestLevel = 0;
        lock (_alerted)
        {
            foreach (var w in p.Windows)
            {
                if (w == null || string.IsNullOrEmpty(w.Label) || double.IsNaN(w.Pct) || w.Pct < 0) continue;
                if (w.Reset is { } r && r < now) continue;
                int level = w.Pct >= s.CritAt ? 2 : w.Pct >= s.WarnAt ? 1 : 0;
                var key = p.Name + "|" + w.Label;
                _alerted.TryGetValue(key, out var prev);
                bool resetChanged = prev.Reset != null && w.Reset != null
                    ? Math.Abs((w.Reset.Value - prev.Reset.Value).TotalMinutes) > ResetSlackMin
                    : prev.Reset != w.Reset;
                if (resetChanged) prev = default;
                if (level == 0)
                {
                    if (prev.Level != 0 || resetChanged) _alerted[key] = (0, w.Reset);
                    continue;
                }
                if (level > prev.Level)
                {
                    _alerted[key] = (level, w.Reset);
                    if (level >= bestLevel)
                    {
                        bestLevel = level;
                        msg = L.F("{0} · {1}: kuota {2}% terpakai", p.Name, WinLabel(w.Label), Math.Round(w.Pct));
                    }
                }
            }
        }
        if (msg != null) Ui(() => IslandWindow.Instance?.Alert(msg, AlertSec));
    }

    // ---------- Claude quota ----------
    DateTime _renewAt = DateTime.MinValue;

    sealed class UsageException : Exception { public UsageException(string m) : base(m) { } }

    // Reasons understandable to users on other devices (no Claude Code / no internet / different login), rather than a dead-end "failed"
    static string Why(Exception ex) => ex switch
    {
        UsageException u => u.Message,
        HttpRequestException or TaskCanceledException => L.T("tidak bisa terhubung ke server, cek internet atau proxy"),
        JsonException => L.T("file login tidak terbaca (formatnya berbeda)"),
        _ => L.T("gagal membaca kuota") + ": " + ex.GetType().Name,
    };

    // Claude Code and Codex allow custom config folders via environment variables
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

    // Claude Code refreshes its own OAuth token when used, so run it once hidden
    // (a tiny Haiku prompt). 10-minute cooldown between attempts so it doesn't keep burning quota.
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
                // respect Retry-After from server; otherwise exponential backoff 2→30 minutes. Old data remains displayed.
                var wait = res.Headers.RetryAfter?.Delta ?? _claudeBackoff;
                _claudeNext = DateTime.UtcNow + wait;
                _claudeBackoff = TimeSpan.FromMinutes(Math.Min(30, _claudeBackoff.TotalMinutes * 2));
                Claude.Error = L.F("dibatasi (429), coba lagi {0} lagi", UI.Span(wait));
                return;
            }
            if (!res.IsSuccessStatusCode)
            {
                if (res.StatusCode == HttpStatusCode.Unauthorized && ClaudeFromDesktop()) return;   // expired token: numbers from Claude Desktop app
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
            lock (_cacheLock) _claudeAt = DateTimeOffset.Now;
            Claude.Error = null;
            _claudeNext = DateTime.UtcNow.AddSeconds(Math.Max(1, Settings.Cur.UsageRefreshSec));
            _claudeBackoff = TimeSpan.FromMinutes(2);
            SaveCache();
            CheckAlerts(Claude);
        }
        catch (Exception ex)
        {
            if (ex is UsageException && ClaudeFromDesktop()) return;   // login file without account: use numbers from Claude Desktop app
            Claude.Error = Why(ex); _claudeNext = DateTime.UtcNow.AddMinutes(2); App.Log(ex);
        }
    }

    // ---------- one-click Claude Code login (for real-time quota) ----------
    public string LoginStatus = "";
    bool _loginRunning;

    // claude.exe: regular install (~/.local/bin), PATH, or bundled copy in Claude Desktop app (newest)
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
    /// Run "claude auth login --claudeai" in a visible window: browser opens automatically, user just signs in. Window is visible so if
    /// the browser fails to open, the URL and instructions remain visible. Once successful, real-time quota is read immediately.
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

    // Without Claude Code login in terminal: quota numbers recorded by Claude Desktop app itself (percent only, no reset time)
    bool ClaudeFromDesktop()
    {
        try
        {
            var f = DesktopUsage.FindFile();
            if (f == null || DesktopUsage.ParseLatest(DesktopUsage.ReadAll(f)) is not { } s) return false;
            var age = DateTimeOffset.Now - DateTimeOffset.FromUnixTimeMilliseconds(s.T);
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            var wins = new List<QuotaWindow>();
            if (s.FiveHour >= 0 && age < TimeSpan.FromHours(5)) wins.Add(new QuotaWindow { Label = "5-hour", Pct = s.FiveHour });   // a 5-hour window older than 5 hours has definitely rolled over
            if (s.Weekly >= 0 && age < TimeSpan.FromDays(7)) wins.Add(new QuotaWindow { Label = "Weekly", Pct = s.Weekly });
            if (wins.Count == 0) return false;
            Claude.Windows = wins;
            Claude.Source = "desktop";
            lock (_cacheLock) _claudeAt = DateTimeOffset.Now;
            Claude.Error = L.F("dari aplikasi Claude Desktop, {0} lalu", UI.Span(age));
            _claudeNext = DateTime.UtcNow.AddSeconds(30);   // file is local and cheap: as soon as Claude Desktop writes a new entry, show it immediately
            SaveCache();
            CheckAlerts(Claude);
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

    // ---------- Antigravity quota: the app's local language server (127.0.0.1, CSRF token from its daemon file) ----------
    public readonly ProviderUsage Agy = new() { Name = "Antigravity" };
    DateTime _agyNext = DateTime.MinValue;

    async Task AgyQuota()
    {
        if (!Settings.Cur.CodexOn || Settings.Cur.Slot2 != "antigravity" || DateTime.UtcNow < _agyNext) return;
        try
        {
            string port = null, tok = null;
            var dir = Path.Combine(Home, ".gemini", "antigravity", "daemon");
            if (Directory.Exists(dir))
                foreach (var f in new DirectoryInfo(dir).GetFiles("hub_*.json").OrderByDescending(x => x.LastWriteTimeUtc))
                {
                    try
                    {
                        using var d = JsonDocument.Parse(File.ReadAllText(f.FullName));
                        using var p = System.Diagnostics.Process.GetProcessById(d.RootElement.GetProperty("pid").GetInt32());   // stale file from a dead session: skip
                        if (p.HasExited) continue;
                        port = d.RootElement.GetProperty("httpPort").GetInt32().ToString();
                        tok = d.RootElement.GetProperty("csrfToken").GetString();
                        break;
                    }
                    catch { }
                }
            if (port == null) { Agy.Error = L.T("buka Antigravity"); _agyNext = DateTime.UtcNow.AddSeconds(30); return; }
            async Task<JsonDocument> Rpc(string method)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/{method}")
                { Content = new StringContent("{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}", Encoding.UTF8, "application/json") };
                req.Headers.Add("X-Codeium-Csrf-Token", tok);
                req.Headers.Add("Connect-Protocol-Version", "1");
                using var res = await Http.SendAsync(req);
                if (!res.IsSuccessStatusCode) throw new UsageException($"HTTP {(int)res.StatusCode}");
                return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            }
            using (var us = await Rpc("GetUserStatus"))
                Agy.Plan = us.RootElement.TryGetProperty("userStatus", out var u) && u.TryGetProperty("planStatus", out var ps) && ps.TryGetProperty("planInfo", out var pi) && pi.TryGetProperty("planName", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null;
            // model groups, each group has weekly and/or 5h buckets (same as Antigravity Usage popup)
            var wins = new List<QuotaWindow>();
            using (var qs = await Rpc("RetrieveUserQuotaSummary"))
                if (qs.RootElement.TryGetProperty("response", out var rsp) && rsp.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var g in groups.EnumerateArray())
                    {
                        var gn = g.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : "?";
                        if (gn.Contains("Claude and GPT", StringComparison.OrdinalIgnoreCase)) continue;
                        gn = gn.Replace("Gemini Models", "Gemini");
                        if (!g.TryGetProperty("buckets", out var bs) || bs.ValueKind != JsonValueKind.Array) continue;
                        foreach (var b in bs.EnumerateArray())
                        {
                            if (!b.TryGetProperty("remainingFraction", out var rf) || rf.ValueKind != JsonValueKind.Number) continue;
                            var win = b.TryGetProperty("window", out var wn) && wn.ValueKind == JsonValueKind.String ? wn.GetString() : "";
                            DateTimeOffset? reset = b.TryGetProperty("resetTime", out var rt) && rt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(rt.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var rd) ? rd : null;
                            wins.Add(new QuotaWindow { Label = gn + " · " + (win == "5h" ? "5h" : "weekly"), Pct = Math.Clamp((1 - rf.GetDouble()) * 100, 0, 100), Reset = reset });
                        }
                    }
            Agy.Windows = wins;
            Agy.Error = wins.Count == 0 ? L.T("kuota tidak ditemukan di respons Antigravity") : null;
            if (wins.Count > 0) { lock (_cacheLock) _agyAt = DateTimeOffset.Now; SaveCache(); }
            _agyNext = DateTime.UtcNow.AddSeconds(Math.Clamp(Settings.Cur.UsageRefreshSec, 1, 60));   // local and cheap: at most every minute, Claude/Codex interval does not apply
            CheckAlerts(Agy);
        }
        catch (Exception ex) { Agy.Error = Why(ex); _agyNext = DateTime.UtcNow.AddMinutes(2); App.Log(ex); }
    }

    // ---------- Codex quota ----------
    async Task CodexQuota()
    {
        if (!Settings.Cur.CodexOn || Settings.Cur.Slot2 != "codex" || DateTime.UtcNow < _codexNext) return;
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
            CheckAlerts(Codex);
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

    // ---------- tokens & cost from local logs (incremental) ----------
    void ScanAll()
    {
        try { ScanDir(Path.Combine(ClaudeDir, "projects"), true); } catch (Exception ex) { App.Log(ex); }
        try { ScanDir(Path.Combine(CodexDir, "sessions"), false); } catch (Exception ex) { App.Log(ex); }
        var cutoff = DateOnly.FromDateTime(UsagePeriod.Oldest(DateTime.Now));
        foreach (var d in _cDay.Keys.Where(k => k < cutoff).ToList()) _cDay.Remove(d);
        foreach (var d in _xDay.Keys.Where(k => k < cutoff).ToList()) _xDay.Remove(d);
        // initial scan leaves many large buffers: return immediately
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        Native.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }

    void ScanDir(string root, bool claude)
    {
        if (!Directory.Exists(root)) return;
        var cutoff = UsagePeriod.Oldest(DateTime.Now);
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
                    // work on bytes: large lines (tool output) are never turned into strings, only usage lines are parsed
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
            // streaming reader: large message bodies are skipped without allocation (JsonDocument allocates tens of MB)
            if (!TryClaude(sp, out var tsText, out var id, out var model, out var inp, out var outp, out var cw, out var cr, out var cw1, out var fast)) return;
            if (model.StartsWith("<")) return;
            if (id.Length > 0 && !_seen.Add(Fnv(id))) return;
            if (!DateTimeOffset.TryParse(tsText, out var when)) return;
            var (pi, po, pr, pw5, pw1) = ClaudePrice(model, inp + cw + cr);
            cw1 = Math.Clamp(cw1, 0, cw);
            double cost = (inp * pi + outp * po + cr * pr + (cw - cw1) * pw5 + cw1 * pw1) / 1e6;
            if (fast) cost *= 2;   // fast mode: exactly 2x standard price (Fable 5.2, Opus 5.5, Opus 5/4.8)
            Bump(_cDay, DateOnly.FromDateTime(when.LocalDateTime), inp + outp + cw + cr, cost);
        }
        catch { /* malformed line: skip */ }
    }

    static bool TryClaude(ReadOnlySpan<byte> json, out string ts, out string key, out string model,
                          out long inp, out long outp, out long cw, out long cr, out long cw1, out bool fast)
    {
        ts = null; key = ""; model = ""; inp = outp = cw = cr = cw1 = 0; fast = false;
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
                            else if (rd.ValueTextEquals("speed"u8)) { rd.Read(); fast = Str(ref rd) == "fast"; }
                            else if (rd.ValueTextEquals("cache_creation"u8))   // cache write breakdown: 1-hour tier is priced at 2x (5-minute 1.25x)
                            {
                                rd.Read();
                                if (rd.TokenType != JsonTokenType.StartObject) { rd.Skip(); continue; }
                                while (rd.Read() && rd.TokenType == JsonTokenType.PropertyName)
                                {
                                    if (rd.ValueTextEquals("ephemeral_1h_input_tokens"u8)) { rd.Read(); cw1 = Num(ref rd); }
                                    else { rd.Read(); rd.Skip(); }
                                }
                            }
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
            if (total <= _codexLast.GetValueOrDefault(file)) return; // duplicate event
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

    // Official Anthropic API pricing (platform.claude.com/docs/en/about-claude/pricing, checked 2026-11-21), USD per million tokens:
    // input, output, cache read, 5-min cache write, 1-hour cache write. New models need to be added here.
    // Not yet counted: 1.1x multiplier for inference_geo "us", Batch API discount, and web search cost.
    static (double In, double Out, double CacheRead, double Write5m, double Write1h) ClaudePrice(string model, long promptTokens)
    {
        model = model.ToLowerInvariant();
        if (model.Contains("fable-5-2") || model.Contains("mythos-5-2")) return (8, 40, 0.2, 10, 16);
        if (model.Contains("fable-5-1") || model.Contains("mythos-5-1")) return (10, 50, 0.25, 12.5, 20);
        if (model.Contains("fable") || model.Contains("mythos")) return (10, 50, 1, 12.5, 20);
        if (model.Contains("opus-5-5")) return (4, 20, 0.2, 5, 8);
        if (model.Contains("opus-4-1") || model.Contains("opus-4-2") || model.Contains("3-opus") || model.EndsWith("opus-4")) return (15, 75, 1.5, 18.75, 30);   // Opus 4.1, 4, 3
        if (model.Contains("opus")) return (5, 25, 0.5, 6.25, 10);   // Opus 5, 4.8, 4.7, 4.6, 4.5
        if (model.Contains("haiku-5")) return promptTokens > 100_000 ? (0.5, 2.5, 0.05, 0.625, 1) : (0.1, 0.5, 0.01, 0.125, 0.2);   // higher rate when prompt > 100k tokens
        if (model.Contains("haiku-4-5")) return (1, 5, 0.1, 1.25, 2);
        if (model.Contains("haiku")) return (0.8, 4, 0.08, 1, 1.6);
        if (model.Contains("sonnet-5-5")) return (2, 10, 0.1, 2.5, 4);
        if (model.Contains("sonnet-5")) return (2, 10, 0.2, 2.5, 4);
        return (3, 15, 0.3, 3.75, 6);   // Sonnet 4.x and other models
    }

    static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }
}
