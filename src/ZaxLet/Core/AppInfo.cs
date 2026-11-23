using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;

namespace ZaxLet;

/// <summary>Descriptive name + native icon of an application from its exe (cached per process name).</summary>
public static class AppInfo
{
    static readonly ConcurrentDictionary<string, (string Title, ImageSource Icon)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static (string Title, ImageSource Icon) Get(string proc, string path) => Cache.GetOrAdd(proc, _ => Load(proc, path));

    static (string, ImageSource) Load(string proc, string path)
    {
        string title = proc.Length > 0 ? char.ToUpper(proc[0]) + proc[1..] : proc;
        ImageSource icon = null;
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                var d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) title = d.Trim();
            }
            catch { }
            try
            {
                using var ic = SD.Icon.ExtractAssociatedIcon(path);
                if (ic != null)
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    src.Freeze();
                    icon = src;
                }
            }
            catch { }
        }
        return (title, icon);
    }
}
