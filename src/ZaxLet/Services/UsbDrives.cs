using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ZaxLet;

/// <summary>Deteksi flashdisk USB dan lepas aman (eject) tanpa hak admin lewat cfgmgr32 + SetupAPI.</summary>
public static class UsbDrives
{
    static readonly object _lock = new();
    static char? _lastArrival;

    /// <summary>Ubah bitmask unitmask dari DEV_BROADCAST_VOLUME menjadi daftar huruf drive ('A'..'Z').</summary>
    public static List<char> LettersFromMask(uint unitmask)
    {
        var list = new List<char>();
        for (int i = 0; i < 26; i++)
            if ((unitmask & (1u << i)) != 0) list.Add((char)('A' + i));
        return list;
    }

    /// <summary>Ambil huruf drive dari argumen perintah ("e", "E:", "e:\").</summary>
    public static bool TryParseDriveLetter(string arg, out char letter)
    {
        letter = '\0';
        var s = (arg ?? "").Trim().TrimEnd('\\', '/').TrimEnd(':');
        if (s.Length != 1) return false;
        char c = char.ToUpperInvariant(s[0]);
        if (c < 'A' || c > 'Z') return false;
        letter = c;
        return true;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{Math.Round(bytes / (1024.0 * 1024 * 1024))} GB";
        if (bytes >= 1024L * 1024) return $"{Math.Round(bytes / (1024.0 * 1024))} MB";
        return $"{Math.Max(1, bytes / 1024)} KB";
    }

    public static void NoteArrival(char letter)
    {
        lock (_lock) _lastArrival = char.ToUpperInvariant(letter);
    }

    public static char? NewestRemovableDrive()
    {
        char? last;
        lock (_lock) last = _lastArrival;
        if (last is { } c && IsRemovable(c)) return c;
        try
        {
            var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable && d.Name.Length > 0).ToList();
            if (drives.Count > 0) return char.ToUpperInvariant(drives[^1].Name[0]);
        }
        catch { }
        return null;
    }

    public static bool IsRemovable(char letter)
    {
        try
        {
            var c = char.ToUpperInvariant(letter);
            if (c < 'C' || c > 'Z') return false;   // abaikan floppy A:/B:
            return new DriveInfo(c + ":\\").DriveType == DriveType.Removable;
        }
        catch { return false; }
    }

    public static void OpenExplorer(char letter)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"{char.ToUpperInvariant(letter)}:\\") { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }

    public static (bool Ok, string Message) Eject(char? letter = null)
    {
        var target = letter ?? NewestRemovableDrive();
        if (target == null) return (false, L.T("Tidak ada flashdisk terpasang"));
        char c = char.ToUpperInvariant(target.Value);
        if (!IsRemovable(c)) return (false, L.F("Drive {0}: bukan flashdisk", c));

        try
        {
            if (!TryGetStorageDeviceNumber($@"\\.\{c}:", out var devType, out var devNum))
                return (false, L.F("Gagal membaca perangkat {0}:", c));

            if (!TryFindDiskDevInst(devType, devNum, out var devInst))
                return (false, L.F("Perangkat {0}: tidak ditemukan", c));

            if (CM_Get_Parent(out var parentInst, devInst, 0) != 0)
                return (false, L.F("Gagal melepas {0}:", c));

            var veto = new char[260];
            int cr = CM_Request_Device_EjectW(parentInst, out _, veto, veto.Length, 0);
            if (cr == 0) return (true, L.F("Aman dicabut ({0}:)", c));
            if (cr == CR_REMOVE_VETOED) return (false, L.T("Masih dipakai: tutup berkas dulu"));
            return (false, L.F("Gagal melepas {0}:", c));
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return (false, L.F("Gagal melepas {0}:", c));
        }
    }

    const int CR_REMOVE_VETOED = 0x00000017;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    static readonly Guid GUID_DEVINTERFACE_DISK = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

    [StructLayout(LayoutKind.Sequential)]
    struct STORAGE_DEVICE_NUMBER { public uint DeviceType, DeviceNumber, PartitionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize, out STORAGE_DEVICE_NUMBER lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr detailData, int detailSize, out int requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Request_Device_EjectW(uint dnDevInst, out uint pVetoType, [Out] char[] pszVetoName, int ulNameLength, uint ulFlags);

    static bool TryGetStorageDeviceNumber(string path, out uint deviceType, out uint deviceNumber)
    {
        deviceType = deviceNumber = 0;
        var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE) return false;
        try
        {
            if (!DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, out var sdn, Marshal.SizeOf<STORAGE_DEVICE_NUMBER>(), out _, IntPtr.Zero))
                return false;
            deviceType = sdn.DeviceType;
            deviceNumber = sdn.DeviceNumber;
            return true;
        }
        finally { CloseHandle(h); }
    }

    static bool TryFindDiskDevInst(uint targetType, uint targetNum, out uint devInst)
    {
        devInst = 0;
        var guid = GUID_DEVINTERFACE_DISK;
        var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) return false;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
            {
                var devData = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out int req, ref devData);
                if (req <= 4) continue;
                var buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, buf, req, out _, ref devData)) continue;
                    string devPath = Marshal.PtrToStringUni(buf + 4);
                    if (TryGetStorageDeviceNumber(devPath, out var t, out var n) && t == targetType && n == targetNum)
                    {
                        devInst = devData.DevInst;
                        return true;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return false;
    }
}
