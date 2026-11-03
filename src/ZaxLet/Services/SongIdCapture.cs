using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace ZaxLet;

public static partial class SongId
{

    /// <summary>Tangkap cuplikan lalu kenali. <paramref name="position"/> dibaca saat cuplikan selesai (posisi putar pemutar).</summary>
    public static async Task<(Hit Hit, TimeSpan EndPos)> RecognizeAsync(string token, Func<TimeSpan> position)
    {
        var pcm = new List<float>();
        int rate = 0;
        var lk = new object();
        void Tap(float[] b, int r) { lock (lk) { rate = r; if (pcm.Count < r * Seconds) pcm.AddRange(b); } }
        AudioFlow.I.Tap += Tap;
        AudioFlow.I.Acquire();
        TimeSpan end;
        try
        {
            for (int i = 0; i < (Seconds + 6) * 10; i++)
            {
                await Task.Delay(100);
                lock (lk) if (rate > 0 && pcm.Count >= rate * Seconds) break;
            }
            end = position();
        }
        finally { AudioFlow.I.Tap -= Tap; AudioFlow.I.Release(); }

        float[] snap;
        lock (lk) snap = pcm.ToArray();
        double peak = 0;
        foreach (var s in snap) peak = Math.Max(peak, Math.Abs(s));
        if (rate == 0 || snap.Length < rate * Seconds / 2 || peak < 0.003) return (null, end);   // hening: tidak ada yang bisa dikenali

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var wav = new ByteArrayContent(ToWav16k(snap, rate));
        wav.Headers.ContentType = new("audio/wav");
        using var form = new MultipartFormDataContent { { new StringContent(token.Trim()), "api_token" }, { wav, "file", "sample.wav" } };
        using var resp = await http.PostAsync("https://api.audd.io/", form);
        return (ParseResponse(await resp.Content.ReadAsStringAsync()), end);
    }
}
