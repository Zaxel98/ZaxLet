using System;
using System.Diagnostics;
using System.Text;
using System.Windows;

namespace ZaxLet;

/// <summary>Send text phone &lt;-&gt; laptop over the control channel: from phone goes straight to PC clipboard, from PC goes to phone clipboard.</summary>
public static class PhoneText
{
    public const int MaxBytes = 40000;   // control frame is capped at 64 KB; base64 adds one third
    public const int MaxUrlLength = 2000;

    /// <summary>Returns an absolute http/https Uri if text is a single valid URL with no internal whitespace, or null.</summary>
    public static Uri TryParseSingleHttpUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.Length == 0 || s.Length > MaxUrlLength) return null;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) return null;
        }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        return uri;
    }

    /// <summary>Text from phone. Goes to PC clipboard; pill notifies.</summary>
    public static void FromPhone(string text, string device)
    {
        if (string.IsNullOrEmpty(text)) return;
        // WPF Clipboard requires UI/STA thread; ShareServer calls from threadpool.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try { Clipboard.SetText(text); }
            catch { IslandWindow.Instance?.Alert(L.T("Teks dari HP diterima, tapi clipboard sedang dipakai aplikasi lain"), 5); return; }
            var url = TryParseSingleHttpUrl(text);
            if (url != null)
            {
                IslandWindow.Instance?.Alert(L.T("Tautan dari HP · klik untuk buka"), 5,
                    () => { try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); } catch { } });
                return;
            }
            var head = text.Length > 60 ? text[..60] + "…" : text;
            IslandWindow.Instance?.Alert(L.F("Teks dari {0} masuk clipboard: {1}", string.IsNullOrWhiteSpace(device) ? L.T("HP") : device, head.Replace('\n', ' ')), 5);
        });
    }

    /// <summary>Send PC clipboard text to phone. Returns number of phones that received it, or -1 if clipboard is not text / too large.</summary>
    public static int SendClipboard()
    {
        string t = null;
        try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { }
        if (string.IsNullOrEmpty(t) || Encoding.UTF8.GetByteCount(t) > MaxBytes) return -1;
        return ShareServer.I.Command("text " + Convert.ToBase64String(Encoding.UTF8.GetBytes(t)));
    }
}
