using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace ZaxLet;

/// <summary>Product page parser for Wishlist: no network and no WPF, so it can be tested standalone. Lenient and never throws: missing fields are returned empty.</summary>
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

    /// <summary>Title, image URL (may be relative), and display-ready price ("IDR 149,000") from a product page's HTML.</summary>
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

    /// <summary>"149000.00" or "149.000" becomes "IDR 149,000"; non-numeric text is returned as-is.</summary>
    public static string Price(string amount, string cur)
    {
        amount = (amount ?? "").Trim();
        if (amount == "") return "";
        var a = Regex.IsMatch(amount, @"^\d{1,3}(\.\d{3})+$") ? amount.Replace(".", "") : amount;   // dot as thousands separator (Indonesian style)
        var shown = double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n.ToString("N0", CultureInfo.InvariantCulture) : amount;
        return (cur ?? "").Trim() == "" ? shown : cur.Trim().ToUpperInvariant() + " " + shown;
    }

    static readonly Regex RupiahText = new(@"(?:Rp\.?|IDR)\s*([\d][\d.,]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Price from text copied from a store page ("Rp149.000", "IDR 149,000", or plain digits); empty if no price number is present.</summary>
    public static string PriceFromText(string text)
    {
        text = (text ?? "").Trim();
        var m = RupiahText.Match(text);
        var amount = m.Success ? m.Groups[1].Value.TrimEnd('.', ',') : Regex.IsMatch(text, @"^[\d][\d.,]*$") ? text.TrimEnd('.', ',') : "";
        return amount == "" ? "" : Price(amount, "IDR");
    }

    /// <summary>Store name from link domain: known stores get a clean name, others get the domain name.</summary>
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

    /// <summary>Fallback title from the end of the link (Shopee and Tokopedia put the product name there) if the page cannot be read.</summary>
    public static string SlugTitle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var seg = u.AbsolutePath.TrimEnd('/');
        seg = seg[(seg.LastIndexOf('/') + 1)..];
        seg = Regex.Replace(seg, @"-i\.\d+\.\d+$", "");   // Shopee id suffix
        seg = WebUtility.UrlDecode(seg).Replace('-', ' ').Replace('_', ' ').Trim();
        return seg.Length >= 4 ? seg : u.Host;
    }
}
