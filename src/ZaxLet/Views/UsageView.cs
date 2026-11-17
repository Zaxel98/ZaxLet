using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

public sealed class UsageView : TabView
{
    public override string Key => "usage";
    public override string Glyph => "";
    public override string Label => "AI";
    public override double H => 188;

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    readonly StackPanel _claude = new(), _codex = new(), _page2 = new() { Visibility = Visibility.Collapsed };
    readonly Grid _main = UI.Cols(-1, 1, -1);
    bool _more;   // halaman 2: semua worker + pemakaian 5 jam
    const int PageWorkers = 2;
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(60) };
    readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromSeconds(20) };
    readonly DispatcherTimer _workerT = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _workerBusy;
    List<WorkerRow> _workerRows = new();

    public UsageView()
    {
        _main.Children.Add(_claude);
        _main.Children.Add(new Border { Width = 1, Background = UI.Track, Margin = new Thickness(0, 2, 0, 2) }.At(0, 1));
        _main.Children.Add(_codex.At(0, 2));
        _claude.Margin = new Thickness(0, 0, 14, 0);
        _codex.Margin = new Thickness(14, 0, 0, 0);
        _page2.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_main);
        Children.Add(_page2);
        _t.Tick += async (_, _) => await UsageService.I.RefreshAsync();
        _redraw.Tick += (_, _) => Render();
        _workerT.Tick += (_, _) => _ = RefreshWorkersAsync();
    }

    public override void OnShow()
    {
        UsageService.I.Changed += OnChanged;
        _more = false;
        _t.Start(); _redraw.Start(); _workerT.Start();
        Render();
        _ = UsageService.I.RefreshAsync();
        _ = RefreshWorkersAsync();
    }

    public override void OnHide()
    {
        UsageService.I.Changed -= OnChanged;
        _t.Stop(); _redraw.Stop(); _workerT.Stop();
    }

    void OnChanged() => Dispatcher.BeginInvoke(Render);

    async Task RefreshWorkersAsync()
    {
        var s = Settings.Cur;
        if (!s.CodexOn || s.Slot2 != "antigravity" || _workerBusy) return;
        _workerBusy = true;
        try
        {
            var workersDir = System.IO.Path.Combine(Home, ".config", "ponytail", "workers");
            var rows = await Task.Run(() => AiTaskScan.Workers(workersDir, DateTimeOffset.Now, AiTaskScan.PidAlive));
            if (!WorkersEqual(_workerRows, rows))
            {
                _workerRows = rows;
                _ = Dispatcher.BeginInvoke(Render);
            }
        }
        finally { _workerBusy = false; }
    }

    static bool WorkersEqual(List<WorkerRow> a, List<WorkerRow> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Info.Name != b[i].Info.Name
                || a[i].State != b[i].State
                || a[i].Info.Phase != b[i].Info.Phase
                || a[i].Info.Updated != b[i].Info.Updated)
                return false;
        }
        return true;
    }

    void Render()
    {
        var s = Settings.Cur;
        bool agy = s.CodexOn && s.Slot2 == "antigravity";
        if (_more && (!agy || _workerRows.Count <= PageWorkers)) _more = false;   // tidak ada lagi yang disembunyikan
        _main.Visibility = _more ? Visibility.Collapsed : Visibility.Visible;
        _page2.Visibility = _more ? Visibility.Visible : Visibility.Collapsed;
        if (_more) { BuildPage2(s); return; }
        _claude.Visibility = s.ClaudeOn ? Visibility.Visible : Visibility.Collapsed;
        _codex.Visibility = s.CodexOn ? Visibility.Visible : Visibility.Collapsed;
        if (s.ClaudeOn) Fill(_claude, UsageService.I.Claude);
        if (s.CodexOn)
        {
            Fill(_codex, agy ? UsageService.I.Agy : UsageService.I.Codex);
            if (agy)
            {
                // halaman 1: yang berjalan dan paling dekat selesai dulu (tahap terbesar, lalu yang paling lama); selesai/mati hanya mengisi sisa slot
                var first = _workerRows.Where(r => r.State == "BERJALAN")
                    .OrderByDescending(r => AiTaskParse.Stage(r.Info.Phase)).ThenBy(r => r.Info.Started)
                    .Concat(_workerRows.Where(r => r.State != "BERJALAN")).ToList();
                WorkerRows.AddWorkerRows(_codex, first, PageWorkers);
                int rest = _workerRows.Count - PageWorkers;
                if (rest > 0)
                {
                    var row = UI.Cols(-1, 0);
                    row.Margin = new Thickness(0, 3, 0, 0);
                    row.Children.Add(UI.T("+" + rest, 11, UI.Dim));
                    row.Children.Add(UI.Btn(UI.T("More", 10.5, UI.Dim), () => { _more = true; Render(); }, UI.Card, 8, new Thickness(8, 1, 8, 2)).At(0, 1));
                    _codex.Children.Add(row);
                }
            }
        }
    }

    /// <summary>Halaman 2: pemakaian 5 jam AI yang tampil di halaman 1 (bersebelahan) lalu semua worker.</summary>
    void BuildPage2(Settings s)
    {
        _page2.Children.Clear();
        var top = UI.Cols(-1, 14, -1, 8, 0);
        int col = 0;
        if (s.ClaudeOn && FiveHour(top, UsageService.I.Claude, col)) col += 2;
        FiveHour(top, UsageService.I.Agy, col);
        top.Children.Add(UI.Btn(UI.T("Kembali", 11.5, UI.Dim), () => { _more = false; Render(); }, UI.Card, 9, new Thickness(9, 3, 9, 3)).At(0, 4));
        _page2.Children.Add(top);

        var list = new StackPanel();
        WorkerRows.AddWorkerRows(list, _workerRows, int.MaxValue);
        _page2.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, MaxHeight = 120, Margin = new Thickness(0, 6, 0, 0), Content = list });
    }

    static bool FiveHour(Grid host, ProviderUsage u, int col)
    {
        var w = u.Windows.FirstOrDefault(x => (x.Label == "5-hour" || x.Label.EndsWith("5h")) && !(u.Error != null && x.Reset != null && x.Reset < DateTimeOffset.Now));
        if (w == null) return false;
        var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock { Foreground = UI.Fg, TextTrimming = TextTrimming.CharacterEllipsis };
        t.Inlines.Add(new Run(u.Name + "  5h  ") { FontSize = 11, Foreground = UI.Dim });
        t.Inlines.Add(new Run($"{Math.Round(w.Pct)}%") { FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (w.Reset != null) t.Inlines.Add(new Run("  " + UI.Span(w.Reset.Value - DateTimeOffset.Now)) { FontSize = 10.5, Foreground = UI.Faint });
        cell.Children.Add(t);
        var bar = new Bar(4) { Margin = new Thickness(0, 2, 0, 0) };
        cell.Children.Add(bar);
        bar.Set(w.Pct / 100, w.Pct >= Settings.Cur.CritAt ? UI.Bad : w.Pct >= Settings.Cur.WarnAt ? UI.Warn : UI.Fg);
        host.Children.Add(cell.At(0, col));
        return true;
    }

    static void Fill(StackPanel host, ProviderUsage u)
    {
        host.Children.Clear();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var logo = u.Name == "Claude" ? Icons.ClaudeLogo(20) : u.Name == "Antigravity" ? Icons.AntigravityLogo(20) : Icons.CodexLogo(20);
        ((FrameworkElement)logo).Margin = new Thickness(0, 0, 8, 0);
        ((FrameworkElement)logo).VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(logo);
        head.Children.Add(UI.T(u.Name, 15, null, true));
        if (!string.IsNullOrEmpty(u.Plan))
            head.Children.Add(new Border
            {
                BorderBrush = UI.Faint, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 1, 0, 0), Child = UI.T(u.Plan, 10.5, UI.Dim)
            });
        host.Children.Add(head);

        if (u.Windows.Count == 0)
            host.Children.Add(new TextBlock { Text = u.Error ?? L.T("memuat…"), Foreground = UI.Dim, FontSize = 12, Margin = new Thickness(0, 12, 0, 8), TextWrapping = TextWrapping.Wrap });
        bool tight = u.Windows.Count > 3;   // 5 bar (Antigravity) harus muat di tinggi tab
        if (u.Name == "Antigravity") PairBars(host, u);
        else foreach (var w in u.Windows)
        {
            if (u.Error != null && w.Reset != null && w.Reset < DateTimeOffset.Now) continue; // jendela lama sudah lewat
            var row = UI.Cols(-1, 0);
            row.Margin = new Thickness(0, tight ? 1 : 5, 0, 0);
            var pct = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = UI.Fg };
            pct.Inlines.Add(new Run(w.Label + "  ") { FontSize = 11.5, Foreground = UI.Dim });
            pct.Inlines.Add(new Run($"{Math.Round(w.Pct)}%") { FontSize = tight ? 13 : 18, FontWeight = FontWeights.SemiBold });
            row.Children.Add(pct);
            if (w.Reset != null)
                row.Children.Add(UI.T("in " + UI.Span(w.Reset.Value - DateTimeOffset.Now), 11.5, UI.Dim).At(0, 1));
            host.Children.Add(row);
            var bar = new Bar(4) { Margin = new Thickness(0, 2, 0, 2) };
            host.Children.Add(bar);
            bar.Set(w.Pct / 100, w.Pct >= Settings.Cur.CritAt ? UI.Bad : w.Pct >= Settings.Cur.WarnAt ? UI.Warn : UI.Fg);
        }
        if (u.Windows.Count > 0 && u.Error != null)
            host.Children.Add(UI.T(u.Error, 10.5, UI.Warn));

        // Claude tanpa sumber real-time: satu tombol membuka login (browser terbuka otomatis, tinggal masuk)
        if (u.Name == "Claude" && u.Source != "api")
        {
            var us = UsageService.I;
            var ask = UI.Btn(UI.T("Real-time: masuk Claude", 11, UI.Fg, true), () => _ = us.LoginClaudeAsync(), UI.AccentBg, 9, new Thickness(9, 3, 9, 3));
            ask.HorizontalAlignment = HorizontalAlignment.Left; ask.Margin = new Thickness(0, 6, 0, 0);
            host.Children.Add(ask);
            var hint = UI.T(string.IsNullOrEmpty(us.LoginStatus) ? "Browser terbuka otomatis; cukup masuk dengan akun Claude-mu." : us.LoginStatus, 10, UI.Faint);
            hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 3, 0, 0);
            host.Children.Add(hint);
        }

        if (u.Name == "Antigravity") return;   // tokennya tampil sebagai kolom Agy di tabel Claude
        // tabel pemakaian: Total (semua catatan sesi) dan Code (sesi yang dicatat Ponytail)
        var code = u.Name == "Claude" && Settings.Cur.ShowPonytail && PonytailService.Available;
        var (pt, pw) = code ? PonytailService.Read() : (null, null);
        var s = Settings.Cur;
        var agy = u.Name == "Claude" && s.CodexOn && s.Slot2 == "antigravity" ? UsageService.I.Agy : null;
        host.Children.Add(Head(code, agy != null));
        host.Children.Add(Line("Today", u.TodayTok, u.TodayCost, code, pt, agy?.TodayTok));
        host.Children.Add(Line("Week", u.WeekTok, u.WeekCost, code, pw, agy?.WeekTok));
    }

    static readonly Brush AgyBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8));

    /// <summary>Antigravity: satu baris per grup model, weekly dan 5h bersebelahan.</summary>
    static void PairBars(StackPanel host, ProviderUsage u)
    {
        var live = u.Windows.Where(w => !(u.Error != null && w.Reset != null && w.Reset < DateTimeOffset.Now));
        foreach (var grp in live.GroupBy(w => w.Label.Split(" · ")[0]))
        {
            var name = UI.T(grp.Key, 11, UI.Dim);
            name.Margin = new Thickness(0, 4, 0, 0);
            host.Children.Add(name);
            var row = UI.Cols(-1, 12, -1);
            int c = 0;
            foreach (var w in grp.OrderBy(w => w.Label.EndsWith("5h") ? 1 : 0))
            {
                var cell = new StackPanel();
                var t = new TextBlock { Foreground = UI.Fg, TextTrimming = TextTrimming.CharacterEllipsis };
                t.Inlines.Add(new Run((w.Label.EndsWith("5h") ? "5h" : "weekly") + "  ") { FontSize = 11, Foreground = UI.Dim });
                t.Inlines.Add(new Run($"{Math.Round(w.Pct)}%") { FontSize = 13, FontWeight = FontWeights.SemiBold });
                if (w.Reset != null) t.Inlines.Add(new Run("  " + UI.Span(w.Reset.Value - DateTimeOffset.Now)) { FontSize = 10.5, Foreground = UI.Faint });
                cell.Children.Add(t);
                var bar = new Bar(4) { Margin = new Thickness(0, 2, 0, 0) };
                cell.Children.Add(bar);
                bar.Set(w.Pct / 100, w.Pct >= Settings.Cur.CritAt ? UI.Bad : w.Pct >= Settings.Cur.WarnAt ? UI.Warn : UI.Fg);
                row.Children.Add(cell.At(0, c)); c += 2;
            }
            host.Children.Add(row);
        }
    }

    static Grid Head(bool code, bool agy)
    {
        var g = UI.Cols(40, -1, 0, 0, 0);
        g.Margin = new Thickness(0, 7, 0, 1);
        g.Children.Add(UI.T("Total", 10.5, UI.Faint).At(0, 1));
        if (code) g.Children.Add(new TextBlock { Text = "Code", FontSize = 10.5, Foreground = UI.Accent, Width = 100, TextAlignment = TextAlignment.Right }.At(0, 3));
        if (agy) g.Children.Add(new TextBlock { Text = "Agy", FontSize = 10.5, Foreground = AgyBrush, Width = 60, TextAlignment = TextAlignment.Right }.At(0, 4));
        return g;
    }

    static Grid Line(string label, long tok, double cost, bool code, PonytailStats p, long? agy)
    {
        var g = UI.Cols(40, -1, 0, 0, 0);
        g.Margin = new Thickness(0, 1, 0, 0);
        g.Children.Add(UI.T(label, 12, UI.Dim));
        g.Children.Add(UI.T(UI.Tok(tok), 12, UI.Dim).At(0, 1));
        if (Settings.Cur.ShowCost) g.Children.Add(new TextBlock { Text = "~$" + cost.ToString("0.00", CultureInfo.InvariantCulture), FontSize = 12, Foreground = UI.Fg, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) }.At(0, 2));
        if (code)
        {
            var t = p.Sessions == 0 ? "—" : (Settings.Cur.ShowCost ? "$" + p.Cost.ToString("0.00", CultureInfo.InvariantCulture) + " · " : "") + p.Turns + " turn";
            g.Children.Add(new TextBlock { Text = t, FontSize = 12, Foreground = UI.Accent, Width = 100, TextAlignment = TextAlignment.Right }.At(0, 3));
        }
        if (agy != null) g.Children.Add(new TextBlock { Text = UI.Tok(agy.Value), FontSize = 12, Foreground = AgyBrush, Width = 60, TextAlignment = TextAlignment.Right }.At(0, 4));
        return g;
    }
}
