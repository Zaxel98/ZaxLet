using System;

namespace ZaxLet;

/// <summary>
/// Auto power-saver mode: active when the laptop runs on battery and the remaining charge is below the threshold (Settings.SaverPct, 0 = off).
/// When active, fast updates are throttled (karaoke 33 ms to 250 ms, Monitor tab 1 s to 3 s). Status is read at most once every 20 s.
/// </summary>
public static class PowerSaver
{
    /// <summary>Pure, tested: save power if on battery, threshold is active, and percentage is at or below threshold.</summary>
    public static bool Decide(bool onBattery, int pct, int threshold) => threshold > 0 && onBattery && pct >= 0 && pct <= threshold;

    static bool _on;
    static DateTime _at = DateTime.MinValue;

    public static bool On
    {
        get
        {
            if ((DateTime.Now - _at).TotalSeconds < 20) return _on;
            _at = DateTime.Now;
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                _on = Decide(ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline, (int)Math.Round(ps.BatteryLifePercent * 100), Settings.Cur.SaverPct);
            }
            catch { _on = false; }
            return _on;
        }
    }

    /// <summary>Called when settings change so the new threshold takes effect immediately.</summary>
    public static void Reset() => _at = DateTime.MinValue;
}
