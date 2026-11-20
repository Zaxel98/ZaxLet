using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace ZaxLet;

/// <summary>
/// Auto-clear ala ISLC: tiap 2 dtk baca ukuran daftar standby ("Cached" di Task Manager), begitu mencapai 1 GB langsung dibuang.
/// Butuh SeProfileSingleProcessPrivilege (admin): bila ZaxLet belum admin, pengguna menyetujui UAC sekali dan `ZaxLet.exe --ram-helper` yang bekerja.
/// Hanya berlaku selama ZaxLet hidup (tidak disimpan): default mati tiap mulai. Standby memang cache yang bisa dibuang kapan saja, jadi manfaat nyatanya kecil.
/// </summary>
public static class MemTrim
{
    const ulong Limit = 1UL << 30;
    static EventWaitHandle _stop;

    public static bool On => _stop != null;

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, ulong[] buf, int len, out int ret);
    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int cls, ref int buf, int len);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll, ref TokenPriv n, int len, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct TokenPriv { public int Count; public long Luid; public int Attr; }

    static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Nyalakan. False bila UAC ditolak.</summary>
    public static bool Start()
    {
        if (On) return true;
        var name = "zaxlet-ram-" + Guid.NewGuid().ToString("N");
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        try
        {
            if (IsAdmin()) new Thread(() => Loop(stop, null)) { IsBackground = true, Name = "ram-clear" }.Start();
            else Process.Start(new ProcessStartInfo(Environment.ProcessPath, $"--ram-helper {name} {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Exception ex)   // 1223 = pengguna menolak UAC
        {
            stop.Dispose();
            if (ex is not System.ComponentModel.Win32Exception) App.Log(ex);
            return false;
        }
        _stop = stop;
        return true;
    }

    public static void Stop() { _stop?.Set(); _stop = null; }   // event tidak di-Dispose: pembantu masih memegangnya sampai membaca sinyal

    /// <summary>`ZaxLet.exe --ram-helper NAMA PID_ZAXLET`: berjalan sebagai admin sampai event diberi sinyal atau ZaxLet berhenti.</summary>
    public static void Helper(string name, string pid)
    {
        try { Loop(EventWaitHandle.OpenExisting(name), Process.GetProcessById(int.Parse(pid))); }
        catch (Exception ex) { App.Log(ex); }
    }

    static void Loop(EventWaitHandle stop, Process parent)
    {
        if (!EnablePrivilege()) { App.Log(new Exception("SeProfileSingleProcessPrivilege tidak tersedia")); return; }
        while (!stop.WaitOne(2000) && parent?.HasExited != true)
        {
            var b = new ulong[22];   // SYSTEM_MEMORY_LIST_INFORMATION: 5 hitungan, PageCountByPriority[8] (standby), RepurposedPagesByPriority[8], 1
            if (NtQuerySystemInformation(80, b, b.Length * 8, out _) != 0) continue;
            if ((ulong)b.Skip(5).Take(8).Sum(x => (long)x) * 4096 < Limit) continue;
            int purge = 4;   // MemoryPurgeStandbyList
            NtSetSystemInformation(80, ref purge, 4);
        }
    }

    static bool EnablePrivilege()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x20 | 0x08, out var tok)) return false;   // ADJUST_PRIVILEGES | QUERY
        try
        {
            var tp = new TokenPriv { Count = 1, Attr = 2 };
            return LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out tp.Luid) && AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(tok); }
    }
}
