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
    public string Image { get; set; }   // filename in the wish folder, empty if none
    public DateTime Added { get; set; }
}

/// <summary>Target product list. Items are added immediately with source and title from the link; real title, image, and price follow if the page can be read.</summary>
public static class Wishlist
{
    public static event Action Changed;
    public static List<WishItem> Items = Store.Load<List<WishItem>>("wishlist.json") ?? new();
    public static string Dir => Path.Combine(Settings.Dir, "wish");

    static readonly HttpClient Http = MakeHttp();
    static HttpClient MakeHttp()
    {
        // honest identity: ZaxLet only reads preview (meta tags) of a single link pasted by the owner. Stores that reject it (403, bot challenge,
        // JavaScript-only pages) are not forced; the link-derived title stays and price/image are filled manually.
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ZaxLet/1.1 (link preview for a personal wishlist; +https://github.com/Zaxel98/ZaxLet)");
        h.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,image/*;q=0.8,*/*;q=0.5");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("id,en;q=0.8");
        return h;
    }

    /// <summary>Manual image from clipboard (e.g. copy product image on Shopee, which blocks automated reading). False if clipboard has no image.</summary>
    public static bool SetImageFromClipboard(WishItem it)
    {
        try
        {
            System.Windows.Media.Imaging.BitmapSource bmp = null;
            // Chrome/Edge put "PNG" format (proper alpha); other apps only put DIB, which often has alpha 0 and renders transparent
            if (System.Windows.Clipboard.GetDataObject() is { } d && d.GetDataPresent("PNG") && d.GetData("PNG") is MemoryStream png)
                bmp = new System.Windows.Media.Imaging.PngBitmapDecoder(png, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            if (bmp == null)
            {
                if (!System.Windows.Clipboard.ContainsImage() || System.Windows.Clipboard.GetImage() is not { } dib) return false;
                bmp = new System.Windows.Media.Imaging.FormatConvertedBitmap(dib, System.Windows.Media.PixelFormats.Bgr32, null, 0);   // drop alpha
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

    /// <summary>False if text is not an http(s) link. Page info fetching runs in the background and updates the item itself.</summary>
    public static bool Add(string url)
    {
        url = (url ?? "").Trim();
        if (!IsWeb(url)) return false;
        var key = WishBridgeCore.Key(url);
        var dup = Items.FirstOrDefault(i => i.Url == url || key != null && WishBridgeCore.Key(i.Url) == key);
        if (dup != null)   // pasting the same link again = fill in still-empty info (from extension, or fetched directly)
        {
            if (string.IsNullOrEmpty(dup.Image) || dup.Price == "") { if (WishBridge.TryGet(url) is { } known) { Fill(dup, known); Save(); } else _ = Fetch(dup); }
            return true;
        }
        var it = new WishItem { Id = Guid.NewGuid().ToString("N"), Url = url, Source = WishParse.Source(url), Title = WishParse.SlugTitle(url), Added = DateTime.Now };
        Items.Insert(0, it);
        if (WishBridge.TryGet(url) is { } page) { Fill(it, page); Save(); }   // extension already read the page in owner's browser: complete without network
        else { Save(); _ = Fetch(it); }
        return true;
    }

    /// <summary>Fill item from page info sent by the extension. Empty fields in the payload do not overwrite existing ones.</summary>
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

    /// <summary>Extension newly sent a page (UI thread): existing item for that link with missing fields is completed; new links are not added automatically.</summary>
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
        catch (Exception ex) { App.Log(ex); }   // read failure (e.g. Shopee) is not an error: item remains and can be filled manually
        if (Items.Contains(it)) Save();
    }

    /// <summary>Test fetching a single link without touching the list (flag --wish-probe=URL): reports status, found meta, and image download.</summary>
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

    /// <summary>Read at most <paramref name="max"/> bytes of the response; the rest is discarded.</summary>
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
