using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ZaxLet;

/// <summary>Daftar dan pengalih perangkat keluaran suara aktif Windows (Core Audio + IPolicyConfig, tanpa paket tambahan).</summary>
public static class AudioOutput
{
    public sealed record Device(string Id, string Name, bool IsDefault);

    const int FlowRender = 0;
    const int StateActive = 1;
    const int StgmRead = 0;
    const int RoleConsole = 0, RoleMultimedia = 1, RoleCommunications = 2;
    const ushort VtLpwstr = 31;

    static PropertyKey FriendlyNameKey = new() { FmtId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Pid = 14 };

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid FmtId; public uint Pid; }

    [StructLayout(LayoutKind.Explicit)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
        [FieldOffset(16)] public ulong _pad;
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigCom { }

    // Antarmuka COM tidak resmi Windows (Win7-Win11) untuk mengganti perangkat audio bawaan.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, int eRole);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, int bVisible);
    }

    static void Release(object com)
    {
        if (com != null)
        {
            try { Marshal.ReleaseComObject(com); } catch { }
        }
    }

    static string ReadFriendlyName(IMMDevice dev)
    {
        IPropertyStore props = null;
        var pv = new PropVariant();
        try
        {
            if (dev.OpenPropertyStore(StgmRead, out props) != 0 || props == null) return null;
            var key = FriendlyNameKey;
            if (props.GetValue(ref key, out pv) != 0) return null;
            if (pv.vt == VtLpwstr && pv.pwszVal != IntPtr.Zero)
                return Marshal.PtrToStringUni(pv.pwszVal);
        }
        catch { }
        finally
        {
            try { PropVariantClear(ref pv); } catch { }
            Release(props);
        }
        return null;
    }

    /// <summary>Daftar perangkat keluaran suara aktif beserta penanda perangkat bawaan saat ini.</summary>
    public static List<Device> List() => TryList(out var devices) ? devices : new List<Device>();

    /// <summary>Baca daftar perangkat keluaran suara aktif; kembalikan false bila enumerasi gagal.</summary>
    public static bool TryList(out List<Device> devices)
    {
        devices = new List<Device>();
        IMMDeviceEnumerator en = null;
        IMMDeviceCollection col = null;
        IMMDevice defDev = null;
        try
        {
            // Pakai tipe RCW tunggal AudioEndpoint.MMDeviceEnumeratorCom agar tidak bentrok dengan cache RCW COM singleton.
            en = (IMMDeviceEnumerator)new AudioEndpoint.MMDeviceEnumeratorCom();
            string defaultId = null;
            if (en.GetDefaultAudioEndpoint(FlowRender, RoleConsole, out defDev) == 0 && defDev != null)
                defDev.GetId(out defaultId);

            if (en.EnumAudioEndpoints(FlowRender, StateActive, out col) != 0 || col == null) return false;
            if (col.GetCount(out uint count) != 0) return false;

            for (uint i = 0; i < count; i++)
            {
                IMMDevice dev = null;
                try
                {
                    if (col.Item(i, out dev) != 0 || dev == null) continue;
                    if (dev.GetId(out var id) != 0 || string.IsNullOrEmpty(id)) continue;
                    var name = ReadFriendlyName(dev);
                    if (string.IsNullOrWhiteSpace(name)) name = id;
                    devices.Add(new Device(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception ex) { App.Log(ex); }
                finally { Release(dev); }
            }
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
        finally
        {
            Release(defDev);
            Release(col);
            Release(en);
        }
    }

    /// <summary>Jadikan perangkat dengan id tertentu sebagai keluaran suara utama (Console, Multimedia, Communications).</summary>
    public static bool SetDefault(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        IPolicyConfig cfg = null;
        try
        {
            cfg = (IPolicyConfig)new PolicyConfigCom();
            int r0 = cfg.SetDefaultEndpoint(deviceId, RoleConsole);
            int r1 = cfg.SetDefaultEndpoint(deviceId, RoleMultimedia);
            cfg.SetDefaultEndpoint(deviceId, RoleCommunications);
            return r0 == 0 && r1 == 0;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
        finally { Release(cfg); }
    }
}
