using System;
using System.Diagnostics;

namespace ZaxLet;

/// <summary>Akhiri proses dari tab Monitor: hanya aplikasi biasa. Proses sistem (di folder Windows, atau jalurnya tidak terbaca) dan ZaxLet sendiri ditolak.</summary>
public static class ProcGuard
{
    public static bool CanKill(string name, string path) =>
        !string.IsNullOrEmpty(path) && !string.Equals(name, "ZaxLet", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Akhiri semua proses ber-nama itu yang lolos CanKill. Baris Monitor menjumlahkan per nama, jadi semuanya ikut.</summary>
    public static void Kill(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { if (p.Id != Environment.ProcessId && CanKill(name, p.MainModule?.FileName)) p.Kill(); }
            catch { /* sudah keluar, atau akses ditolak (proses admin) */ }
            finally { p.Dispose(); }
        }
    }
}
