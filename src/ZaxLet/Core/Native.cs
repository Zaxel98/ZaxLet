using System;
using System.Runtime.InteropServices;

namespace ZaxLet;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcPerf { public long Idle, Kernel, User, Dpc, Interrupt; public uint IntCount; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MemStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MemStatus m);
    [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Rect r, int size);

    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;

    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MonitorInfo mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    /// <summary>True selama tombol Alt (kiri/kanan) sedang ditahan, di aplikasi mana pun.</summary>
    public static bool AltDown() => (GetAsyncKeyState(0x12) & 0x8000) != 0;

    /// <summary>True bila jendela aktif (bukan milik kita / desktop / taskbar) menutupi seluruh monitor utama.</summary>
    public static bool ForegroundFullscreenOnPrimary()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(h, out var pid);
        if (pid == Environment.ProcessId) return false;
        var sb = new System.Text.StringBuilder(64);
        GetClassName(h, sb, 64);
        if (sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow") return false;
        var primary = MonitorFromPoint(new Point(), 1);
        if (MonitorFromWindow(h, 2) != primary) return false;
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(primary, ref mi) || !GetWindowRect(h, out var r)) return false;
        return r.Left <= mi.Monitor.Left && r.Top <= mi.Monitor.Top && r.Right >= mi.Monitor.Right && r.Bottom >= mi.Monitor.Bottom;
    }

    public static Rect? ForegroundBounds()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        return DwmGetWindowAttribute(h, 9, out var r, Marshal.SizeOf<Rect>()) == 0 ? r : null;
    }
}
