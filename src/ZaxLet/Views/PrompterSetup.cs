using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZaxLet;

/// <summary>Jendela penyiapan teleprompter: menempel/memuat naskah, mengatur kecepatan & ukuran huruf, lalu memulai PrompterWindow.</summary>
public sealed class PrompterSetup : Window
{
    static readonly Brush Bg = UI.B("#FF16161A"), CardBg = UI.B("#FF1F1F24"),
        Fg = UI.B("#FFE6E6EA"), Dim = UI.B("#FFB4B4BC");

    const int MaxChars = 20000;
    const long MaxFileBytes = 200 * 1024;

    static PrompterSetup _i;
    static string _lastScript;

    readonly TextBox _box;
    readonly TextBlock _counter;
    readonly TextBlock _err;

    public static void ShowIt()
    {
        _i ??= new PrompterSetup();
        _i.Show();
        if (_i.WindowState == WindowState.Minimized) _i.WindowState = WindowState.Normal;
        _i.Activate();
        _i.Dispatcher.BeginInvoke(() => _i._box.Focus());
    }

    PrompterSetup()
    {
        Title = L.T("Teleprompter");
        Width = 520;
        Height = 440;
        MinWidth = 440;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        Foreground = Fg;
        Icon = IslandWindow.Instance?.Icon;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var guide = new TextBlock
        {
            Text = L.T("1. Tempel atau ketik naskah di kotak.\n2. Atur kecepatan dan ukuran huruf.\n3. Tekan Mulai: naskah bergulir di bawah kamera dan TIDAK ikut terekam. Arahkan kursor ke naskah untuk menjeda, roda mouse mengubah kecepatan, klik kanan menutup."),
            Foreground = Dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        };
        root.Children.Add(new Border
        {
            Background = CardBg,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = guide
        });

        _box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = MaxChars,
            Background = Brushes.Transparent,
            Foreground = Fg,
            CaretBrush = Fg,
            BorderThickness = new Thickness(0),
            FontSize = 13.5,
            Padding = new Thickness(10, 8, 10, 8),
            VerticalContentAlignment = VerticalAlignment.Top,
            Text = InitialScript()
        };
        var boxHost = new Border
        {
            Background = CardBg,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 10, 0, 6),
            Child = _box
        };
        Grid.SetRow(boxHost, 1);
        root.Children.Add(boxHost);

        _err = new TextBlock
        {
            Text = L.T("Naskah masih kosong"),
            Foreground = UI.Bad,
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };
        _counter = new TextBlock
        {
            Foreground = Dim,
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusRow = UI.Cols(-1, 0);
        statusRow.Children.Add(_err);
        statusRow.Children.Add(_counter.At(0, 1));
        Grid.SetRow(statusRow, 2);
        root.Children.Add(statusRow);

        var wpmCtl = Num(60, 300, 10, " kpm",
            () => Math.Clamp(Settings.Cur.PrompterWpm, 60, 300),
            v => Settings.Cur.PrompterWpm = v);
        var fontCtl = Num(16, 48, 2, " pt",
            () => Math.Clamp(Settings.Cur.PrompterFont, 16, 48),
            v => Settings.Cur.PrompterFont = v);

        var opts = new Grid { Margin = new Thickness(0, 10, 0, 12) };
        opts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        opts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        opts.Children.Add(Labeled("Kecepatan", wpmCtl));
        opts.Children.Add(Labeled("Ukuran huruf", fontCtl).At(0, 1));
        Grid.SetRow(opts, 3);
        root.Children.Add(opts);

        var btnLoad = UI.Btn(UI.T("Buka file .txt", 12.5, Fg), OpenTxt, CardBg, 10, new Thickness(12, 7, 12, 7));
        var btnClose = UI.Btn(UI.T("Tutup", 12.5, Dim), Close, CardBg, 10, new Thickness(14, 7, 14, 7));
        btnClose.Margin = new Thickness(0, 0, 8, 0);
        var btnStart = UI.Btn(UI.T("Mulai", 12.5, Fg, true), StartPrompter, UI.AccentBg, 10, new Thickness(18, 7, 18, 7));
        btnStart.BorderBrush = UI.Accent;
        btnStart.BorderThickness = new Thickness(1);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rightBtns.Children.Add(btnClose);
        rightBtns.Children.Add(btnStart);

        var actions = UI.Cols(0, -1);
        actions.Children.Add(btnLoad);
        actions.Children.Add(rightBtns.At(0, 1));
        Grid.SetRow(actions, 4);
        root.Children.Add(actions);

        Content = root;

        _box.TextChanged += (_, _) =>
        {
            _lastScript = _box.Text;
            if (_err.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(_box.Text))
                _err.Visibility = Visibility.Collapsed;
            UpdateCounter();
        };
        UpdateCounter();

        Loaded += (_, _) => _box.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        Closed += (_, _) => _i = null;
    }

    static string InitialScript()
    {
        if (!string.IsNullOrEmpty(_lastScript)) return _lastScript;
        try
        {
            if (Clipboard.ContainsText())
            {
                var t = Clipboard.GetText() ?? "";
                return t.Length > MaxChars ? t[..MaxChars] : t;
            }
        }
        catch { }
        return "";
    }

    void UpdateCounter()
    {
        var s = _box.Text;
        int words = 0;
        bool inWord = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) inWord = false;
            else if (!inWord) { inWord = true; words++; }
        }
        _counter.Text = $"{words} kata · {s.Length} / {MaxChars} karakter";
    }

    void OpenTxt()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Teks (*.txt)|*.txt" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > MaxFileBytes)
            {
                _err.Text = L.T("Ukuran file maksimal 200 KB");
                _err.Visibility = Visibility.Visible;
                return;
            }
            var text = File.ReadAllText(dlg.FileName, Encoding.UTF8);
            if (text.Length > MaxChars) text = text[..MaxChars];
            _err.Visibility = Visibility.Collapsed;
            _box.Text = text;
            _box.Focus();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _err.Text = L.T("Gagal membaca file");
            _err.Visibility = Visibility.Visible;
        }
    }

    void StartPrompter()
    {
        var text = _box.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            _err.Text = L.T("Naskah masih kosong");
            _err.Visibility = Visibility.Visible;
            _box.Focus();
            return;
        }
        _lastScript = _box.Text;
        PrompterWindow.Start(text);
        Close();
    }

    static UIElement Labeled(string label, UIElement ctl)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var t = UI.T(label, 12.5, Dim);
        t.Margin = new Thickness(0, 0, 8, 0);
        sp.Children.Add(t);
        sp.Children.Add(ctl);
        return sp;
    }

    static UIElement Num(int min, int max, int step, string suffix, Func<int> get, Action<int> set)
    {
        var val = UI.T("", 13, Fg, true);
        val.MinWidth = 64;
        val.TextAlignment = TextAlignment.Center;
        void Paint() => val.Text = get() + L.T(suffix);
        void Bump(int d) { set(Math.Clamp(get() + d, min, max)); Settings.Apply(); Paint(); }
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(UI.Btn(UI.T("−", 14.5, Fg), () => Bump(-step), CardBg, 999, new Thickness(9, 0, 9, 2)));
        row.Children.Add(val);
        row.Children.Add(UI.Btn(UI.T("+", 14.5, Fg), () => Bump(step), CardBg, 999, new Thickness(8, 0, 8, 2)));
        Paint();
        return row;
    }
}
