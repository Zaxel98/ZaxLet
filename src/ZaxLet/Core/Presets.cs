using System.Linq;

namespace ZaxLet;

/// <summary>
/// One-click notification style for new users: Quiet (essentials only), Balanced, Full (all popups and idle widgets).
/// Only sets the popup and pill-content toggle group; other settings are untouched. Advanced users can still toggle individually
/// (the style becomes "Custom").
/// </summary>
public static class Presets
{
    public static void Apply(Settings s, string name)
    {
        bool quiet = name == "tenang", full = name == "lengkap";
        s.OsdOn = !quiet;                 // volume, brightness, Caps Lock, Bluetooth, microphone popups
        s.DownloadAlert = !quiet;
        s.PrivacyOn = full;               // "mic/camera in use" only in Full
        s.NotifNewDefault = !quiet;       // new phone apps: Quiet waits to be selected first
        s.NotifOngoing = full;            // ongoing notifications (Gojek, etc.) that keep updating
        s.IdleUsage = !quiet;
        s.IdleSpeed = full;
        s.IdleWave = !quiet;
        s.IdleLyrics = !quiet;
    }

    static string Key(Settings s) => string.Concat(new[] { s.OsdOn, s.DownloadAlert, s.PrivacyOn, s.NotifNewDefault, s.NotifOngoing, s.IdleUsage, s.IdleSpeed, s.IdleWave, s.IdleLyrics }.Select(b => b ? '1' : '0'));

    /// <summary>"tenang", "seimbang", "lengkap", or "custom" if the toggles do not match any of them.</summary>
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
