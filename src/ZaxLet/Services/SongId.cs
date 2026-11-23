using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ZaxLet;

/// <summary>
/// Shazam-like song recognizer for untitled audio (compilations, streams, unlabeled videos). Captures ~12 seconds of PC audio output
/// (loopback, only when the button is pressed), then sends it to the AudD service (api.audd.io) using the user's own token. Returns:
/// title, artist, and the timestamp of the clip within the song (used to sync lyrics).
/// </summary>
public static partial class SongId
{
    public const int Seconds = 12;
    public sealed record Hit(string Title, string Artist, double At);

    /// <summary>Mono clip to 16 kHz 16-bit WAV (sufficient for recognition, ~380 KB for 12s).</summary>
    public static byte[] ToWav16k(IReadOnlyList<float> mono, int rate)
    {
        double ratio = rate / 16000.0;
        int n = (int)(mono.Count / ratio);
        using var ms = new MemoryStream(44 + n * 2);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++)
        {
            int a = (int)(i * ratio), b = Math.Max(a + 1, (int)((i + 1) * ratio));
            double s = 0;
            for (int k = a; k < b && k < mono.Count; k++) s += mono[k];
            w.Write((short)Math.Clamp(s / (b - a) * 32767, -32768, 32767));   // box average = simple low-pass filter
        }
        return ms.ToArray();
    }

    /// <summary>"01:23" or "1:02:03" -> seconds; -1 if unreadable.</summary>
    public static double ParseTimecode(string t)
    {
        double sec = 0;
        foreach (var p in (t ?? "").Split(':')) { if (!double.TryParse(p, out var v)) return -1; sec = sec * 60 + v; }
        return sec;
    }

    /// <summary>AudD result: null = unrecognized; throws if service rejects (invalid token, quota exhausted).</summary>
    public static Hit ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("status", out var st) && st.GetString() == "error")
            throw new InvalidOperationException(r.TryGetProperty("error", out var er) && er.TryGetProperty("error_message", out var em) ? em.GetString() : "AudD error");
        if (!r.TryGetProperty("result", out var res) || res.ValueKind != JsonValueKind.Object) return null;
        string S(string k) => res.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        return string.IsNullOrWhiteSpace(S("title")) ? null : new Hit(S("title"), S("artist"), Math.Max(0, ParseTimecode(S("timecode"))));
    }
}
