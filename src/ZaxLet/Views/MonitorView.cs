using System;
using System.Collections.Generic;
using System.Linq;
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

    static readonly Brush Blue = UI.B("#FF3B82F6"), Pink = UI.B("#FFF472B6"), Teal = UI.B("#FF2DD4BF");

    readonly SysMon _s = new();
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly RingBadge _rC = new("C", 26), _rM = new("M", 22), _rG = new("G", 22);
    readonly TextBlock _cpu = new() { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _gpu = UI.T("", 14, null, true), _temp = UI.T("", 12, UI.Dim), _mem = UI.T("", 12.5), _bat = UI.T("", 12.5);
    readonly TextBlock _down = UI.T("", 12.5), _up = UI.T("", 12.5);
    readonly Spark _cpuSpark = new(110, 22, Blue, 40, 1.0), _downSpark = new(46, 16, Blue), _upSpark = new(46, 16, Teal);
    readonly Bar _memBar = new(5);
    readonly StackPanel _coreBox = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Height = 24 };
    readonly List<Border> _coreBars = new();
    string _kill; DateTime _killUntil;
    readonly StackPanel _top = new(), _gpuBox = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _batHost = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    StackPanel _rightPanel;
    Border _ovBtn, _acBtn;
    readonly TextBlock _ovTxt = UI.T("Overlay", 11, UI.Dim, true), _acTxt = UI.T("Auto-clear", 10.5, UI.Dim, true);
    readonly TextBlock _rightTitle = UI.T("Memori terbanyak", 11.5, UI.Dim);
    readonly StackPanel _perfBox = new() { ClipToBounds = true, Height = 168 };   // tinggi tetap: ganti daftar proses tanpa mengubah ukuran island
    PerfProbe _probe;   // hidup hanya selama tab terbuka dan "tampilkan di island" menyala

    public MonitorView()
    {
        var left = new StackPanel();

        // CPU: cincin C + persen + grafik riwayat, GPU di kanan
        var a = UI.Cols(0, 0, -1, 0);
        a.Children.Add(_rC);
        a.Children.Add(_cpu.At(0, 1));
        _cpuSpark.Margin = new Thickness(12, 0, 12, 0);
        _cpuSpark.VerticalAlignment = VerticalAlignment.Center;
        a.Children.Add(_cpuSpark.At(0, 2));
        _gpuBox.Children.Add(_rG);
        _gpu.Margin = new Thickness(7, 0, 0, 0);
        _gpuBox.Children.Add(_gpu);
        a.Children.Add(_gpuBox.At(0, 3));
        left.Children.Add(a);

        left.Children.Add(_coreBox);

        // memori: cincin M + angka + bar
        var m = UI.Cols(0, 0, -1, 0);
        m.Margin = new Thickness(0, 7, 0, 0);
        m.Children.Add(_rM);
        _mem.Margin = new Thickness(8, 0, 10, 0);
        _mem.VerticalAlignment = VerticalAlignment.Center;
        m.Children.Add(_mem.At(0, 1));
        _memBar.VerticalAlignment = VerticalAlignment.Center;
        m.Children.Add(_memBar.At(0, 2));
        // auto-clear ala ISLC: buang cache (standby) begitu mencapai 1 GB; mati sampai dinyalakan
        _acBtn = UI.Btn(_acTxt, () => { if (MemTrim.On) MemTrim.Stop(); else if (!MemTrim.Start()) IslandWindow.Instance?.Alert(L.T("Auto-clear butuh izin admin. Coba lagi dan pilih Ya di jendela Windows."), 8, null, false); Render(); }, UI.Card, 9, new Thickness(8, 2, 8, 2));
        _acBtn.Margin = new Thickness(8, 0, 0, 0);
        m.Children.Add(_acBtn.At(0, 3));
        left.Children.Add(m);

        // jaringan: panah biru unduh, panah teal unggah, masing-masing dengan grafik mini
        var n = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        n.Children.Add(UI.Icon("", 13, Blue));
        _down.Margin = new Thickness(5, 0, 6, 0); _down.MinWidth = 58;
        n.Children.Add(_down);
        n.Children.Add(_downSpark);
        var upIcon = UI.Icon("", 13, Teal);
        upIcon.Margin = new Thickness(16, 0, 0, 0);
        n.Children.Add(upIcon);
        _up.Margin = new Thickness(5, 0, 6, 0); _up.MinWidth = 58;
        n.Children.Add(_up);
        n.Children.Add(_upSpark);
        left.Children.Add(n);

        // baterai: ikon berpetir + persen + sisa waktu; suhu CPU bila ada
        var b = UI.Cols(0, 0, -1, 0);
        b.Margin = new Thickness(0, 7, 0, 0);
        b.Children.Add(_batHost);
        b.Children.Add(_bat.At(0, 1));
        _temp.VerticalAlignment = VerticalAlignment.Center;
        b.Children.Add(_temp.At(0, 3));
        left.Children.Add(b);

        var g = UI.Cols(-1, 1, 232);
        g.Children.Add(left);
        g.Children.Add(new Border { Width = 1, Background = UI.Track, Margin = new Thickness(14, 2, 14, 2) }.At(0, 1));
        var right = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        var head = UI.Cols(-1, 0);   // judul + tombol on/off overlay performa
        head.Children.Add(_rightTitle);
        _ovBtn = UI.Btn(_ovTxt, () => { var s = Settings.Cur; if (PerfOverlay.IsOn && FpsSource.NeedsAsk) { FpsSource.Ask(); Render(); return; } bool turnOn = !PerfOverlay.IsOn; s.PerfShow = turnOn ? "on" : "off"; Settings.Apply(); Render(); if (turnOn) PerfOverlay.AskAccess(); }, UI.Card, 9, new Thickness(9, 2, 9, 2));
        head.Children.Add(_ovBtn.At(0, 1));
        right.Children.Add(head);
        _top.Margin = new Thickness(0, 6, 0, 0);
        right.Children.Add(_top);
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
        };
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
        if (_warm) Render();
        _s.Sample(); _t.Start(); Render(); _ = _s.SampleSlowAsync();
    }
    bool _visible;   // Warm() menggambar saat tab belum terbuka: probe sensor tidak boleh ikut menyala
    public override void OnHide() { _visible = false; _t.Stop(); _probe?.Dispose(); _probe = null; }

    void RenderPerf(Settings st)
    {
        bool want = st.PerfInIsland && _visible;
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
            row.Margin = new Thickness(0, 1, 0, 1);
            row.Children.Add(UI.T(L.T(m.Label), 11.5, UI.Dim).At(0, 0));
            var vt = UI.T(System.Text.RegularExpressions.Regex.Replace(v[m.Key], @"^(inti|mem|bus|kipas|batas:)\s+", ""), 11.5);   // label baris sudah menjelaskan
            vt.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(vt.At(0, 1));
            _perfBox.Children.Add(row);
        }
    }

    const int PerfRows = 10;
    int _perfTick;

    static Brush Level(double f) => f > .85 ? UI.Bad : f > .6 ? UI.Warn : Blue;

    void Render()
    {
        var st = Settings.Cur;
        _gpuBox.Visibility = st.MonGpu ? Visibility.Visible : Visibility.Collapsed;
        _coreBox.Visibility = st.MonCores ? Visibility.Visible : Visibility.Collapsed;
        RenderPerf(st);

        _rC.Set(_s.Cpu, Level(_s.Cpu));
        _cpu.Inlines.Clear();
        _cpu.Inlines.Add(new System.Windows.Documents.Run(Math.Round(_s.Cpu * 100).ToString()) { FontSize = 22, FontWeight = FontWeights.SemiBold });
        _cpu.Inlines.Add(new System.Windows.Documents.Run(" %") { FontSize = 12, Foreground = UI.Dim });
        _cpuSpark.Push(_s.Cpu);
        _temp.Text = _s.CpuTemp is { } c ? $"{Math.Round(c)}°C" : "";

        double gu = _s.GpuUtil ?? 0;
        _rG.Set(gu, _s.GpuUtil == null ? UI.Faint : Level(gu));
        _gpu.Text = _s.GpuUtil == null ? "—" : $"{Math.Round(gu * 100)}%" + (_s.GpuTemp is { } gt ? $"  {Math.Round(gt)}°C" : "");

        while (_coreBars.Count < _s.Cores.Length)
        {
            var bar = new Border { Width = 11, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3), Background = Blue };
            _coreBars.Add(bar);
            _coreBox.Children.Add(new Border { Width = 11, Margin = new Thickness(0, 0, 4, 0), Background = UI.Track, CornerRadius = new CornerRadius(3), Child = bar });
        }
        for (int i = 0; i < _s.Cores.Length && i < _coreBars.Count; i++)
        {
            _coreBars[i].Height = Math.Max(3, 24 * _s.Cores[i]);
            _coreBars[i].Background = Level(_s.Cores[i]);
        }

        _rM.Set(_s.MemPct, Level(_s.MemPct));
        _memBar.Set(_s.MemPct, Level(_s.MemPct));
        _mem.Inlines.Clear();
        _mem.Inlines.Add(new System.Windows.Documents.Run($"{_s.MemUsedGb:0.0}") { FontWeight = FontWeights.SemiBold });
        _mem.Inlines.Add(new System.Windows.Documents.Run($" / {_s.MemTotalGb:0.0} GB") { Foreground = UI.Dim, FontSize = 11.5 });

        _down.Text = Rate(_s.NetDownKb); _downSpark.Push(_s.NetDownKb);
        _up.Text = Rate(_s.NetUpKb); _upSpark.Push(_s.NetUpKb);

        if (_s.BatPct < 0) { _batHost.Child = null; _bat.Text = ""; }
        else
        {
            _batHost.Child = Icons.Battery(_s.BatPct, _s.Charging);
            _bat.Text = $"{Math.Round(_s.BatPct * 100)}%" + (_s.BatLeft.Length > 0 ? $"  ·  {_s.BatLeft}" : "");
        }

        _top.Children.Clear();
        foreach (var (name, bytes, path) in _s.Top)
        {
            var (title, icon) = AppInfo.Get(name, path);
            var row = UI.Cols(26, -1, 0);
            row.Margin = new Thickness(0, 2, 0, 2);
            if (icon != null)
                row.Children.Add(new Image { Source = icon, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left });
            else
            {
                var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = UI.Card, HorizontalAlignment = HorizontalAlignment.Left, Child = UI.T(name.Length > 0 ? name[..1].ToUpper() : "?", 10, UI.Dim) };
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
            row.Children.Add(UI.T(bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.0}G" : $"{bytes >> 20}M", 12.5, UI.Dim).At(0, 2));
            _top.Children.Add(row);
        }
    }

    static string Rate(double kb) => kb >= 1024 ? $"{kb / 1024:0.0} MB/s" : $"{kb:0} KB/s";
}
