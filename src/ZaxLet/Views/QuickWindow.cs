using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZaxLet;

/// <summary>
/// Kotak Aksi cepat: pintasan global membuka satu kolom ketik di bawah island; Enter menjalankan satu perintah (lihat <see cref="QuickCmd"/>), Esc atau
/// klik di luar menutup. Tidak ada pemindaian atau proses latar: jendela hanya ada selama dipakai.
/// </summary>
public sealed class QuickWindow : Window
{
    static QuickWindow _cur;
    readonly IntPtr _prevFg;
    readonly TextBox _box;
    readonly TextBlock _hint;

    public static void Toggle()
    {
        if (_cur != null) { _cur.Close(); return; }
        var prevFg = Native.GetForegroundWindow();
        _cur = new QuickWindow(prevFg);
        _cur.Show();
        _cur.Activate();
        _cur._box.Focus();
    }

    QuickWindow(IntPtr prevFg)
    {
        _prevFg = prevFg;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.Height; Width = 480;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2; Top = 64;

        _box = new TextBox { FontSize = 17, Foreground = Brushes.White, CaretBrush = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 2, 0, 2) };
        _hint = new TextBlock { Text = L.T(QuickCmd.Help), FontSize = 11.5, Foreground = UI.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var sp = new StackPanel();
        sp.Children.Add(_box); sp.Children.Add(_hint);
        Content = new Border { Background = Brushes.Black, BorderBrush = UI.Track, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 12, 18, 12), Child = sp };

        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Run(_box.Text); e.Handled = true; }
        };
        _box.TextChanged += (_, _) =>   // hasil hitungan tampil langsung saat mengetik
        {
            _sleepArmed = false;
            var p = QuickCmd.Parse(_box.Text);
            _hint.Foreground = p.Kind == QuickCmd.Kind.Calc ? UI.Accent : UI.Faint;
            _hint.Text = p.Kind == QuickCmd.Kind.Calc ? (p.Arg.Contains('=') ? p.Arg : "= " + p.Arg) : L.T(QuickCmd.Help);
        };
        Deactivated += (_, _) => { if (_cur == this) Close(); };
        Closed += (_, _) => { if (_cur == this) _cur = null; };
    }

    bool _sleepArmed;

    void Run(string text)
    {
        var c = QuickCmd.Parse(text);
        if (c.Kind == QuickCmd.Kind.None) { Close(); return; }
        if (c.Kind == QuickCmd.Kind.Unknown) { _hint.Text = L.T(c.Arg); _hint.Foreground = UI.Warn; return; }   // tetap terbuka: perbaiki ketikan
        string msg;
        var isl = IslandWindow.Instance;
        switch (c.Kind)
        {
            case QuickCmd.Kind.Timer:
                TimerService.I.SetMode(TMode.Timer); TimerService.I.SetTotal(TimeSpan.FromSeconds(c.Value)); TimerService.I.Start();
                msg = L.F("Timer {0} berjalan", DurText.Fmt(c.Value)); break;
            case QuickCmd.Kind.Note:
                CalendarService.I.Add(DateOnly.FromDateTime(DateTime.Today), c.Arg); msg = L.T("Dicatat di tugas hari ini"); break;
            case QuickCmd.Kind.Water:
                int ml = c.Value > 0 ? (int)c.Value : Settings.Cur.WaterCupMl;
                HydrationService.I.Add(ml); msg = L.F("Minum +{0} ml", ml); break;
            case QuickCmd.Kind.Theme:
                Settings.Cur.Theme = c.Arg; Settings.Apply(); msg = L.F("Tema: {0}", c.Arg); break;
            case QuickCmd.Kind.Hide:
                isl?.ToggleHidden(); msg = ""; break;
            case QuickCmd.Kind.Perf:
                PerfOverlay.Toggle(); msg = ""; break;
            case QuickCmd.Kind.Bt:
                msg = isl?.BtSummary() ?? ""; break;
            case QuickCmd.Kind.Lock: Close(); SysActions.Lock(); return;
            case QuickCmd.Kind.Sleep:
                if (!_sleepArmed) { _sleepArmed = true; _hint.Text = L.T("Tidur? Tekan Enter sekali lagi"); _hint.Foreground = UI.Warn; return; }
                Close(); SysActions.Sleep(); return;
            case QuickCmd.Kind.MusicSleep:
                if (c.Value < 0) { MediaService.I.SetSleepEndOfTrack(); msg = L.T("Tidur musik: akhir lagu"); }
                else if (c.Value == 0) { MediaService.I.CancelSleepTimer(); msg = L.T("Tidur musik dimatikan"); }
                else { int mins = (int)c.Value; MediaService.I.SetSleepMinutes(mins); msg = L.F("Tidur musik {0} menit", mins); }
                break;
            case QuickCmd.Kind.Dark: case QuickCmd.Kind.Light:
                SysActions.SetDark(c.Kind == QuickCmd.Kind.Dark); msg = L.T(c.Kind == QuickCmd.Kind.Dark ? "Tema Windows: gelap" : "Tema Windows: terang"); break;
            case QuickCmd.Kind.Wifi: case QuickCmd.Kind.Bluetooth:
                SysActions.Radio(c.Kind == QuickCmd.Kind.Wifi ? Windows.Devices.Radios.RadioKind.WiFi : Windows.Devices.Radios.RadioKind.Bluetooth, (int)c.Value, c.Kind == QuickCmd.Kind.Wifi ? "Wi-Fi" : "Bluetooth"); msg = ""; break;
            case QuickCmd.Kind.Power:
                SysActions.PowerPlan(c.Arg); msg = ""; break;
            case QuickCmd.Kind.Awake:
                msg = SysActions.Awake(c.Arg, (int)c.Value); break;
            case QuickCmd.Kind.Ocr:
                Close(); ScreenOcr.PickAndCopy(); return;
            case QuickCmd.Kind.Color:
                Close(); ColorPick.Pick(); return;
            case QuickCmd.Kind.Remind:
                var rem = ReminderService.I.Add(c.Arg);
                msg = rem != null ? L.F("Diingatkan {0}: {1}", rem.DueAt.ToString("HH.mm"), rem.Label) : L.T("Format pengingat tidak valid");
                break;
            case QuickCmd.Kind.Topmost:
                msg = L.T(Native.ToggleTopmost(_prevFg).Message);
                break;
            case QuickCmd.Kind.Eject:
                char? drv = c.Arg.Length > 0 ? c.Arg[0] : null;
                Close();
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    var (_, res) = UsbDrives.Eject(drv);
                    isl?.Alert(res, 5, null, false);
                });
                return;
            case QuickCmd.Kind.Hp:
                if (string.IsNullOrEmpty(c.Arg))
                {
                    int n = PhoneText.SendClipboard();
                    msg = L.T(n < 0 ? "Clipboard tidak berisi teks (atau terlalu besar)" : n == 0 ? "HP belum tersambung" : "Dikirim ke HP");
                }
                else if (System.Text.Encoding.UTF8.GetByteCount(c.Arg) > PhoneText.MaxBytes)
                {
                    msg = L.T("Teks terlalu panjang");
                }
                else
                {
                    int n = ShareServer.I.Command("text " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c.Arg)));
                    msg = L.T(n > 0 ? "Dikirim ke HP" : "HP belum tersambung");
                }
                break;
            case QuickCmd.Kind.Calc:
                try { Clipboard.SetText(c.Arg.Contains('=') ? c.Arg[(c.Arg.IndexOf('=') + 1)..].Trim().Split(' ')[0] : c.Arg); } catch { }   // hasil saja, tanpa satuan
                msg = L.F("{0} (disalin)", c.Arg.Contains('=') ? c.Arg : "= " + c.Arg); break;
            case QuickCmd.Kind.Open:
                var key = IslandWindow.TabOrder.FirstOrDefault(k => k != "settings" && !Settings.Cur.HiddenTabs.Contains(k)
                    && (k == c.Arg || IslandWindow.TabMeta[k].Label.ToLowerInvariant().StartsWith(c.Arg) || L.T(IslandWindow.TabMeta[k].Label).ToLowerInvariant().StartsWith(c.Arg)));
                if (key == null) { _hint.Text = L.T("Tab tidak ditemukan"); _hint.Foreground = UI.Warn; return; }
                Close(); isl?.GoTab(key); return;
            default: msg = ""; break;
        }
        Close();
        if (msg.Length > 0) isl?.Alert(msg, 3, null, false);
    }
}
