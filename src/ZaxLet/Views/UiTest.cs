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
/// UI test tool (<c>ZaxLet.exe --ui-test</c>): renders every Settings page and every guide step to PNG without showing any window,
/// and logs any exceptions thrown while building pages. Output goes to %ZAXLET_UI_OUT% (or the data\ui folder).
/// </summary>
public static class UiTest
{
    public static void Run()
    {
        var dir = Environment.GetEnvironmentVariable("ZAXLET_UI_OUT") is { Length: > 0 } d ? d : Path.Combine(Settings.Dir, "ui");
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        SettingsView._hpGuideShown = true;   // do not open the guide window during tests

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

        // frequently changing tabs: rendered as-is
        if (Environment.GetEnvironmentVariable("ZAXLET_UI_KEEP") != "1")   // ZAXLET_UI_KEEP=1: use data folder contents (copy), not samples
        {
            ClipboardHistory.Items.Clear();
            ClipboardHistory.Items.Add(new ClipboardHistory.Item { Text = "git status && dotnet test --no-restore", At = DateTime.Now.AddMinutes(-2), Pinned = true });
            ClipboardHistory.Items.Add(new ClipboardHistory.Item { Text = "Ini adalah contoh teks clipboard yang sangat panjang untuk menguji apakah pemotongan baris atau elipsis di tab Clipboard bekerja dengan rapi tanpa menabrak tombol pin dan hapus di sebelah kanan layar.", At = DateTime.Now.AddMinutes(-5) });
            ClipboardHistory.Items.Add(new ClipboardHistory.Item { Text = "https://example.com/docs/zaxlet/release-notes", At = DateTime.Now.AddMinutes(-12) });
            ClipboardHistory.Items.Add(new ClipboardHistory.Item { Text = "var x = UI.Tok(245_800_000);", At = DateTime.Now.AddMinutes(-25) });
            ClipboardHistory.Items.Add(new ClipboardHistory.Item { Text = "Catatan rapat sore: cek kuota AI dan jadwal rilis", At = DateTime.Now.AddMinutes(-40) });
        }
        bool prevClip = Settings.Cur.ClipboardOn;
        Settings.Cur.ClipboardOn = true;
        foreach (var (name, make) in new (string, Func<TabView>)[]
        {
            ("timer", () => new TimerView()), ("shelf", () => new ShelfView()),
            ("water", () => new WaterView()), ("music", () => new MusicView()), ("ctrl", () => new ControlView()),
            ("usage", () => new UsageView()), ("monitor", () => MonitorView.Preview()), ("today", () => new CalendarView()),
            ("prayer", () => new PrayerView()), ("comm", () => new AppsView()), ("clipboard", () => new ClipboardView()),
            ("capture", () => new ToolsView()),
        })
        {
            try { var v = make(); v.OnShow(); Shot(v, "tab-" + name, 660, 262); v.OnHide(); log.AppendLine("ok tab " + name); }
            catch (Exception ex) { log.AppendLine($"GAGAL tab {name}: {ex}"); }
        }
        Settings.Cur.ClipboardOn = prevClip;

        try
        {
            var prevClaudeOn = Settings.Cur.ClaudeOn; var prevCodexOn = Settings.Cur.CodexOn;
            var prevSlot2 = Settings.Cur.Slot2; var prevCost = Settings.Cur.ShowCost;
            Settings.Cur.ClaudeOn = true; Settings.Cur.CodexOn = true; Settings.Cur.Slot2 = "antigravity";
            Settings.Cur.ShowCost = true;
            var now = DateTimeOffset.Now;
            var claude = new ProviderUsage
            {
                Name = "Claude", Plan = "Max", Source = "api",
                TodayTok = 245_800_000, WeekTok = 892_450_000, MonthTok = 3_400_000_000, TodayCost = 1234.56, WeekCost = 4567.89, MonthCost = 12345.67,
                Windows = new()
                {
                    new QuotaWindow { Label = "5-hour", Pct = 82, Reset = now.AddHours(2).AddMinutes(15) },
                    new QuotaWindow { Label = "Weekly", Pct = 64, Reset = now.AddDays(3).AddHours(10) },
                }
            };
            var agy = new ProviderUsage
            {
                Name = "Antigravity", Plan = "Pro",
                TodayTok = 156_400_000, WeekTok = 780_900_000, MonthTok = 2_900_000_000,
                Windows = new()
                {
                    new QuotaWindow { Label = "Gemini Pro · weekly", Pct = 78, Reset = now.AddDays(2).AddHours(5) },
                    new QuotaWindow { Label = "Gemini Pro · 5h", Pct = 91, Reset = now.AddHours(3).AddMinutes(20) },
                    new QuotaWindow { Label = "Claude · weekly", Pct = 32, Reset = now.AddDays(4).AddHours(1) },
                    new QuotaWindow { Label = "Claude · 5h", Pct = 18, Reset = now.AddHours(4).AddMinutes(10) },
                }
            };
            var aiBig = UsageView.Preview(claude, agy, (prov, w) =>
                prov == "Antigravity" && w.Label == "Gemini Pro · 5h"
                    ? new BurnProjection(true, true, TimeSpan.FromMinutes(45), 18.5)
                    : new BurnProjection(true, false, null, 2.0));
            Shot(aiBig, "tab-ai-big", 660, 262);
            Settings.Cur.ClaudeOn = prevClaudeOn; Settings.Cur.CodexOn = prevCodexOn;
            Settings.Cur.Slot2 = prevSlot2; Settings.Cur.ShowCost = prevCost;
            log.AppendLine("ok tab ai-big");
        }
        catch (Exception ex) { log.AppendLine("GAGAL tab ai-big: " + ex); }

        // island-idle.png skipped: Pill lives inside IslandWindow (a real window with dynamic animation/sizing)
        log.AppendLine("skip island-idle: tidak dirender tanpa jendela nyata");

        try
        {
            var t0 = new DateTime(2026, 9, 11, 20, 27, 0);
            var root = @"C:\Users\contoh\Documents";
            var groups = new System.Collections.Generic.List<DupGroup>
            {
                new(713000, "a", new[] { new DupFile(root + @"\House Flipper\Profiles.backup\Profile 1\Backup-2026-09-11-20-27-01-an-extremely-long-file-name.json", 713000, t0), new DupFile(root + @"\House Flipper\Profiles.backup\Profile 1\Backup-2026-09-11-20-26-59.json", 713000, t0), new DupFile(root + @"\catatan.md", 713000, t0) }),
                new(824000, "b", new[] { new DupFile(root + @"\skripsi\bab3.docx", 824000, t0), new DupFile(root + @"\skripsi\salinan\bab3 (1).docx", 824000, t0) }),
            };
            var host = DupWindow.Preview(root, groups);
            host.Width = 680; host.Height = 480;
            host.Measure(new Size(680, 480)); host.Arrange(new Rect(0, 0, 680, 480)); host.UpdateLayout();
            var bmp = new RenderTargetBitmap(680, 480, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, "dialog-dup.png")); enc.Save(fs);
            log.AppendLine("ok dialog dup");
        }
        catch (Exception ex) { log.AppendLine("GAGAL dialog dup: " + ex); }

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
