using System;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>
/// Cuaca dari open-meteo.com (tanpa kunci). Mati bawaannya: yang dikirim hanya koordinat lokasi di Pengaturan. Diperbarui tiap 30 menit
/// hanya selama menyala; peringatan hujan muncul di pill paling sering tiap 3 jam.
/// </summary>
public sealed class WeatherService
{
    public static readonly WeatherService I = new();
    public event Action Changed;
    public WeatherParse.Now Current;
    public string Summary => Current == null ? "" : $"{Current.Temp:0}°C · {L.T(WeatherParse.Describe(Current.Code))}";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMinutes(30) };
    DateTime _rainAt = DateTime.MinValue, _lastAt = DateTime.MinValue;

    WeatherService() { _t.Tick += async (_, _) => await Refresh(); }

    /// <summary>Mulai atau hentikan sesuai Pengaturan (dipanggil saat mulai dan tiap Pengaturan berubah).</summary>
    public void Apply()
    {
        _t.Stop();
        if (!Settings.Cur.WeatherOn) { if (Current != null) { Current = null; Changed?.Invoke(); } return; }
        _t.Start();
        if (Current == null || DateTime.Now - _lastAt > TimeSpan.FromMinutes(5)) _ = Refresh();   // Pengaturan lain yang berubah tidak memicu permintaan baru
    }

    async Task Refresh()
    {
        var s = Settings.Cur;
        if (!s.WeatherOn || s.Lat == 0 && s.Lon == 0) return;
        _lastAt = DateTime.Now;
        try
        {
            var url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.###}&longitude={1:0.###}&current=temperature_2m,weather_code&hourly=precipitation_probability&forecast_days=2&timezone=auto", s.Lat, s.Lon);
            var now = WeatherParse.Read(await Http.GetStringAsync(url), DateTime.Now);
            if (now == null) return;
            Current = now;
            Changed?.Invoke();
            if (s.RainAlert && now.RainNext2h >= 60 && DateTime.Now - _rainAt > TimeSpan.FromHours(3))
            {
                _rainAt = DateTime.Now;
                IslandWindow.Instance?.Alert(L.F("Kemungkinan hujan {0}% dalam 2 jam ke depan", now.RainNext2h), 10);
            }
        }
        catch (Exception ex) { App.Log(ex); }   // jaringan putus: tampilan terakhir dipertahankan
    }
}
