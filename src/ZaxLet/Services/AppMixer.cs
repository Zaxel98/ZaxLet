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

    static AudioEndpoint.IEnumerator _enum;
    static readonly Dictionary<uint, (string Name, string Path)> _proc = new();

    /// <summary>Panggil f untuk tiap sesi aktif/tidak aktif (bukan kedaluwarsa) milik proses nyata. Kembalikan true dari f untuk menahan objek COM sesi.</summary>
    static void Walk(Func<uint, IVol, bool> f)
    {
        AudioEndpoint.IDevice dev = null; object o = null; IEnum list = null;
        try
        {
            _enum ??= (AudioEndpoint.IEnumerator)new AudioEndpoint.MMDeviceEnumeratorCom();
            if (_enum.GetDefaultAudioEndpoint(AudioEndpoint.Render, 0, out dev) != 0) { ResetEnum(); return; }
            var iid = typeof(IManager2).GUID;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0 || ((IManager2)o).GetSessionEnumerator(out list) != 0) return;
            list.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                object s = null;
                bool keep = false;
                try
                {
                    if (list.GetSession(i, out s) != 0) continue;
                    var c = (IControl2)s;
                    if (c.GetState(out int st) != 0 || st == 2 || c.GetProcessId(out uint pid) != 0 || pid == 0) continue;   // 2 = kedaluwarsa; pid 0 = suara sistem
                    keep = f(pid, (IVol)s);
                }
                finally { if (s != null && !keep) Marshal.ReleaseComObject(s); }
            }
        }
        catch { ResetEnum(); /* perangkat hilang atau sesi tertutup di tengah jalan */ }
        finally
        {
            if (list != null) Marshal.ReleaseComObject(list);
            if (o != null) Marshal.ReleaseComObject(o);
            if (dev != null) Marshal.ReleaseComObject(dev);
        }
    }

    static void ResetEnum()
    {
        if (_enum == null) return;
        try { Marshal.ReleaseComObject(_enum); } catch { }
        _enum = null;
    }

    static (string Name, string Path)? Proc(uint pid)
    {
        if (_proc.TryGetValue(pid, out var cached)) return cached;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string path = null;
            try { path = p.MainModule?.FileName; } catch { }
            var res = (p.ProcessName, path);
            _proc[pid] = res;
            return res;
        }
        catch { return null; }
    }

    public static bool SameApps(IReadOnlyList<Row> a, IReadOnlyList<Row> b)
    {
        if (a == null || b == null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    public static List<Row> List()
    {
        var map = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<uint>();
        Walk((pid, v) =>
        {
            seen.Add(pid);
            if (Proc(pid) is not var (name, path) || v.GetMasterVolume(out float vol) != 0 || v.GetMute(out bool mute) != 0) return false;
            map[name] = map.TryGetValue(name, out var r) ? r with { Vol = Math.Max(r.Vol, vol), Mute = r.Mute && mute } : new Row(name, path, vol, mute);
            return false;
        });
        if (_proc.Count > seen.Count)
        {
            var dead = new List<uint>();
            foreach (var k in _proc.Keys) if (!seen.Contains(k)) dead.Add(k);
            foreach (var k in dead) _proc.Remove(k);
        }
        var rows = new List<Row>(map.Values);
        rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    /// <summary>Atur volume semua sesi milik aplikasi ber-nama itu; level di atas 0 sekaligus membuka bisu.</summary>
    public static void Set(string name, float level)
    {
        var g = Guid.Empty;
        float clamped = Math.Clamp(level, 0f, 1f);
        Walk((pid, v) =>
        {
            if (Proc(pid) is var (n, _) && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                v.SetMasterVolume(clamped, ref g);
                if (clamped > 0) v.SetMute(false, ref g);
            }
            return false;
        });
    }

    /// <summary>Bisukan atau bunyikan semua sesi milik aplikasi ber-nama itu.</summary>
    public static void SetMute(string name, bool mute)
    {
        var g = Guid.Empty;
        Walk((pid, v) =>
        {
            if (Proc(pid) is var (n, _) && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                v.SetMute(mute, ref g);
            return false;
        });
    }

    /// <summary>Pegangan sesi audio selama menyeret slider agar tidak membaca ulang semua proses tiap gerakan.</summary>
    public sealed class DragHandle : IDisposable
    {
        readonly List<IVol> _vols = new();

        internal DragHandle(string name) => Walk((pid, v) =>
        {
            if (Proc(pid) is var (n, _) && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                _vols.Add(v);
                return true;
            }
            return false;
        });

        public void Set(float level)
        {
            var g = Guid.Empty;
            float clamped = Math.Clamp(level, 0f, 1f);
            foreach (var v in _vols)
            {
                try
                {
                    v.SetMasterVolume(clamped, ref g);
                    if (clamped > 0) v.SetMute(false, ref g);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            foreach (var v in _vols)
                try { Marshal.ReleaseComObject(v); } catch { }
            _vols.Clear();
        }
    }

    public static DragHandle BeginDrag(string name) => new(name);
}
