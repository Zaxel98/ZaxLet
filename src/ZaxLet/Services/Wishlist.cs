using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ZaxLet;

public sealed class WishItem
{
    public string Id { get; set; }
    public string Url { get; set; }
    public string Title { get; set; }
    public string Price { get; set; } = "";
    public string Source { get; set; } = "";
    public string Image { get; set; }   // nama file di folder wish, kosong bila tidak ada
    public DateTime Added { get; set; }
}

/// <summary>Daftar produk incaran. Item langsung masuk dengan sumber dan judul dari link; judul asli, gambar, dan harga menyusul bila halamannya bisa dibaca.</summary>
public static class Wishlist
{
    public static event Action Changed;
    public static List<WishItem> Items = Store.Load<List<WishItem>>("wishlist.json") ?? new();
    public static string Dir => Path.Combine(Settings.Dir, "wish");

    static readonly HttpClient Http = MakeHttp();
    static HttpClient MakeHttp()
    {
        // identitas jujur: ZaxLet hanya membaca pratinjau (meta tag) satu link yang ditempel pemilik. Toko yang menolak (403, tantangan bot,
        // halaman JavaScript) tidak dipaksa; judul dari link tetap tampil dan harga serta gambar diisi manual.
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ZaxLet/1.1 (link preview for a personal wishlist; +https://github.com/Zaxel98/ZaxLet)");
        h.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,image/*;q=0.8,*/*;q=0.5");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("id,en;q=0.8");
        return h;
    }

    /// <summary>Gambar manual dari clipboard (mis. salin gambar produk di Shopee, yang menolak dibaca otomatis). False bila clipboard tidak berisi gambar.</summary>
    public static bool SetImageFromClipboard(WishItem it)
    {
        try
        {
            System.Windows.Media.Imaging.BitmapSource bmp = null;
            // Chrome/Edge menaruh format "PNG" (alfa benar); aplikasi lain hanya DIB, yang sering beralfa 0 dan tampil transparan
            if (System.Windows.Clipboard.GetDataObject() is { } d && d.GetDataPresent("PNG") && d.GetData("PNG") is MemoryStream png)
                bmp = new System.Windows.Media.Imaging.PngBitmapDecoder(png, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            if (bmp == null)
            {
                if (!System.Windows.Clipboard.ContainsImage() || System.Windows.Clipboard.GetImage() is not { } dib) return false;
                bmp = new System.Windows.Media.Imaging.FormatConvertedBitmap(dib, System.Windows.Media.PixelFormats.Bgr32, null, 0);   // buang alfa
            }
            Directory.CreateDirectory(Dir);
            var name = it.Id + ".png";
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(Path.Combine(Dir, name))) enc.Save(fs);
            it.Image = name;
            return true;
        }
        catch (Exception ex) { App.Log(ex); return false; }
    }

    public static bool IsWeb(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>False bila teksnya bukan link http(s). Pengambilan info halaman berjalan di latar dan memperbarui item sendiri.</summary>
    public static bool Add(string url)
    {
        url = (url ?? "").Trim();
        if (!IsWeb(url)) return false;
        var key = WishBridgeCore.Key(url);
        var dup = Items.FirstOrDefault(i => i.Url == url || key != null && WishBridgeCore.Key(i.Url) == key);
        if (dup != null)   // tempel link yang sama lagi = isi info yang masih kosong (dari ekstensi, atau dibaca langsung)
        {
            if (string.IsNullOrEmpty(dup.Image) || dup.Price == "") { if (WishBridge.TryGet(url) is { } known) { Fill(dup, known); Save(); } else _ = Fetch(dup); }
            return true;
        }
        var it = new WishItem { Id = Guid.NewGuid().ToString("N"), Url = url, Source = WishParse.Source(url), Title = WishParse.SlugTitle(url), Added = DateTime.Now };
        Items.Insert(0, it);
        if (WishBridge.TryGet(url) is { } page) { Fill(it, page); Save(); }   // ekstensi sudah membaca halamannya di browser pemilik: lengkap tanpa jaringan
        else { Save(); _ = Fetch(it); }
        return true;
    }

    /// <summary>Isi item dari info halaman yang dikirim ekstensi. Yang kosong di kiriman tidak menimpa yang sudah ada.</summary>
    static void Fill(WishItem it, WishBridgeCore.Page p)
    {
        if (p.Title != "") it.Title = p.Title;
        if (p.Price != "") it.Price = p.Price;
        if (p.Image != null)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var name = it.Id + WishBridgeCore.ImageExt(p.Image);
                File.WriteAllBytes(Path.Combine(Dir, name), p.Image);
                if (it.Image != null && it.Image != name) try { File.Delete(Path.Combine(Dir, it.Image)); } catch { }
                it.Image = name;
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    /// <summary>Ekstensi baru mengirim halaman (UI thread): item yang sudah ada untuk link itu dan masih kosong dilengkapi; link baru tidak ditambahkan sendiri.</summary>
    public static void OnPageSeen(WishBridgeCore.Page p)
    {
        var key = WishBridgeCore.Key(p.Url);
        var it = Items.FirstOrDefault(i => WishBridgeCore.Key(i.Url) == key);
        if (it == null || !string.IsNullOrEmpty(it.Image) && it.Price != "") return;
        Fill(it, p);
        Save();
    }

    public static void Remove(WishItem it)
    {
        Items.Remove(it);
        try { if (it.Image != null) File.Delete(Path.Combine(Dir, it.Image)); } catch { }
        Save();
    }

    public static void Save()
    {
        Store.Save("wishlist.json", Items);
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    static async Task Fetch(WishItem it)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var html = System.Text.Encoding.UTF8.GetString(await Read(it.Url, 512 * 1024, cts.Token));
            var (title, image, price) = WishParse.Meta(html);
            if (title != "") it.Title = title;
            it.Price = price;
            if (image != "" && Uri.TryCreate(new Uri(it.Url), image, out var iu) && (iu.Scheme == Uri.UriSchemeHttp || iu.Scheme == Uri.UriSchemeHttps))
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllBytes(Path.Combine(Dir, it.Id), await Read(iu.AbsoluteUri, 3 * 1024 * 1024, cts.Token));
                it.Image = it.Id;
            }
        }
        catch (Exception ex) { App.Log(ex); }   // gagal baca (mis. Shopee) bukan kesalahan: item tetap ada dan bisa diisi manual
        if (Items.Contains(it)) Save();
    }

    /// <summary>Uji pengambilan satu link tanpa menyentuh daftar (flag --wish-probe=URL): laporan status, meta yang ditemukan, dan unduhan gambar.</summary>
    public static async Task<string> Probe(string url)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var bytes = await Read(url, 512 * 1024, cts.Token);
            var html = System.Text.Encoding.UTF8.GetString(bytes);
            sb.AppendLine($"html: {bytes.Length} byte");
            var (title, image, price) = WishParse.Meta(html);
            sb.AppendLine($"title: {title}\nimage: {image}\nprice: {price}");
            if (image != "" && Uri.TryCreate(new Uri(url), image, out var iu))
                sb.AppendLine($"image download: {(await Read(iu.AbsoluteUri, 3 * 1024 * 1024, cts.Token)).Length} byte");
        }
        catch (Exception ex) { sb.AppendLine($"GAGAL {ex.GetType().Name}: {ex.Message}"); }
        return sb.ToString();
    }

    /// <summary>Baca respons paling banyak <paramref name="max"/> byte; sisanya dibuang.</summary>
    static async Task<byte[]> Read(string url, int max, CancellationToken ct)
    {
        using var rsp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        rsp.EnsureSuccessStatusCode();
        await using var s = await rsp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while (ms.Length < max && (n = await s.ReadAsync(buf, ct)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }
}
