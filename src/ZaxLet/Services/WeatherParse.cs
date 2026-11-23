using System;
using System.Text.Json;

namespace ZaxLet;

/// <summary>Parses open-meteo responses for the weather widget: current temperature, WMO weather code, and highest rain chance in the next two hours. Pure, no network.</summary>
public static class WeatherParse
{
    public sealed record Now(double Temp, int Code, int RainNext2h);

    /// <summary>Null if the payload is incomplete or not JSON. <c>hourly.time</c> is parsed as the location's local time (timezone=auto).</summary>
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

    /// <summary>Short weather name (translation key) from a WMO code.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Cerah", 1 or 2 => "Cerah berawan", 3 => "Berawan", 45 or 48 => "Berkabut",
        >= 51 and <= 57 => "Gerimis", >= 61 and <= 67 => "Hujan", >= 71 and <= 77 => "Salju", >= 80 and <= 82 => "Hujan deras", >= 95 => "Badai petir", _ => "Cuaca"
    };
}
