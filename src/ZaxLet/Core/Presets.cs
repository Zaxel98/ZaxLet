using System.Linq;

namespace ZaxLet;

/// <summary>
/// Gaya notifikasi sekali pilih untuk pengguna baru: Tenang (hanya yang penting), Seimbang, Lengkap (semua popup dan widget idle).
/// Hanya mengatur kelompok sakelar popup dan isi pill; pengaturan lain tidak disentuh. Pengguna lanjutan tetap bisa mengatur satu per satu
/// (gayanya jadi "Kustom").
/// </summary>
public static class Presets
{
    public static void Apply(Settings s, string name)
    {
        bool quiet = name == "tenang", full = name == "lengkap";
        s.OsdOn = !quiet;                 // popup volume, kecerahan, Caps Lock, Bluetooth, mikrofon
        s.DownloadAlert = !quiet;
        s.WishSuggest = !quiet;
        s.PrivacyOn = full;               // "mikrofon/kamera dipakai" hanya di Lengkap
        s.NotifNewDefault = !quiet;       // aplikasi HP baru: Tenang menunggu dipilih dulu
        s.NotifOngoing = full;            // notifikasi berjalan (Gojek, dll) yang berubah terus
        s.IdleUsage = !quiet;
        s.IdleSpeed = full;
        s.IdleWave = !quiet;
        s.IdleLyrics = !quiet;
    }

    static string Key(Settings s) => string.Concat(new[] { s.OsdOn, s.DownloadAlert, s.WishSuggest, s.PrivacyOn, s.NotifNewDefault, s.NotifOngoing, s.IdleUsage, s.IdleSpeed, s.IdleWave, s.IdleLyrics }.Select(b => b ? '1' : '0'));

    /// <summary>"tenang", "seimbang", "lengkap", atau "custom" bila sakelarnya tidak cocok dengan salah satunya.</summary>
    public static string Detect(Settings s)
    {
        foreach (var p in new[] { "tenang", "seimbang", "lengkap" })
        {
            var t = new Settings();
            Apply(t, p);
            if (Key(t) == Key(s)) return p;
        }
        return "custom";
    }
}
