using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace ZaxLet;

/// <summary>
/// Riwayat teks yang disalin (hanya di memori, hilang saat ZaxLet ditutup). Salinan dari pengelola kata sandi (penanda
/// "Clipboard Viewer Ignore" / "ExcludeClipboardContentFromMonitorProcessing") tidak dicatat.
/// </summary>
public static class ClipboardHistory
{
    public sealed class Item { public string Text; public DateTime At; public bool Pinned; }

    public const int Max = 40, MaxChars = 20000;
    public static readonly List<Item> Items = new();
    public static event Action Changed;
    static string _own;   // teks yang baru kita tulis sendiri: jangan dicatat lagi sebagai salinan baru

    /// <summary>Dipanggil saat clipboard berubah (WM_CLIPBOARDUPDATE), di thread UI.</summary>
    public static void Capture()
    {
        if (!Settings.Cur.ClipboardOn) return;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                var d = Clipboard.GetDataObject();
                if (d == null || d.GetDataPresent("Clipboard Viewer Ignore") || d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
                if (!d.GetDataPresent(DataFormats.UnicodeText)) return;
                Add(d.GetData(DataFormats.UnicodeText) as string);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40); }   // clipboard sedang dikunci aplikasi lain
            catch { return; }
        }
    }

    public static void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text == _own) { _own = null; return; }
        if (text.Length > MaxChars) text = text[..MaxChars];
        var old = Items.FirstOrDefault(x => x.Text == text);
        bool pinned = old?.Pinned ?? false;
        if (old != null) Items.Remove(old);
        Items.Insert(0, new Item { Text = text, At = DateTime.Now, Pinned = pinned });
        while (Items.Count > Max) { var drop = Items.FindLastIndex(x => !x.Pinned); if (drop < 0) break; Items.RemoveAt(drop); }
        Changed?.Invoke();
    }

    public static void CopyBack(Item it)
    {
        try { _own = it.Text; Clipboard.SetText(it.Text); }
        catch { _own = null; }
    }

    public static void Remove(Item it) { Items.Remove(it); Changed?.Invoke(); }
    public static void Clear() { Items.RemoveAll(x => !x.Pinned); Changed?.Invoke(); }
    public static void TogglePin(Item it) { it.Pinned = !it.Pinned; Changed?.Invoke(); }

    /// <summary>Satu baris ringkas untuk daftar: spasi/baris baru dipadatkan, dipotong dengan "…".</summary>
    public static string OneLine(string t, int max = 90)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(t.Trim(), @"\s+", " ");
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

/// <summary>Suara adzan pilihan pengguna: diputar saat masuk waktu sholat, dihentikan dengan klik pada pill.</summary>
public static class AdhanPlayer
{
    static System.Windows.Media.MediaPlayer _p;
    public static bool Playing { get; private set; }

    public static bool Play(string file)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file)) return false;
            Stop();
            _p = new System.Windows.Media.MediaPlayer();
            _p.MediaEnded += (_, _) => Stop();
            _p.MediaFailed += (_, _) => Stop();
            _p.Open(new Uri(file));
            _p.Play();
            Playing = true;
            return true;
        }
        catch (Exception ex) { App.Log(ex); Playing = false; return false; }
    }

    public static void Stop()
    {
        try { _p?.Stop(); _p?.Close(); } catch { }
        _p = null;
        Playing = false;
    }
}
