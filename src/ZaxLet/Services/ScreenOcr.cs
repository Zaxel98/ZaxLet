using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace ZaxLet;

/// <summary>OCR area layar offline via Windows.Media.Ocr, lalu salin teks ke clipboard.</summary>
public static class ScreenOcr
{
    const int PreviewMax = 40;

    public static void PickAndCopy()
    {
        var engine = CreateEngine();
        if (engine == null)
        {
            IslandWindow.Instance?.Alert(L.T("Bahasa OCR Windows belum terpasang"), 5);
            return;
        }
        CaptureService.PickArea(bmp =>
        {
            var sb = ToSoftwareBitmap(bmp);
            RecognizeAndCopy(sb, engine);
        });
    }

    static OcrEngine CreateEngine()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine != null) return engine;
        var en = OcrEngine.AvailableRecognizerLanguages
            .FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        return en != null ? OcrEngine.TryCreateFromLanguage(en) : null;
    }

    static SoftwareBitmap ToSoftwareBitmap(SD.Bitmap bmp)
    {
        var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bmp.Width * 4;
            var bytes = new byte[rowBytes * bmp.Height];
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            }
            else
            {
                for (int y = 0; y < bmp.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * rowBytes, rowBytes);
            }
            return SoftwareBitmap.CreateCopyFromBuffer(
                bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    static async void RecognizeAndCopy(SoftwareBitmap sb, OcrEngine engine)
    {
        try
        {
            using (sb)
            {
                var res = await engine.RecognizeAsync(sb);
                var text = string.Join(Environment.NewLine, res.Lines.Select(l => l.Text)).Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    IslandWindow.Instance?.Alert(L.T("Tidak ada teks terbaca"), 4);
                    return;
                }
                try { System.Windows.Clipboard.SetText(text); } catch { }
                var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                var preview = flat.Length > PreviewMax ? flat[..PreviewMax] + "…" : flat;
                IslandWindow.Instance?.Alert(L.F("OCR disalin: {0}", preview), 5);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            IslandWindow.Instance?.Alert(L.T("Gagal membaca teks OCR"), 4);
        }
    }
}
