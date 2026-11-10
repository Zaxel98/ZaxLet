using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace ZaxLet;

/// <summary>Pembaca halaman produk untuk Wishlist: tanpa jaringan dan tanpa WPF, jadi bisa dites mandiri. Longgar dan tidak pernah melempar: yang tidak ketemu dikembalikan kosong.</summary>
public static class WishParse
{
    static readonly Regex MetaTag = new(@"<meta\s[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TitleTag = new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex JsonPrice = new(@"""price""\s*:\s*""?([\d.,]+)""?", RegexOptions.Compiled);
    static readonly Regex JsonCur = new(@"""priceCurrency""\s*:\s*""([A-Za-z]{3})""", RegexOptions.Compiled);

    static string Attr(string tag, string name)
    {
        var m = Regex.Match(tag, name + @"\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim() : "";
    }

    /// <summary>Judul, alamat gambar (bisa relatif), dan harga siap tampil ("IDR 149,000") dari HTML sebuah halaman produk.</summary>
    public static (string Title, string Image, string Price) Meta(string html)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(html)) return ("", "", "");
        foreach (Match m in MetaTag.Matches(html))
        {
            var key = Attr(m.Value, "property"); if (key == "") key = Attr(m.Value, "name"); if (key == "") key = Attr(m.Value, "itemprop");
            var val = Attr(m.Value, "content");
            if (key != "" && val != "" && !meta.ContainsKey(key)) meta[key] = val;
        }
        string G(params string[] ks) { foreach (var k in ks) if (meta.TryGetValue(k, out var v)) return v; return ""; }

        var title = G("og:title", "twitter:title");
        if (title == "") { var t = TitleTag.Match(html); if (t.Success) title = WebUtility.HtmlDecode(t.Groups[1].Value).Trim(); }
        var image = G("og:image", "og:image:url", "twitter:image");

        var amount = G("og:price:amount", "product:price:amount");
        var cur = G("og:price:currency", "product:price:currency");
        if (amount == "") { var p = JsonPrice.Match(html); if (p.Success) amount = p.Groups[1].Value; var c = JsonCur.Match(html); if (c.Success) cur = c.Groups[1].Value; }
        return (title, image, Price(amount, cur));
    }

    /// <summary>"149000.00" atau "149.000" jadi "IDR 149,000"; teks yang bukan angka dikembalikan apa adanya.</summary>
    public static string Price(string amount, string cur)
    {
        amount = (amount ?? "").Trim();
        if (amount == "") return "";
        var a = Regex.IsMatch(amount, @"^\d{1,3}(\.\d{3})+$") ? amount.Replace(".", "") : amount;   // titik sebagai pemisah ribuan (gaya Indonesia)
        var shown = double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n.ToString("N0", CultureInfo.InvariantCulture) : amount;
        return (cur ?? "").Trim() == "" ? shown : cur.Trim().ToUpperInvariant() + " " + shown;
    }

    static readonly Regex RupiahText = new(@"(?:Rp\.?|IDR)\s*([\d][\d.,]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Harga dari teks yang disalin dari halaman toko ("Rp149.000", "IDR 149,000", atau angka saja); kosong bila tidak ada angka harga.</summary>
    public static string PriceFromText(string text)
    {
        text = (text ?? "").Trim();
        var m = RupiahText.Match(text);
        var amount = m.Success ? m.Groups[1].Value.TrimEnd('.', ',') : Regex.IsMatch(text, @"^[\d][\d.,]*$") ? text.TrimEnd('.', ',') : "";
        return amount == "" ? "" : Price(amount, "IDR");
    }

    /// <summary>Nama toko dari domain link: toko yang dikenal diberi nama rapi, lainnya nama domainnya.</summary>
    public static string Source(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        var host = u.Host.ToLowerInvariant();
        foreach (var (key, name) in new[] { ("shopee", "Shopee"), ("tokopedia", "Tokopedia"), ("tiktok", "TikTok Shop"), ("lazada", "Lazada"), ("blibli", "Blibli"), ("kyou", "Kyou Hobby") })
            if (host.Contains(key)) return name;
        var l = host.Split('.');
        var label = l.Length >= 3 && l[^2].Length <= 3 ? l[^3] : l.Length >= 2 ? l[^2] : l[0];   // example.co.id -> example
        return label == "" ? host : char.ToUpperInvariant(label[0]) + label[1..];
    }

    /// <summary>Judul cadangan dari bagian akhir link (Shopee dan Tokopedia menaruh nama produk di sana) bila halaman tidak bisa dibaca.</summary>
    public static string SlugTitle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var seg = u.AbsolutePath.TrimEnd('/');
        seg = seg[(seg.LastIndexOf('/') + 1)..];
        seg = Regex.Replace(seg, @"-i\.\d+\.\d+$", "");   // akhiran id Shopee
        seg = WebUtility.UrlDecode(seg).Replace('-', ' ').Replace('_', ' ').Trim();
        return seg.Length >= 4 ? seg : u.Host;
    }
}
