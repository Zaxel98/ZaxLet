using System;
using System.Text.Json;

namespace ZaxLet;

/// <summary>Pembaca jawaban open-meteo untuk widget cuaca: suhu sekarang, kode cuaca WMO, dan peluang hujan tertinggi dua jam ke depan. Murni, tanpa jaringan.</summary>
public static class WeatherParse
{
    public sealed record Now(double Temp, int Code, int RainNext2h);

    /// <summary>Null bila isinya tidak lengkap atau bukan JSON. Jam <c>hourly.time</c> dibaca sebagai waktu lokal lokasi (timezone=auto).</summary>
    public static Now Read(string json, DateTime localNow)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var cur = doc.RootElement.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();
            int rain = 0;
            if (doc.RootElement.TryGetProperty("hourly", out var h) && h.TryGetProperty("time", out var times) && h.TryGetProperty("precipitation_probability", out var pr))
            {
                int n = Math.Min(times.GetArrayLength(), pr.GetArrayLength());
                for (int i = 0; i < n; i++)
                {
                    if (!DateTime.TryParse(times[i].GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)) continue;
                    if (t >= localNow.AddHours(-1) && t <= localNow.AddHours(2) && pr[i].ValueKind == JsonValueKind.Number) rain = Math.Max(rain, pr[i].GetInt32());
                }
            }
            return new Now(temp, code, rain);
        }
        catch { return null; }
    }

    /// <summary>Nama cuaca singkat (kunci terjemahan) dari kode WMO.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Cerah", 1 or 2 => "Cerah berawan", 3 => "Berawan", 45 or 48 => "Berkabut",
        >= 51 and <= 57 => "Gerimis", >= 61 and <= 67 => "Hujan", >= 71 and <= 77 => "Salju", >= 80 and <= 82 => "Hujan deras", >= 95 => "Badai petir", _ => "Cuaca"
    };
}
