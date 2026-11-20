using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZaxLet;

/// <summary>Rahasia di JSON dienkripsi DPAPI (terikat ke akun Windows ini): "dpapi:" + base64. Nilai polos lama tetap terbaca dan ditulis terenkripsi pada Save berikutnya.</summary>
public sealed class SecretConverter : JsonConverter<string>
{
    const string Prefix = "dpapi:";

    public override string Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var s = r.GetString() ?? "";
        if (!s.StartsWith(Prefix)) return s;   // migrasi dari nilai polos
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s[Prefix.Length..]), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }   // berkas dari akun/PC lain: anggap kosong
    }

    public override void Write(Utf8JsonWriter w, string v, JsonSerializerOptions o) =>
        w.WriteStringValue(string.IsNullOrEmpty(v) ? "" : Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(v), null, DataProtectionScope.CurrentUser)));
}

public sealed class Settings
{
    // lokasi: auto (dari IP) | device (sensor lokasi Windows, minta izin) | manual (cari nama kota)
    public string LocMode { get; set; } = "auto";
    public string City { get; set; } = "Bandung";
    public double Lat { get; set; } = -6.9175;
    public double Lon { get; set; } = 107.6191;
    public DateTime LocAt { get; set; }

    // umum
    public string Lang { get; set; } = "auto";                  // auto (ikut Windows) | id | en; berlaku setelah dimulai ulang
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
    public bool IdleSpeed { get; set; }
    public bool IdleWave { get; set; } = true;
    public bool IdleLyrics { get; set; } = true;

    // musik
    public bool MusicLyrics { get; set; } = true;
    public string MediaIgnore { get; set; } = "";              // aplikasi yang musiknya diabaikan island, dipisah koma (mis. "discord, zoom")
    public int LyricOffsetMs { get; set; }                     // bawaan semua lagu; geseran per lagu ada di LyricOffsets
    public Dictionary<string, int> LyricOffsets { get; set; } = new();   // "artis|judul" huruf kecil -> ms
    public bool TintCover { get; set; } = true;
    public bool LyricKaraoke { get; set; } = true;             // isi per kata mengikuti tempo penyanyi
    public double FlowTolSec { get; set; } = 1.5;              // karaoke vokal boleh menyimpang sejauh ini dari hitungan waktu (detik)
    // fitur tambahan (referensi: ReyLand)
    public bool Gestures { get; set; } = true;                 // scroll di island = volume, klik dua kali = putar/jeda, geser horizontal = pindah tab
    public bool OsdOn { get; set; } = true;                    // popup di island: volume, kecerahan, Caps Lock, Bluetooth, mikrofon
    public bool PrivacyOn { get; set; }                       // tampilkan aplikasi yang memakai mikrofon/kamera
    public bool DownloadAlert { get; set; } = true;            // unduhan selesai
    public bool YieldOn { get; set; }                          // island menyingkir bila menutupi tombol aplikasi lain
    public bool ClipboardOn { get; set; } = true;              // riwayat clipboard (hanya di memori)
    public bool AdhanOn { get; set; }
    public string AdhanFile { get; set; } = "";                // berkas suara adzan pilihan pengguna
    public bool AutoUpdate { get; set; } = true;
    public string UpdateRepo { get; set; } = "Zaxel98/ZaxLet";
    public DateTime UpdateCheckedAt { get; set; }
    public bool TourDone { get; set; }
    public List<string> GuidesSeen { get; set; } = new();      // panduan yang sudah tampil otomatis sekali (hp, discord); tombol info membukanya lagi
    public bool Advanced { get; set; }                         // Pengaturan: tampilkan juga opsi lanjutan
    public bool YtChapters { get; set; } = true;              // video YouTube panjang: baca daftar lagunya (judul dan lirik mengikuti lagu yang diputar)
    public bool YtProgress { get; set; } = true;               // posisi YouTube dibaca langsung dari bilah progres di halaman (aksesibilitas Windows, lokal)
    [JsonConverter(typeof(SecretConverter))] public string SongIdToken { get; set; } = "";             // token AudD milik pengguna untuk tombol "Kenali lagu"
    public bool RightClickHide { get; set; } = true;          // klik kanan pill = sembunyikan seperti mode fullscreen
    public bool LyricFlow { get; set; } = true;              // isi karaoke mengikuti vokal yang terdengar (membaca keluaran audio PC, lokal)
    public string LyricPace { get; set; } = "normal";           // fast | normal | slow

    // usage
    public bool ClaudeOn { get; set; } = true;
    public bool CodexOn { get; set; } = true;                  // kotak kedua tab Usage dan pill menyala
    public string Slot2 { get; set; } = "codex";               // codex | antigravity: isi kotak kedua (bisa tukar)
    // notifikasi HP di pill idle
    public bool NotifOn { get; set; } = true;
    public double NotifShowSec { get; set; } = 6;
    public bool NotifNewDefault { get; set; } = true;          // aplikasi yang belum pernah diatur: tampil atau tidak
    public bool NotifOngoing { get; set; }                    // notifikasi berjalan (Gojek, dll) tampil tiap berubah
    public bool NotifFullscreen { get; set; } = true;          // pill muncul sementara untuk notifikasi walau fullscreen / disembunyikan
    public bool NotifOtp { get; set; } = true;                 // kode OTP di notifikasi: tombol Salin (tidak masuk riwayat clipboard)
    // sembunyikan pill sementara (klik kanan pill, pintasan, atau menu tray); menutupi mis. kolom pencarian aplikasi
    public bool AltGhost { get; set; } = true;                 // tahan Alt di atas pill = pill hilang sementara
    public string HideKey { get; set; } = "ctrl+alt+i";
    public int SaverPct { get; set; } = 20;                    // mode hemat otomatis di baterai ≤ nilai ini (%); 0 = mati
    public string AnimFps { get; set; } = "auto";              // auto: animasi latar 30 fps, buka/tutup/ganti tab 60 fps | full: semua 60 fps (berlaku setelah mulai ulang)
    public bool PlayLogOn { get; set; } = true;               // catat lagu yang didengar (hanya di PC ini) untuk rekap mingguan
    public bool IdleContext { get; set; } = true;             // baris tambahan di pill idle menurut waktu (cuaca, sholat, tugas, minum)
    public bool TrackFlash { get; set; } = true;             // tepi pill menyala sekali saat lagu ganti
    public int PillRadius { get; set; }                       // 0 = otomatis; 8-40 = radius sudut bawah pill (px)
    public int IdleExtra { get; set; }                         // tambahan lebar pill saat idle (px), 0-160
    public string IslandShape { get; set; } = "notch";        // notch (bahu cekung) | pill (kapsul bulat gaya Dynamic Island)
    public string Theme { get; set; } = "black";               // black | aurora | matcha | senja | neon
    public int IslandScale { get; set; } = 100;                // persen ukuran island; 0 = otomatis menurut lebar layar
    public int IslandOffsetX { get; set; }                     // geser horizontal dari tengah monitor (px), -600..600
    public string ShowMode { get; set; } = "always";           // always | activity (hanya saat ada aktivitas) | hidden (sampai dibuka dari tray)
    public bool FsMusic { get; set; }                          // lagu dan lirik tetap tampil di atas aplikasi layar penuh (tidak bisa diklik)
    public int MonitorIdx { get; set; }                        // 0 = monitor utama
    public string MiniClock { get; set; } = "off";             // off | clock | date: pil jam kecil saat island menyingkir karena menutupi tombol aplikasi lain
    public string PerfShow { get; set; } = "off";              // off | game (saat aplikasi layar penuh di depan) | on: overlay performa ala RivaTuner
    public string PerfItems { get; set; } = PerfFormat.DefaultItems;   // metrik yang tampil, dipisah koma (lihat PerfFormat.Metrics)
    public bool PerfInIsland { get; set; }                     // tab Monitor di island menampilkan metrik pilihan (menggantikan daftar proses); tanpa mengubah ukuran island
    public string PerfCorner { get; set; } = "tl";           // tl | tr | bl | br
    public int PerfBgAlpha { get; set; } = 88;                 // persen kepekatan latar overlay performa (teks tetap jelas); 0 = latar hilang
    public bool BtBattery { get; set; } = true;                // baterai headset/perangkat Bluetooth: tampil saat tersambung dan peringatan saat rendah
    public int BtLowPct { get; set; } = 20;
    public string QuickKey { get; set; } = "";                // pintasan global kotak Aksi cepat (timer, fokus, catat, ...); kosong = mati
    public string OpenKey { get; set; } = "";                   // pintasan global untuk membuka/menutup island dari keyboard; kosong = mati
    public string CapKey { get; set; } = "";                    // pintasan global untuk tangkap area; kosong = mati (Ctrl+Shift+S bentrok dengan Simpan Sebagai di banyak aplikasi)
    public int SnoozeMin { get; set; } = 10;                   // 0 = sampai ditampilkan lagi
    // romaji untuk lirik Jepang (seperti Metrolist)
    public bool LyricRomaji { get; set; } = true;
    public bool IdleRomaji { get; set; }                       // baris lirik di pill idle memakai romaji
    public bool RomajiOnly { get; set; }                       // tab Musik: sembunyikan lirik asli bila ada romaji (hanya hasil romaji); lagu non-Jepang tetap normal
    // pintasan untuk tab Aplikasi (Discord: samakan dengan Keybinds di Discord; Meet: tab Meet harus tab aktif)
    public string DiscordClientId { get; set; } = "";           // aplikasi di discord.com/developers (RPC lokal)
    [JsonConverter(typeof(SecretConverter))] public string DiscordSecret { get; set; } = "";
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
    public bool ImsakOn { get; set; }                          // pengingat imsak (10 menit sebelum Subuh) dan tanda "buka puasa" saat Maghrib
    public string WorshipMode { get; set; } = "sholat";        // sholat | other (jadwal mingguan sendiri) | off
    public string WorshipText { get; set; } = "";              // "min 08:00 Kebaktian; jum,sab 18.30 Latihan koor"
    public bool PrayerResumeAsk { get; set; } = true;          // setelah jeda musik karena sholat, pill menawarkan memutarnya lagi
    public bool WeatherOn { get; set; }                        // cuaca open-meteo (koordinat lokasi dikirim ke open-meteo.com)
    public bool RainAlert { get; set; } = true;                // peringatan hujan 2 jam ke depan (bila cuaca menyala)
    public bool PrayerAlert { get; set; } = true;
    public bool PrayerPauseMusic { get; set; }

    // HP: token tetap supaya tautan bisa disimpan/di-bookmark; port tetap supaya aturan firewall cukup sekali
    [JsonConverter(typeof(SecretConverter))] public string ShareToken { get; set; } = "";
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
    public bool WishBridgeOn { get; set; } = true;             // terima info halaman produk dari ekstensi browser (hanya 127.0.0.1)
    public bool WishSuggest { get; set; } = true;              // link produk yang dikenali ekstensi ditawarkan ke Wishlist saat disalin
    [JsonConverter(typeof(SecretConverter))] public string WishToken { get; set; } = "";              // rahasia ekstensi browser; dibuat sekali, tidak ikut cadangan
    public bool MeetOn { get; set; } = true;                  // deteksi rapat (Zoom, Meet, Teams, ...) lewat pemakaian mikrofon
    public bool MeetMic { get; set; } = true;                  // tombol mikrofon di pill selama rapat
    public bool MeetPause { get; set; } = true;                // musik dijeda selama rapat
    public bool MeetHold { get; set; } = true;                 // notifikasi HP ditahan selama rapat
    public int FocusMin { get; set; } = 25;                    // lama Mode Fokus manual
    public bool FocusPomo { get; set; }                        // fase kerja Pomodoro otomatis jadi Mode Fokus
    public bool QuietOn { get; set; }                          // jam tenang terjadwal: tahan notifikasi HP di rentang jam ini
    public string QuietFrom { get; set; } = "22:00";
    public string QuietTo { get; set; } = "06:00";
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
    // ZAXLET_DATA_DIR: folder data lain (uji tanpa menyentuh data yang sebenarnya)
    public static string Dir => Environment.GetEnvironmentVariable("ZAXLET_DATA_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZaxLet");
    public static double TzHours => TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalHours;

    public static void Load()
    {
        var s = Store.Load<Settings>("settings.json");
        Cur = s ?? new Settings(); UI.SetAccent(Cur.Accent); L.Init(Cur.Lang);
        if (s != null) Save();   // menulis ulang rahasia lama yang polos sebagai terenkripsi (migrasi sekali jalan)
    }
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
            if (!File.Exists(p)) return null;
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(p)); }
            catch (JsonException)
            {
                File.Copy(p, p + ".bad", true);   // berkas rusak: simpan salinannya sebelum Save berikutnya menimpa
                throw;
            }
        }
        catch (Exception ex) { App.Log(ex); return null; }
    }

    public static void Save<T>(string name, T value)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            var p = Path.Combine(Settings.Dir, name);
            File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(value, Opt));
            File.Move(p + ".tmp", p, true);   // ganti utuh: crash di tengah tulis tidak merusak berkas lama
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
