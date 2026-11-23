using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZaxLet;

/// <summary>Secrets in JSON are DPAPI-encrypted (bound to this Windows account): "dpapi:" + base64. Legacy plaintext values are still read and written encrypted on the next Save.</summary>
public sealed class SecretConverter : JsonConverter<string>
{
    const string Prefix = "dpapi:";

    public override string Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var s = r.GetString() ?? "";
        if (!s.StartsWith(Prefix)) return s;   // migrate from plaintext value
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s[Prefix.Length..]), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }   // file from another account/PC: treat as empty
    }

    public override void Write(Utf8JsonWriter w, string v, JsonSerializerOptions o) =>
        w.WriteStringValue(string.IsNullOrEmpty(v) ? "" : Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(v), null, DataProtectionScope.CurrentUser)));
}

public sealed class Settings
{
    // location: auto (from IP) | device (Windows location sensor, asks permission) | manual (search city name)
    public string LocMode { get; set; } = "auto";
    public string City { get; set; } = "Bandung";
    public double Lat { get; set; } = -6.9175;
    public double Lon { get; set; } = 107.6191;
    public DateTime LocAt { get; set; }

    // general
    public string Lang { get; set; } = "auto";                  // auto (follow Windows) | id | en; takes effect after restart
    public bool HoverOpen { get; set; } = true;
    public int CloseDelayMs { get; set; } = 500;
    public bool AutoHideFullscreen { get; set; } = true;
    public string Accent { get; set; } = "teal";
    public string ClockStyle { get; set; } = "colon";         // dot (17.52) | colon (17:52) | 12h
    public bool ClockSeconds { get; set; }
    public List<string> HiddenTabs { get; set; } = new();
    public string LastTab { get; set; } = "music";
    public string OpenTab { get; set; } = "last";                // tab opened first: last (last used) or tab key
    public List<string> TabOrder { get; set; } = new();         // tab order in bar (empty = default)

    // idle pill
    public bool IdleBattery { get; set; } = true;
    public bool IdleBatteryPct { get; set; } = true;
    public bool IdleUsage { get; set; } = true;
    public string IdleUsageProviders { get; set; } = "both";   // both | claude | codex
    public string IdleUsageWindow { get; set; } = "5h";        // 5h | weekly
    public string IdleUsageStyle { get; set; } = "ringpct";    // ring | ringpct | pct | bar
    public string IdleSpeedStyle { get; set; } = "down";       // down | both (download+upload)
    // position of each item in idle pill: left | center | right (order within zone is fixed: wave, usage, speed, clock, battery)
    public string PosClock { get; set; } = "center";
    public string PosUsage { get; set; } = "left";
    public string PosSpeed { get; set; } = "left";
    public string PosWave { get; set; } = "left";
    public string PosBattery { get; set; } = "right";
    public bool IdleExactCenter { get; set; } = true;           // "center" component centered on screen (left/right padded to match the wider side)
    public int IdleGap { get; set; } = 14;                      // fixed gap between idle components (px)
    public bool IdleSpeed { get; set; }
    public bool IdleWave { get; set; } = true;
    public bool IdleLyrics { get; set; } = true;

    // music
    public bool MusicLyrics { get; set; } = true;
    public string MediaIgnore { get; set; } = "";              // apps whose media is ignored by island, comma-separated (e.g., "discord, zoom")
    public int LyricOffsetMs { get; set; }                     // default for all songs; per-song offsets are in LyricOffsets
    public Dictionary<string, int> LyricOffsets { get; set; } = new();   // lowercase "artist|title" -> ms
    public bool TintCover { get; set; } = true;
    public bool LyricKaraoke { get; set; } = true;             // per-word fill following singer tempo
    public double FlowTolSec { get; set; } = 1.5;              // vocal karaoke may deviate this far from timing calculation (seconds)
    // extra features (reference: ReyLand)
    public bool Gestures { get; set; } = true;                 // scroll on island = volume, double-click = play/pause, horizontal swipe = switch tab
    public bool OsdOn { get; set; } = true;                    // island popups: volume, brightness, Caps Lock, Bluetooth, microphone
    public bool PrivacyOn { get; set; }                       // show apps using microphone/camera
    public bool DownloadAlert { get; set; } = true;            // download completed
    public bool YieldOn { get; set; }                          // island moves aside when covering another app's buttons
    public bool ClipboardOn { get; set; } = true;              // clipboard history (in-memory only)
    public bool AdhanOn { get; set; }
    public string AdhanFile { get; set; } = "";                // user-selected adhan audio file
    public bool AutoUpdate { get; set; } = true;
    public string UpdateRepo { get; set; } = "Zaxel98/ZaxLet";
    public DateTime UpdateCheckedAt { get; set; }
    public bool TourDone { get; set; }
    public List<string> GuidesSeen { get; set; } = new();      // guides already shown automatically once (phone, discord); info button reopens them
    public bool Advanced { get; set; }                         // Settings: also show advanced options
    public bool YtChapters { get; set; } = true;              // long YouTube videos: read tracklist (title and lyrics follow currently playing track)
    public bool YtProgress { get; set; } = true;               // YouTube position read directly from on-page progress bar (Windows accessibility, local)
    [JsonConverter(typeof(SecretConverter))] public string SongIdToken { get; set; } = "";             // user's AudD token for "Identify song" button
    public bool RightClickHide { get; set; } = true;          // right-click pill = hide like fullscreen mode
    public bool LyricFlow { get; set; } = true;              // karaoke fill follows audible vocals (reads PC audio output, local)
    public string LyricPace { get; set; } = "normal";           // fast | normal | slow

    // usage
    public bool ClaudeOn { get; set; } = true;
    public bool CodexOn { get; set; } = true;                  // second box in Usage tab and pill is lit
    public string Slot2 { get; set; } = "codex";               // codex | antigravity: second box content (swappable)
    // phone notifications in idle pill
    public bool NotifOn { get; set; } = true;
    public double NotifShowSec { get; set; } = 6;
    public bool NotifNewDefault { get; set; } = true;          // unconfigured apps: show or not
    public bool NotifOngoing { get; set; }                    // ongoing notifications (Gojek, etc.) shown on every change
    public bool NotifFullscreen { get; set; } = true;          // pill appears briefly for notifications even when fullscreen / hidden
    public bool NotifOtp { get; set; } = true;                 // OTP code in notification: Copy button (excluded from clipboard history)
    // temporarily hide pill (right-click pill, shortcut, or tray menu); e.g. when covering an app's search bar
    public bool AltGhost { get; set; } = true;                 // hold Alt over pill = pill disappears temporarily
    public string HideKey { get; set; } = "ctrl+alt+i";
    public int SaverPct { get; set; } = 20;                    // automatic power-saver mode at battery <= this value (%); 0 = off
    public string AnimFps { get; set; } = "auto";              // auto: background animations 30 fps, open/close/tab switch 60 fps | full: all 60 fps (takes effect after restart)
    public bool PlayLogOn { get; set; } = true;               // log listened songs (on this PC only) for weekly recap
    public bool IdleContext { get; set; } = true;             // extra line in idle pill by time of day (weather, prayer, tasks, water)
    public bool TrackFlash { get; set; } = true;             // pill edge flashes once when track changes
    public int PillRadius { get; set; }                       // 0 = auto; 8-40 = pill bottom corner radius (px)
    public int IdleExtra { get; set; }                         // extra pill width when idle (px), 0-160
    public string IslandShape { get; set; } = "notch";        // notch (concave shoulders) | pill (Dynamic Island style rounded capsule)
    public string Theme { get; set; } = "black";               // black | aurora | matcha | senja | neon
    public int IslandScale { get; set; } = 100;                // island size percentage; 0 = automatic by screen width
    public int IslandOffsetX { get; set; }                     // horizontal offset from monitor center (px), -600..600
    public string ShowMode { get; set; } = "always";           // always | activity (only when active) | hidden (until opened from tray)
    public bool FsMusic { get; set; }                          // song and lyrics stay visible above fullscreen apps (click-through)
    public int MonitorIdx { get; set; }                        // 0 = primary monitor
    public string MiniClock { get; set; } = "off";             // off | clock | date: small clock pill when island yields to avoid covering another app's buttons
    public string PerfShow { get; set; } = "off";              // off | game (when fullscreen app is in foreground) | on: RivaTuner-style performance overlay
    public string PerfItems { get; set; } = PerfFormat.DefaultItems;   // metrics to display, comma-separated (see PerfFormat.Metrics)
    public bool PerfInIsland { get; set; }                     // Monitor tab in island shows selected metrics (replaces process list) without resizing island
    public string PerfCorner { get; set; } = "tl";           // tl | tr | bl | br
    public int PerfBgAlpha { get; set; } = 88;                 // performance overlay background opacity percent (text stays sharp); 0 = no background
    public bool BtBattery { get; set; } = true;                // Bluetooth headset/device battery: show when connected and warn when low
    public int BtLowPct { get; set; } = 20;
    public bool BtPauseOnLost { get; set; } = true;            // auto-pause music when Bluetooth device disconnects
    public string QuickKey { get; set; } = "";                // global shortcut for Quick Actions box (timer, focus, note, ...); empty = off
    public string OpenKey { get; set; } = "";                   // global shortcut to open/close island from keyboard; empty = off
    public string CapKey { get; set; } = "";                    // global shortcut for area capture; empty = off (Ctrl+Shift+S conflicts with Save As in many apps)
    public int SnoozeMin { get; set; } = 10;                   // 0 = until shown again
    // romaji for Japanese lyrics (like Metrolist)
    public bool LyricRomaji { get; set; } = true;
    public bool IdleRomaji { get; set; }                       // lyrics line in idle pill uses romaji
    public bool RomajiOnly { get; set; }                       // Music tab: hide original lyrics when romaji is available (romaji only); non-Japanese songs stay normal
    // shortcuts for Apps tab (Discord: match Keybinds in Discord; Meet: Meet tab must be active)
    public string DiscordClientId { get; set; } = "";           // app at discord.com/developers (local RPC)
    [JsonConverter(typeof(SecretConverter))] public string DiscordSecret { get; set; } = "";
    public bool DiscordSetupAsked { get; set; }                  // Discord connect guide already shown once
    public string PosVoice { get; set; } = "right";
    public string PosPhone { get; set; } = "right";              // phone connection status dot
    public bool IdlePhoneDot { get; set; } = true;              // green dot when in a voice channel
    public Dictionary<string, string> AppKeys { get; set; } = new()
    {
        ["discord.mic"] = "ctrl+shift+m", ["discord.deaf"] = "ctrl+shift+d",
        ["meet.mic"] = "ctrl+d", ["meet.cam"] = "ctrl+e",
    };
    public Dictionary<string, bool> NotifApps { get; set; } = new();

    public bool ShowPonytail { get; set; } = true;
    public bool ShowCost { get; set; } = true;
    public double UsageRefreshSec { get; set; } = 180;   // any value from 1 second; server still rate-limits via 429
    public int WarnAt { get; set; } = 70;
    public int CritAt { get; set; } = 90;

    // monitor
    public bool MonGpu { get; set; } = true;
    public bool MonTop { get; set; } = true;
    public bool AppUsageOn { get; set; }                       // log screen time per app locally (opt-in)

    // calendar
    public bool WeekMonday { get; set; } = true;
    public bool ShowHolidays { get; set; } = true;

    // prayer
    public string PrayerMethod { get; set; } = "kemenag";      // kemenag | mwl | isna | egypt | karachi
    public bool AsrHanafi { get; set; }
    public int Ihtiyat { get; set; } = 2;
    public bool ImsakOn { get; set; }                          // imsak reminder (10 minutes before Subuh) and "break fast" badge at Maghrib
    public string WorshipMode { get; set; } = "sholat";        // sholat | other (custom weekly schedule) | off
    public string WorshipText { get; set; } = "";              // "min 08:00 Kebaktian; jum,sab 18.30 Latihan koor"
    public bool PrayerResumeAsk { get; set; } = true;          // after pausing music for prayer, pill offers to resume it
    public bool WeatherOn { get; set; }                        // open-meteo weather (location coordinates sent to open-meteo.com)
    public bool RainAlert { get; set; } = true;                // 2-hour rain forecast alert (when weather is on)
    public bool PrayerAlert { get; set; } = true;
    public bool PrayerPauseMusic { get; set; }

    // phone: persistent token so links can be saved/bookmarked; fixed port so firewall rule is one-time
    [JsonConverter(typeof(SecretConverter))] public string ShareToken { get; set; } = "";
    public int SharePort { get; set; } = 47890;
    public bool ShareAuto { get; set; }
    public bool SendToOn { get; set; } = true;                 // "ZaxLet HP" shortcut in Explorer SendTo menu
    public string ShareQr { get; set; } = "lan";               // lan | tailscale
    public int CamRes { get; set; } = 720;                      // phone camera frame height
    public int CamFps { get; set; } = 15;
    public bool CamFront { get; set; }
    public bool VCamOn { get; set; }                            // virtual camera auto-starts when ZaxLet launches
    public string VCamFit { get; set; } = "crop";               // crop (fill) | fit (full frame, black bars)
    public int VCamRot { get; set; }                            // 0 | 90 | 180 | 270
    public bool VCamMirror { get; set; }
    public string VCamRatio { get; set; } = "16:9";             // 1:1 | 4:3 | 9:16 | 16:9
    public bool VCamAuto { get; set; } = true;                  // phone tilted (landscape) = auto full 16:9

    // tools
    public string CapDir { get; set; } = "";
    public bool CapClipboard { get; set; } = true;
    public int RecFps { get; set; } = 30;

    // water: gentle reminder, only during active hours, and (smart mode) only when intake falls behind schedule
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
    // ZAXLET_DATA_DIR: alternate data folder (test without touching real data)
    public static string Dir => Environment.GetEnvironmentVariable("ZAXLET_DATA_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZaxLet");
    public static double TzHours => TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalHours;

    public static void Load()
    {
        var s = Store.Load<Settings>("settings.json");
        Cur = s ?? new Settings(); UI.SetAccent(Cur.Accent); L.Init(Cur.Lang);
        if (s != null) Save();   // rewrite legacy plaintext secrets as encrypted (one-time migration)
    }
    public static void Save() => Store.Save("settings.json", Cur);

    /// <summary>Save then notify windows/tabs to refresh.</summary>
    public static void Apply() { UI.SetAccent(Cur.Accent); Save(); Applied?.Invoke(); }

    public static void Reset() { Cur = new Settings(); Apply(); }
}

/// <summary>Small JSON store in %LOCALAPPDATA%\ZaxLet; read/write failures must not crash the app.</summary>
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
                File.Copy(p, p + ".bad", true);   // corrupt file: keep a copy before the next Save overwrites it
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
            File.Move(p + ".tmp", p, true);   // atomic replace: mid-write crash does not corrupt the old file
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
