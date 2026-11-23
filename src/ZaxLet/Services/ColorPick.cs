using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace ZaxLet;

/// <summary>Full-screen color picker without magnifier: click reads the pixel under the cursor (GDI, DPI-aware) and copies #RRGGBB.</summary>
public static class ColorPick
{
    public static void Pick() => CaptureService.HideAndCapture(() =>
    {
        var overlay = new CrosshairOverlay();
        overlay.ShowDialog();
        if (overlay.Picked is { } c)
        {
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            try { Clipboard.SetText(hex); } catch { }
            IslandWindow.Instance?.Alert(L.F("{0} disalin", hex), 4);
        }
    });

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr hdc, int x, int y);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    static SD.Color ReadPixel(int x, int y)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return SD.Color.Empty;
        try
        {
            uint cr = GetPixel(hdc, x, y);
            if (cr == 0xFFFFFFFF) return SD.Color.Empty;
            return SD.Color.FromArgb((int)(cr & 0xFF), (int)((cr >> 8) & 0xFF), (int)((cr >> 16) & 0xFF));
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    sealed class CrosshairOverlay : Window
    {
        public SD.Color? Picked;

        public CrosshairOverlay()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;

            SourceInitialized += (_, _) =>
            {
                var vs = WF.SystemInformation.VirtualScreen;
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowPos(hwnd, new IntPtr(-1), vs.X, vs.Y, vs.Width, vs.Height, 0x0010);
            };
            Loaded += (_, _) => Activate();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            MouseLeftButtonDown += (_, _) =>
            {
                Hide();
                if (Native.GetCursorPos(out var pt))
                {
                    var c = ReadPixel(pt.X, pt.Y);
                    if (!c.IsEmpty) Picked = c;
                }
                Close();
            };
        }
    }
}
