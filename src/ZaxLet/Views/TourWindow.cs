using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ZaxLet;

/// <summary>Tur singkat untuk pengguna baru (muncul sekali, bisa dibuka lagi dari Pengaturan > Umum).</summary>
public sealed class TourWindow : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static readonly (string Title, string Body)[] Steps =
    {
        ("Selamat datang di ZaxLet", "Island di atas layar menampilkan jam, lagu beserta liriknya, timer, dan notifikasi HP, lalu terbuka menjadi panel berisi banyak alat."),
        ("Buka dan geser", "Arahkan kursor (atau klik, sesuai Pengaturan) ke island untuk membuka panel. Pindah tab lewat ikon di bawah atau sapuan dua jari ke samping. Scroll di atas island untuk volume; klik dua kali island yang tertutup untuk putar atau jeda."),
        ("Sembunyikan", "Klik kanan island untuk menyembunyikannya; arahkan kursor ke tepi atas layar untuk mengintip, klik kanan lagi untuk menampilkannya terus. Ctrl+Alt+I dan menu tray juga bisa."),
        ("Pengaturan", "Ikon roda gigi di panel atau ikon ZaxLet di tray membuka Pengaturan: bahasa, tab, lokasi sholat, sambungan HP, pembaruan, dan cadangan."),
    };

    int _i;
    readonly TextBlock _step = UI.T("", 11, UI.Faint), _title = UI.T("", 20, null, true), _body = UI.T("", 13.5, UI.Dim);
    readonly TextBlock _nextText = UI.T("", 12.5, UI.Fg, true);

    public static void ShowIt(bool force = false)
    {
        if (!force && Settings.Cur.TourDone) return;
        new TourWindow().Show();
    }

    TourWindow()
    {
        Title = "ZaxLet";
        Width = 500; Height = 330;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = Brushes.Black;
        _body.TextWrapping = TextWrapping.Wrap;
        _title.Margin = new Thickness(0, 6, 0, 10);

        var root = UI.Rows(0, -1, 0);
        root.Margin = new Thickness(28, 22, 28, 22);
        var top = new StackPanel();
        top.Children.Add(_step); top.Children.Add(_title);
        root.Children.Add(top);
        root.Children.Add(_body.At(1));
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btns.Children.Add(UI.Btn(UI.T("Lewati", 12.5, UI.Dim), Close, UI.Card, 10, new Thickness(14, 6, 14, 6)));
        var next = UI.Btn(_nextText, Next, UI.AccentBg, 10, new Thickness(16, 6, 16, 6));
        next.Margin = new Thickness(8, 0, 0, 0);
        btns.Children.Add(next);
        root.Children.Add(btns.At(2));
        Content = root;
        Closed += (_, _) => { Settings.Cur.TourDone = true; Settings.Save(); };
        SourceInitialized += (_, _) => { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4); };
        Render();
    }

    void Next() { if (_i + 1 >= Steps.Length) Close(); else { _i++; Render(); } }

    void Render()
    {
        _step.Text = $"{_i + 1} / {Steps.Length}";
        _title.Text = L.T(Steps[_i].Title);
        _body.Text = L.T(Steps[_i].Body);
        _nextText.Text = L.T(_i + 1 >= Steps.Length ? "Selesai" : "Berikutnya");
    }
}
