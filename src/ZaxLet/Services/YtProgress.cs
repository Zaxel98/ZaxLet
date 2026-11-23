using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ZaxLet;

/// <summary>
/// Actual YouTube playback position, read from its page progress bar via Windows accessibility (UI Automation). The position
/// reported by browsers to Windows (SMTC) is often stale or empty; YouTube's progress bar always holds the current second and video
/// length. Fully local, nothing is sent. Only succeeds when the video tab is the active tab in its window (window title
/// contains the video title); otherwise the caller falls back to Windows' report.
/// </summary>
public static class YtProgress
{
    public sealed record Reading(double Pos, double Max);

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    static IntPtr FindWindow(string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            var t = sb.ToString();
            if (t.Contains("YouTube", StringComparison.OrdinalIgnoreCase) && t.Contains(title, StringComparison.Ordinal)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Null if there is no YouTube window with that title, or its progress bar is unreadable. <paramref name="durSec"/> = known duration (0 = unknown).</summary>
    public static Reading Read(string title, double durSec)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var h = FindWindow(title.Trim());
            if (h == IntPtr.Zero) return null;
            var sliders = AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider));
            foreach (AutomationElement e in sliders)
            {
                var cls = e.Current.ClassName ?? "";
                if (cls.Contains("volume", StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.TryGetCurrentPattern(RangeValuePattern.Pattern, out var p)) continue;
                var r = ((RangeValuePattern)p).Current;
                bool progress = cls.Contains("progress", StringComparison.OrdinalIgnoreCase);
                bool sameLength = durSec <= 0 || Math.Abs(r.Maximum - durSec) <= 5;   // skip bars belonging to other videos (other tabs)
                if ((progress || durSec > 0) && sameLength && r.Maximum >= 5) return new Reading(r.Value, r.Maximum);
            }
        }
        catch (Exception) { /* window closed / accessibility tree not ready: retry on next tick */ }
        return null;
    }
}
