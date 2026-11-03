using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace ZaxLet;

/// <summary>
/// Island menyingkir bila menutupi tombol aplikasi lain. Tiap detik: bila jendela depan beririsan dengan area island, tombol, tab, kolom
/// teks, atau menu miliknya diperiksa lewat UI Automation (hasil di-cache per jendela dan posisi, diulang tiap ±6 detik). Ada elemen interaktif
/// yang tertutup island = <see cref="Blocked"/>; island lalu bersikap seperti saat fullscreen (hanya mengintip di tepi atas layar).
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

    /// <summary>islandPx mengembalikan persegi island (kiri, atas, kanan, bawah) dalam piksel layar.</summary>
    public void Start(Func<(int L, int T, int R, int B)> islandPx) => _t = new Timer(_ => Poll(islandPx()), null, 3000, 1000);

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
            if (sig == _sig && (DateTime.Now - _at).TotalSeconds < 6) return;   // hasil sebelumnya masih berlaku
            _sig = sig; _at = DateTime.Now; _hwnd = h;
            Blocked = BlocksControls(h, island);
        }
        catch { /* jendela menutup / pohon aksesibilitas belum siap: putaran berikut */ }
        finally { Volatile.Write(ref _busy, 0); }
    }

    static bool BlocksControls(IntPtr h, (int L, int T, int R, int B) island)
    {
        var root = AutomationElement.FromHandle(h);
        var cls = root.Current.ClassName ?? "";
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;   // desktop dan taskbar bukan aplikasi yang tertutup
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
