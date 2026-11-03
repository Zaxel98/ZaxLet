using System;
using Path = System.IO.Path;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SD = System.Drawing;

namespace ZaxLet;

public sealed class ShelfView : TabView
{
    public override string Key => "shelf";
    public override string Glyph => "";
    public override string Label => "Shelf";
    public override bool CenterV => false;
    public override double H => 188;

    readonly TextBlock _title = UI.T("", 14, null, true);
    readonly Border _chip;
    readonly TextBlock _chipText = UI.T("Sambung HP", 12), _camText = UI.T("Kamera", 12), _flipText = UI.T("", 12);
    readonly Border _camChip, _flipChip;
    readonly Grid _body = new();
    bool _phone = ShareServer.I.On;

    public ShelfView()
    {
        var root = UI.Rows(0, -1);
        var head = UI.Cols(-1, 0, 0, 0);
        head.Margin = new Thickness(0, 0, 0, 8);
        head.Children.Add(_title);
        _camChip = UI.Btn(_camText, ToggleCam, UI.CardOn, 12, new Thickness(10, 4, 10, 4));
        _camChip.Margin = new Thickness(0, 0, 6, 0);
        head.Children.Add(_camChip.At(0, 1));
        _flipChip = UI.Btn(_flipText, FlipCam, UI.CardOn, 12, new Thickness(10, 4, 10, 4));
        _flipChip.Margin = new Thickness(0, 0, 6, 0);
        head.Children.Add(_flipChip.At(0, 2));
        _chip = UI.Btn(_chipText, TogglePhone, UI.CardOn, 12, new Thickness(10, 4, 10, 4));
        head.Children.Add(_chip.At(0, 3));
        root.Children.Add(head);
        root.Children.Add(_body.At(1));
        Children.Add(root);
    }

    readonly System.Windows.Threading.DispatcherTimer _camT = new() { Interval = TimeSpan.FromSeconds(1) };
    public override void OnShow() { Shelf.Changed += Render; _camT.Tick -= CamTick; _camT.Tick += CamTick; _camT.Start(); Render(); }
    public override void OnHide() { Shelf.Changed -= Render; _camT.Stop(); }
    void CamTick(object s, EventArgs e) => RefreshCam();

    // kamera HP: nyalakan/matikan dan ganti depan/belakang langsung dari tab
    void ToggleCam()
    {
        if (PhoneCam.Active) ShareServer.I.Command("cam off"); else CameraWindow.CamOn();
        Render();
    }

    void FlipCam()
    {
        Settings.Cur.CamFront = !Settings.Cur.CamFront;
        Settings.Save();
        if (PhoneCam.Active) CameraWindow.CamOn();   // kamera sedang menyala: pindah kamera tanpa mematikan layanan
        Render();
    }

    void RefreshCam()
    {
        _camChip.Visibility = _flipChip.Visibility = _phone ? Visibility.Visible : Visibility.Collapsed;
        _camText.Text = L.T(PhoneCam.Active ? "Matikan kamera" : "Nyalakan kamera");
        _flipText.Text = L.T(Settings.Cur.CamFront ? "Depan" : "Belakang") + " ⇄";
        _camChip.Background = PhoneCam.Active ? UI.AccentBg : UI.CardOn;
    }

    void TogglePhone()
    {
        _phone = !_phone;
        if (_phone) { ShareServer.I.Start(); PhoneGuide.Show(false); }   // pertama kali dinyalakan: panduan sambung HP tampil sendiri
        else ShareServer.I.Stop();
        Render();
    }

    void Render()
    {
        _title.Text = L.F("Shelf · {0} file", Shelf.Items.Count);
        _chipText.Text = L.T(_phone ? "Putuskan HP" : "Sambung HP");
        _chip.Background = _phone ? UI.AccentBg : UI.CardOn;
        RefreshCam();
        _body.Children.Clear();
        _body.ColumnDefinitions.Clear();
        _body.Children.Add(new Rectangle { Stroke = UI.Faint, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4, 3 }, RadiusX = 12, RadiusY = 12 });

        var files = new StackPanel { Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Top };   // satu file per baris selebar slot: nama terbaca utuh, gulir bila banyak
        if (Shelf.Items.Count == 0 && !_phone)
        {
            var hint = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            hint.Children.Add(UI.Icon("", 20, UI.Dim));
            var t = UI.T("Seret file ke sini untuk disimpan sementara", 12.5, UI.Dim);
            t.Margin = new Thickness(0, 6, 0, 0);
            hint.Children.Add(t);
            _body.Children.Add(hint);
            return;
        }
        foreach (var p in Shelf.Items.ToArray()) files.Children.Add(Chip(p));

        if (!_phone)
        {
            _body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = files });
            return;
        }
        // mode HP: file di kiri, QR di kanan
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = files };
        _body.Children.Add(sv);
        // QR halaman web dihapus: sambungan HP lewat aplikasi (unduh + pairing), jadi di sini cukup status dan panduannya
        var qr = new StackPanel { Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Center, Width = 150 };
        var (ok, state) = PhoneGuide.State();
        qr.Children.Add(GuideWindow.Status(ok, state));
        var guide = UI.Btn(UI.T("Panduan sambung HP", 11.5, UI.Fg, true), () => PhoneGuide.Show(true), UI.AccentBg, 10, new Thickness(10, 5, 10, 5));
        guide.Margin = new Thickness(0, 8, 0, 0); guide.HorizontalAlignment = HorizontalAlignment.Left;
        qr.Children.Add(guide);
        Grid.SetColumn(qr, 1);
        _body.Children.Add(qr);
    }

    UIElement Chip(string path)
    {
        var sp = UI.Cols(0, -1);   // ikon + nama selebar sisa slot (agar nama bisa turun baris, bukan melebar tanpa batas)
        sp.Children.Add(new Image { Source = IconOf(path), Width = 20, Height = 20, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Top });
        var name = UI.T(Path.GetFileName(path), 12);
        name.TextWrapping = TextWrapping.Wrap;   // nama panjang turun ke baris berikutnya, tidak dipotong
        name.TextTrimming = TextTrimming.None;
        sp.Children.Add(name.At(0, 1));
        var b = UI.Btn(sp, () => Open(path), UI.Card, 10, new Thickness(8, 5, 8, 5));
        b.Margin = new Thickness(0, 0, 0, 5);
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        b.ToolTip = path + L.T("\nKlik: buka · seret: keluarkan · klik kanan: hapus");
        b.MouseRightButtonUp += (_, e) => { e.Handled = true; Shelf.Remove(path); };
        Point? down = null;
        b.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(b);
        b.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || down == null) return;
            var d = e.GetPosition(b) - down.Value;
            if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
            down = null;
            IslandWindow.Instance?.KeepOpen(true);
            try { DragDrop.DoDragDrop(b, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy); }
            finally { IslandWindow.Instance?.KeepOpen(false); }
        };
        return b;
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }

    static ImageSource IconOf(string path)
    {
        try
        {
            using var ic = SD.Icon.ExtractAssociatedIcon(path);
            if (ic == null) return null;
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    public static ImageSource Qr(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.BitmapByteQRCode(data).GetGraphic(6); // BMP: warna persis (PNG tampil abu-abu di WPF)
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(png);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
