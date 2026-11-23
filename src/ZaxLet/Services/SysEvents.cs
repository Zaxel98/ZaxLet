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

/// <summary>Default audio device volume and mute (Core Audio, no extra packages). flow: 0 output (speaker), 1 input (microphone).</summary>
public static class AudioEndpoint
{
    public const int Render = 0, Capture = 1;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDevice
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

    public static bool SetMuted(bool mute, int flow = Render)
    {
        var g = Guid.Empty;
        return With(flow, v => v.SetMute(mute, ref g) == 0);
    }

    public static bool SetVolume(float level, bool unmute = true, int flow = Render)
    {
        var g = Guid.Empty;
        return With(flow, v => v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref g) == 0 && (!unmute || level <= 0 || v.SetMute(false, ref g) == 0));
    }
}

/// <summary>Internal screen brightness via WMI (late-bound, without System.Management). Null on PCs without an internal display.</summary>
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
            _fails++;   // no controllable display (desktop PC)
        }
        catch { _fails++; _svc = null; }
        return null;
    }
}

/// <summary>Connected/disconnected Bluetooth devices (WinRT DeviceWatcher). Devices already connected at startup are not reported.</summary>
public sealed class BluetoothWatch
{
    readonly Dictionary<string, string> _names = new();
    readonly HashSet<string> _checked = new();
    readonly HashSet<string> _stale = new();   // id still in DeviceWatcher list but Windows reports disconnected
    readonly List<DeviceWatcher> _w = new();
    System.Threading.Timer _poll;
    public event Action<string, bool> Changed;   // name, connected?
    public List<string> Connected() { lock (_names) return _names.Where(p => !_stale.Contains(p.Key)).Select(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }

    public void Start()
    {
        try
        {
            Add(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
            Add(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
            _poll = new System.Threading.Timer(_ => Reconcile(), null, 4000, 4000);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>DeviceWatcher's Removed event often never fires for classic headsets: reconcile list against Windows status every 4 seconds, both ways.</summary>
    void Reconcile()
    {
        try
        {
            KeyValuePair<string, string>[] snap;
            lock (_names) snap = _names.ToArray();
            foreach (var (id, name) in snap)
            {
                if (BtBattery.IsConnected(id) is not bool on) continue;
                bool fire;
                lock (_names)
                {
                    bool first = _checked.Add(id);   // stale entry from startup (headset already off when ZaxLet started): hide without warning or pausing music
                    fire = on ? _stale.Remove(id) : _stale.Add(id);
                    if (first && !on) fire = false;
                }
                if (fire) Changed?.Invoke(name, on);
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void Add(string aqs)
    {
        var w = DeviceInformation.CreateWatcher(aqs);
        bool ready = false;
        w.Added += (_, d) => { lock (_names) { _names[d.Id] = d.Name; _stale.Remove(d.Id); } if (ready) Changed?.Invoke(d.Name, true); };
        w.Removed += (_, u) =>
        {
            string n; bool was;
            lock (_names) { if (!_names.Remove(u.Id, out n)) return; was = !_stale.Remove(u.Id); }   // already reported disconnected by Reconcile: don't double-fire
            if (ready && was) Changed?.Invoke(n, false);
        };
        w.EnumerationCompleted += (_, _) => ready = true;
        w.Start();
        _w.Add(w);
    }
}

/// <summary>
/// Battery level of classic Bluetooth devices that report it to Windows (headsets via Hands-Free, matching Windows Settings): device property
/// {104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2 on the "Hands-Free AG" node. Via Configuration Manager, without admin or spawning processes.
/// Devices that do not report battery (e.g. game controllers) won't appear; that is a Windows limitation, not a bug.
/// </summary>
public static class BtBattery
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buf, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint inst, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_PropertyW(uint inst, ref DevKey key, out uint type, byte[] buf, ref uint size, uint flags);

    [StructLayout(LayoutKind.Sequential)] struct DevKey { public Guid Id; public uint Pid; }

    const uint FilterEnumerator = 1, FilterPresent = 0x100, HfpType = 0x3;   // DEVPROP_TYPE_BYTE
    static DevKey Battery = new() { Id = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), Pid = 2 };
    static DevKey FriendlyName = new() { Id = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Pid = 14 };

    static DevKey IsConn = new() { Id = new Guid("83DA6326-97A6-4088-9453-A1923F573B29"), Pid = 15 };   // DEVPKEY_Device_IsConnected

    /// <summary>
    /// Actual Windows connection status for a WinRT id ("Bluetooth#Bluetooth{adapter}-{mac}"). null = unknown (node not found): do not treat as disconnected.
    /// DeviceWatcher's "Connected" list can go stale (headset powered off without Removed ever firing), so this is used as the source of truth.
    /// </summary>
    public static bool? IsConnected(string winrtId)
    {
        try
        {
            int dash = winrtId.LastIndexOf('-');
            if (dash < 0) return null;
            var mac = winrtId[(dash + 1)..].Replace(":", "").ToUpperInvariant();
            if (mac.Length != 12) return null;
            foreach (var enumerator in new[] { "BTHENUM", "BTHLE" })
            {
                if (CM_Get_Device_ID_List_SizeW(out var len, enumerator, FilterEnumerator) != 0 || len < 2) continue;
                var buf = new char[len];
                if (CM_Get_Device_ID_ListW(enumerator, buf, len, FilterEnumerator) != 0) continue;
                foreach (var id in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!id.StartsWith(enumerator + @"\DEV_" + mac, StringComparison.OrdinalIgnoreCase) || CM_Locate_DevNodeW(out var inst, id, 1) != 0) continue;   // 1 = PHANTOM: disconnected device node is not "present"
                    var b = new byte[1]; uint sz = 1;
                    if (CM_Get_DevNode_PropertyW(inst, ref IsConn, out _, b, ref sz, 0) == 0) return b[0] != 0;
                }
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return null;
    }

    /// <summary>"soundcore R50i NC Hands-Free AG" -> "soundcore R50i NC".</summary>
    public static string Clean(string n) => System.Text.RegularExpressions.Regex.Replace(n ?? "", @"\s+Hands-Free( AG)?$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    /// <summary>Device names and battery percentages for all reporting devices; empty if none.</summary>
    public static Dictionary<string, int> Read()
    {
        var r = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (CM_Get_Device_ID_List_SizeW(out var len, "BTHENUM", FilterEnumerator | FilterPresent) != 0 || len < 2) return r;
            var buf = new char[len];
            if (CM_Get_Device_ID_ListW("BTHENUM", buf, len, FilterEnumerator | FilterPresent) != 0) return r;
            foreach (var id in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!id.StartsWith(@"BTHENUM\{0000111E", StringComparison.OrdinalIgnoreCase) || CM_Locate_DevNodeW(out var inst, id, 0) != 0) continue;   // 111E = Hands-Free
                var b = new byte[1]; uint sz = 1;
                if (CM_Get_DevNode_PropertyW(inst, ref Battery, out var t, b, ref sz, 0) != 0 || b[0] > 100) continue;
                var nb = new byte[512]; uint ns = 512;
                string name = CM_Get_DevNode_PropertyW(inst, ref FriendlyName, out _, nb, ref ns, 0) == 0 ? System.Text.Encoding.Unicode.GetString(nb, 0, (int)ns).TrimEnd('\0') : id;
                r[Clean(name)] = b[0];
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return r;
    }
}

/// <summary>Apps currently using microphone/camera, from Windows permission logs (CapabilityAccessManager): LastUsedTimeStart > LastUsedTimeStop means currently in use.</summary>
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
        return DateTime.UtcNow - DateTime.FromFileTimeUtc(start) < TimeSpan.FromHours(12);   // unclosed records (app crashed) are considered stale
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

/// <summary>Active network name and type (0 Public, 1 Private, 2 Domain), and whether the ZaxLet HP firewall rule covers Public networks.</summary>
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
            // NetworkListManager (COM, late-bound): readable by standard users, unlike NetworkList registry under HKLM
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

    /// <summary>The "ZaxLet HP" rule exists, is active, and applies to the Public profile (no Profile= restriction means all profiles).</summary>
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
