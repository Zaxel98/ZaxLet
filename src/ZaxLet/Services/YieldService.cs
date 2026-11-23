using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace ZaxLet;

/// <summary>
/// Island yields if it covers another app's buttons. Every second: if the foreground window overlaps the island area, its buttons, tabs, text
/// fields, or menus are checked via UI Automation (result cached per window and position, refreshed every ~6 s). Any interactive element
/// covered by the island = <see cref="Blocked"/>; the island then behaves like in fullscreen (only peeks at the top edge of the screen).
/// </summary>
public sealed class YieldService
{
    [StructLayout(LayoutKind.Sequential)] struct R { public int L, T, R2, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public volatile bool Blocked;
    Timer _t;
    int _busy;
    IntPtr _hwnd;
    string _sig = "";
    DateTime _at = DateTime.MinValue;

    /// <summary>islandPx returns the island rectangle (left, top, right, bottom) in screen pixels.</summary>
    public void Start(Func<(int L, int T, int R, int B)> islandPx) => _t = new Timer(_ => Poll(islandPx()), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Timer only runs while the feature is enabled.</summary>
    public void Enable(bool on)
    {
        if (on == _on || _t == null) return;
        _on = on;
        _t.Change(on ? 3000 : Timeout.Infinite, on ? 1000 : Timeout.Infinite);
        if (!on) Blocked = false;
    }
    bool _on;

    public static bool Overlaps((int L, int T, int R, int B) a, (int L, int T, int R, int B) b, int min = 12) =>
        Math.Min(a.R, b.R) - Math.Max(a.L, b.L) >= min && Math.Min(a.B, b.B) - Math.Max(a.T, b.T) >= min;

    void Poll((int L, int T, int R, int B) island)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (!Settings.Cur.YieldOn) { Blocked = false; return; }
            var h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero || GetWindowThreadProcessId(h, out var pid) == 0 || pid == Environment.ProcessId || !GetWindowRect(h, out var wr)) { Blocked = false; return; }
            if (!Overlaps((wr.L, wr.T, wr.R2, wr.B), island, 1)) { Blocked = false; return; }
            var sig = $"{h}|{wr.L},{wr.T},{wr.R2},{wr.B}|{island}";
            if (sig == _sig && (DateTime.Now - _at).TotalSeconds < 6) return;   // previous result is still valid
            _sig = sig; _at = DateTime.Now; _hwnd = h;
            Blocked = !IsOffice(pid) && BlocksControls(h, island);   // Word, Excel, PowerPoint: island stays fully visible (slide show still hides it via fullscreen detection)
        }
        catch { /* window closing / accessibility tree not ready: retry next tick */ }
        finally { Volatile.Write(ref _busy, 0); }
    }

    static bool IsOffice(uint pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); return p.ProcessName.ToLowerInvariant() is "winword" or "excel" or "powerpnt"; }
        catch { return false; }
    }

    static bool BlocksControls(IntPtr h, (int L, int T, int R, int B) island)
    {
        var root = AutomationElement.FromHandle(h);
        var cls = root.Current.ClassName ?? "";
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;   // desktop and taskbar are not obscured apps
        var types = new[] { ControlType.Button, ControlType.TabItem, ControlType.Edit, ControlType.ComboBox, ControlType.MenuItem, ControlType.Hyperlink, ControlType.SplitButton, ControlType.CheckBox };
        Condition c = new OrCondition(types.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t)).ToArray());
        var cr = new CacheRequest { TreeScope = TreeScope.Element };
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(AutomationElement.IsOffscreenProperty);
        using (cr.Activate())
        {
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, c))
            {
                if (e.Cached.IsOffscreen) continue;
                var r = e.Cached.BoundingRectangle;
                if (r.IsEmpty) continue;
                if (Overlaps(((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom), island)) return true;
            }
        }
        return false;
    }
}
