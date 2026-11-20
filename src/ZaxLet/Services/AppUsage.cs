using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>Data waktu layar lokal: tanggal (yyyy-MM-dd) -> nama proses -> detik.</summary>
public sealed class AppUsageData
{
    public Dictionary<string, Dictionary<string, int>> Days { get; set; } = new();
}

/// <summary>
/// Pencatat waktu layar per aplikasi (lokal di appusage.json, opt-in).
/// Mengambil sampel tiap 5 detik hanya saat aktif dalam 2 menit terakhir dan menyimpan nama proses + detik per hari.
/// </summary>
public sealed class AppUsage
{
    public static readonly AppUsage I = new();

    const int SampleSec = 5;
    const int MaxIdleSec = 120;
    const int FlushEverySec = 60;
    const int KeepDays = 30;
    const string FileName = "appusage.json";

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    DispatcherTimer _t;
    AppUsageData _data;
    DateTime _lastFlush = DateTime.UtcNow;
    bool _dirty;

    /// <summary>Mulai atau hentikan pencatat sesuai pengaturan AppUsageOn.</summary>
    public void Apply()
    {
        if (Settings.Cur.AppUsageOn)
        {
            if (_data == null)
            {
                _data = Store.Load<AppUsageData>(FileName) ?? new AppUsageData();
                _data.Days ??= new Dictionary<string, Dictionary<string, int>>();
                Prune(_data.Days, DateTime.Now, KeepDays);
            }
            if (_t == null)
            {
                _t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SampleSec) };
                _t.Tick += (_, _) => Sample();
                _t.Start();
            }
        }
        else
        {
            if (_t != null)
            {
                _t.Stop();
                _t = null;
            }
            Flush();
        }
    }

    public void Flush()
    {
        if (!_dirty || _data == null) return;
        Prune(_data.Days, DateTime.Now, KeepDays);
        Store.Save(FileName, _data);
        _dirty = false;
        _lastFlush = DateTime.UtcNow;
    }

    void Sample()
    {
        if (!Settings.Cur.AppUsageOn || _data == null) return;
        if (!IsUserActive(MaxIdleSec)) return;
        var proc = ForegroundAppName();
        if (string.IsNullOrEmpty(proc)) return;

        var key = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!_data.Days.TryGetValue(key, out var day))
        {
            day = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _data.Days[key] = day;
        }
        day[proc] = day.TryGetValue(proc, out var cur) ? cur + SampleSec : SampleSec;
        _dirty = true;

        if ((DateTime.UtcNow - _lastFlush).TotalSeconds >= FlushEverySec) Flush();
    }

    public List<(string Name, int Sec)> TodayTop(int n)
    {
        if (_data?.Days == null) return new();
        var key = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return _data.Days.TryGetValue(key, out var day) ? TopN(day, n) : new();
    }

    public int TotalLastDays(int days)
    {
        if (_data?.Days == null) return 0;
        return SumDays(_data.Days, DateTime.Now, days).Values.Sum();
    }

    static bool IsUserActive(int maxIdleSec)
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return false;
        uint idleMs = unchecked((uint)Environment.TickCount - lii.dwTime);
        return idleMs < (uint)(maxIdleSec * 1000);
    }

    static string ForegroundAppName()
    {
        try
        {
            int pid = Native.ForegroundPid();
            if (pid <= 0 || pid == Environment.ProcessId) return null;
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName;
            if (string.IsNullOrWhiteSpace(name) || name.Equals("ZaxLet", StringComparison.OrdinalIgnoreCase) || name.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                return null;

            if (name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                return ResolveUwpChildName(Native.GetForegroundWindow(), (uint)pid);

            return name;
        }
        catch { return null; }
    }

    static string ResolveUwpChildName(IntPtr hwnd, uint hostPid)
    {
        if (hwnd == IntPtr.Zero) return null;
        string found = null;
        EnumChildWindows(hwnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint cpid);
            if (cpid == 0 || cpid == hostPid || cpid == (uint)Environment.ProcessId) return true;
            try
            {
                using var cp = Process.GetProcessById((int)cpid);
                var cn = cp.ProcessName;
                if (!string.IsNullOrWhiteSpace(cn) && !cn.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                {
                    found = cn;
                    return false;
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---------- pembantu murni ----------

    public static string DisplayName(string procName) =>
        string.IsNullOrEmpty(procName) ? "" : char.IsLower(procName[0]) ? char.ToUpper(procName[0]) + procName[1..] : procName;

    public static string FormatDuration(int seconds)
    {
        if (seconds < 60) return $"{Math.Max(0, seconds)}d";
        int mins = seconds / 60;
        if (mins < 60) return $"{mins}m";
        int hrs = mins / 60;
        int rem = mins % 60;
        return rem == 0 ? $"{hrs}j" : $"{hrs}j {rem}m";
    }

    public static List<(string Name, int Sec)> TopN(IReadOnlyDictionary<string, int> counts, int n)
    {
        if (counts == null || n <= 0) return new();
        return counts
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(n)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    public static Dictionary<string, int> SumDays(IReadOnlyDictionary<string, Dictionary<string, int>> days, DateTime today, int numDays)
    {
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (days == null || numDays <= 0) return totals;
        var from = today.Date.AddDays(-(numDays - 1));
        var to = today.Date;
        foreach (var (dateKey, map) in days)
        {
            if (map == null || !DateTime.TryParseExact(dateKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (d.Date < from || d.Date > to) continue;
            foreach (var (proc, sec) in map)
                totals[proc] = totals.TryGetValue(proc, out var cur) ? cur + sec : sec;
        }
        return totals;
    }

    public static void Prune(Dictionary<string, Dictionary<string, int>> days, DateTime today, int keepDays = KeepDays)
    {
        if (days == null) return;
        var cutoff = today.Date.AddDays(-keepDays);
        var old = days.Keys
            .Where(k => !DateTime.TryParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d.Date < cutoff)
            .ToList();
        foreach (var k in old) days.Remove(k);
    }
}
