using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;
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
    public static string ImageToTarget(string src, long targetBytes, CancellationToken ct) =>
        ImageToTarget(src, targetBytes, ct, keepResolution: true, out _);

    public static string ImageToTarget(string src, long targetBytes, CancellationToken ct, bool keepResolution) =>
        ImageToTarget(src, targetBytes, ct, keepResolution, out _);

    public static string ImageToTarget(string src, long targetBytes, CancellationToken ct, bool keepResolution, out string methodNote)
    {
        ValidateSource(src, ref targetBytes);
        ct.ThrowIfCancellationRequested();

        long srcBytes = new FileInfo(src).Length;
        long searchTarget = srcBytes > 1 ? Math.Min(targetBytes, srcBytes - 1) : targetBytes;

        var (baseBmp, origLongSide) = DecodeImageNormalized(src, allowDecodeCap: !keepResolution);
        long smallest = long.MaxValue;
        byte[] bestBytes = null;
        methodNote = null;

        if (keepResolution)
        {
            var sw = Stopwatch.StartNew();
            byte[] origRaw = CopyToBgr32(baseBmp, out int origW, out int origH, out int origStride);
            byte[] origSmall = DownscaleBoxBgr32(origRaw, origW, origH, origStride, SsimMaxLongSide, out int smallW, out int smallH, out int smallStride);

            double bestScore = double.NegativeInfinity;
            double bestSsim = 0.0;
            int bestQuality = 0;
            int bestBlur = 0;
            bool bestChroma = false;

            void RunPass(int minQ, int maxQ, bool allowSkipBlur3)
            {
                byte[] srcBuf = origRaw ?? CopyToBgr32(baseBmp, out _, out _, out _);
                byte[] curBuf = null;

                for (int blur = 0; blur <= 3; blur++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (blur == 3 && allowSkipBlur3 && bestBytes != null && sw.ElapsedMilliseconds > 5500)
                        break;

                    byte[] levelBuf;
                    if (blur == 0)
                    {
                        levelBuf = srcBuf;
                    }
                    else
                    {
                        byte[] prev = blur == 1 ? srcBuf : curBuf;
                        curBuf = Blur3x3Weighted(prev, origW, origH, origStride);
                        if (blur == 1 && !ReferenceEquals(srcBuf, origSmall))
                            origRaw = null;
                        levelBuf = curBuf;
                    }

                    for (int c = 0; c < 2; c++)
                    {
                        ct.ThrowIfCancellationRequested();
                        bool useChroma = c == 1;
                        BitmapSource candidate;
                        if (useChroma)
                        {
                            var chromaBuf = ReduceChroma2x2YCbCr(levelBuf, origW, origH, origStride);
                            candidate = CreateBgr32(chromaBuf, origW, origH, origStride);
                        }
                        else
                        {
                            candidate = blur == 0 ? baseBmp : CreateBgr32(levelBuf, origW, origH, origStride);
                        }

                        var (hit, q, minBytes) = SearchJpegQuality(candidate, searchTarget, ct, minQ, maxQ);
                        if (minBytes < smallest) smallest = minBytes;
                        if (hit != null)
                        {
                            var (score, ssim) = EvaluateJpegCandidate(hit, origSmall, smallW, smallH, smallStride);
                            if (bestBytes == null || score > bestScore || (score == bestScore && q > bestQuality))
                            {
                                bestBytes = hit;
                                bestScore = score;
                                bestSsim = ssim;
                                bestQuality = q;
                                bestBlur = blur;
                                bestChroma = useChroma;
                            }
                        }
                    }
                }
            }

            RunPass(30, 95, allowSkipBlur3: true);
            if (bestBytes == null)
                RunPass(18, 29, allowSkipBlur3: false);

            if (bestBytes != null)
            {
                string blurPart = bestBlur switch
                {
                    0 => "",
                    1 => ", haluskan 1x",
                    2 => ", haluskan 2x",
                    _ => ", haluskan 3x"
                };
                string chromaPart = bestChroma ? " + chroma" : "";
                int simPct = Math.Clamp((int)Math.Round(100.0 * bestSsim), 0, 100);
                methodNote = $"kualitas {bestQuality}{blurPart}{chromaPart} · {L.F("kemiripan {0}%", simPct)}";
                if (bestSsim < 0.80)
                    methodNote += "\n" + L.T("Hasil cukup buram; pertimbangkan menaikkan target atau mengizinkan resolusi turun");
            }
        }
        else
        {
            int initialLongSide = Math.Min(origLongSide, Math.Max(baseBmp.PixelWidth, baseBmp.PixelHeight));
            int longSide = initialLongSide;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var scaled = ScaleToLongSide(baseBmp, longSide);
                var (hit, q, minBytes) = SearchJpegQuality(scaled, searchTarget, ct);
                if (minBytes < smallest) smallest = minBytes;
                if (hit != null)
                {
                    bestBytes = hit;
                    methodNote = longSide < initialLongSide
                        ? $"resolusi diturunkan ke {longSide} px"
                        : $"kualitas {q}";
                    break;
                }
                if (longSide <= MinImageLongSide) break;
                int next = (int)Math.Round(longSide * 0.9);
                if (next >= longSide) next = longSide - 1;
                longSide = Math.Max(MinImageLongSide, next);
            }
        }

        if (bestBytes == null || bestBytes.Length >= srcBytes)
        {
            if (smallest >= srcBytes)
                throw new InvalidOperationException(L.F("File sudah paling kecil ({0}); hasil kompresi tidak lebih kecil dari file asli.", FmtSize(srcBytes)));
            if (keepResolution)
                throw new InvalidOperationException(L.F("Target tidak tercapai pada resolusi penuh. Ukuran terkecil: {0}. Hapus centang \"Pertahankan resolusi\" untuk mengecilkan ukuran piksel.", FmtSize(smallest)));
            throw new InvalidOperationException(L.F("Target tidak tercapai. Ukuran terkecil: {0}.", FmtSize(smallest)));
        }

        var outPath = UniqueOutputPath(src, bestBytes.Length, ".jpg");
        File.WriteAllBytes(outPath, bestBytes);
        return outPath;
    }

    static readonly (int Quality, int MaxLongSide)[] PdfImageLadder =
    {
        (90, 0),
        (85, 3000),
        (80, 2400),
        (75, 2000),
        (70, 1800),
        (65, 1600),
        (60, 1400),
        (55, 1200),
        (50, 1000),
        (45, 900),
        (40, 800),
    };

    /// <summary>Kompres PDF dengan mengompres ulang gambar di dalam PDF (teks &amp; vektor tetap tajam); fallback ke raster halaman bila target tidak tercapai.</summary>
    public static string PdfToTarget(string src, long targetBytes, CancellationToken ct) =>
        PdfToTarget(src, targetBytes, ct, null, out _);

    public static string PdfToTarget(string src, long targetBytes, CancellationToken ct, Action<string> onProgress) =>
        PdfToTarget(src, targetBytes, ct, onProgress, out _);

    public static string PdfToTarget(string src, long targetBytes, CancellationToken ct, Action<string> onProgress, out bool usedRasterFallback)
    {
        ValidateSource(src, ref targetBytes);
        ct.ThrowIfCancellationRequested();

        long smallestImagePdf = long.MaxValue;
        int pageCount = 0;
        byte[] recompressed = null;
        try
        {
            recompressed = SearchPdfImageRecompression(src, targetBytes, ct, onProgress, out pageCount, out smallestImagePdf);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch
        {
            // Bila PDFsharp gagal mem-parsing struktur PDF (bukan password), lanjutkan ke fallback raster
        }

        if (recompressed != null)
        {
            usedRasterFallback = false;
            return Task.Run(() => WriteAndValidatePdfAsync(src, recompressed, pageCount, ct), ct).GetAwaiter().GetResult();
        }

        usedRasterFallback = true;
        return PdfRasterToTarget(src, targetBytes, ct, onProgress, smallestImagePdf);
    }

    static byte[] SearchPdfImageRecompression(
        string src, long targetBytes, CancellationToken ct, Action<string> onProgress,
        out int pageCount, out long smallestBytes)
    {
        smallestBytes = long.MaxValue;
        using (var probe = OpenPdfForModify(src))
        {
            pageCount = probe.PageCount;
            if (pageCount <= 0)
                throw new InvalidOperationException(L.T("PDF tidak memiliki halaman."));
        }

        int maxProbes = (int)Math.Ceiling(Math.Log2(PdfImageLadder.Length + 1));
        int probeIdx = 0;
        int lo = 0, hi = PdfImageLadder.Length - 1;
        byte[] best = null;

        while (lo <= hi)
        {
            ct.ThrowIfCancellationRequested();
            int mid = lo + (hi - lo) / 2;
            probeIdx++;
            onProgress?.Invoke(L.F("Mencoba kualitas {0}/{1}", probeIdx, maxProbes));

            var (quality, maxLongSide) = PdfImageLadder[mid];
            byte[] cur = RecompressPdfImagesOnce(src, quality, maxLongSide, ct);
            if (cur.Length < smallestBytes) smallestBytes = cur.Length;

            if (cur.Length <= targetBytes)
            {
                best = cur;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return best;
    }

    static PdfSharp.Pdf.PdfDocument OpenPdfForModify(string src)
    {
        PdfSharp.Pdf.PdfDocument doc;
        try
        {
            doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex) when (
            ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("protected", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(L.T("PDF dilindungi password dan tidak dapat dikompres."), ex);
        }

        if (doc.SecuritySettings.IsEncrypted)
        {
            doc.Dispose();
            throw new InvalidOperationException(L.T("PDF dilindungi password dan tidak dapat dikompres."));
        }
        return doc;
    }

    static byte[] RecompressPdfImagesOnce(string src, int quality, int maxLongSide, CancellationToken ct)
    {
        using var doc = OpenPdfForModify(src);
        doc.Options.CompressContentStreams = true;
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;

        var allObjs = doc.Internals.GetAllObjects();
        var maskDicts = new HashSet<PdfDictionary>();
        var images = new List<PdfDictionary>();

        foreach (var obj in allObjs)
        {
            if (obj is not PdfDictionary dict || dict.Stream == null) continue;
            if (!string.Equals(dict.Elements.GetName("/Subtype"), "/Image", StringComparison.Ordinal)) continue;
            images.Add(dict);

            if (dict.Elements.GetDictionary("/SMask") is PdfDictionary smask)
                maskDicts.Add(smask);
            if (dict.Elements.GetDictionary("/Mask") is PdfDictionary mask)
                maskDicts.Add(mask);
        }

        foreach (var dict in images)
        {
            ct.ThrowIfCancellationRequested();
            if (maskDicts.Contains(dict)) continue;
            TryRecompressImageDict(dict, quality, maxLongSide);
        }

        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    static void TryRecompressImageDict(PdfDictionary dict, int quality, int maxLongSide)
    {
        if (dict.Elements.GetBoolean("/ImageMask")) return;
        if (dict.Elements.GetInteger("/BitsPerComponent") != 8) return;

        int width = dict.Elements.GetInteger("/Width");
        int height = dict.Elements.GetInteger("/Height");
        if (width < 100 || height < 100) return;

        byte[] origStream = dict.Stream.Value;
        if (origStream == null || origStream.Length < 20 * 1024) return;

        // Lewati jika menggunakan /Mask array (color-key masking tidak cocok dengan JPEG lossy) atau /Decode terbalik
        if (dict.Elements.GetObject("/Mask") is PdfArray) return;
        if (HasInvertedDecode(dict)) return;

        string filter = GetSingleFilterName(dict);
        if (filter != "/DCTDecode" && filter != "/FlateDecode") return;

        if (!TryGetColorChannels(dict, out int channels)) return;
        bool isGray = channels == 1;

        BitmapSource decoded = null;
        try
        {
            decoded = filter == "/DCTDecode"
                ? DecodeDctImage(origStream, isGray)
                : DecodeFlateImage(dict, origStream, width, height, channels);
        }
        catch
        {
            decoded = null;
        }
        if (decoded == null) return;

        BitmapSource scaled = maxLongSide > 0 ? ScaleToLongSide(decoded, maxLongSide) : decoded;
        if (isGray && scaled.Format != PixelFormats.Gray8)
        {
            var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            scaled = gray;
        }
        else if (!isGray && scaled.Format == PixelFormats.Cmyk32)
        {
            return;
        }

        byte[] newJpeg = EncodeJpeg(scaled, quality);
        if (newJpeg.Length >= origStream.Length) return;

        dict.Stream.Value = newJpeg;
        dict.Elements.SetName("/Filter", "/DCTDecode");
        dict.Elements.SetInteger("/Width", scaled.PixelWidth);
        dict.Elements.SetInteger("/Height", scaled.PixelHeight);
        dict.Elements.SetName("/ColorSpace", isGray ? "/DeviceGray" : "/DeviceRGB");
        dict.Elements.SetInteger("/BitsPerComponent", 8);
        dict.Elements.SetInteger("/Length", newJpeg.Length);
        dict.Elements.Remove("/DecodeParms");
    }

    static bool HasInvertedDecode(PdfDictionary dict)
    {
        if (dict.Elements.GetObject("/Decode") is not PdfArray arr) return false;
        for (int i = 0; i + 1 < arr.Elements.Count; i += 2)
        {
            double dMin = arr.Elements.GetReal(i);
            double dMax = arr.Elements.GetReal(i + 1);
            if (Math.Abs(dMin) > 1e-6 || Math.Abs(dMax - 1.0) > 1e-6)
                return true;
        }
        return false;
    }

    static string GetSingleFilterName(PdfDictionary dict)
    {
        var item = dict.Elements.GetValue("/Filter");
        if (item is PdfName name) return name.Value;
        if (item is PdfArray arr && arr.Elements.Count == 1 && arr.Elements[0] is PdfName first)
            return first.Value;
        return null;
    }

    static bool TryGetColorChannels(PdfDictionary dict, out int channels)
    {
        channels = 0;
        var cs = dict.Elements.GetValue("/ColorSpace");
        if (cs is PdfName name)
        {
            if (name.Value == "/DeviceRGB") { channels = 3; return true; }
            if (name.Value == "/DeviceGray") { channels = 1; return true; }
            return false;
        }
        if (cs is PdfArray arr && arr.Elements.Count >= 2 && arr.Elements[0] is PdfName csType && csType.Value == "/ICCBased")
        {
            if (arr.Elements.GetObject(1) is PdfDictionary icc)
            {
                int n = icc.Elements.GetInteger("/N");
                if (n == 1 || n == 3) { channels = n; return true; }
            }
        }
        return false;
    }

    static BitmapSource DecodeDctImage(byte[] jpegBytes, bool isGray)
    {
        using var ms = new MemoryStream(jpegBytes, writable: false);
        var dec = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (dec.Frames.Count == 0) return null;
        BitmapSource frame = dec.Frames[0];
        if (frame.Format == PixelFormats.Cmyk32) return null;

        var targetFmt = isGray ? PixelFormats.Gray8 : PixelFormats.Bgr24;
        if (frame.Format != targetFmt && (isGray || (frame.Format != PixelFormats.Bgr32 && frame.Format != PixelFormats.Rgb24)))
        {
            var conv = new FormatConvertedBitmap(frame, targetFmt, null, 0);
            conv.Freeze();
            return conv;
        }
        if (!frame.IsFrozen) frame.Freeze();
        return frame;
    }

    static BitmapSource DecodeFlateImage(PdfDictionary dict, byte[] compressed, int width, int height, int channels)
    {
        byte[] raw;
        using (var inMs = new MemoryStream(compressed, writable: false))
        using (var zlib = new ZLibStream(inMs, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            zlib.CopyTo(outMs);
            raw = outMs.ToArray();
        }

        int predictor = 1;
        var dpObj = dict.Elements.GetObject("/DecodeParms");
        PdfDictionary dp = dpObj as PdfDictionary ?? (dpObj is PdfArray dpArr && dpArr.Elements.Count > 0 ? dpArr.Elements.GetObject(0) as PdfDictionary : null);
        if (dp != null)
        {
            int p = dp.Elements.GetInteger("/Predictor");
            if (p > 0) predictor = p;
            int cols = dp.Elements.GetInteger("/Columns");
            if (cols > 0 && cols != width) return null;
            int dpColors = dp.Elements.GetInteger("/Colors");
            if (dpColors > 0 && dpColors != channels) return null;
        }

        int stride = checked(width * channels);
        byte[] pixels;
        if (predictor == 1)
        {
            if (raw.Length < height * stride) return null;
            pixels = raw;
        }
        else if (predictor >= 10)
        {
            int rowLen = stride + 1;
            if (raw.Length < height * rowLen) return null;
            pixels = new byte[height * stride];
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * rowLen;
                byte filter = raw[srcRow];
                int dstRow = y * stride;
                int prevRow = (y - 1) * stride;
                for (int x = 0; x < stride; x++)
                {
                    byte val = raw[srcRow + 1 + x];
                    byte left = x >= channels ? pixels[dstRow + x - channels] : (byte)0;
                    byte up = y > 0 ? pixels[prevRow + x] : (byte)0;
                    byte upLeft = (y > 0 && x >= channels) ? pixels[prevRow + x - channels] : (byte)0;
                    pixels[dstRow + x] = filter switch
                    {
                        0 => val,
                        1 => (byte)(val + left),
                        2 => (byte)(val + up),
                        3 => (byte)(val + ((left + up) >> 1)),
                        4 => (byte)(val + PaethPredictor(left, up, upLeft)),
                        _ => throw new InvalidDataException()
                    };
                }
            }
        }
        else
        {
            return null;
        }

        var fmt = channels == 1 ? PixelFormats.Gray8 : PixelFormats.Rgb24;
        var bmp = BitmapSource.Create(width, height, 96, 96, fmt, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    static byte PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return (byte)a;
        if (pb <= pc) return (byte)b;
        return (byte)c;
    }

    public static string PdfRasterToTarget(string src, long targetBytes, CancellationToken ct, Action<string> onProgress, long minKnownBytes = long.MaxValue)
    {
        ValidateSource(src, ref targetBytes);
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => PdfRasterToTargetAsync(src, targetBytes, ct, onProgress, minKnownBytes), ct).GetAwaiter().GetResult();
    }

    static async Task<string> PdfRasterToTargetAsync(string src, long targetBytes, CancellationToken ct, Action<string> onProgress, long minKnownBytes = long.MaxValue)
    {
        StorageFile file;
        Windows.Data.Pdf.PdfDocument doc;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(src);
            doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
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
        long smallest = minKnownBytes;
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

        return await WriteAndValidatePdfAsync(src, bestPdf, pageCount, ct);
    }

    static async Task<string> WriteAndValidatePdfAsync(string src, byte[] pdfBytes, int expectedPageCount, CancellationToken ct)
    {
        var outPath = UniqueOutputPath(src, pdfBytes.Length, ".pdf");
        await File.WriteAllBytesAsync(outPath, pdfBytes, ct);

        // Validasi dengan membuka ulang PDF keluaran dan memastikan jumlah halaman sama
        try
        {
            var outFile = await StorageFile.GetFileFromPathAsync(outPath);
            var check = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(outFile);
            if ((int)check.PageCount != expectedPageCount)
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

    static async Task<BitmapSource> RenderPdfPageAsync(Windows.Data.Pdf.PdfPage page, uint destWidth)
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

    static (BitmapSource Bmp, int OrigLongSide) DecodeImageNormalized(string src, bool allowDecodeCap = true)
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
        if (allowDecodeCap && origLong > MaxDecodeLongSide)
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

    static byte[] CopyToBgr32(BitmapSource bmp, out int width, out int height, out int stride)
    {
        BitmapSource bgr32 = bmp.Format == PixelFormats.Bgr32
            ? bmp
            : new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0);
        width = bgr32.PixelWidth;
        height = bgr32.PixelHeight;
        stride = width * 4;
        var buf = new byte[stride * height];
        bgr32.CopyPixels(buf, stride, 0);
        return buf;
    }

    static BitmapSource CreateBgr32(byte[] buf, int width, int height, int stride)
    {
        var res = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, buf, stride);
        res.Freeze();
        return res;
    }

    static byte[] Blur3x3Weighted(byte[] src, int width, int height, int stride)
    {
        var dst = new byte[src.Length];
        if (width <= 0 || height <= 0) return dst;

        for (int y = 0; y < height; y++)
        {
            int ym = y > 0 ? y - 1 : 0;
            int yp = y + 1 < height ? y + 1 : height - 1;
            int rowM = ym * stride;
            int row0 = y * stride;
            int rowP = yp * stride;

            for (int x = 0; x < width; x++)
            {
                int xm = (x > 0 ? x - 1 : 0) * 4;
                int x0 = x * 4;
                int xp = (x + 1 < width ? x + 1 : width - 1) * 4;

                for (int c = 0; c < 3; c++)
                {
                    int sum =
                        src[rowM + xm + c] + 2 * src[rowM + x0 + c] + src[rowM + xp + c] +
                        2 * src[row0 + xm + c] + 4 * src[row0 + x0 + c] + 2 * src[row0 + xp + c] +
                        src[rowP + xm + c] + 2 * src[rowP + x0 + c] + src[rowP + xp + c];
                    dst[row0 + x0 + c] = (byte)((sum + 8) >> 4);
                }
                dst[row0 + x0 + 3] = 255;
            }
        }
        return dst;
    }

    static byte[] ReduceChroma2x2YCbCr(byte[] src, int width, int height, int stride)
    {
        var dst = new byte[src.Length];
        Span<double> ys = stackalloc double[4];
        for (int y = 0; y < height; y += 2)
        {
            int yEnd = Math.Min(y + 2, height);
            for (int x = 0; x < width; x += 2)
            {
                int xEnd = Math.Min(x + 2, width);
                double sumCb = 0, sumCr = 0;
                int count = 0;

                for (int py = y; py < yEnd; py++)
                {
                    int row = py * stride;
                    for (int px = x; px < xEnd; px++)
                    {
                        int idx = row + px * 4;
                        byte b = src[idx];
                        byte g = src[idx + 1];
                        byte r = src[idx + 2];
                        ys[count++] = 0.299 * r + 0.587 * g + 0.114 * b;
                        sumCb += -0.168736 * r - 0.331264 * g + 0.5 * b + 128.0;
                        sumCr += 0.5 * r - 0.418688 * g - 0.081312 * b + 128.0;
                    }
                }

                double cb = sumCb / count - 128.0;
                double cr = sumCr / count - 128.0;
                int k = 0;
                for (int py = y; py < yEnd; py++)
                {
                    int row = py * stride;
                    for (int px = x; px < xEnd; px++)
                    {
                        double luma = ys[k++];
                        int r = Math.Clamp((int)Math.Round(luma + 1.402 * cr), 0, 255);
                        int g = Math.Clamp((int)Math.Round(luma - 0.344136 * cb - 0.714136 * cr), 0, 255);
                        int b = Math.Clamp((int)Math.Round(luma + 1.772 * cb), 0, 255);
                        int idx = row + px * 4;
                        dst[idx] = (byte)b;
                        dst[idx + 1] = (byte)g;
                        dst[idx + 2] = (byte)r;
                        dst[idx + 3] = 255;
                    }
                }
            }
        }
        return dst;
    }

    // Konstanta SSIM standar (Wang et al., 2004) untuk piksel 8-bit [0..255]:
    // C1 = (0.01 * 255)^2 = 6.5025, C2 = (0.03 * 255)^2 = 58.5225.
    const double SsimC1 = (0.01 * 255.0) * (0.01 * 255.0);
    const double SsimC2 = (0.03 * 255.0) * (0.03 * 255.0);
    // Penalti kesalahan chroma (rata-rata |dCb|+|dCr| pada skala 0..255) terhadap SSIM luma [0..1].
    const double ChromaErrorWeight = 0.002;
    const int SsimMaxLongSide = 1600;

    static (double Score, double SsimLuma) EvaluateJpegCandidate(
        byte[] jpegBytes, byte[] origSmall, int smallW, int smallH, int smallStride)
    {
        BitmapSource decoded;
        using (var ms = new MemoryStream(jpegBytes, writable: false))
        {
            var dec = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            decoded = dec.Frames[0];
        }
        var candRaw = CopyToBgr32(decoded, out int cw, out int ch, out int cStride);
        byte[] candSmall = (cw == smallW && ch == smallH && cStride == smallStride)
            ? candRaw
            : DownscaleBoxBgr32(candRaw, cw, ch, cStride, SsimMaxLongSide, out _, out _, out _);
        double ssim = ComputeSsimLuma(origSmall, candSmall, smallW, smallH, smallStride);
        double chromaMae = ComputeMeanAbsChromaError(origSmall, candSmall, smallW, smallH, smallStride);
        return (ComputeSimilarityScore(ssim, chromaMae), ssim);
    }

    public static byte[] DownscaleBoxBgr32(
        byte[] src, int width, int height, int stride, int maxLongSide,
        out int outWidth, out int outHeight, out int outStride)
    {
        if (src == null || width <= 0 || height <= 0)
        {
            outWidth = width;
            outHeight = height;
            outStride = stride;
            return Array.Empty<byte>();
        }

        int curLong = Math.Max(width, height);
        if (curLong <= maxLongSide)
        {
            outWidth = width;
            outHeight = height;
            if (stride == width * 4)
            {
                outStride = stride;
                return src;
            }
            outStride = width * 4;
            var copy = new byte[outStride * height];
            for (int y = 0; y < height; y++)
                System.Buffer.BlockCopy(src, y * stride, copy, y * outStride, outStride);
            return copy;
        }

        double scale = (double)maxLongSide / curLong;
        outWidth = Math.Max(1, (int)Math.Round(width * scale));
        outHeight = Math.Max(1, (int)Math.Round(height * scale));
        if (width >= height) outWidth = maxLongSide;
        else outHeight = maxLongSide;

        outStride = outWidth * 4;
        var dst = new byte[outStride * outHeight];

        var x0Arr = new int[outWidth];
        var x1Arr = new int[outWidth];
        for (int ox = 0; ox < outWidth; ox++)
        {
            int x0 = (int)((long)ox * width / outWidth);
            int x1 = Math.Max(x0 + 1, (int)((long)(ox + 1) * width / outWidth));
            if (x1 > width) x1 = width;
            x0Arr[ox] = x0;
            x1Arr[ox] = x1;
        }

        for (int oy = 0; oy < outHeight; oy++)
        {
            int y0 = (int)((long)oy * height / outHeight);
            int y1 = Math.Max(y0 + 1, (int)((long)(oy + 1) * height / outHeight));
            if (y1 > height) y1 = height;
            int dyCount = y1 - y0;
            int dstRow = oy * outStride;

            for (int ox = 0; ox < outWidth; ox++)
            {
                int x0 = x0Arr[ox], x1 = x1Arr[ox];
                int count = dyCount * (x1 - x0);
                int sumB = 0, sumG = 0, sumR = 0;

                for (int py = y0; py < y1; py++)
                {
                    int srcRow = py * stride;
                    for (int px = x0; px < x1; px++)
                    {
                        int idx = srcRow + px * 4;
                        sumB += src[idx];
                        sumG += src[idx + 1];
                        sumR += src[idx + 2];
                    }
                }

                int dIdx = dstRow + ox * 4;
                int half = count >> 1;
                dst[dIdx] = (byte)((sumB + half) / count);
                dst[dIdx + 1] = (byte)((sumG + half) / count);
                dst[dIdx + 2] = (byte)((sumR + half) / count);
                dst[dIdx + 3] = 255;
            }
        }
        return dst;
    }

    public static double ComputeSsimLuma(byte[] origBgr32, byte[] candBgr32, int width, int height, int stride)
    {
        if (origBgr32 == null || candBgr32 == null || width <= 0 || height <= 0)
            return 0.0;

        int stepX = width >= 8 ? 8 : width;
        int stepY = height >= 8 ? 8 : height;
        int limitX = width >= 8 ? width - 7 : 1;
        int limitY = height >= 8 ? height - 7 : 1;

        double ssimSum = 0.0;
        int windowCount = 0;

        for (int by = 0; by < limitY; by += stepY)
        {
            int winH = Math.Min(stepY, height - by);
            for (int bx = 0; bx < limitX; bx += stepX)
            {
                int winW = Math.Min(stepX, width - bx);
                double sumX = 0, sumY = 0, sumXX = 0, sumYY = 0, sumXY = 0;

                for (int dy = 0; dy < winH; dy++)
                {
                    int row = (by + dy) * stride + bx * 4;
                    for (int dx = 0; dx < winW; dx++)
                    {
                        int idx = row + dx * 4;
                        double x = 0.299 * origBgr32[idx + 2] + 0.587 * origBgr32[idx + 1] + 0.114 * origBgr32[idx];
                        double y = 0.299 * candBgr32[idx + 2] + 0.587 * candBgr32[idx + 1] + 0.114 * candBgr32[idx];
                        sumX += x;
                        sumY += y;
                        sumXX += x * x;
                        sumYY += y * y;
                        sumXY += x * y;
                    }
                }

                double n = winW * winH;
                double muX = sumX / n;
                double muY = sumY / n;
                double varX = Math.Max(0.0, sumXX / n - muX * muX);
                double varY = Math.Max(0.0, sumYY / n - muY * muY);
                double covXY = sumXY / n - muX * muY;
                double num = (2.0 * muX * muY + SsimC1) * (2.0 * covXY + SsimC2);
                double den = (muX * muX + muY * muY + SsimC1) * (varX + varY + SsimC2);
                ssimSum += den > 0.0 ? num / den : 1.0;
                windowCount++;
            }
        }

        return windowCount > 0 ? ssimSum / windowCount : 0.0;
    }

    public static double ComputeMeanAbsChromaError(byte[] origBgr32, byte[] candBgr32, int width, int height, int stride)
    {
        if (origBgr32 == null || candBgr32 == null || width <= 0 || height <= 0)
            return 0.0;

        double sumErr = 0.0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x * 4;
                byte b1 = origBgr32[idx], g1 = origBgr32[idx + 1], r1 = origBgr32[idx + 2];
                byte b2 = candBgr32[idx], g2 = candBgr32[idx + 1], r2 = candBgr32[idx + 2];

                double cb1 = -0.168736 * r1 - 0.331264 * g1 + 0.5 * b1 + 128.0;
                double cr1 = 0.5 * r1 - 0.418688 * g1 - 0.081312 * b1 + 128.0;
                double cb2 = -0.168736 * r2 - 0.331264 * g2 + 0.5 * b2 + 128.0;
                double cr2 = 0.5 * r2 - 0.418688 * g2 - 0.081312 * b2 + 128.0;

                sumErr += Math.Abs(cb1 - cb2) + Math.Abs(cr1 - cr2);
            }
        }
        return sumErr / (2.0 * width * height);
    }

    public static double ComputeSimilarityScore(double ssimLuma, double meanAbsChromaError) =>
        ssimLuma - ChromaErrorWeight * meanAbsChromaError;

    static (byte[] Hit, int Quality, long MinBytes) SearchJpegQuality(
        BitmapSource bmp, long targetBytes, CancellationToken ct, int minQuality = 12, int maxQuality = 92)
    {
        var bMax = EncodeJpeg(bmp, maxQuality);
        if (bMax.Length <= targetBytes) return (bMax, maxQuality, bMax.Length);

        var bMin = EncodeJpeg(bmp, minQuality);
        if (bMin.Length > targetBytes) return (null, minQuality, bMin.Length);

        byte[] best = bMin;
        int bestQ = minQuality;
        int lo = minQuality + 1, hi = maxQuality - 1;
        while (lo <= hi)
        {
            ct.ThrowIfCancellationRequested();
            int mid = lo + (hi - lo) / 2;
            var cur = EncodeJpeg(bmp, mid);
            if (cur.Length <= targetBytes)
            {
                best = cur;
                bestQ = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return (best, bestQ, best.Length);
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
