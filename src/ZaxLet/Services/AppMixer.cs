using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZaxLet;

/// <summary>Volume per aplikasi lewat API sesi audio Windows (IAudioSessionManager2), pada perangkat keluaran bawaan. Sesi dijumlahkan per nama proses.</summary>
public static class AppMixer
{
    public sealed record Row(string Name, string Path, float Vol, bool Mute);

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr g, uint f, [MarshalAs(UnmanagedType.IUnknown)] out object c);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr g, uint f, [MarshalAs(UnmanagedType.IUnknown)] out object v);
        [PreserveSig] int GetSessionEnumerator(out IEnum e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnum
    {
        [PreserveSig] int GetCount(out int n);
        [PreserveSig] int GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object s);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IControl2
    {
        [PreserveSig] int GetState(out int s);
        [PreserveSig] int GetDisplayName(out IntPtr n);
        [PreserveSig] int SetDisplayName(IntPtr n, IntPtr ctx);
        [PreserveSig] int GetIconPath(out IntPtr p);
        [PreserveSig] int SetIconPath(IntPtr p, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVol
    {
        [PreserveSig] int SetMasterVolume(float l, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float l);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
    }

    /// <summary>Panggil f untuk tiap sesi aktif/tidak aktif (bukan kedaluwarsa) milik proses nyata. Gagal = tidak ada yang dipanggil.</summary>
    static void Walk(Action<uint, IVol> f)
    {
        AudioEndpoint.IEnumerator en = null; AudioEndpoint.IDevice dev = null; object o = null; IEnum list = null;
        try
        {
            en = (AudioEndpoint.IEnumerator)new AudioEndpoint.MMDeviceEnumeratorCom();
            if (en.GetDefaultAudioEndpoint(AudioEndpoint.Render, 0, out dev) != 0) return;
            var iid = typeof(IManager2).GUID;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0 || ((IManager2)o).GetSessionEnumerator(out list) != 0) return;
            list.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                object s = null;
                try
                {
                    if (list.GetSession(i, out s) != 0) continue;
                    var c = (IControl2)s;
                    if (c.GetState(out int st) != 0 || st == 2 || c.GetProcessId(out uint pid) != 0 || pid == 0) continue;   // 2 = kedaluwarsa; pid 0 = suara sistem
                    f(pid, (IVol)s);
                }
                finally { if (s != null) Marshal.ReleaseComObject(s); }
            }
        }
        catch { /* perangkat hilang atau sesi tertutup di tengah jalan */ }
        finally
        {
            if (list != null) Marshal.ReleaseComObject(list);
            if (o != null) Marshal.ReleaseComObject(o);
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    static (string Name, string Path)? Proc(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string path = null;
            try { path = p.MainModule?.FileName; } catch { }
            return (p.ProcessName, path);
        }
        catch { return null; }
    }

    public static List<Row> List()
    {
        var map = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        Walk((pid, v) =>
        {
            if (Proc(pid) is not var (name, path) || v.GetMasterVolume(out float vol) != 0 || v.GetMute(out bool mute) != 0) return;
            map[name] = map.TryGetValue(name, out var r) ? r with { Vol = Math.Max(r.Vol, vol), Mute = r.Mute && mute } : new Row(name, path, vol, mute);
        });
        var rows = new List<Row>(map.Values);
        rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    /// <summary>Atur volume semua sesi milik aplikasi ber-nama itu; level di atas 0 sekaligus membuka bisu.</summary>
    public static void Set(string name, float level)
    {
        var g = Guid.Empty;
        Walk((pid, v) =>
        {
            if (Proc(pid) is var (n, _) && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                v.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref g);
                if (level > 0) v.SetMute(false, ref g);
            }
        });
    }
}
