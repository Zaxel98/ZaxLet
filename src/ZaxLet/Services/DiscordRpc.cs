using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZaxLet;

/// <summary>
/// Controls Discord via local RPC (named pipe discord-ipc-N): voice channel status, mute, deafen, and disconnect from VC.
/// Requires an app at discord.com/developers (Client ID + Secret, redirect http://localhost); permission is granted once via
/// Discord's "Authorize" dialog. Token is stored in %LOCALAPPDATA%\ZaxLet\discord-token.json.
/// </summary>
public sealed class DiscordRpc
{
    public static readonly DiscordRpc I = new();
    public event Action Changed;
    public bool Connected, InVoice, Muted, Deaf;
    public string Channel = "", Status = L.T("Belum diatur");
    public static bool Configured => !string.IsNullOrWhiteSpace(Settings.Cur.DiscordClientId) && !string.IsNullOrWhiteSpace(Settings.Cur.DiscordSecret);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    const string Redirect = "http://localhost";
    NamedPipeClientStream _pipe;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    TaskCompletionSource<bool> _ready;
    bool _started;
    int _nonce;

    sealed class Token { [System.Text.Json.Serialization.JsonConverter(typeof(SecretConverter))] public string Access { get; set; } [System.Text.Json.Serialization.JsonConverter(typeof(SecretConverter))] public string Refresh { get; set; } public DateTime Expires { get; set; } }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(Loop);
    }

    /// <summary>Delete token (re-authorize on next connection).</summary>
    public void ResetToken() { try { File.Delete(Path.Combine(Settings.Dir, "discord-token.json")); } catch { } }

    void Notify() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    async Task Loop()
    {
        while (true)
        {
            try
            {
                if (!Configured) { Status = L.T("Isi Client ID dan Secret di Pengaturan > Aplikasi"); Notify(); }
                else { await Session(); }
            }
            catch (Exception ex) { Status = ex is TimeoutException ? L.T("Discord belum terbuka") : ex.Message; }
            Connected = InVoice = false; Channel = ""; _chId = ""; lock (_names) { _speak.Clear(); } Speaking = ""; Notify();   // subscriptions drop along with the pipe
            await Task.Delay(5000);
        }
    }

    async Task Session()
    {
        _pipe = null;
        for (int i = 0; i < 10 && _pipe == null; i++)
        {
            var p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await p.ConnectAsync(400); _pipe = p; } catch { p.Dispose(); }
        }
        if (_pipe == null) throw new TimeoutException();
        Task reader = null;
        try
        {
            _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            reader = Task.Run(ReadLoop);
            await Write(0, JsonSerializer.Serialize(new { v = 1, client_id = Settings.Cur.DiscordClientId }));
            await Wait(_ready.Task, 6000);

            var tok = LoadToken();
            if (tok == null || tok.Expires < DateTime.UtcNow.AddMinutes(5)) tok = await Obtain(tok);
            await Command("AUTHENTICATE", new { access_token = tok.Access });
            foreach (var evt in new[] { "VOICE_CHANNEL_SELECT", "VOICE_SETTINGS_UPDATE" }) await Command("SUBSCRIBE", new { }, evt);
            Connected = true; Status = L.T("Tersambung ke Discord");
            await Refresh();
            await reader;   // finishes when Discord closes the pipe
        }
        finally
        {
            var p = Interlocked.Exchange(ref _pipe, null);
            p?.Dispose();   // close pipe first so ReadLoop stops before the next session
            if (reader != null) try { await reader; } catch { }
        }
    }

    static async Task Wait(Task t, int ms) { if (await Task.WhenAny(t, Task.Delay(ms)) != t) throw new TimeoutException(); await t; }

    // ---------- OAuth: code from Discord Authorize dialog -> token ----------
    async Task<Token> Obtain(Token old)
    {
        var s = Settings.Cur;
        Dictionary<string, string> form;
        if (old?.Refresh != null)
            form = new() { ["grant_type"] = "refresh_token", ["refresh_token"] = old.Refresh };
        else
        {
            Status = L.T("Setujui dialog \"Authorize\" di Discord"); Notify();
            var r = await Command("AUTHORIZE", new { client_id = s.DiscordClientId, scopes = new[] { "rpc", "rpc.voice.read", "rpc.voice.write" } }, null, 120000);
            form = new() { ["grant_type"] = "authorization_code", ["code"] = r.GetProperty("data").GetProperty("code").GetString(), ["redirect_uri"] = Redirect };
        }
        form["client_id"] = s.DiscordClientId; form["client_secret"] = s.DiscordSecret;
        using var res = await Http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form));
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            if (old != null) { ResetToken(); return await Obtain(null); }   // refresh rejected: re-authorize
            throw new Exception("Token ditolak Discord (cek Secret dan redirect http://localhost)");
        }
        using var doc = JsonDocument.Parse(body);
        var j = doc.RootElement;
        var t = new Token { Access = j.GetProperty("access_token").GetString(), Refresh = j.GetProperty("refresh_token").GetString(), Expires = DateTime.UtcNow.AddSeconds(j.GetProperty("expires_in").GetInt32()) };
        Store.Save("discord-token.json", t);
        return t;
    }

    static Token LoadToken()
    {
        var t = Store.Load<Token>("discord-token.json");
        if (t != null) Store.Save("discord-token.json", t);   // migration: rewrite old plain token as encrypted
        return t;
    }

    // ---------- pipe protocol: [opcode int32][length int32][JSON] ----------
    async Task Write(int op, string json)
    {
        var b = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + b.Length];
        BitConverter.GetBytes(op).CopyTo(buf, 0);
        BitConverter.GetBytes(b.Length).CopyTo(buf, 4);
        b.CopyTo(buf, 8);
        await _writeLock.WaitAsync();
        try
        {
            var pipe = _pipe;
            if (pipe == null) throw new IOException("pipa ditutup");
            await pipe.WriteAsync(buf);
            await pipe.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    async Task ReadLoop()
    {
        var head = new byte[8];
        try
        {
            while (true)
            {
                await ReadFull(head, 8);
                int op = BitConverter.ToInt32(head, 0), len = BitConverter.ToInt32(head, 4);
                var data = new byte[len];
                await ReadFull(data, len);
                if (op == 2) return;
                if (op == 3) { await Write(4, Encoding.UTF8.GetString(data)); continue; }   // ping -> pong
                using var doc = JsonDocument.Parse(data);
                Handle(doc.RootElement.Clone());
            }
        }
        catch { /* pipe closed */ }
        finally { lock (_pending) { foreach (var p in _pending.Values) p.TrySetCanceled(); _pending.Clear(); } }
    }

    async Task ReadFull(byte[] buf, int n)
    {
        int got = 0;
        while (got < n)
        {
            var pipe = _pipe;
            if (pipe == null) throw new IOException("pipa ditutup");
            int r = await pipe.ReadAsync(buf.AsMemory(got, n - got));
            if (r <= 0) throw new IOException("pipa ditutup");
            got += r;
        }
    }

    void Handle(JsonElement m)
    {
        var evt = m.TryGetProperty("evt", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        if (evt == "READY") { _ready?.TrySetResult(true); return; }
        var nonce = m.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        TaskCompletionSource<JsonElement> tcs = null;
        if (nonce != null) { lock (_pending) _pending.Remove(nonce, out tcs); }
        if (tcs != null)
        {
            if (evt == "ERROR") tcs.TrySetException(new Exception(m.GetProperty("data").GetProperty("message").GetString()));
            else tcs.TrySetResult(m);
            return;
        }
        if (evt is "SPEAKING_START" or "SPEAKING_STOP" && m.TryGetProperty("data", out var sd) && sd.TryGetProperty("user_id", out var su) && su.GetString() is { } uid)
        {
            bool needRefresh = false;
            lock (_names)
            {
                if (evt == "SPEAKING_START") _speak.Add(uid); else _speak.Remove(uid);
                if (!_names.ContainsKey(uid) && DateTime.UtcNow - _namesAt > TimeSpan.FromSeconds(5)) { _namesAt = DateTime.UtcNow; needRefresh = true; }   // someone new joined: re-fetch name list
            }
            if (needRefresh) _ = Refresh();
            UpdateSpeaking();
        }
        else if (evt == "VOICE_CHANNEL_SELECT") _ = Refresh();
        else if (evt == "VOICE_SETTINGS_UPDATE" && m.TryGetProperty("data", out var d)) ApplySettings(d);
    }

    async Task<JsonElement> Command(string cmd, object args, string evt = null, int timeoutMs = 8000)
    {
        var nonce = Interlocked.Increment(ref _nonce).ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[nonce] = tcs;
        try
        {
            await Write(1, JsonSerializer.Serialize(new { cmd, args, evt, nonce }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
            await Wait(tcs.Task, timeoutMs);
            return tcs.Task.Result;
        }
        finally { lock (_pending) _pending.Remove(nonce); }
    }

    void ApplySettings(JsonElement d)
    {
        if (d.TryGetProperty("mute", out var mu)) Muted = mu.GetBoolean();
        if (d.TryGetProperty("deaf", out var de)) Deaf = de.GetBoolean();
        Notify();
    }

    async Task Refresh()
    {
        try
        {
            var vc = (await Command("GET_SELECTED_VOICE_CHANNEL", new { })).GetProperty("data");
            InVoice = vc.ValueKind == JsonValueKind.Object;
            Channel = InVoice && vc.TryGetProperty("name", out var nm) ? nm.GetString() : "";
            await TrackSpeakers(InVoice ? vc : default);
            var vs = (await Command("GET_VOICE_SETTINGS", new { })).GetProperty("data");
            ApplySettings(vs);
        }
        catch (Exception ex) { Status = ex.Message; }
        Notify();
    }

    // ---------- who is currently speaking (SPEAKING_START/STOP events under "rpc" scope) ----------
    string _chId = "";
    DateTime _namesAt;
    readonly Dictionary<string, string> _names = new();   // user id -> name in this channel
    readonly HashSet<string> _speak = new();
    public string Speaking = "";

    void UpdateSpeaking()
    {
        lock (_names) Speaking = string.Join(", ", _speak.Select(i => _names.GetValueOrDefault(i, "?")));
        Notify();
    }

    async Task TrackSpeakers(JsonElement vc)
    {
        var id = vc.ValueKind == JsonValueKind.Object && vc.TryGetProperty("id", out var ci) ? ci.GetString() : "";
        lock (_names)
        {
            _names.Clear();
            if (vc.ValueKind == JsonValueKind.Object && vc.TryGetProperty("voice_states", out var vss) && vss.ValueKind == JsonValueKind.Array)
                foreach (var v in vss.EnumerateArray())
                    if (v.TryGetProperty("user", out var u) && u.TryGetProperty("id", out var uid))
                        _names[uid.GetString()] = v.TryGetProperty("nick", out var nk) && nk.ValueKind == JsonValueKind.String && nk.GetString().Length > 0 ? nk.GetString() : u.TryGetProperty("username", out var un) ? un.GetString() : "?";
        }
        if (id == _chId) return;
        await Subscribe(_chId, false);
        lock (_names) _speak.Clear();
        _chId = id;
        await Subscribe(id, true);
        UpdateSpeaking();
    }

    async Task Subscribe(string channel, bool on)
    {
        if (string.IsNullOrEmpty(channel)) return;
        foreach (var evt in new[] { "SPEAKING_START", "SPEAKING_STOP" })
            try { await Command(on ? "SUBSCRIBE" : "UNSUBSCRIBE", new { channel_id = channel }, evt); }
            catch (Exception ex) { App.Log(ex); }   // e.g., insufficient permissions: the rest keeps working
    }

    // ---------- UI actions ----------
    public async Task SetMute(bool on) { try { await Command("SET_VOICE_SETTINGS", new { mute = on }); } catch (Exception ex) { Status = ex.Message; Notify(); } }
    public async Task SetDeaf(bool on) { try { await Command("SET_VOICE_SETTINGS", new { deaf = on }); } catch (Exception ex) { Status = ex.Message; Notify(); } }
    public async Task Leave() { try { await Command("SELECT_VOICE_CHANNEL", new { channel_id = (string)null, force = true }); await Refresh(); } catch (Exception ex) { Status = ex.Message; Notify(); } }
}
