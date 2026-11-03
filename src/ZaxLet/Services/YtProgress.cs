using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ZaxLet;

/// <summary>
/// Posisi putar YouTube yang sebenarnya, dibaca dari bilah progres di halamannya lewat aksesibilitas Windows (UI Automation). Posisi
/// yang dilaporkan peramban ke Windows (SMTC) sering basi atau kosong; bilah progres YouTube selalu memuat detik sekarang dan panjang
/// video. Lokal sepenuhnya, tidak ada yang dikirim. Hanya berhasil bila tab videonya sedang menjadi tab aktif di jendelanya (judul jendela
/// memuat judul video); selain itu pemanggil memakai laporan Windows.
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

    /// <summary>Null bila tidak ada jendela YouTube dengan judul itu, atau bilah progresnya tidak terbaca. <paramref name="durSec"/> = durasi yang diketahui (0 = belum tahu).</summary>
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
                bool sameLength = durSec <= 0 || Math.Abs(r.Maximum - durSec) <= 5;   // bar milik video lain (tab lain) dilewati
                if ((progress || durSec > 0) && sameLength && r.Maximum >= 5) return new Reading(r.Value, r.Maximum);
            }
        }
        catch (Exception) { /* jendela menutup / pohon aksesibilitas belum siap: coba lagi di putaran berikut */ }
        return null;
    }
}
