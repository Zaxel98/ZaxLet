using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

public sealed class MonitorView : TabView
{
    public override string Key => "monitor";
    public override string Glyph => "";
    public override string Label => "Monitor";
    public override double H => 204;

    static readonly Brush Blue = UI.B("#FF3B82F6"), Teal = UI.B("#FF2DD4BF");

    readonly SysMon _s = new();
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };

    // 4 ukuran huruf: 18 (angka utama), 12.5 (isi), 11.5 (judul/kontrol), 10 (keterangan redup)
    readonly TextBlock _cpu = UI.T("0%", 18, null, true), _cpuLbl = UI.T("CPU", 10, UI.Dim);
    readonly TextBlock _memPct = UI.T("0%", 18, null, true), _memLbl = UI.T("RAM", 10, UI.Dim);
    readonly TextBlock _gpu = UI.T("—", 18, null, true), _gpuLbl = UI.T("GPU", 10, UI.Dim);
    readonly Bar _cpuBar = new(4), _memBar = new(4), _gpuBar = new(4);
    readonly ColumnDefinition _gpuCol = new() { Width = new GridLength(1, GridUnitType.Star) };
    readonly StackPanel _gpuBox = new();

    readonly StackPanel _coreBox = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Height = 20 };
    readonly List<Border> _coreBars = new();

    readonly TextBlock _down = UI.T("", 12.5), _up = UI.T("", 12.5), _netType = UI.T("", 10, UI.Dim);
    readonly Spark _downSpark = new(48, 16, Blue), _upSpark = new(48, 16, Teal);
    readonly TextBlock _gwPing = UI.T("—", 12.5, UI.Dim, true), _gwMeta = UI.T("", 10, UI.Dim);
    readonly TextBlock _netPing = UI.T("—", 12.5, UI.Dim, true), _netMeta = UI.T("", 10, UI.Dim);

    readonly Border _batHost = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBlock _bat = UI.T("", 12.5);
    readonly TextBlock _acTxt = UI.T("Auto-clear", 11.5, UI.Dim, true);
    Border _acBtn;

    readonly StackPanel _top = new();
    string _kill; DateTime _killUntil;
    StackPanel _rightPanel;
    Border _ovBtn;
    readonly TextBlock _ovTxt = UI.T("Overlay", 11.5, UI.Dim, true);
    readonly TextBlock _rightTitle = UI.T("Memori terbanyak", 11.5, UI.Dim);
    readonly TextBlock _usageTxt = UI.T("", 10, UI.Dim);
    readonly StackPanel _perfBox = new() { ClipToBounds = true, Height = 168 };   // tinggi tetap: ganti daftar proses tanpa mengubah ukuran island
    PerfProbe _probe;   // hidup hanya selama tab terbuka dan "tampilkan di island" menyala
    bool _preview;

    public MonitorView()
    {
        var left = new StackPanel();

        // tiga unit seragam: CPU, RAM, GPU (angka besar, bar tipis status, label redup di bawah)
        var units = new Grid();
        units.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        units.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        units.ColumnDefinitions.Add(_gpuCol);

        var cpuUnit = MakeUnit(_cpu, _cpuBar, _cpuLbl, 0);
        var memUnit = MakeUnit(_memPct, _memBar, _memLbl, 12);
        _gpuBox = MakeUnit(_gpu, _gpuBar, _gpuLbl, 12);
        units.Children.Add(cpuUnit.At(0, 0));
        units.Children.Add(memUnit.At(0, 1));
        units.Children.Add(_gpuBox.At(0, 2));
        left.Children.Add(units);

        left.Children.Add(_coreBox);

        // jaringan: dua kolom sama lebar dengan label redup di bawah angka
        var net = UI.Cols(-1, -1);
        net.Margin = new Thickness(0, 8, 0, 0);
        var downCol = new StackPanel();
        var downTop = new StackPanel { Orientation = Orientation.Horizontal };
        downTop.Children.Add(UI.Icon("", 12, Blue));
        _down.Margin = new Thickness(4, 0, 8, 0); _down.VerticalAlignment = VerticalAlignment.Center;
        downTop.Children.Add(_down);
        _downSpark.VerticalAlignment = VerticalAlignment.Center;
        downTop.Children.Add(_downSpark);
        downCol.Children.Add(downTop);
        var downSub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        downSub.Children.Add(UI.T(L.T("Unduh"), 10, UI.Dim));
        _netType.Margin = new Thickness(8, 0, 0, 0);
        downSub.Children.Add(_netType);
        downCol.Children.Add(downSub);
        net.Children.Add(downCol.At(0, 0));

        var upCol = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var upTop = new StackPanel { Orientation = Orientation.Horizontal };
        upTop.Children.Add(UI.Icon("", 12, Teal));
        _up.Margin = new Thickness(4, 0, 8, 0); _up.VerticalAlignment = VerticalAlignment.Center;
        upTop.Children.Add(_up);
        _upSpark.VerticalAlignment = VerticalAlignment.Center;
        upTop.Children.Add(_upSpark);
        upCol.Children.Add(upTop);
        var upLbl = UI.T(L.T("Unggah"), 10, UI.Dim);
        upLbl.Margin = new Thickness(0, 4, 0, 0);
        upCol.Children.Add(upLbl);
        net.Children.Add(upCol.At(0, 1));
        left.Children.Add(net);

        // ping: sejajar dengan dua kolom jaringan di atasnya
        var ping = UI.Cols(-1, -1);
        ping.Margin = new Thickness(0, 8, 0, 0);
        var gwRow = new StackPanel { Orientation = Orientation.Horizontal };
        var gwLbl = UI.T("Gateway", 10, UI.Dim); gwLbl.VerticalAlignment = VerticalAlignment.Center;
        gwRow.Children.Add(gwLbl);
        _gwPing.Margin = new Thickness(8, 0, 0, 0); _gwPing.VerticalAlignment = VerticalAlignment.Center;
        _gwMeta.Margin = new Thickness(4, 0, 0, 0); _gwMeta.VerticalAlignment = VerticalAlignment.Center;
        gwRow.Children.Add(_gwPing);
        gwRow.Children.Add(_gwMeta);
        ping.Children.Add(gwRow.At(0, 0));

        var netRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        var netLbl = UI.T("Internet", 10, UI.Dim); netLbl.VerticalAlignment = VerticalAlignment.Center;
        netRow.Children.Add(netLbl);
        _netPing.Margin = new Thickness(8, 0, 0, 0); _netPing.VerticalAlignment = VerticalAlignment.Center;
        _netMeta.Margin = new Thickness(4, 0, 0, 0); _netMeta.VerticalAlignment = VerticalAlignment.Center;
        netRow.Children.Add(_netPing);
        netRow.Children.Add(_netMeta);
        ping.Children.Add(netRow.At(0, 1));
        left.Children.Add(ping);

        // baris bawah: baterai di kiri, Auto-clear di kanan
        var bottom = UI.Cols(-1, 0);
        bottom.Margin = new Thickness(0, 8, 0, 0);
        var batRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        batRow.Children.Add(_batHost);
        _bat.VerticalAlignment = VerticalAlignment.Center;
        batRow.Children.Add(_bat);
        bottom.Children.Add(batRow.At(0, 0));
        // auto-clear ala ISLC: buang cache (standby) begitu mencapai 1 GB; mati sampai dinyalakan
        _acBtn = UI.Btn(_acTxt, () => { if (MemTrim.On) MemTrim.Stop(); else if (!MemTrim.Start()) IslandWindow.Instance?.Alert(L.T("Auto-clear butuh izin admin. Coba lagi dan pilih Ya di jendela Windows."), 8, null, false); Render(); }, UI.Card, 8, new Thickness(8, 4, 8, 4));
        bottom.Children.Add(_acBtn.At(0, 1));
        left.Children.Add(bottom);

        var g = UI.Cols(-1, 1, 232);
        g.Children.Add(left);
        g.Children.Add(new Border { Width = 1, Background = UI.Track, Margin = new Thickness(12, 4, 12, 4) }.At(0, 1));
        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var head = UI.Cols(-1, 0);   // judul + tombol on/off overlay performa
        _rightTitle.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(_rightTitle);
        _ovBtn = UI.Btn(_ovTxt, () => { var s = Settings.Cur; if (PerfOverlay.IsOn && FpsSource.NeedsAsk) { FpsSource.Ask(); Render(); return; } bool turnOn = !PerfOverlay.IsOn; s.PerfShow = turnOn ? "on" : "off"; Settings.Apply(); Render(); if (turnOn) PerfOverlay.AskAccess(); }, UI.Card, 8, new Thickness(8, 4, 8, 4));
        head.Children.Add(_ovBtn.At(0, 1));
        right.Children.Add(head);
        _top.Margin = new Thickness(0, 8, 0, 0);
        right.Children.Add(_top);
        _usageTxt.TextTrimming = TextTrimming.CharacterEllipsis;
        _usageTxt.Margin = new Thickness(0, 4, 0, 0);
        _usageTxt.Visibility = Visibility.Collapsed;
        right.Children.Add(_usageTxt);
        _perfBox.Margin = new Thickness(0, 4, 0, 0); _perfBox.Visibility = Visibility.Collapsed;
        right.Children.Add(_perfBox);
        g.Children.Add(right.At(0, 2));
        _rightPanel = right;
        Children.Add(g);
        _t.Tick += async (_, _) =>
        {
            var iv = TimeSpan.FromSeconds(PowerSaver.On ? 3 : 1);   // hemat baterai: sampel tiap 3 dtk
            if (_t.Interval != iv) _t.Interval = iv;
            _s.Sample(); Render(); await _s.SampleSlowAsync();
            if (_visible) await PingAsync();
        };
    }

    static StackPanel MakeUnit(TextBlock val, Bar bar, TextBlock lbl, double leftMargin)
    {
        var p = new StackPanel { Margin = new Thickness(leftMargin, 0, 0, 0) };
        p.Children.Add(val);
        bar.Margin = new Thickness(0, 4, 0, 4);
        p.Children.Add(bar);
        lbl.TextTrimming = TextTrimming.CharacterEllipsis;
        p.Children.Add(lbl);
        return p;
    }

    // Pemanasan saat aplikasi mulai: sampel pertama (dasar selisih CPU/jaringan), GPU, dan daftar proses sudah siap sebelum tab dibuka.
    public async void Warm()
    {
        try
        {
            _s.Sample();
            await _s.SampleSlowAsync();
            Render();
            _warm = true;
        }
        catch (Exception ex) { App.Log(ex); }
    }
    bool _warm;

    // Bila sudah dipanaskan: tampilkan angka terakhir seketika, lalu sampel baru menyusul tanpa menunggu tick pertama.
    public override void OnShow()
    {
        _visible = true;
        if (_preview) return;
        if (_warm) Render();
        _s.Sample(); _t.Start(); Render(); _ = _s.SampleSlowAsync(); _ = PingAsync();
    }
    bool _visible;   // Warm() menggambar saat tab belum terbuka: probe sensor tidak boleh ikut menyala
    public override void OnHide() { _visible = false; _t.Stop(); _probe?.Dispose(); _probe = null; }

    internal static MonitorView Preview()
    {
        var v = new MonitorView { _preview = true };
        var s = v._s;
        s.Cpu = 0.37;
        s.CpuTemp = 54;
        s.Cores = new[] { 0.22, 0.45, 0.31, 0.68, 0.19, 0.41, 0.35, 0.52 };
        s.MemUsedGb = 14.2;
        s.MemTotalGb = 15.7;
        s.MemPct = 14.2 / 15.7;
        s.GpuUtil = 0.62;
        s.GpuTemp = 61;
        s.NetDownKb = 340;
        s.NetUpKb = 1.2 * 1024;
        s.BatPct = 0.84;
        s.BatLeft = "2j 15m";
        s.Top = new List<(string, long, string)>
        {
            ("Code", 2_576_980_378L, ""),
            ("chrome", 941_621_248L, ""),
            ("dotnet", 412_090_368L, ""),
            ("explorer", 184_549_376L, ""),
        };
        s.GwPing.Push(4);
        s.NetPing.Push(18);
        foreach (var d in new double[] { 120, 260, 190, 410, 340 }) v._downSpark.Push(d);
        foreach (var u in new double[] { 400, 950, 720, 1450, 1228 }) v._upSpark.Push(u);
        v.Render();
        return v;
    }

    async Task PingAsync()
    {
        try
        {
            if (await _s.SamplePingAsync() && _visible) RenderPing();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void RenderPerf(Settings st)
    {
        bool want = st.PerfInIsland && _visible && !_preview;
        if (want && _probe == null) _probe = new PerfProbe(PerfFormat.Items(st.PerfItems));
        else if (!want && _probe != null) { _probe.Dispose(); _probe = null; }
        bool perf = _probe != null;
        _rightPanel.Visibility = Visibility.Visible;   // tombol overlay selalu ada
        _top.Visibility = perf || !st.MonTop ? Visibility.Collapsed : Visibility.Visible;
        _perfBox.Visibility = perf ? Visibility.Visible : Visibility.Collapsed;
        _rightTitle.Text = L.T(perf || !st.MonTop ? "Performa" : "Memori terbanyak");
        bool on = PerfOverlay.IsOn;
        _ovBtn.Background = on ? UI.AccentBg : UI.Card;
        _ovTxt.Text = L.T(on ? FpsSource.NeedsAsk ? "Izinkan FPS" : "Overlay nyala" : "Overlay mati");
        _ovTxt.Foreground = on ? UI.Fg : UI.Dim;
        _acBtn.Background = MemTrim.On ? UI.AccentBg : UI.Card;
        _acTxt.Foreground = MemTrim.On ? UI.Fg : UI.Dim;
        if (!perf) return;
        var items = PerfFormat.Items(st.PerfItems);
        var v = _probe.Read();
        _perfBox.Children.Clear();
        var rows = PerfFormat.Metrics.Where(m => items.Contains(m.Key) && v.TryGetValue(m.Key, out var x) && x != null).ToList();
        if (rows.Count == 0) { _perfBox.Children.Add(UI.T(L.T("Belum ada metrik dipilih"), 11.5, UI.Faint)); return; }
        // lebih dari satu halaman: ganti halaman tiap 4 detik (tinggi daftar tetap, ukuran island tidak berubah)
        int pages = (rows.Count + PerfRows - 1) / PerfRows, page = pages > 1 ? _perfTick++ / 4 % pages : 0;
        if (pages > 1) _rightTitle.Text += $"  {page + 1}/{pages}";
        foreach (var m in rows.Skip(page * PerfRows).Take(PerfRows))
        {
            var row = UI.Cols(-1, 0);
            row.Margin = new Thickness(0, 0, 0, 4);
            row.Children.Add(UI.T(L.T(m.Label), 11.5, UI.Dim).At(0, 0));
            var vt = UI.T(System.Text.RegularExpressions.Regex.Replace(v[m.Key], @"^(inti|mem|bus|kipas|batas:)\s+", ""), 11.5);   // label baris sudah menjelaskan
            vt.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(vt.At(0, 1));
            _perfBox.Children.Add(row);
        }
    }

    const int PerfRows = 10;
    int _perfTick;

    static Brush Level(double f) => f > .85 ? UI.Bad : f > .6 ? UI.Warn : UI.Good;

    static string FormatMem(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.0} GB" : $"{bytes >> 20} MB";

    void Render()
    {
        var st = Settings.Cur;
        _gpuBox.Visibility = st.MonGpu ? Visibility.Visible : Visibility.Collapsed;
        _gpuCol.Width = st.MonGpu ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _coreBox.Visibility = st.MonCores ? Visibility.Visible : Visibility.Collapsed;
        RenderPerf(st);

        _cpu.Text = $"{Math.Round(_s.Cpu * 100)}%";
        _cpuBar.Set(_s.Cpu, Level(_s.Cpu));
        _cpuLbl.Text = _s.CpuTemp is { } c ? $"CPU · {Math.Round(c)}°C" : "CPU";

        _memPct.Text = $"{Math.Round(_s.MemPct * 100)}%";
        _memBar.Set(_s.MemPct, Level(_s.MemPct));
        _memLbl.Text = $"RAM · {_s.MemUsedGb:0.0}/{_s.MemTotalGb:0.0} GB";

        double gu = _s.GpuUtil ?? 0;
        _gpu.Text = _s.GpuUtil == null ? "—" : $"{Math.Round(gu * 100)}%";
        _gpuBar.Set(gu, _s.GpuUtil == null ? UI.Faint : Level(gu));
        _gpuLbl.Text = _s.GpuTemp is { } gt ? $"GPU · {Math.Round(gt)}°C" : "GPU";

        while (_coreBars.Count < _s.Cores.Length)
        {
            var bar = new Border { Width = 12, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3), Background = UI.Good };
            _coreBars.Add(bar);
            _coreBox.Children.Add(new Border { Width = 12, Margin = new Thickness(0, 0, 4, 0), Background = UI.Track, CornerRadius = new CornerRadius(3), Child = bar });
        }
        for (int i = 0; i < _s.Cores.Length && i < _coreBars.Count; i++)
        {
            _coreBars[i].Height = Math.Max(4, 20 * _s.Cores[i]);
            _coreBars[i].Background = Level(_s.Cores[i]);
        }

        _down.Text = Rate(_s.NetDownKb);
        _up.Text = Rate(_s.NetUpKb);
        if (!_preview) { _downSpark.Push(_s.NetDownKb); _upSpark.Push(_s.NetUpKb); }
        _netType.Text = _preview && _s.ConnectionType == "none"
            ? "Wi-Fi"
            : (_s.ConnectionType == "none" ? L.T("Tidak tersambung") : _s.ConnectionType);
        RenderPing();

        if (_s.BatPct < 0) { _batHost.Child = null; _bat.Text = ""; }
        else
        {
            _batHost.Child = Icons.Battery(_s.BatPct, _s.Charging);
            _bat.Text = $"{Math.Round(_s.BatPct * 100)}%" + (_s.BatLeft.Length > 0 ? $" · {_s.BatLeft}" : "");
        }

        _top.Children.Clear();
        foreach (var (name, bytes, path) in _s.Top)
        {
            var (title, icon) = AppInfo.Get(name, path);
            var row = UI.Cols(24, -1, 0);
            row.Margin = new Thickness(0, 0, 0, 4);
            if (icon != null)
                row.Children.Add(new Image { Source = icon, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left });
            else
            {
                var dot = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = UI.Card, HorizontalAlignment = HorizontalAlignment.Left, Child = UI.T(name.Length > 0 ? name[..1].ToUpper() : "?", 10, UI.Dim) };
                ((TextBlock)dot.Child).HorizontalAlignment = HorizontalAlignment.Center;
                row.Children.Add(dot);
            }
            bool armed = _kill == name && DateTime.Now < _killUntil;
            row.Children.Add(UI.T(armed ? L.T("Akhiri") + " " + title + "? " + L.T("Klik kanan lagi") : title, 12.5, armed ? UI.Warn : UI.Fg).At(0, 1));
            if (ProcGuard.CanKill(name, path))   // klik kanan = siapkan, klik kanan lagi dalam 5 dtk = akhiri (konfirmasi)
            {
                row.Background = Brushes.Transparent;
                var n = name;
                row.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (_kill == n && DateTime.Now < _killUntil) { _kill = null; ProcGuard.Kill(n); }
                    else { _kill = n; _killUntil = DateTime.Now.AddSeconds(5); }
                    Render();
                };
            }
            row.Children.Add(UI.T(FormatMem(bytes), 12.5, UI.Dim).At(0, 2));
            _top.Children.Add(row);
        }
        RenderUsage(st);
    }

    void RenderUsage(Settings st)
    {
        var top3 = st.AppUsageOn ? AppUsage.I.TodayTop(3) : null;
        if (top3 == null || top3.Count == 0) { _usageTxt.Visibility = Visibility.Collapsed; return; }
        _usageTxt.Text = L.T("Hari ini") + ": " + string.Join(" · ", top3.Select(x => $"{AppUsage.DisplayName(x.Name)} {AppUsage.FormatDuration(x.Sec)}"));
        var top8 = AppUsage.I.TodayTop(8);
        int total7 = AppUsage.I.TotalLastDays(7);
        _usageTxt.ToolTip = L.T("Hari ini (8 teratas):") + "\n"
            + string.Join("\n", top8.Select(x => $"{AppUsage.DisplayName(x.Name)} · {AppUsage.FormatDuration(x.Sec)}"))
            + "\n" + L.F("Total 7 hari: {0}", AppUsage.FormatDuration(total7));
        _usageTxt.Visibility = Visibility.Visible;
    }

    static Brush PingBrush(int tier) => tier switch { 0 => UI.Good, 1 => UI.Warn, 2 => UI.Bad, _ => UI.Dim };

    void RenderPing()
    {
        RenderTarget(_preview || (_s.NetUp && _s.HasGateway), _s.GwPing, _gwPing, _gwMeta);
        RenderTarget(_preview || _s.NetUp, _s.NetPing, _netPing, _netMeta);
    }

    static void RenderTarget(bool active, PerfFormat.PingRing ring, TextBlock val, TextBlock meta)
    {
        if (!active || ring.Count == 0)
        {
            val.Text = "—";
            val.Foreground = UI.Dim;
            meta.Text = "";
            return;
        }
        val.Text = PerfFormat.PingMs(ring.Latest);
        val.Foreground = PingBrush(PerfFormat.PingTier(ring.Latest, ring.Count > 0));
        var avg = PerfFormat.PingRound(ring.AvgMs);
        var jit = PerfFormat.PingRound(ring.JitterMs);
        meta.Text = avg is { } a
            ? L.F("rata {0} · ±{1} · {2}%", a, jit ?? 0, ring.LossPct)
            : L.F("hilang {0}%", ring.LossPct);
    }

    static string Rate(double kb) => kb >= 1024 ? $"{kb / 1024:0.0} MB/s" : $"{kb:0} KB/s";
}
