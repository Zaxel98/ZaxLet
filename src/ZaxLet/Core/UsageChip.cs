using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZaxLet;

/// <summary>Chip usage AI di pill idle. Gaya (Settings.IdleUsageStyle): ring, ringpct, pct, bar.</summary>
public sealed class UsageChip : StackPanel
{
    readonly RingBadge _ring;
    readonly TextBlock _lbl, _pct;
    readonly Bar _bar = new(4) { Width = 30, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    readonly Brush _base;
    public string Key { get; private set; } = "";

    public UsageChip(string letter, Brush baseColor)
    {
        _base = baseColor;
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(7, 0, 7, 0);
        _ring = new RingBadge(letter);
        _lbl = new TextBlock { Text = letter, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = baseColor, VerticalAlignment = VerticalAlignment.Center };
        _pct = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        Children.Add(_ring); Children.Add(_lbl); Children.Add(_bar); Children.Add(_pct);
    }

    /// <summary>Lebar persen tetap ("100%") supaya angka berubah tidak menggeser komponen lain.</summary>
    public void Fix() => _pct.MinWidth = UI.TextWidth("88%", 13, FontWeights.SemiBold);

    /// <summary>Isi chip dari kuota; false bila tidak ada data untuk ditampilkan (chip disembunyikan).</summary>
    public bool Update(ProviderUsage u, bool enabled)
    {
        var s = Settings.Cur;
        QuotaWindow w = null;
        if (enabled)
        {
            var want = s.IdleUsageWindow == "weekly" ? "Weekly" : "5-hour";
            bool Live(QuotaWindow q) => !(u.Error != null && q.Reset != null && q.Reset < DateTimeOffset.Now);
            w = u.Windows.FirstOrDefault(q => q.Label == want && Live(q)) ?? u.Windows.FirstOrDefault(Live);
        }
        Visibility = w == null ? Visibility.Collapsed : Visibility.Visible;
        if (w == null) { Key = ""; return false; }

        var col = w.Pct >= s.CritAt ? UI.Bad : w.Pct >= s.WarnAt ? UI.Warn : _base;
        var st = s.IdleUsageStyle;
        _ring.Visibility = st is "ring" or "ringpct" ? Visibility.Visible : Visibility.Collapsed;
        _lbl.Visibility = st is "pct" or "bar" ? Visibility.Visible : Visibility.Collapsed;
        _bar.Visibility = st == "bar" ? Visibility.Visible : Visibility.Collapsed;
        _pct.Visibility = st == "ring" ? Visibility.Collapsed : Visibility.Visible;
        _pct.Margin = new Thickness(st == "pct" ? 4 : 5, 0, 0, 0);
        _ring.Set(w.Pct / 100, col);
        _bar.Set(w.Pct / 100, col);
        _pct.Text = $"{Math.Round(w.Pct)}%";
        _pct.Foreground = u.Error != null ? UI.Dim : st == "pct" ? col : Brushes.White;   // data lama tampil redup
        Key = st + _pct.Text;
        return true;
    }
}
