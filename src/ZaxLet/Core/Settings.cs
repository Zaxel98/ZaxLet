using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ZaxLet;

public sealed class Settings
{
    // lokasi: auto (dari IP) | device (sensor lokasi Windows, minta izin) | manual (cari nama kota)
    public string LocMode { get; set; } = "auto";
    public string City { get; set; } = "Bandung";
    public double Lat { get; set; } = -6.9175;
    public double Lon { get; set; } = 107.6191;
    public DateTime LocAt { get; set; }

    // umum
    public bool HoverOpen { get; set; } = true;
    public int CloseDelayMs { get; set; } = 500;
    public bool AutoHideFullscreen { get; set; } = true;
    public string Accent { get; set; } = "teal";
    public string ClockStyle { get; set; } = "dot";           // dot (17.52) | colon (17:52) | 12h
    public bool ClockSeconds { get; set; }
    public List<string> HiddenTabs { get; set; } = new();
    public string LastTab { get; set; } = "music";
    public string OpenTab { get; set; } = "last";                // tab yang dibuka pertama: last (terakhir dipakai) atau kunci tab
    public List<string> TabOrder { get; set; } = new();         // urutan tab di bar (kosong = bawaan)

    // pill idle
    public bool IdleBattery { get; set; } = true;
    public bool IdleBatteryPct { get; set; } = true;
    public bool IdleUsage { get; set; } = true;
    public string IdleUsageProviders { get; set; } = "both";   // both | claude | codex
    public string IdleUsageWindow { get; set; } = "5h";        // 5h | weekly
    public string IdleUsageStyle { get; set; } = "ringpct";    // ring | ringpct | pct | bar
    public string IdleSpeedStyle { get; set; } = "down";       // down | both (unduh+unggah)
    // posisi tiap item di pill idle: left | center | right (urutan di dalam zona tetap: wave, usage, kecepatan, jam, baterai)
    public string PosClock { get; set; } = "center";
    public string PosUsage { get; set; } = "left";
    public string PosSpeed { get; set; } = "left";
    public string PosWave { get; set; } = "left";
    public string PosBattery { get; set; } = "right";
    public bool IdleExactCenter { get; set; } = true;           // komponen "tengah" tepat di tengah layar (kiri/kanan dibuat selebar yang terbesar)
    public int IdleGap { get; set; } = 14;                      // jarak tetap antar komponen idle (px)
    public bool IdleSpeed { get; set; } = true;
    public bool IdleWave { get; set; } = true;
    public bool IdleLyrics { get; set; } = true;

    // musik
    public bool MusicLyrics { get; set; } = true;
    public int LyricOffsetMs { get; set; }
    public bool TintCover { get; set; } = true;
    public bool LyricKaraoke { get; set; } = true;             // isi per kata mengikuti tempo penyanyi
    public double FlowTolSec { get; set; } = 1.5;              // karaoke vokal boleh menyimpang sejauh ini dari hitungan waktu (detik)
    public bool LyricFlow { get; set; } = true;               // isi karaoke mengikuti vokal yang terdengar (membaca keluaran audio PC, lokal)
    public string LyricPace { get; set; } = "normal";           // fast | normal | slow

    // usage
    public bool ClaudeOn { get; set; } = true;
    public bool CodexOn { get; set; } = true;
    // notifikasi HP di pill idle
    public bool NotifOn { get; set; } = true;
    public double NotifShowSec { get; set; } = 6;
    public bool NotifNewDefault { get; set; } = true;          // aplikasi yang belum pernah diatur: tampil atau tidak
    public bool NotifOngoing { get; set; } = true;             // notifikasi berjalan (Gojek, dll) tampil tiap berubah
    public bool NotifFullscreen { get; set; } = true;          // pill muncul sementara untuk notifikasi walau fullscreen / disembunyikan
    // sembunyikan pill sementara (klik kanan pill, pintasan, atau menu tray); menutupi mis. kolom pencarian aplikasi
    public bool AltGhost { get; set; } = true;                 // tahan Alt di atas pill = pill hilang sementara
    public string HideKey { get; set; } = "ctrl+alt+i";
    public int SnoozeMin { get; set; } = 10;                   // 0 = sampai ditampilkan lagi
    // romaji untuk lirik Jepang (seperti Metrolist)
    public bool LyricRomaji { get; set; } = true;
    public bool IdleRomaji { get; set; }                       // baris lirik di pill idle memakai romaji
    public bool RomajiOnly { get; set; }                       // tab Musik: sembunyikan lirik asli bila ada romaji (hanya hasil romaji); lagu non-Jepang tetap normal
    // pintasan untuk tab Aplikasi (Discord: samakan dengan Keybinds di Discord; Meet: tab Meet harus tab aktif)
    public string DiscordClientId { get; set; } = "";           // aplikasi di discord.com/developers (RPC lokal)
    public string DiscordSecret { get; set; } = "";
    public bool DiscordSetupAsked { get; set; }                  // panduan sambung Discord sudah pernah ditampilkan (sekali)
    public string PosVoice { get; set; } = "right";
    public string PosPhone { get; set; } = "right";              // titik status koneksi HP
    public bool IdlePhoneDot { get; set; } = true;              // titik hijau saat berada di voice channel
    public Dictionary<string, string> AppKeys { get; set; } = new()
    {
        ["discord.mic"] = "ctrl+shift+m", ["discord.deaf"] = "ctrl+shift+d",
        ["meet.mic"] = "ctrl+d", ["meet.cam"] = "ctrl+e",
    };
    public Dictionary<string, bool> NotifApps { get; set; } = new();

    public bool ShowPonytail { get; set; } = true;
    public bool ShowCost { get; set; } = true;
    public double UsageRefreshSec { get; set; } = 180;   // bebas, mulai 1 detik; server tetap membatasi lewat 429
    public int WarnAt { get; set; } = 70;
    public int CritAt { get; set; } = 90;

    // monitor
    public bool MonGpu { get; set; } = true;
    public bool MonTop { get; set; } = true;
    public bool MonCores { get; set; } = true;

    // kalender
    public bool WeekMonday { get; set; } = true;
    public bool ShowHolidays { get; set; } = true;

    // sholat
    public string PrayerMethod { get; set; } = "kemenag";      // kemenag | mwl | isna | egypt | karachi
    public bool AsrHanafi { get; set; }
    public int Ihtiyat { get; set; } = 2;
    public bool PrayerAlert { get; set; } = true;
    public bool PrayerPauseMusic { get; set; }

    // HP: token tetap supaya tautan bisa disimpan/di-bookmark; port tetap supaya aturan firewall cukup sekali
    public string ShareToken { get; set; } = "";
    public int SharePort { get; set; } = 47890;
    public bool ShareAuto { get; set; }
    public string ShareQr { get; set; } = "lan";               // lan | tailscale
    public int CamRes { get; set; } = 720;                      // tinggi bingkai kamera HP
    public int CamFps { get; set; } = 15;
    public bool CamFront { get; set; }
    public bool VCamOn { get; set; }                            // kamera virtual aktif otomatis saat ZaxLet mulai
    public string VCamFit { get; set; } = "crop";               // crop (isi penuh) | fit (utuh, bar hitam)
    public int VCamRot { get; set; }                            // 0 | 90 | 180 | 270
    public bool VCamMirror { get; set; }
    public string VCamRatio { get; set; } = "16:9";             // 1:1 | 4:3 | 9:16 | 16:9
    public bool VCamAuto { get; set; } = true;                  // HP dimiringkan (landscape) = otomatis 16:9 penuh

    // alat
    public string CapDir { get; set; } = "";
    public bool CapClipboard { get; set; } = true;
    public int RecFps { get; set; } = 30;
    public bool EyeOn { get; set; }
    public double EyeMinutes { get; set; } = 20;
    public double PomoWork { get; set; } = 25;
    public double PomoBreak { get; set; } = 5;

    // minum: pengingat lembut, hanya saat jam aktif, dan (mode pintar) hanya bila asupan tertinggal dari jadwal
    public bool WaterOn { get; set; } = true;
    public int WaterGoalMl { get; set; } = 2000;
    public int WaterCupMl { get; set; } = 250;
    public string WaterFrom { get; set; } = "08:00";
    public string WaterTo { get; set; } = "22:00";
    public double WaterEverySec { get; set; } = 3600;
    public bool WaterSmart { get; set; } = true;
    public double WaterShowSec { get; set; } = 10;

    public static Settings Cur = new();
    public static event Action Applied;
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZaxLet");
    public static double TzHours => TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalHours;

    public static void Load() { Cur = Store.Load<Settings>("settings.json") ?? new Settings(); UI.SetAccent(Cur.Accent); }
    public static void Save() => Store.Save("settings.json", Cur);

    /// <summary>Simpan lalu beri tahu jendela/tab agar menyegarkan diri.</summary>
    public static void Apply() { UI.SetAccent(Cur.Accent); Save(); Applied?.Invoke(); }

    public static void Reset() { Cur = new Settings(); Apply(); }
}

/// <summary>JSON kecil di %LOCALAPPDATA%\ZaxLet; gagal baca/tulis tidak boleh menjatuhkan aplikasi.</summary>
public static class Store
{
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static T Load<T>(string name) where T : class
    {
        try
        {
            var p = Path.Combine(Settings.Dir, name);
            return File.Exists(p) ? JsonSerializer.Deserialize<T>(File.ReadAllText(p)) : null;
        }
        catch (Exception ex) { App.Log(ex); return null; }
    }

    public static void Save<T>(string name, T value)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(Path.Combine(Settings.Dir, name), JsonSerializer.Serialize(value, Opt));
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
