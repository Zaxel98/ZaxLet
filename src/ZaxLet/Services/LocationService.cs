using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ZaxLet;

/// <summary>
/// Lokasi untuk jadwal sholat tanpa mengetik koordinat: dari IP (otomatis), dari sensor lokasi Windows (minta izin),
/// atau cari nama kota (Open-Meteo geocoding, tanpa kunci API). Hasil disimpan di Settings agar tetap jalan saat offline.
/// </summary>
public static class LocationService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    public static string Status = "";
    public static event Action Changed;

    /// <summary>Terapkan mode lokasi aktif. Mode otomatis dibatasi sekali per 6 jam kecuali force.</summary>
    public static async Task RefreshAsync(bool force = false)
    {
        var s = Settings.Cur;
        if (s.LocMode == "manual") return;
        if (!force && DateTime.Now - s.LocAt < TimeSpan.FromHours(6)) return;
        try
        {
            if (s.LocMode == "device" && await FromDevice()) return;
            await FromIp(s.LocMode == "device" ? L.T("Sensor lokasi tidak tersedia, memakai IP. ") : "");
        }
        catch (Exception ex) { Status = L.T("Gagal mendeteksi lokasi: ") + ex.Message; App.Log(ex); }
    }

    static async Task FromIp(string prefix)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/"));
        var r = doc.RootElement;
        if (!r.TryGetProperty("success", out var ok) || !ok.GetBoolean()) throw new Exception("layanan IP menolak");
        Set(r.GetProperty("city").GetString(), r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), prefix + "dari IP");
    }

    static async Task<bool> FromDevice()
    {
        try
        {
            var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
            if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed) return false;
            var g = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracyInMeters = 5000 };
            var pos = await g.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10));
            var c = pos.Coordinate.Point.Position;
            var name = await ReverseName(c.Latitude, c.Longitude) ?? "Lokasi perangkat";
            Set(name, c.Latitude, c.Longitude, "dari sensor perangkat");
            return true;
        }
        catch { return false; }
    }

    // nama kota terdekat dari koordinat (untuk label saja); gagal = null
    static async Task<string> ReverseName(double lat, double lon)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, FormattableString.Invariant($"https://nominatim.openstreetmap.org/reverse?format=json&zoom=10&lat={lat}&lon={lon}"));
            req.Headers.UserAgent.ParseAdd("ZaxLetApp/1.0");   // kebijakan Nominatim: wajib identifikasi aplikasi
            using var res = await Http.SendAsync(req);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("address", out var a)) return null;
            foreach (var k in new[] { "city", "town", "municipality", "county", "state" })
                if (a.TryGetProperty(k, out var v)) return v.GetString();
            return null;
        }
        catch { return null; }
    }

    /// <summary>Cari kota dari nama; true bila ketemu dan diterapkan.</summary>
    public static async Task<bool> SearchAsync(string name)
    {
        try
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=id&name=" + Uri.EscapeDataString(name);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var rs) || rs.GetArrayLength() == 0) { Status = L.F("Kota \"{0}\" tidak ditemukan", name); return false; }
            var r = rs[0];
            var label = r.GetProperty("name").GetString() + (r.TryGetProperty("country", out var c) ? ", " + c.GetString() : "");
            Set(label, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), "dari pencarian");
            return true;
        }
        catch (Exception ex) { Status = L.T("Pencarian gagal: ") + ex.Message; return false; }
    }

    static void Set(string city, double lat, double lon, string how)
    {
        var s = Settings.Cur;
        s.City = string.IsNullOrWhiteSpace(city) ? "Lokasi saya" : city;
        s.Lat = lat; s.Lon = lon; s.LocAt = DateTime.Now;
        Status = $"{s.City} ({how})";
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { Settings.Apply(); Changed?.Invoke(); });
    }
}
