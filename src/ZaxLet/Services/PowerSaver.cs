using System;

namespace ZaxLet;

/// <summary>
/// Mode hemat otomatis: aktif saat laptop berjalan dari baterai dan sisanya di bawah ambang (Settings.SaverPct, 0 = mati).
/// Saat aktif, pembaruan cepat dikurangi (karaoke 33 ms jadi 250 ms, tab Monitor 1 dtk jadi 3 dtk). Status dibaca paling sering tiap 20 dtk.
/// </summary>
public static class PowerSaver
{
    /// <summary>Murni, dites: hemat bila di baterai, ambang aktif, dan persen tidak lebih dari ambang.</summary>
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

    /// <summary>Dipanggil saat pengaturan berubah supaya ambang baru langsung berlaku.</summary>
    public static void Reset() => _at = DateTime.MinValue;
}
