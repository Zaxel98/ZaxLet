using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZaxLet;

/// <summary>Wishlist produk: satu baris per item (gambar, judul, harga, sumber) dengan tombol buka link, edit, hapus. Item masuk dari link di clipboard.</summary>
public sealed class WishlistView : TabView
{
    public override string Key => "wish";
    public override string Glyph => "";
    public override string Label => "Wishlist";
    public override bool CenterV => false;

    readonly TextBlock _title = UI.T("", 14, null, true), _hint = UI.T("", 11, UI.Warn);
    readonly StackPanel _list = new();
    WishItem _edit;   // item yang sedang diedit; selama itu daftar tidak digambar ulang supaya ketikan tidak hilang
    System.Windows.Threading.DispatcherTimer _hintT;

    public WishlistView()
    {
        var root = UI.Rows(0, -1);
        var head = UI.Cols(0, -1, 0, 0);
        head.Children.Add(_title);
        _hint.Margin = new Thickness(10, 0, 0, 0);
        head.Children.Add(_hint.At(0, 1));
        head.Children.Add(UI.Btn(UI.T("+ Tempel link", 11.5, UI.Fg, true), Paste, UI.AccentBg, 9, new Thickness(10, 4, 10, 4)).At(0, 2));
        var info = UI.IconBtn("", () => WishGuide.Show(true), 13);   // info: panduan ekstensi browser (pertama kali tampil sendiri saat item pertama ditambah)
        info.ToolTip = L.T("Panduan: isi otomatis dari browser");
        head.Children.Add(info.At(0, 3));
        root.Children.Add(head);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list, Margin = new Thickness(0, 8, 0, 0) }.At(1));
        Children.Add(root);
    }

    public override void OnShow() { Wishlist.Changed += OnChanged; Render(); }
    public override void OnHide() { Wishlist.Changed -= OnChanged; _edit = null; }
    void OnChanged() { if (_edit == null) Render(); }

    void Paste()
    {
        string text = null;
        try { text = Clipboard.GetText(); } catch { }   // clipboard bisa sedang dipakai aplikasi lain
        if (!Wishlist.Add(text)) { Hint("Clipboard tidak berisi link. Salin link produknya dulu."); return; }
        if (!WishBridge.Connected) WishGuide.Show(false);   // pertama kali: tawarkan panduan isi otomatis (sekali saja)
    }

    void Hint(string s)
    {
        _hint.Text = L.T(s);
        _hintT ??= new() { Interval = TimeSpan.FromSeconds(4) };
        _hintT.Tick -= ClearHint; _hintT.Tick += ClearHint;
        _hintT.Stop(); _hintT.Start();
    }

    void ClearHint(object s, EventArgs e) { _hintT.Stop(); _hint.Text = ""; }

    void Render()
    {
        _title.Text = L.F("Wishlist · {0} item", Wishlist.Items.Count);
        _list.Children.Clear();
        if (Wishlist.Items.Count == 0)
        {
            var e = UI.T("Salin link produk dari Shopee, Tokopedia, dll., lalu klik Tempel link.", 12, UI.Dim);
            e.TextWrapping = TextWrapping.Wrap; e.Margin = new Thickness(0, 14, 0, 0);
            _list.Children.Add(e);
            return;
        }
        foreach (var it in Wishlist.Items.ToArray()) _list.Children.Add(Row(it));
    }

    UIElement Row(WishItem it)
    {
        var g = UI.Cols(0, -1, 0);
        var pic = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(8), Background = UI.Track, ClipToBounds = true, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        var img = Thumb(it);
        pic.Child = img != null ? new Image { Source = img, Stretch = Stretch.UniformToFill } : UI.Icon("", 16, UI.Faint);
        g.Children.Add(pic);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (_edit == it)
        {
            var t = new TextBox { Text = it.Title, FontSize = 12, Padding = new Thickness(6, 2, 6, 2) };
            var p = new TextBox { Text = it.Price, FontSize = 12, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 4, 0, 0), ToolTip = L.T("Harga, mis. IDR 149,000") };
            info.Children.Add(t); info.Children.Add(p);
            var pic2 = UI.Btn(UI.T("Tempel gambar dari clipboard", 11, UI.Dim), () =>
            {
                it.Title = t.Text.Trim() == "" ? it.Title : t.Text.Trim(); it.Price = p.Text.Trim();   // ketikan yang belum disimpan jangan hilang saat daftar digambar ulang
                if (Wishlist.SetImageFromClipboard(it)) { Wishlist.Save(); Render(); } else Hint("Clipboard tidak berisi gambar. Salin gambarnya dulu.");
            }, UI.Card, 8, new Thickness(8, 2, 8, 2));
            pic2.Margin = new Thickness(0, 4, 6, 0);
            var price2 = UI.Btn(UI.T("Tempel harga", 11, UI.Dim), () =>
            {
                string clip = null;
                try { clip = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { }
                var pr = WishParse.PriceFromText(clip);
                if (pr == "") Hint("Clipboard tidak berisi harga. Salin harganya dulu, mis. Rp149.000.");
                else p.Text = pr;
            }, UI.Card, 8, new Thickness(8, 2, 8, 2));
            price2.Margin = new Thickness(0, 4, 0, 0);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            tools.Children.Add(pic2); tools.Children.Add(price2);
            info.Children.Add(tools);
            g.Children.Add(info.At(0, 1));
            g.Children.Add(UI.IconBtn("", () => { it.Title = t.Text.Trim() == "" ? it.Title : t.Text.Trim(); it.Price = p.Text.Trim(); _edit = null; Wishlist.Save(); }, 14).At(0, 2));
        }
        else
        {
            var name = UI.T(it.Title, 12.5);
            name.TextWrapping = TextWrapping.Wrap; name.MaxHeight = 34; name.ToolTip = it.Title;   // dua baris, sisanya dipotong
            info.Children.Add(name);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            if (it.Price != "") { var pr = UI.T(it.Price, 12, UI.Fg, true); pr.Margin = new Thickness(0, 0, 8, 0); line.Children.Add(pr); }
            if (it.Source != "") line.Children.Add(new Border { Background = UI.CardOn, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 1), Child = UI.T(it.Source, 10.5, UI.Dim) });
            info.Children.Add(line);
            g.Children.Add(info.At(0, 1));
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            btns.Children.Add(UI.IconBtn("", () => Open(it.Url), 13));
            btns.Children.Add(UI.IconBtn("", () => { _edit = it; Render(); }, 13));
            btns.Children.Add(UI.IconBtn("", () => Wishlist.Remove(it), 13));
            g.Children.Add(btns.At(0, 2));
        }
        return new Border { Background = UI.Card, CornerRadius = new CornerRadius(10), Padding = new Thickness(7), Margin = new Thickness(0, 0, 0, 5), Child = g };
    }

    static readonly System.Collections.Generic.Dictionary<string, (long Stamp, ImageSource Img)> Thumbs = new();   // gambar diperbaiki dan diperkecil sekali per berkas, bukan tiap tab digambar

    static ImageSource Thumb(WishItem it)
    {
        if (string.IsNullOrEmpty(it.Image)) return null;
        try
        {
            var path = Path.Combine(Wishlist.Dir, it.Image);
            long stamp = File.GetLastWriteTimeUtc(path).Ticks;
            if (Thumbs.TryGetValue(it.Image, out var c) && c.Stamp == stamp) return c.Img;
            // PreservePixelFormat: tanpa itu WPF mengalikan warna dengan alfa saat memuat, dan gambar beralfa 0 kehilangan warnanya
            BitmapSource full;
            using (var fs = File.OpenRead(path))   // dibaca penuh lalu dilepas, jadi file bisa dihapus kapan saja
                full = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            full = OpaqueIfBlank(full);            // sebelum diperkecil: pengecilan mengalikan warna dengan alfa
            double sc = Math.Min(1, 96.0 / Math.Max(1, full.PixelWidth));
            var small = new TransformedBitmap(full, new ScaleTransform(sc, sc));
            small.Freeze();
            Thumbs[it.Image] = (stamp, small);
            return small;
        }
        catch { return null; }   // gambar rusak atau bukan gambar: tampil ikon saja
    }

    /// <summary>Gambar lama yang tersimpan dengan alfa 0 di semua piksel (salinan DIB dari peramban) tampil abu-abu: alfanya dipaksa penuh, warnanya masih utuh.</summary>
    static BitmapSource OpaqueIfBlank(BitmapSource src)
    {
        try
        {
            var fc = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = fc.PixelWidth, h = fc.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            fc.CopyPixels(px, stride, 0);
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) return src;   // ada piksel yang terlihat: gambar normal
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            var fixedSrc = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            fixedSrc.Freeze();
            return fixedSrc;
        }
        catch { return src; }
    }

    static void Open(string url)
    {
        if (!Wishlist.IsWeb(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }
}
