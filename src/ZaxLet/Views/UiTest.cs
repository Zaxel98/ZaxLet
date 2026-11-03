using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZaxLet;

/// <summary>
/// Alat uji tampilan (<c>ZaxLet.exe --ui-test</c>): merender tiap halaman Pengaturan dan tiap langkah panduan ke PNG tanpa menampilkan jendela
/// apa pun, dan mencatat pengecualian yang muncul saat membangun halaman. Keluaran di %ZAXLET_UI_OUT% (atau folder data\ui).
/// </summary>
public static class UiTest
{
    public static void Run()
    {
        var dir = Environment.GetEnvironmentVariable("ZAXLET_UI_OUT") is { Length: > 0 } d ? d : Path.Combine(Settings.Dir, "ui");
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        SettingsView._hpGuideShown = true;   // jangan membuka jendela panduan saat uji

        void Shot(FrameworkElement content, string name, double w, double h)
        {
            var host = new Border { Background = Brushes.Black, Width = w, Height = h, Padding = new Thickness(14), Child = content };
            host.Measure(new Size(w, h)); host.Arrange(new Rect(0, 0, w, h)); host.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, name + ".png")); enc.Save(fs);
        }

        foreach (var adv in new[] { false, true })
        {
            Settings.Cur.Advanced = adv;
            foreach (var page in new[] { "general", "island", "music", "phone", "system", "calendar", "remind", "tools", "addons" })
            {
                try
                {
                    SettingsView.StartPage = page;
                    var v = new SettingsView();
                    v.OnShow();
                    Shot(v, $"settings-{page}{(adv ? "-lanjutan" : "")}", 560, adv ? 1500 : 1100);
                    v.OnHide();
                    log.AppendLine($"ok settings {page} adv={adv}");
                }
                catch (Exception ex) { log.AppendLine($"GAGAL settings {page} adv={adv}: {ex}"); }
            }
        }

        void Guide(string id, System.Collections.Generic.List<GuideStep> steps)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                try
                {
                    var s = steps[i];
                    var sp = new StackPanel { Width = 470 };
                    sp.Children.Add(UI.T($"Langkah {i + 1} dari {steps.Count}", 11, UI.Faint));
                    sp.Children.Add(new TextBlock { Text = L.T(s.Title), FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
                    sp.Children.Add(new TextBlock { Text = L.T(s.Body), FontSize = 13, Foreground = UI.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
                    if (s.Build != null) sp.Children.Add(s.Build());
                    s.Tick?.Invoke();
                    Shot(sp, $"panduan-{id}-{i + 1}", 520, 520);
                    log.AppendLine($"ok panduan {id} langkah {i + 1}");
                }
                catch (Exception ex) { log.AppendLine($"GAGAL panduan {id} langkah {i + 1}: {ex}"); }
            }
        }
        Guide("hp", PhoneGuide.Steps());
        Guide("discord", DiscordGuide.Steps());

        lock (L.Misses) File.WriteAllLines(Path.Combine(dir, "missing.txt"), L.Misses);
        File.WriteAllText(Path.Combine(dir, "ui-test.txt"), log.ToString());
    }
}
