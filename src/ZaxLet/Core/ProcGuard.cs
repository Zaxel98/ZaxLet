using System;
using System.Diagnostics;

namespace ZaxLet;

/// <summary>Terminate a process from the Monitor tab: regular apps only. System processes (in the Windows folder, or whose path is unreadable) and ZaxLet itself are rejected.</summary>
public static class ProcGuard
{
    public static bool CanKill(string name, string path) =>
        !string.IsNullOrEmpty(path) && !string.Equals(name, "ZaxLet", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Terminate all processes with that name that pass CanKill. Monitor rows aggregate by name, so all of them are included.</summary>
    public static void Kill(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { if (p.Id != Environment.ProcessId && CanKill(name, p.MainModule?.FileName)) p.Kill(); }
            catch { /* already exited, or access denied (admin process) */ }
            finally { p.Dispose(); }
        }
    }
}
