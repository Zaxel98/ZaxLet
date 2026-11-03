using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZaxLet;

/// <summary>
/// Kontrol Discord lewat RPC lokal (named pipe discord-ipc-N): status voice channel, mute, deafen, dan keluar dari VC.
/// Butuh aplikasi di discord.com/developers (Client ID + Secret, redirect http://localhost); izin diberikan sekali lewat
/// dialog "Authorize" di Discord. Token disimpan di %LOCALAPPDATA%\ZaxLet\discord-token.json.
/// </summary>
public sealed class DiscordRpc
{
    public static readonly DiscordRpc I = new();
    public event Action Changed;
    public bool Connected, InVoice, Muted, Deaf;
    public string Channel = "", Status = "Belum diatur";
    public static bool Configured => !string.IsNullOrWhiteSpace(Settings.Cur.DiscordClientId) && !string.IsNullOrWhiteSpace(Settings.Cur.DiscordSecret);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    const string Redirect = "http://localhost";
    NamedPipeClientStream _pipe;
    readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    TaskCompletionSource<bool> _ready;
    bool _started;
    int _nonce;

    sealed class Token { public string Access { get; set; } public string Refresh { get; set; } public DateTime Expires { get; set; } }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(Loop);
    }

    /// <summary>Hapus token (otorisasi ulang di sambungan berikutnya).</summary>
    public void ResetToken() { try { File.Delete(Path.Combine(Settings.Dir, "discord-token.json")); } catch { } }

    void Notify() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    async Task Loop()
    {
        while (true)
        {
            try
            {
                if (!Configured) { Status = "Isi Client ID dan Secret di Pengaturan > Aplikasi"; Notify(); }
                else { await Session(); }
            }
            catch (Exception ex) { Status = ex is TimeoutException ? "Discord belum terbuka" : ex.Message; }
            Connected = InVoice = false; Channel = ""; Notify();
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
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(ReadLoop);
        await Write(0, JsonSerializer.Serialize(new { v = 1, client_id = Settings.Cur.DiscordClientId }));
        await Wait(_ready.Task, 6000);

        var tok = LoadToken();
        if (tok == null || tok.Expires < DateTime.UtcNow.AddMinutes(5)) tok = await Obtain(tok);
        await Command("AUTHENTICATE", new { access_token = tok.Access });
        foreach (var evt in new[] { "VOICE_CHANNEL_SELECT", "VOICE_SETTINGS_UPDATE" }) await Command("SUBSCRIBE", new { }, evt);
        Connected = true; Status = "Tersambung ke Discord";
        await Refresh();
        await reader;   // selesai saat Discord menutup pipa
    }

    static async Task Wait(Task t, int ms) { if (await Task.WhenAny(t, Task.Delay(ms)) != t) throw new TimeoutException(); await t; }

    // ---------- OAuth: kode dari dialog Authorize di Discord -> token ----------
    async Task<Token> Obtain(Token old)
    {
        var s = Settings.Cur;
        Dictionary<string, string> form;
        if (old?.Refresh != null)
            form = new() { ["grant_type"] = "refresh_token", ["refresh_token"] = old.Refresh };
        else
        {
            Status = "Setujui dialog \"Authorize\" di Discord"; Notify();
            var r = await Command("AUTHORIZE", new { client_id = s.DiscordClientId, scopes = new[] { "rpc", "rpc.voice.read", "rpc.voice.write" } }, null, 120000);
            form = new() { ["grant_type"] = "authorization_code", ["code"] = r.GetProperty("data").GetProperty("code").GetString(), ["redirect_uri"] = Redirect };
        }
        form["client_id"] = s.DiscordClientId; form["client_secret"] = s.DiscordSecret;
        using var res = await Http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form));
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            if (old != null) { ResetToken(); return await Obtain(null); }   // refresh ditolak: otorisasi ulang
            throw new Exception("Token ditolak Discord (cek Secret dan redirect http://localhost)");
        }
        using var doc = JsonDocument.Parse(body);
        var j = doc.RootElement;
        var t = new Token { Access = j.GetProperty("access_token").GetString(), Refresh = j.GetProperty("refresh_token").GetString(), Expires = DateTime.UtcNow.AddSeconds(j.GetProperty("expires_in").GetInt32()) };
        Store.Save("discord-token.json", t);
        return t;
    }

    static Token LoadToken() => Store.Load<Token>("discord-token.json");

    // ---------- protokol pipa: [opcode int32][panjang int32][JSON] ----------
    async Task Write(int op, string json)
    {
        var b = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + b.Length];
        BitConverter.GetBytes(op).CopyTo(buf, 0);
        BitConverter.GetBytes(b.Length).CopyTo(buf, 4);
        b.CopyTo(buf, 8);
        await _pipe.WriteAsync(buf);
        await _pipe.FlushAsync();
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
        catch { /* pipa tertutup */ }
        finally { foreach (var p in _pending.Values) p.TrySetCanceled(); _pending.Clear(); }
    }

    async Task ReadFull(byte[] buf, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = await _pipe.ReadAsync(buf.AsMemory(got, n - got));
            if (r <= 0) throw new IOException("pipa ditutup");
            got += r;
        }
    }

    void Handle(JsonElement m)
    {
        var evt = m.TryGetProperty("evt", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        if (evt == "READY") { _ready?.TrySetResult(true); return; }
        var nonce = m.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (nonce != null && _pending.Remove(nonce, out var tcs))
        {
            if (evt == "ERROR") tcs.TrySetException(new Exception(m.GetProperty("data").GetProperty("message").GetString()));
            else tcs.TrySetResult(m);
            return;
        }
        if (evt == "VOICE_CHANNEL_SELECT") _ = Refresh();
        else if (evt == "VOICE_SETTINGS_UPDATE" && m.TryGetProperty("data", out var d)) ApplySettings(d);
    }

    async Task<JsonElement> Command(string cmd, object args, string evt = null, int timeoutMs = 8000)
    {
        var nonce = Interlocked.Increment(ref _nonce).ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[nonce] = tcs;
        await Write(1, JsonSerializer.Serialize(new { cmd, args, evt, nonce }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        await Wait(tcs.Task, timeoutMs);
        return tcs.Task.Result;
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
            var vs = (await Command("GET_VOICE_SETTINGS", new { })).GetProperty("data");
            ApplySettings(vs);
        }
        catch (Exception ex) { Status = ex.Message; }
        Notify();
    }

    // ---------- aksi dari UI ----------
    public async Task SetMute(bool on) { try { await Command("SET_VOICE_SETTINGS", new { mute = on }); } catch (Exception ex) { Status = ex.Message; Notify(); } }
    public async Task SetDeaf(bool on) { try { await Command("SET_VOICE_SETTINGS", new { deaf = on }); } catch (Exception ex) { Status = ex.Message; Notify(); } }
    public async Task Leave() { try { await Command("SELECT_VOICE_CHANNEL", new { channel_id = (string)null, force = true }); await Refresh(); } catch (Exception ex) { Status = ex.Message; Notify(); } }
}
