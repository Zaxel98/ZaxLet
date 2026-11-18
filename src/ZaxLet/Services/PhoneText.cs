using System;
using System.Text;
using System.Windows;

namespace ZaxLet;

/// <summary>Kirim teks HP &lt;-&gt; laptop lewat kanal kontrol: dari HP langsung masuk clipboard PC, dari PC masuk clipboard HP.</summary>
public static class PhoneText
{
    public const int MaxBytes = 40000;   // bingkai kontrol dibatasi 64 KB; base64 menambah sepertiga

    /// <summary>Teks dari HP. Masuk clipboard PC; pill memberi tahu.</summary>
    public static void FromPhone(string text, string device)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Clipboard WPF butuh thread UI/STA; ShareServer memanggil dari threadpool.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try { Clipboard.SetText(text); }
            catch { IslandWindow.Instance?.Alert(L.T("Teks dari HP diterima, tapi clipboard sedang dipakai aplikasi lain"), 5); return; }
            var head = text.Length > 60 ? text[..60] + "…" : text;
            IslandWindow.Instance?.Alert(L.F("Teks dari {0} masuk clipboard: {1}", string.IsNullOrWhiteSpace(device) ? L.T("HP") : device, head.Replace('\n', ' ')), 5);
        });
    }

    /// <summary>Kirim teks clipboard PC ke HP. Hasil: jumlah HP yang menerima, atau -1 bila clipboard bukan teks / terlalu besar.</summary>
    public static int SendClipboard()
    {
        string t = null;
        try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { }
        if (string.IsNullOrEmpty(t) || Encoding.UTF8.GetByteCount(t) > MaxBytes) return -1;
        return ShareServer.I.Command("text " + Convert.ToBase64String(Encoding.UTF8.GetBytes(t)));
    }
}
