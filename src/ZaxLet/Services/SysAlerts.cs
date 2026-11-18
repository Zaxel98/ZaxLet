using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace ZaxLet;

/// <summary>
/// Peringatan lembut di pill untuk suhu CPU/GPU tinggi (> 90 °C), baterai laptop rendah (<= 15%), baterai penuh (100%), dan ruang drive sistem hampir penuh.
/// Memakai histeresis agar tiap episode hanya memperingatkan sekali sampai kondisi kembali aman.
/// </summary>
public sealed class SysAlerts
{
    const double TempHighC = 90;
    const double TempResetC = 80;
    const int BatLowPct = 15;
    const int BatLowResetPct = 25;
    const int BatFullPct = 100;
    const double DiskLowGb = 10;
    const double DiskLowFrac = 0.08;
    const double DiskResetGb = 12;
    const double DiskResetFrac = 0.10;
    const double IntervalSec = 30;
    const double SaverIntervalSec = 90;
    const double AlertSec = 8;

    public static readonly SysAlerts I = new();

    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(IntervalSec) };
    readonly SysMon _mon = new();
    bool _busy, _cpuHot, _gpuHot, _batLow, _batFull, _diskLow;

    SysAlerts() { _t.Tick += async (_, _) => await TickAsync(); }

    public void Start() => _t.Start();

    static void Ui(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    async Task TickAsync()
    {
        var iv = TimeSpan.FromSeconds(PowerSaver.On ? SaverIntervalSec : IntervalSec);
        if (_t.Interval != iv) _t.Interval = iv;
        if (_busy) return;
        _busy = true;
        try
        {
            CheckBattery();
            await _mon.SampleDisksAsync();
            CheckSystemDisk();
            await _mon.SampleTempsAsync();
            CheckTemp(_mon.CpuTemp, ref _cpuHot, "Suhu CPU tinggi: {0:0}°C");
            CheckTemp(_mon.GpuTemp, ref _gpuHot, "Suhu GPU tinggi: {0:0}°C");
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _busy = false; }
    }

    static void CheckTemp(double? temp, ref bool active, string fmt)
    {
        if (temp is not { } c) return;
        if (c > TempHighC)
        {
            if (active) return;
            active = true;
            Ui(() => IslandWindow.Instance?.Alert(L.F(fmt, c), AlertSec));
        }
        else if (c < TempResetC)
        {
            active = false;
        }
    }

    void CheckBattery()
    {
        try
        {
            var ps = WF.SystemInformation.PowerStatus;
            if ((ps.BatteryChargeStatus & WF.BatteryChargeStatus.NoSystemBattery) != 0) return;

            int pct = (int)Math.Round(Math.Clamp(ps.BatteryLifePercent, 0, 1) * 100);
            bool charging = ps.PowerLineStatus == WF.PowerLineStatus.Online;

            if (!charging && pct <= BatLowPct)
            {
                if (!_batLow)
                {
                    _batLow = true;
                    Ui(() => IslandWindow.Instance?.Alert(L.F("Baterai tinggal {0}%", pct), AlertSec));
                }
            }
            else if (pct > BatLowResetPct)
            {
                _batLow = false;
            }

            if (charging && pct >= BatFullPct)
            {
                if (!_batFull)
                {
                    _batFull = true;
                    Ui(() => IslandWindow.Instance?.Alert(L.T("Baterai penuh 100%"), AlertSec));
                }
            }
            else if (!charging)
            {
                _batFull = false;
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void CheckSystemDisk()
    {
        try
        {
            var sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(sysRoot)) return;
            var sysDrive = sysRoot.TrimEnd('\\', '/');
            foreach (var d in _mon.Drives)
            {
                if (!string.Equals(d.Letter, sysDrive, StringComparison.OrdinalIgnoreCase)) continue;
                if (d.FreeGb < DiskLowGb || d.FreeFrac < DiskLowFrac)
                {
                    if (!_diskLow)
                    {
                        _diskLow = true;
                        Ui(() => IslandWindow.Instance?.Alert(L.F("Ruang drive {0} hampir penuh (sisa {1:0.#} GB)", d.Letter, d.FreeGb), AlertSec));
                    }
                }
                else if (d.FreeGb > DiskResetGb && d.FreeFrac > DiskResetFrac)
                {
                    _diskLow = false;
                }
                break;
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
