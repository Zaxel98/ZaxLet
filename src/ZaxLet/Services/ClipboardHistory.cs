using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZaxLet;

/// <summary>
/// Riwayat teks dan gambar yang disalin (hanya di memori, hilang saat ZaxLet ditutup). Salinan dari pengelola kata sandi
/// (penanda "Clipboard Viewer Ignore" / "ExcludeClipboardContentFromMonitorProcessing") tidak dicatat.
/// </summary>
public static class ClipboardHistory
{
    public sealed class Item
    {
        public string Text;
        public byte[] ImagePng;
        public string ImageHash;
        public BitmapSource Thumb;
        public int Width, Height;
        public DateTime At;
        public bool Pinned;
        public bool IsSecret;
        public bool IsImage => ImagePng != null;
    }

    public const int Max = 40, MaxChars = 20000, MaxImages = 6, ThumbWidth = 120;
    public const long MaxImagePixels = 8_000_000, MaxImageBytes = 8 * 1024 * 1024, MaxTotalImageBytes = 24 * 1024 * 1024;
    public static readonly List<Item> Items = new();
    public static event Action Changed;
    static string _own, _ownImageHash;   // salinan yang baru kita tulis sendiri: jangan dicatat lagi sebagai salinan baru

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
                if (d.GetDataPresent(DataFormats.UnicodeText))
                {
                    Add(d.GetData(DataFormats.UnicodeText) as string);
                    return;
                }
                TryCaptureImage(d);
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
        InsertText(text);
    }

    static void InsertText(string text)
    {
        if (text.Length > MaxChars) text = text[..MaxChars];
        var old = Items.FirstOrDefault(x => !x.IsImage && x.Text == text);
        bool pinned = old?.Pinned ?? false;
        if (old != null) Items.Remove(old);
        Items.Insert(0, new Item { Text = text, At = DateTime.Now, Pinned = pinned });
        while (Items.Count(x => !x.IsImage) > Max) { var drop = Items.FindLastIndex(x => !x.IsImage && !x.Pinned); if (drop < 0) break; Items.RemoveAt(drop); }
        Changed?.Invoke();
    }

    static void TryCaptureImage(IDataObject d)
    {
        byte[] pngBytes;
        BitmapSource frame;
        if (d.GetDataPresent("PNG") && d.GetData("PNG") is Stream pngStream)
        {
            if (pngStream.CanSeek && pngStream.Length > MaxImageBytes) return;
            pngBytes = ReadBounded(pngStream, MaxImageBytes);
            if (pngBytes == null || pngBytes.Length == 0) return;
            using var ms = new MemoryStream(pngBytes, writable: false);
            frame = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > MaxImagePixels) return;
        }
        else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } dib)
        {
            if (dib.PixelWidth <= 0 || dib.PixelHeight <= 0 || (long)dib.PixelWidth * dib.PixelHeight > MaxImagePixels) return;
            var bgr = new FormatConvertedBitmap(dib, PixelFormats.Bgr32, null, 0);   // buang alfa DIB yang sering 0
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bgr));
            using var ms = new MemoryStream();
            enc.Save(ms);
            if (ms.Length == 0 || ms.Length > MaxImageBytes) return;
            pngBytes = ms.ToArray();
            frame = bgr;
        }
        else return;

        AddImage(pngBytes, frame);
    }

    static void AddImage(byte[] pngBytes, BitmapSource frame)
    {
        var hash = Convert.ToHexString(SHA256.HashData(pngBytes));
        if (hash == _ownImageHash) { _ownImageHash = null; return; }
        double scale = Math.Min(1.0, (double)ThumbWidth / Math.Max(1, frame.PixelWidth));
        BitmapSource thumb = scale < 1.0 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
        if (!thumb.IsFrozen) thumb.Freeze();
        var old = Items.FirstOrDefault(x => x.IsImage && x.ImageHash == hash);
        bool pinned = old?.Pinned ?? false;
        if (old != null) Items.Remove(old);
        Items.Insert(0, new Item { ImagePng = pngBytes, ImageHash = hash, Thumb = thumb, Width = frame.PixelWidth, Height = frame.PixelHeight, At = DateTime.Now, Pinned = pinned });
        while (Items.Count(x => x.IsImage) > MaxImages || TotalImageBytes() > MaxTotalImageBytes)
        {
            var drop = Items.FindLastIndex(x => x.IsImage && !x.Pinned);
            if (drop < 0) break;
            Items.RemoveAt(drop);
        }
        if (TotalImageBytes() > MaxTotalImageBytes)
        {
            Items.RemoveAt(0);
            return;
        }
        Changed?.Invoke();
    }

    static long TotalImageBytes()
    {
        long total = 0;
        foreach (var it in Items) if (it.IsImage) total += it.ImagePng.Length;
        return total;
    }

    static byte[] ReadBounded(Stream s, long max)
    {
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int read;
        while ((read = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + read > max) return null;
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    public static void CopyBack(Item it)
    {
        if (it.IsImage)
        {
            try
            {
                using var ms = new MemoryStream(it.ImagePng, writable: false);
                var bmp = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                bmp.Freeze();
                var d = new DataObject();
                d.SetImage(bmp);
                d.SetData("PNG", new MemoryStream(it.ImagePng, writable: false));
                _ownImageHash = it.ImageHash;
                Clipboard.SetDataObject(d, true);
            }
            catch { _ownImageHash = null; }
            return;
        }
        try { _own = it.Text; Clipboard.SetText(it.Text); }
        catch { _own = null; }
    }

    /// <summary>Salin teks hasil ubahan sebagai entri teratas baru tanpa menghapus item asli; abaikan item gambar atau rahasia.</summary>
    public static bool CopyTransformed(Item source, string text)
    {
        if (source == null || source.IsImage || source.IsSecret || string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            _own = text;
            Clipboard.SetText(text);
            InsertText(text);
            return true;
        }
        catch
        {
            _own = null;
            return false;
        }
    }

    /// <summary>Taruh kode di clipboard dengan penanda pengecualian: tidak masuk riwayat ZaxLet maupun riwayat clipboard Windows (Win+V) dan clipboard cloud.</summary>
    public static bool CopySecret(string code)
    {
        try
        {
            var d = new DataObject();
            d.SetText(code);
            d.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(1)));
            d.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            d.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(d, true);
            return true;
        }
        catch { return false; }   // clipboard sedang dikunci aplikasi lain
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
