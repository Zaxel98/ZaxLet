using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ZaxLet;

/// <summary>USB flash drive detection and safe eject without admin rights via cfgmgr32 + SetupAPI.</summary>
public static class UsbDrives
{
    static readonly object _lock = new();
    static readonly HashSet<char> _seen = new();
    static char? _lastArrival;

    /// <summary>Convert unitmask bitmask from DEV_BROADCAST_VOLUME to a list of drive letters ('A'..'Z').</summary>
    public static List<char> LettersFromMask(uint unitmask)
    {
        var list = new List<char>();
        for (int i = 0; i < 26; i++)
            if ((unitmask & (1u << i)) != 0) list.Add((char)('A' + i));
        return list;
    }

    /// <summary>Extract drive letter from command argument ("e", "E:", "e:\").</summary>
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

    public static bool IsSeen(char letter)
    {
        var c = char.ToUpperInvariant(letter);
        lock (_lock) return _seen.Contains(c);
    }

    public static List<char> PresentDriveLetters()
    {
        uint mask = GetLogicalDrives();
        if (mask != 0) return LettersFromMask(mask);
        try { return DriveInfo.GetDrives().Where(d => d.Name.Length > 0).Select(d => char.ToUpperInvariant(d.Name[0])).ToList(); }
        catch { return new List<char>(); }
    }

    public static bool NoteArrival(char letter)
    {
        var c = char.ToUpperInvariant(letter);
        lock (_lock)
        {
            _lastArrival = c;
            return _seen.Add(c);
        }
    }

    public static void NoteRemoval(char letter)
    {
        var c = char.ToUpperInvariant(letter);
        lock (_lock)
        {
            _seen.Remove(c);
            if (_lastArrival == c) _lastArrival = null;
        }
    }

    public static void PruneRemoved(IEnumerable<char> present)
    {
        var set = new HashSet<char>(present.Select(char.ToUpperInvariant));
        lock (_lock)
        {
            _seen.RemoveWhere(c => !set.Contains(c));
            if (_lastArrival is { } c && !set.Contains(c)) _lastArrival = null;
        }
    }

    public static char? NewestRemovableDrive()
    {
        char? last;
        lock (_lock) last = _lastArrival;
        if (last is { } c && IsRemovable(c)) return c;
        try
        {
            var drives = DriveInfo.GetDrives().Where(d => d.Name.Length > 0 && IsRemovable(d.Name[0])).ToList();
            if (drives.Count > 0) return char.ToUpperInvariant(drives[^1].Name[0]);
        }
        catch { }
        return null;
    }

    public static bool IsRemovable(char letter) => IsRemovable(letter, out _);

    public static bool IsRemovable(char letter, out string diag)
    {
        var c = char.ToUpperInvariant(letter);
        if (c < 'C' || c > 'Z') { diag = $"USB {c}: DriveType=Floppy Decision=Reject Reason=Floppy"; return false; }
        if (IsSystemDrive(c)) { diag = $"USB {c}: Decision=Reject Reason=SystemDrive"; return false; }
        try
        {
            var dt = new DriveInfo(c + ":\\").DriveType;
            if (dt is DriveType.CDRom or DriveType.Network or DriveType.Ram or DriveType.Unknown or DriveType.NoRootDirectory)
            {
                diag = $"USB {c}: DriveType={dt} Decision=Reject Reason=ExcludedDriveType";
                return false;
            }
            if (dt == DriveType.Removable)
            {
                diag = $"USB {c}: DriveType={dt} Decision=Allow Reason=DriveTypeRemovable";
                return true;
            }
            if (dt != DriveType.Fixed)
            {
                diag = $"USB {c}: DriveType={dt} Decision=Reject Reason=UnsupportedDriveType";
                return false;
            }

            string path = $@"\\.\{c}:";
            int busType = GetBusType(path);
            if (busType == BusTypeUsb)
            {
                diag = $"USB {c}: DriveType={dt} BusType={busType} Caps=0x0 Decision=Allow Reason=BusTypeUsb";
                return true;
            }

            uint allCaps = 0;
            string matchedId = "";
            string reason = "InternalBus";
            bool allow = false;

            if (!TryGetStorageDeviceNumber(path, out var devType, out var devNum))
            {
                reason = "NoDeviceNumber";
            }
            else if (!TryFindDiskDevInst(devType, devNum, out var devInst))
            {
                reason = "DevInstNotFound";
            }
            else
            {
                uint node = devInst;
                for (int depth = 0; depth < 32 && node != 0; depth++)
                {
                    uint len = 4;
                    if (CM_Get_DevNode_Registry_PropertyW(node, CM_DRP_CAPABILITIES, out _, out uint caps, ref len, 0) == 0)
                    {
                        allCaps |= caps;
                        if (!allow && (caps & (CM_DEVCAP_REMOVABLE | CM_DEVCAP_SURPRISEREMOVALOK)) != 0)
                        {
                            allow = true;
                            reason = $"DevCap(0x{caps:X})";
                        }
                    }

                    var idBuf = new char[260];
                    if (CM_Get_Device_IDW(node, idBuf, idBuf.Length, 0) == 0)
                    {
                        int nul = Array.IndexOf(idBuf, '\0');
                        string devId = new(idBuf, 0, nul >= 0 ? nul : idBuf.Length);
                        if (devId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) || devId.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase))
                        {
                            if (matchedId.Length == 0) matchedId = devId;
                            if (!allow)
                            {
                                allow = true;
                                reason = devId.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase) ? "USBSTOR" : "USB";
                            }
                        }
                    }

                    if (CM_Get_Parent(out uint parent, node, 0) != 0 || parent == node) break;
                    node = parent;
                }
            }

            diag = $"USB {c}: DriveType={dt} BusType={busType} Caps=0x{allCaps:X}{(matchedId.Length > 0 ? $" Id={matchedId}" : "")} Decision={(allow ? "Allow" : "Reject")} Reason={reason}";
            return allow;
        }
        catch (Exception ex)
        {
            diag = $"USB {c}: Decision=Reject Reason=Exception({ex.GetType().Name}:{ex.Message})";
            return false;
        }
    }

    static bool IsSystemDrive(char c)
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            return !string.IsNullOrEmpty(root) && char.ToUpperInvariant(root[0]) == c;
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
    const uint CM_DRP_CAPABILITIES = 0x00000010;
    const uint CM_DEVCAP_REMOVABLE = 0x00000004;
    const uint CM_DEVCAP_SURPRISEREMOVALOK = 0x00000080;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    const int BusTypeUsb = 7;
    const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    static readonly Guid GUID_DEVINTERFACE_DISK = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

    [StructLayout(LayoutKind.Sequential)]
    struct STORAGE_DEVICE_NUMBER { public uint DeviceType, DeviceNumber, PartitionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    struct STORAGE_PROPERTY_QUERY { public uint PropertyId, QueryType, AdditionalParameters; }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("kernel32.dll")]
    static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize, out STORAGE_DEVICE_NUMBER lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, ref STORAGE_PROPERTY_QUERY lpInBuffer, int nInBufferSize, [Out] byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

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
    static extern int CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, out uint pulRegDataType, out uint buffer, ref uint pulLength, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint dnDevInst, [Out] char[] buffer, int bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Request_Device_EjectW(uint dnDevInst, out uint pVetoType, [Out] char[] pszVetoName, int ulNameLength, uint ulFlags);

    static int GetBusType(string path)
    {
        var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE) return -1;
        try
        {
            var q = new STORAGE_PROPERTY_QUERY();
            var buf = new byte[64];
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, ref q, Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(), buf, buf.Length, out int ret, IntPtr.Zero) || ret < 32)
                return -1;
            return BitConverter.ToInt32(buf, 28);
        }
        finally { CloseHandle(h); }
    }

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
