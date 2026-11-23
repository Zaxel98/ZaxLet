using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace ZaxLet;

/// <summary>System actions for the Quick Actions box: lock, sleep, Windows theme, Wi-Fi/Bluetooth radios. All via official Windows APIs (no extra packages).</summary>
public static class SysActions
{
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    public static void Lock() => LockWorkStation();

    public static void Sleep() => SetSuspendState(false, false, false);   // false = sleep, not hibernate

    /// <summary>Windows app and system theme; broadcasts ImmersiveColorSet so Explorer and open apps update without signing out.</summary>
    public static void SetDark(bool dark)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
        {
            k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        }
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "ImmersiveColorSet", 2, 200, out _);   // WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
    }

    /// <summary>mode: 1 on, 0 off, -1 toggle. Result appears in the pill.</summary>
    public static async void Radio(RadioKind kind, int mode, string label)
    {
        string msg;
        try
        {
            var r = await Windows.Devices.Radios.Radio.RequestAccessAsync() == RadioAccessStatus.Allowed ? (await Windows.Devices.Radios.Radio.GetRadiosAsync()).FirstOrDefault(x => x.Kind == kind) : null;
            if (r == null) msg = L.F("{0}: tidak tersedia atau izin ditolak", label);
            else
            {
                bool on = mode < 0 ? r.State != RadioState.On : mode == 1;
                msg = await r.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed ? L.F(on ? "{0} nyala" : "{0} mati", label) : L.F("{0}: tidak tersedia atau izin ditolak", label);
            }
        }
        catch (Exception ex) { App.Log(ex); msg = L.F("{0}: tidak tersedia atau izin ditolak", label); }
        IslandWindow.Instance?.Alert(msg, 3, null, false);
    }

    static string _origPowerScheme;
    static readonly object _powerLock = new();

    static (int Code, string Output) RunPowerCfg(string args)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo("powercfg.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        p.Start();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(5000))
        {
            try { p.Kill(true); } catch { }
            return (-1, "");
        }
        Task.WaitAll(new Task[] { outTask, errTask }, 1000);
        return (p.ExitCode, outTask.Result);
    }

    /// <summary>Switch Windows power plan via powercfg (no admin) or restore the plan active before ZaxLet changed it.</summary>
    public static async void PowerPlan(string mode)
    {
        string msg;
        try
        {
            msg = await Task.Run(() =>
            {
                lock (_powerLock)
                {
                    if (mode == "kembali")
                    {
                        if (string.IsNullOrEmpty(_origPowerScheme))
                            return L.T("Belum ada rencana daya sebelumnya");
                        var (rc, _) = RunPowerCfg("/setactive " + _origPowerScheme);
                        if (rc != 0)
                            return L.T("Rencana daya ini tidak tersedia di PC ini");
                        _origPowerScheme = null;
                        return L.T("Rencana daya: kembali ke awal");
                    }

                    var (target, label) = mode switch
                    {
                        "hemat" => ("SCHEME_MAX", "Daya hemat"),
                        "seimbang" => ("SCHEME_BALANCED", "Daya seimbang"),
                        "performa" => ("SCHEME_MIN", "Daya performa"),
                        _ => (null, null)
                    };
                    if (target == null)
                        return L.T("Rencana daya ini tidak tersedia di PC ini");

                    string origGuid = null;
                    if (_origPowerScheme == null)
                    {
                        var (gc, gout) = RunPowerCfg("/getactivescheme");
                        if (gc == 0)
                        {
                            var m = Regex.Match(gout, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                            if (m.Success) origGuid = m.Value;
                        }
                    }

                    var (sc, _) = RunPowerCfg("/setactive " + target);
                    if (sc != 0)
                        return L.T("Rencana daya ini tidak tersedia di PC ini");

                    if (_origPowerScheme == null && origGuid != null)
                        _origPowerScheme = origGuid;

                    return L.F("Rencana daya: {0}", L.T(label));
                }
            });
        }
        catch (Exception ex) { App.Log(ex); msg = L.T("Rencana daya ini tidak tersedia di PC ini"); }
        IslandWindow.Instance?.Alert(msg, 3, null, false);
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern uint SetThreadExecutionState(uint esFlags);
    const uint ES_CONTINUOUS = 0x80000000;
    const uint ES_SYSTEM_REQUIRED = 0x0001;
    const uint ES_DISPLAY_REQUIRED = 0x0002;

    static readonly object _awakeLock = new();
    static Thread _awakeThread;
    static AutoResetEvent _awakeWake;
    static bool _awakeDesired;
    static bool _awakeApplied;
    static bool _awakeTimed;
    static DateTime _awakeUntilUtc;
    static long _awakeGen;
    static Timer _awakeTimer;
    static bool _awakeExitHooked;

    static void EnsureAwakeThreadLocked()
    {
        if (!_awakeExitHooked && System.Windows.Application.Current != null)
        {
            _awakeExitHooked = true;
            System.Windows.Application.Current.Exit += (_, _) => StopAwake(silent: true, expectedGen: -1);
        }
        if (_awakeThread != null) return;
        _awakeWake = new AutoResetEvent(false);
        _awakeThread = new Thread(() =>
        {
            while (true)
            {
                lock (_awakeLock)
                {
                    while (_awakeDesired != _awakeApplied)
                    {
                        bool want = _awakeDesired;
                        try
                        {
                            uint res = SetThreadExecutionState(want ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
                            if (res != 0) _awakeApplied = want;
                            else
                            {
                                App.Log(new InvalidOperationException("SetThreadExecutionState gagal: " + Marshal.GetLastWin32Error()));
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            App.Log(ex);
                            break;
                        }
                    }
                    Monitor.PulseAll(_awakeLock);
                }
                _awakeWake.WaitOne();
            }
        })
        {
            IsBackground = true,
            Name = "ZaxLet.KeepAwake"
        };
        _awakeThread.Start();
    }

    static string StopAwake(bool silent, long expectedGen, bool expired = false)
    {
        lock (_awakeLock)
        {
            if (expectedGen >= 0 && expectedGen != _awakeGen) return "";
            _awakeGen++;
            _awakeTimer?.Dispose();
            _awakeTimer = null;
            _awakeTimed = false;
            _awakeUntilUtc = default;
            _awakeDesired = false;
            if (_awakeThread != null)
            {
                _awakeWake.Set();
                var deadline = Environment.TickCount64 + 500;
                while (_awakeApplied && Environment.TickCount64 < deadline)
                    Monitor.Wait(_awakeLock, 100);
            }
        }
        string msg = expired ? L.T("Jaga layar: mati (waktu habis)") : L.T("Jaga layar: mati");
        if (!silent) IslandWindow.Instance?.Alert(msg, 3, null, false);
        return msg;
    }

    static string StartAwake(int minutes)
    {
        try
        {
            lock (_awakeLock)
            {
                EnsureAwakeThreadLocked();
                long gen = ++_awakeGen;
                _awakeTimer?.Dispose();
                if (minutes > 0)
                {
                    _awakeTimed = true;
                    _awakeUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
                    _awakeTimer = new Timer(_ => StopAwake(silent: false, expectedGen: gen, expired: true), null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _awakeTimed = false;
                    _awakeUntilUtc = default;
                    _awakeTimer = null;
                }
                _awakeDesired = true;
                _awakeWake.Set();
                var deadline = Environment.TickCount64 + 500;
                while (!_awakeApplied && Environment.TickCount64 < deadline)
                    Monitor.Wait(_awakeLock, 100);
                if (!_awakeApplied)
                {
                    _awakeDesired = false;
                    _awakeTimer?.Dispose();
                    _awakeTimer = null;
                    _awakeTimed = false;
                    _awakeUntilUtc = default;
                    return L.T("Jaga layar: gagal mengaktifkan");
                }
            }
            return minutes > 0 ? L.F("Jaga layar: nyala ({0} menit)", minutes) : L.T("Jaga layar: nyala");
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return L.T("Jaga layar: gagal mengaktifkan");
        }
    }

    /// <summary>Keep display and PC awake temporarily via SetThreadExecutionState on a dedicated background thread.</summary>
    public static string Awake(string mode, int minutes = 0)
    {
        if (mode == "status")
        {
            lock (_awakeLock)
            {
                if (!_awakeDesired && !_awakeApplied) return L.T("Jaga layar: mati");
                if (_awakeTimed)
                {
                    int rem = Math.Max(1, (int)Math.Ceiling((_awakeUntilUtc - DateTime.UtcNow).TotalMinutes));
                    return L.F("Jaga layar: nyala ({0} menit lagi)", rem);
                }
                return L.T("Jaga layar: nyala");
            }
        }
        if (mode == "off") return StopAwake(silent: true, expectedGen: -1);
        if (mode == "toggle")
        {
            bool active;
            lock (_awakeLock) active = _awakeDesired || _awakeApplied;
            return active ? StopAwake(silent: true, expectedGen: -1) : StartAwake(0);
        }
        if (mode == "on") return StartAwake(0);
        if (mode == "timer") return StartAwake(minutes);
        return L.T("Jaga layar: mati");
    }
}
