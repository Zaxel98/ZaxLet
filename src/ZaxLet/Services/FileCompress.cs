using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ZaxLet;

/// <summary>
/// Kompresi gambar dan PDF ke ukuran target di folder temp per-pengguna (%LOCALAPPDATA%\ZaxLet\compressed\).
/// File asli tidak pernah diubah atau dihapus.
/// </summary>
public static class FileCompress
{
    public const long MaxSourceBytes = 300L * 1024 * 1024;
    public const long MinTargetBytes = 20L * 1024;
    const int MinImageLongSide = 480;
    const int MaxDecodeLongSide = 4096;
    const double MinPdfDpi = 100.0;
    const double StartPdfDpi = 150.0;
    const int MaxPdfRenderWidth = 2400;

    static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff", ".gif"
    };

    public static string OutputDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZaxLet", "compressed");

    public static bool IsSupported(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return IsImageExt(ext) || IsPdfExt(ext);
    }

    public static bool IsImageExt(string ext) => !string.IsNullOrEmpty(ext) && ImageExts.Contains(ext);
    public static bool IsPdfExt(string ext) => string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase);

    public static string FmtSize(long bytes)
    {
        var inv = CultureInfo.CurrentCulture;
        if (bytes >= 1024L * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.#", inv) + " MB";
        return Math.Max(1, (long)Math.Round(bytes / 1024.0)).ToString(inv) + " KB";
    }

    /// <summary>Kompres gambar ke JPEG dengan ukuran <= targetBytes. Keluaran selalu JPEG.</summary>
    public static string ImageToTarget(string src, long targetBytes, CancellationToken ct)
    {
        ValidateSource(src, ref targetBytes);
        ct.ThrowIfCancellationRequested();

        var (baseBmp, origLongSide) = DecodeImageNormalized(src);
        int longSide = Math.Min(origLongSide, Math.Max(baseBmp.PixelWidth, baseBmp.PixelHeight));
        long smallest = long.MaxValue;
        byte[] bestBytes = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var scaled = ScaleToLongSide(baseBmp, longSide);
            var (hit, minBytes) = SearchJpegQuality(scaled, targetBytes, ct);
            if (minBytes < smallest) smallest = minBytes;
            if (hit != null)
            {
                bestBytes = hit;
                break;
            }
            if (longSide <= MinImageLongSide) break;
            int next = (int)Math.Round(longSide * 0.85);
            if (next >= longSide) next = longSide - 1;
            longSide = Math.Max(MinImageLongSide, next);
        }

        if (bestBytes == null)
            throw new InvalidOperationException(L.F("Target tidak tercapai. Ukuran terkecil: {0}.", FmtSize(smallest)));

        var outPath = UniqueOutputPath(src, bestBytes.Length, ".jpg");
        File.WriteAllBytes(outPath, bestBytes);
        return outPath;
    }

    /// <summary>Kompres PDF dengan merender tiap halaman ke JPEG dan menulis PDF minimal. Tidak mendukung PDF berpassword.</summary>
    public static string PdfToTarget(string src, long targetBytes, CancellationToken ct) =>
        PdfToTarget(src, targetBytes, ct, null);

    public static string PdfToTarget(string src, long targetBytes, CancellationToken ct, Action<string> onProgress)
    {
        ValidateSource(src, ref targetBytes);
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => PdfToTargetAsync(src, targetBytes, ct, onProgress), ct).GetAwaiter().GetResult();
    }

    static async Task<string> PdfToTargetAsync(string src, long targetBytes, CancellationToken ct, Action<string> onProgress)
    {
        StorageFile file;
        PdfDocument doc;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(src);
            doc = await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex) when (IsPasswordError(ex))
        {
            throw new InvalidOperationException(L.T("PDF dilindungi password dan tidak dapat dikompres."));
        }

        if (doc.IsPasswordProtected)
            throw new InvalidOperationException(L.T("PDF dilindungi password dan tidak dapat dikompres."));

        int pageCount = (int)doc.PageCount;
        if (pageCount <= 0)
            throw new InvalidOperationException(L.T("PDF tidak memiliki halaman."));

        double dpi = StartPdfDpi;
        long smallest = long.MaxValue;
        byte[] bestPdf = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pages = new List<(BitmapSource Bmp, double WidthPt, double HeightPt)>(pageCount);
            for (uint i = 0; i < (uint)pageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                onProgress?.Invoke(L.F("Halaman {0}/{1}", i + 1, pageCount));
                using var page = doc.GetPage(i);
                double wPt = page.Size.Width * 72.0 / 96.0;
                double hPt = page.Size.Height * 72.0 / 96.0;
                uint destW = (uint)Math.Clamp(Math.Round(wPt * dpi / 72.0), 100, MaxPdfRenderWidth);
                var bmp = await RenderPdfPageAsync(page, destW);
                pages.Add((bmp, wPt, hPt));
            }

            var (hit, minBytes) = SearchPdfQuality(pages, targetBytes, ct);
            if (minBytes < smallest) smallest = minBytes;
            if (hit != null)
            {
                bestPdf = hit;
                break;
            }
            if (dpi <= MinPdfDpi + 0.5) break;
            dpi = Math.Max(MinPdfDpi, dpi * 0.85);
        }

        if (bestPdf == null)
            throw new InvalidOperationException(L.F("Target tidak tercapai. Ukuran terkecil: {0}.", FmtSize(smallest)));

        var outPath = UniqueOutputPath(src, bestPdf.Length, ".pdf");
        await File.WriteAllBytesAsync(outPath, bestPdf, ct);

        // Validasi dengan membuka ulang PDF keluaran dan memastikan jumlah halaman sama
        try
        {
            var outFile = await StorageFile.GetFileFromPathAsync(outPath);
            var check = await PdfDocument.LoadFromFileAsync(outFile);
            if ((int)check.PageCount != pageCount)
            {
                try { File.Delete(outPath); } catch { }
                throw new InvalidOperationException(L.T("PDF hasil gagal divalidasi (jumlah halaman tidak cocok)."));
            }
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            try { File.Delete(outPath); } catch { }
            throw new InvalidOperationException(L.T("PDF hasil tidak dapat dibuka kembali."), ex);
        }

        return outPath;
    }

    static bool IsPasswordError(Exception ex)
    {
        // Windows.Data.Pdf throws HRESULT 0x8007052B (ERROR_WRONG_PASSWORD) atau UnauthorizedAccessException
        int hr = ex.HResult;
        return hr == unchecked((int)0x8007052B) || hr == unchecked((int)0x80070005) ||
               ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase);
    }

    static async Task<BitmapSource> RenderPdfPageAsync(PdfPage page, uint destWidth)
    {
        using var ras = new InMemoryRandomAccessStream();
        var opts = new PdfPageRenderOptions
        {
            DestinationWidth = destWidth,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
        };
        await page.RenderToStreamAsync(ras, opts);
        ras.Seek(0);
        using var netStream = ras.AsStreamForRead();
        var dec = BitmapDecoder.Create(netStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bgr = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgr32, null, 0);
        bgr.Freeze();
        return bgr;
    }

    static void ValidateSource(string src, ref long targetBytes)
    {
        if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
            throw new FileNotFoundException(L.T("Sumber bukan file yang valid atau tidak ditemukan."));
        var info = new FileInfo(src);
        if (info.Length <= 0)
            throw new InvalidOperationException(L.T("File kosong."));
        if (info.Length > MaxSourceBytes)
            throw new InvalidOperationException(L.T("Ukuran file melebihi batas 300 MB."));
        if (targetBytes < MinTargetBytes) targetBytes = MinTargetBytes;
    }

    static (BitmapSource Bmp, int OrigLongSide) DecodeImageNormalized(string src)
    {
        int origW, origH;
        ushort orientation = 1;
        using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var f = dec.Frames[0];
            origW = f.PixelWidth;
            origH = f.PixelHeight;
            if (f.Metadata is BitmapMetadata meta)
            {
                try
                {
                    var q = meta.GetQuery("/app1/ifd/{ushort=274}") ?? meta.GetQuery("/ifd/{ushort=274}");
                    if (q != null) orientation = Convert.ToUInt16(q, CultureInfo.InvariantCulture);
                }
                catch { }
            }
        }

        int origLong = Math.Max(origW, origH);
        BitmapSource raw;
        if (origLong > MaxDecodeLongSide)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(src);
            if (origW >= origH) bi.DecodePixelWidth = MaxDecodeLongSide;
            else bi.DecodePixelHeight = MaxDecodeLongSide;
            bi.EndInit();
            bi.Freeze();
            raw = bi;
        }
        else
        {
            using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            raw = dec.Frames[0];
            if (!raw.IsFrozen) raw.Freeze();
        }

        var oriented = ApplyExifOrientation(raw, orientation);
        var flat = FlattenToWhiteBgr32(oriented);
        int effectiveLong = orientation is 5 or 6 or 7 or 8 ? Math.Max(origH, origW) : origLong;
        return (flat, effectiveLong);
    }

    static BitmapSource ApplyExifOrientation(BitmapSource src, ushort orientation)
    {
        Transform t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null
        };
        if (t == null) return src;
        var tb = new TransformedBitmap(src, t);
        tb.Freeze();
        return tb;
    }

    static BitmapSource FlattenToWhiteBgr32(BitmapSource src)
    {
        if (src.Format == PixelFormats.Bgr32 || src.Format == PixelFormats.Bgr24)
        {
            if (!src.IsFrozen) src.Freeze();
            return src;
        }
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var buf = new byte[stride * h];
        bgra.CopyPixels(buf, stride, 0);
        for (int i = 0; i < buf.Length; i += 4)
        {
            byte a = buf[i + 3];
            if (a == 255) continue;
            if (a == 0)
            {
                buf[i] = 255;
                buf[i + 1] = 255;
                buf[i + 2] = 255;
                buf[i + 3] = 255;
                continue;
            }
            int inv = 255 - a;
            buf[i] = (byte)((buf[i] * a + 255 * inv + 127) / 255);
            buf[i + 1] = (byte)((buf[i + 1] * a + 255 * inv + 127) / 255);
            buf[i + 2] = (byte)((buf[i + 2] * a + 255 * inv + 127) / 255);
            buf[i + 3] = 255;
        }
        var res = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, buf, stride);
        res.Freeze();
        return res;
    }

    static BitmapSource ScaleToLongSide(BitmapSource src, int targetLong)
    {
        int curLong = Math.Max(src.PixelWidth, src.PixelHeight);
        if (targetLong >= curLong) return src;
        double scale = (double)targetLong / curLong;
        var tb = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        tb.Freeze();
        return tb;
    }

    static byte[] EncodeJpeg(BitmapSource bmp, int quality)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static (byte[] Hit, long MinBytes) SearchJpegQuality(BitmapSource bmp, long targetBytes, CancellationToken ct)
    {
        var b90 = EncodeJpeg(bmp, 90);
        if (b90.Length <= targetBytes) return (b90, b90.Length);

        var b40 = EncodeJpeg(bmp, 40);
        if (b40.Length > targetBytes) return (null, b40.Length);

        byte[] best = b40;
        int lo = 41, hi = 89;
        while (lo <= hi)
        {
            ct.ThrowIfCancellationRequested();
            int mid = lo + (hi - lo) / 2;
            var cur = EncodeJpeg(bmp, mid);
            if (cur.Length <= targetBytes)
            {
                best = cur;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return (best, best.Length);
    }

    static (byte[] Hit, long MinBytes) SearchPdfQuality(List<(BitmapSource Bmp, double WidthPt, double HeightPt)> pages, long targetBytes, CancellationToken ct)
    {
        byte[] BuildAt(int q)
        {
            var encoded = new List<(byte[] Jpeg, int PxW, int PxH, double WPt, double HPt)>(pages.Count);
            foreach (var p in pages)
            {
                ct.ThrowIfCancellationRequested();
                encoded.Add((EncodeJpeg(p.Bmp, q), p.Bmp.PixelWidth, p.Bmp.PixelHeight, p.WidthPt, p.HeightPt));
            }
            return BuildMinimalPdf(encoded);
        }

        var b90 = BuildAt(90);
        if (b90.Length <= targetBytes) return (b90, b90.Length);

        var b40 = BuildAt(40);
        if (b40.Length > targetBytes) return (null, b40.Length);

        byte[] best = b40;
        int lo = 41, hi = 89;
        while (lo <= hi)
        {
            ct.ThrowIfCancellationRequested();
            int mid = lo + (hi - lo) / 2;
            var cur = BuildAt(mid);
            if (cur.Length <= targetBytes)
            {
                best = cur;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return (best, best.Length);
    }

    static byte[] BuildMinimalPdf(List<(byte[] Jpeg, int PxW, int PxH, double WPt, double HPt)> pages)
    {
        using var ms = new MemoryStream();
        var offsets = new List<long> { 0 }; // index 0 is free object 0
        void WriteAscii(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        // Header + binary comment
        ms.Write(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', (byte)'1', (byte)'.', (byte)'4', (byte)'\n', (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        // 1: Catalog
        offsets.Add(ms.Position);
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // 2: Pages
        offsets.Add(ms.Position);
        var kids = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            if (i > 0) kids.Append(' ');
            kids.Append(3 + i * 3).Append(" 0 R");
        }
        WriteAscii($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            int pageId = 3 + i * 3;
            int imgId = pageId + 1;
            int cntId = pageId + 2;
            string wStr = p.WPt.ToString("0.###", inv);
            string hStr = p.HPt.ToString("0.###", inv);

            // Page object
            offsets.Add(ms.Position);
            WriteAscii($"{pageId} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {wStr} {hStr}] /Resources << /XObject << /Im0 {imgId} 0 R >> >> /Contents {cntId} 0 R >>\nendobj\n");

            // Image XObject
            offsets.Add(ms.Position);
            WriteAscii($"{imgId} 0 obj\n<< /Type /XObject /Subtype /Image /Width {p.PxW} /Height {p.PxH} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {p.Jpeg.Length} >>\nstream\n");
            ms.Write(p.Jpeg, 0, p.Jpeg.Length);
            WriteAscii("\nendstream\nendobj\n");

            // Content stream
            string content = $"q\n{wStr} 0 0 {hStr} 0 0 cm\n/Im0 Do\nQ\n";
            offsets.Add(ms.Position);
            WriteAscii($"{cntId} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        }

        long xrefOffset = ms.Position;
        WriteAscii($"xref\n0 {offsets.Count}\n");
        WriteAscii("0000000000 65535 f \n");
        for (int i = 1; i < offsets.Count; i++)
            WriteAscii(offsets[i].ToString("D10", inv) + " 00000 n \n");

        WriteAscii($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    static string UniqueOutputPath(string src, long outBytes, string ext)
    {
        // Folder yang sama dengan sumber: surat.pdf -> surat_compressed.pdf; bila tak bisa ditulis, pakai folder cadangan di LocalAppData.
        string orig = Path.GetFileNameWithoutExtension(src);
        if (string.IsNullOrWhiteSpace(orig)) orig = "file";
        string dir = Path.GetDirectoryName(src);
        if (string.IsNullOrEmpty(dir) || !CanWrite(dir)) { dir = OutputDir; Directory.CreateDirectory(dir); }
        string candidate = Path.Combine(dir, $"{orig}_compressed{ext}");
        int idx = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(dir, $"{orig}_compressed ({idx++}){ext}");
        return candidate;
    }

    static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".zaxlet-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
    }
}
