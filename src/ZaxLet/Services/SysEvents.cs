using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace ZaxLet;

/// <summary>Volume dan bisu perangkat suara bawaan (Core Audio, tanpa paket tambahan). flow: 0 keluaran (speaker), 1 masukan (mikrofon).</summary>
public static class AudioEndpoint
{
    public const int Render = 0, Capture = 1;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
        [PreserveSig] int GetChannelCount(out uint c);
        [PreserveSig] int SetMasterVolumeLevel(float l, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float l, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float l);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float l);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float l, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float l, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float l);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float l);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
    }

    static T With<T>(int flow, Func<IVolume, T> f, T fail = default)
    {
        object vol = null; IDevice dev = null; IEnumerator en = null;
        try
        {
            en = (IEnumerator)new MMDeviceEnumeratorCom();
            if (en.GetDefaultAudioEndpoint(flow, 0, out dev) != 0) return fail;
            var iid = typeof(IVolume).GUID;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out vol) != 0) return fail;
            return f((IVolume)vol);
        }
        catch { return fail; }
        finally
        {
            if (vol != null) Marshal.ReleaseComObject(vol);
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    public static float? Volume(int flow = Render) => With<float?>(flow, v => v.GetMasterVolumeLevelScalar(out var l) == 0 ? (float?)l : null);
    public static bool? Muted(int flow = Render) => With<bool?>(flow, v => v.GetMute(out var m) == 0 ? (bool?)m : null);

    public static bool SetVolume(float level, bool unmute = true, int flow = Render)
    {
        var g = Guid.Empty;
        return With(flow, v => v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref g) == 0 && (!unmute || level <= 0 || v.SetMute(false, ref g) == 0));
    }
}

/// <summary>Kecerahan layar internal lewat WMI (late-bound, tanpa System.Management). Null di PC tanpa layar internal.</summary>
public static class Brightness
{
    static dynamic _svc;
    static int _fails;

    public static int? Get()
    {
        if (_fails >= 3) return null;
        try
        {
            if (_svc == null)
            {
                dynamic loc = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator"));
                _svc = loc.ConnectServer(".", @"root\wmi");
            }
            foreach (dynamic o in (System.Collections.IEnumerable)_svc.ExecQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"))
            {
                _fails = 0;
                return Convert.ToInt32(o.CurrentBrightness);
            }
            _fails++;   // tidak ada layar yang bisa diatur (PC desktop)
        }
        catch { _fails++; _svc = null; }
        return null;
    }
}

/// <summary>Perangkat Bluetooth yang tersambung/terputus (WinRT DeviceWatcher). Perangkat yang sudah tersambung saat mulai tidak dilaporkan.</summary>
public sealed class BluetoothWatch
{
    readonly Dictionary<string, string> _names = new();
    readonly List<DeviceWatcher> _w = new();
    public event Action<string, bool> Changed;   // nama, tersambung?

    public void Start()
    {
        try
        {
            Add(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
            Add(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void Add(string aqs)
    {
        var w = DeviceInformation.CreateWatcher(aqs);
        bool ready = false;
        w.Added += (_, d) => { lock (_names) _names[d.Id] = d.Name; if (ready) Changed?.Invoke(d.Name, true); };
        w.Removed += (_, u) =>
        {
            string n;
            lock (_names) { if (!_names.Remove(u.Id, out n)) return; }
            if (ready) Changed?.Invoke(n, false);
        };
        w.EnumerationCompleted += (_, _) => ready = true;
        w.Start();
        _w.Add(w);
    }
}

/// <summary>Aplikasi yang sedang memakai mikrofon/kamera, dari catatan izin Windows (CapabilityAccessManager): LastUsedTimeStart > LastUsedTimeStop berarti sedang dipakai.</summary>
public static class PrivacyWatch
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    public static List<string> InUse(string capability)   // "microphone" | "webcam"
    {
        var r = new List<string>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Root + capability);
            if (k == null) return r;
            foreach (var n in k.GetSubKeyNames())
            {
                using var s = k.OpenSubKey(n);
                if (n == "NonPackaged") { foreach (var m in s.GetSubKeyNames()) { using var t = s.OpenSubKey(m); if (Active(t)) r.Add(Pretty(m)); } }
                else if (Active(s)) r.Add(Pretty(n));
            }
        }
        catch { }
        return r;
    }

    static bool Active(RegistryKey k)
    {
        if (k?.GetValue("LastUsedTimeStart") is not long start || start <= 0) return false;
        long stop = k.GetValue("LastUsedTimeStop") is long x ? x : 0;
        if (start <= stop) return false;
        return DateTime.UtcNow - DateTime.FromFileTimeUtc(start) < TimeSpan.FromHours(12);   // catatan yang tak pernah ditutup (aplikasi mati mendadak) dianggap basi
    }

    /// <summary>"C:#Program Files#Google#Chrome#Application#chrome.exe" -> "chrome"; "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "Microsoft.WindowsCamera".</summary>
    public static string Pretty(string key)
    {
        int i = key.LastIndexOf('#');
        if (i >= 0) return Path.GetFileNameWithoutExtension(key[(i + 1)..]);
        int u = key.IndexOf('_');
        return u > 0 ? key[..u] : key;
    }
}

/// <summary>Nama dan jenis jaringan aktif (0 Publik, 1 Pribadi, 2 Domain), dan apakah aturan firewall ZaxLet HP mencakup jaringan Publik.</summary>
public static class NetInfo
{
    public sealed record Net(string Name, int Category)
    {
        public string Kind => Category == 0 ? "Publik" : Category == 2 ? "Domain" : "Pribadi";
    }

    public static Net Current()
    {
        try
        {
            // NetworkListManager (COM, late-bound): boleh dibaca pengguna biasa, tidak seperti registri NetworkList di HKLM
            dynamic nlm = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));
            foreach (dynamic c in (System.Collections.IEnumerable)nlm.GetNetworkConnections())
            {
                dynamic n = c.GetNetwork();
                if (!(bool)n.IsConnectedToInternet) continue;
                return new Net((string)n.GetName(), (int)n.GetCategory());
            }
        }
        catch { }
        return null;
    }

    /// <summary>Aturan "ZaxLet HP" ada, aktif, dan berlaku untuk profil Publik (tanpa batasan Profile= berarti semua profil).</summary>
    public static bool FirewallOpenToPublic()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
            foreach (var n in k.GetValueNames())
            {
                if (k.GetValue(n) is not string v || !v.Contains("|Name=ZaxLet HP|", StringComparison.Ordinal)) continue;
                if (!v.Contains("|Active=TRUE|", StringComparison.OrdinalIgnoreCase)) continue;
                var prof = v.Split('|').FirstOrDefault(p => p.StartsWith("Profile=", StringComparison.Ordinal));
                if (prof == null || prof.Contains("Public", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }
}
