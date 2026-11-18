using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace ZaxLet;

/// <summary>Aksi sistem untuk kotak Aksi cepat: kunci, tidur, tema Windows, radio Wi-Fi/Bluetooth. Semua lewat API Windows resmi (tanpa paket tambahan).</summary>
public static class SysActions
{
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    public static void Lock() => LockWorkStation();

    public static void Sleep() => SetSuspendState(false, false, false);   // false = tidur, bukan hibernasi

    /// <summary>Tema aplikasi dan sistem Windows; siaran ImmersiveColorSet agar Explorer dan aplikasi terbuka ikut berubah tanpa masuk ulang.</summary>
    public static void SetDark(bool dark)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
        {
            k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        }
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "ImmersiveColorSet", 2, 200, out _);   // WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
    }

    /// <summary>mode: 1 nyala, 0 mati, -1 balik. Hasilnya muncul di pill.</summary>
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

    /// <summary>Ganti rencana daya Windows lewat powercfg (tanpa admin) atau pulihkan rencana sebelum diubah ZaxLet.</summary>
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
}
