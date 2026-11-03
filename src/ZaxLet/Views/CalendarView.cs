using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZaxLet;

public sealed class CalendarView : TabView
{
    public override string Key => "calendar";
    public override string Glyph => "";
    public override string Label => "Hari ini";
    public override bool CenterV => false;

    static readonly string[] Days = L.English ? new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" }
        : new[] { "Senin", "Selasa", "Rabu", "Kamis", "Jumat", "Sabtu", "Minggu" };
    static readonly string[] Short = L.English ? new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }
        : new[] { "Sen", "Sel", "Rab", "Kam", "Jum", "Sab", "Min" };
    static readonly string[] Months = L.English ? new[] { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" }
        : new[] { "Januari", "Februari", "Maret", "April", "Mei", "Juni", "Juli", "Agustus", "September", "Oktober", "November", "Desember" };

    static readonly Brush TaskRed = UI.B("#CCE5484D"), DoneRed = UI.B("#55E5484D");   // tugas belum selesai = merah pekat, semua selesai = merah redup
    readonly CalendarService _c = CalendarService.I;
    readonly TextBlock _head = UI.T("", 13, null, true), _city = UI.T("", 11.5, UI.Dim), _month = UI.T("", 13, null, true);
    readonly Grid _grid = new();
    readonly TextBlock _info = new() { Foreground = UI.Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _tasks = new();
    readonly TextBox _input = new() { FontSize = 12, Padding = new Thickness(8, 3, 8, 3) };
    DateTime _first = new(DateTime.Now.Year, DateTime.Now.Month, 1);
    DateOnly _sel = DateOnly.FromDateTime(DateTime.Now);

    public CalendarView()
    {
        var body = UI.Cols(250, 1, -1);

        // kiri: navigasi bulan + kisi tanggal (padat: 6 baris x 23 px)
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nav = UI.Cols(0, -1, 0);
        nav.Children.Add(UI.IconBtn("", () => Move(-1), 11));
        nav.Children.Add(_month.At(0, 1));
        _month.HorizontalAlignment = HorizontalAlignment.Center;
        nav.Children.Add(UI.IconBtn("", () => Move(1), 11).At(0, 2));
        left.Children.Add(nav);
        for (int i = 0; i < 7; i++) _grid.ColumnDefinitions.Add(new ColumnDefinition());
        left.Children.Add(_grid);
        body.Children.Add(left);
        body.Children.Add(new Border { Width = 1, Background = UI.Track, Margin = new Thickness(0, 2, 0, 2) }.At(0, 1));

        // kanan: tanggal terpilih + kota, info libur, daftar tugas, kolom tambah
        var right = UI.Rows(0, 0, -1, 0);
        right.Margin = new Thickness(14, 0, 0, 0);
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(_head);
        var pin = UI.Icon("", 11, UI.Dim);
        pin.Margin = new Thickness(10, 0, 4, 0);
        top.Children.Add(pin);
        top.Children.Add(_city);
        right.Children.Add(top);
        _info.Margin = new Thickness(0, 3, 0, 4);
        right.Children.Add(_info.At(1));
        right.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _tasks }.At(2));

        _input.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            _c.Add(_sel, _input.Text);
            _input.Text = "";
        };
        var hint = UI.Rows(0);
        var ph = new TextBlock { Text = L.T("Tambah tugas…"), Foreground = UI.Faint, FontSize = 12, IsHitTestVisible = false, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _input.TextChanged += (_, _) => ph.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        hint.Children.Add(_input);
        hint.Children.Add(ph);
        right.Children.Add(hint.At(3));
        body.Children.Add(right.At(0, 2));
        Children.Add(body);
    }

    public override void OnShow()
    {
        _c.Changed += OnChanged;
        _sel = DateOnly.FromDateTime(DateTime.Now);
        _first = new DateTime(_sel.Year, _sel.Month, 1);
        _ = _c.LoadHolidaysAsync();
        _ = _c.LoadAgendaAsync();
        Render();
    }

    public override void OnHide() => _c.Changed -= OnChanged;

    void OnChanged() => Dispatcher.BeginInvoke(Render);

    void Move(int delta) { _first = _first.AddMonths(delta); Render(); }

    static string LongDate(DateOnly d) => $"{Days[((int)d.DayOfWeek + 6) % 7]}, {d.Day} {Months[d.Month - 1]} {d.Year}";

    void Render()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var st = Settings.Cur;
        _month.Text = $"{Months[_first.Month - 1]} {_first.Year}";

        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        for (int r = 0; r < 7; r++) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(r == 0 ? 18 : 23) });
        for (int i = 0; i < 7; i++)
        {
            var h = UI.T(Short[(i + (st.WeekMonday ? 0 : 6)) % 7], 11, UI.Dim);
            h.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(h, i);
            _grid.Children.Add(h);
        }
        int firstDow = st.WeekMonday ? 1 : 0;   // DayOfWeek kolom pertama
        var start = _first.AddDays(-(((int)_first.DayOfWeek - firstDow + 7) % 7));
        for (int k = 0; k < 42; k++)
        {
            var d = DateOnly.FromDateTime(start.AddDays(k));
            bool inMonth = d.Month == _first.Month;
            bool holiday = st.ShowHolidays && _c.Holidays.ContainsKey(d);
            var label = UI.T(d.Day.ToString(), 12, holiday && inMonth ? UI.Bad : inMonth ? UI.Fg : UI.Faint, inMonth);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var cell = new Border
            {
                Width = 28, Height = 21, CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand,
                Background = _c.HasTasks(d) ? (_c.On(d).All(t => t.Done) ? DoneRed : TaskRed) : d == _sel ? UI.CardOn : Brushes.Transparent,   // merah = ada catatan/tugas di tanggal itu
                BorderBrush = d == today ? UI.Accent : d == _sel ? UI.B("#99FFFFFF") : Brushes.Transparent, BorderThickness = new Thickness(1.3)
            };
            if (_c.HasTasks(d) && inMonth) label.Foreground = Brushes.White;
            cell.Child = label;
            var dd = d;
            cell.MouseLeftButtonUp += (_, _) => { _sel = dd; if (dd.Month != _first.Month) _first = new DateTime(dd.Year, dd.Month, 1); Render(); };
            Grid.SetRow(cell, 1 + k / 7);
            Grid.SetColumn(cell, k % 7);
            _grid.Children.Add(cell);
        }

        // panel kanan
        _head.Text = LongDate(_sel);
        _city.Text = st.City;
        bool hol = st.ShowHolidays;
        var info = hol && _c.Holidays.TryGetValue(_sel, out var hn) ? L.F("Libur: {0}", hn) : L.T("Tidak ada acara.");
        if (hol && !_c.Holidays.ContainsKey(_sel) && _c.NextHoliday(_sel) is { } nx)
        {
            int days = nx.Date.DayNumber - today.DayNumber;
            info += L.F(" Libur: {0} · {1}, {2} {3}", nx.Name, Short[((int)nx.Date.DayOfWeek + 6) % 7], nx.Date.Day, Months[nx.Date.Month - 1][..3]) + (days > 0 ? L.F(" ({0} hari lagi)", days) : "");
        }
        if (_c.Agenda.TryGetValue(_sel, out var evs))   // acara dari kalender iCal pribadi
        {
            foreach (var ev in evs.Take(3)) info += "\n• " + (ev.Time.Length > 0 ? ev.Time + " " : "") + ev.Title;
            if (evs.Count > 3) info += "\n" + L.F("+{0} acara lagi", evs.Count - 3);
        }
        _info.Text = info;

        _tasks.Children.Clear();
        var list = _c.On(_sel).OrderBy(t => t.Done).ToList();
        if (list.Count == 0)
        {
            var e = UI.T("Belum ada tugas", 11.5, UI.Dim);
            e.Margin = new Thickness(0, 8, 0, 8);
            e.HorizontalAlignment = HorizontalAlignment.Center;
            _tasks.Children.Add(e);
        }
        foreach (var t in list)
        {
            var row = UI.Cols(0, -1, 0);
            row.Margin = new Thickness(0, 1, 0, 1);
            var tt = t;
            row.Children.Add(UI.Btn(UI.Icon(t.Done ? "" : "", 12, t.Done ? UI.Accent : UI.Dim), () => _c.Toggle(tt), Brushes.Transparent, 6, new Thickness(2)));
            var tx = UI.T(t.Text, 12, t.Done ? UI.Faint : UI.Fg);
            tx.Margin = new Thickness(6, 0, 6, 0);
            if (t.Done) tx.TextDecorations = TextDecorations.Strikethrough;
            row.Children.Add(tx.At(0, 1));
            row.Children.Add(UI.IconBtn("", () => _c.Remove(tt), 10).At(0, 2));
            _tasks.Children.Add(row);
        }
    }
}
