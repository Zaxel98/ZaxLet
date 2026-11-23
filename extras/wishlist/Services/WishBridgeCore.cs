using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ZaxLet;

/// <summary>
/// Pure part of the browser extension bridge for Wishlist (no network and no WPF, so it is testable): link key, request origin
/// check, and product page payload parsing. All inputs are treated as untrusted and validated here.
/// </summary>
public static class WishBridgeCore
{
    public sealed record Page(string Url, string Title, string Price, byte[] Image);

    public const int MaxBody = 2 * 1024 * 1024, MaxImage = 1536 * 1024, MaxTitle = 300;

    static readonly string[] Tracking = { "utm_", "fbclid", "gclid", "xptdk", "sp_atk", "mibextid", "ref", "ref_", "spm", "scm", "from", "share_source", "_gl" };

    static bool IsTracking(string name) => Tracking.Any(t => t.EndsWith('_') ? name.StartsWith(t, StringComparison.OrdinalIgnoreCase) : name.Equals(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Comparison key for two links to the same page: no www, no #fragment, no trailing slash, no tracking parameters.</summary>
    public static string Key(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        var host = u.Host.ToLowerInvariant(); if (host.StartsWith("www.")) host = host[4..];
        var q = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !IsTracking(p.Split('=')[0])).OrderBy(p => p, StringComparer.Ordinal);
        var qs = string.Join("&", q);
        return host + u.AbsolutePath.TrimEnd('/') + (qs == "" ? "" : "?" + qs);
    }

    /// <summary>Host and path only: fallback when parameters differ between the copied link and the tab URL.</summary>
    public static string PathKey(string url) => Key(url) is { } k ? k.Split('?')[0] : null;

    /// <summary>Browser extensions only (chrome-extension:// for Chrome, Edge, Brave; moz-extension:// for Firefox). Web pages have http(s) origin and are rejected.</summary>
    public static bool OriginAllowed(string origin) =>
        origin != null && (origin.StartsWith("chrome-extension://", StringComparison.Ordinal) || origin.StartsWith("moz-extension://", StringComparison.Ordinal));

    /// <summary>Constant-time token comparison.</summary>
    public static bool TokenOk(string given, string expected)
    {
        if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(expected) || given.Length != expected.Length) return false;
        int d = 0;
        for (int i = 0; i < given.Length; i++) d |= given[i] ^ expected[i];
        return d == 0;
    }

    /// <summary>Strip short "... | Store Name" title suffix: where stores usually put their name.</summary>
    public static string CleanTitle(string t)
    {
        t = (t ?? "").Trim();
        if (t.Length > 10 && t.StartsWith("Jual ", StringComparison.OrdinalIgnoreCase)) t = t[5..];   // Indonesian marketplace tab titles start with "Jual "
        int i = t.LastIndexOf(" | ", StringComparison.Ordinal);
        if (i > 8 && t.Length - i - 3 <= 32) t = t[..i].Trim();
        return t.Length > MaxTitle ? t[..MaxTitle] : t;
    }

    /// <summary>Local webhook {"text":"...","seconds":6}: text 1-200 chars (single line), display duration 2-30s (default 6). Null if invalid.</summary>
    public static (string Text, double Seconds)? ParseNotify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var text = r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString().Replace('\r', ' ').Replace('\n', ' ').Trim() : "";
            if (text.Length == 0) return null;
            if (text.Length > 200) text = text[..200];
            double s = r.TryGetProperty("seconds", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetDouble() : 6;
            return (text, Math.Clamp(s, 2, 30));
        }
        catch { return null; }
    }

    static bool ImageMagic(byte[] b) =>
        b.Length > 12 && (b[0] == 0xFF && b[1] == 0xD8                                      // JPEG
                          || b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47   // PNG
                          || b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46); // WEBP (RIFF)

    public static string ImageExt(byte[] b) => b.Length > 3 && b[0] == 0x89 ? ".png" : b.Length > 3 && b[0] == 0x52 ? ".webp" : ".jpg";

    /// <summary>
    /// Extension payload: {"url","title","price","priceText","currency","image":"data:image/jpeg;base64,..."}. Null if invalid.
    /// Price uses the structured number if present, otherwise the page price text (WishParse.PriceFromText).
    /// </summary>
    public static Page ParsePage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString().Trim() : "";
            var url = S("url");
            if (Key(url) == null || url.Length > 2000) return null;
            var title = CleanTitle(S("title"));
            if (title == "") return null;
            var price = WishParse.Price(S("price"), S("currency"));
            if (price == "") price = WishParse.PriceFromText(S("priceText"));
            byte[] img = null;
            var im = S("image");
            int c = im.IndexOf("base64,", StringComparison.Ordinal);
            if (im.StartsWith("data:image/", StringComparison.Ordinal) && c > 0 && im.Length - c < MaxImage * 4 / 3 + 16)
            {
                try { var b = Convert.FromBase64String(im[(c + 7)..]); if (b.Length <= MaxImage && ImageMagic(b)) img = b; } catch { }
            }
            return new Page(url, title, price, img);
        }
        catch { return null; }
    }
}
