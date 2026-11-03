using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>Fitur tambahan island: gestur, popup OSD, indikator mikrofon/kamera, unduhan selesai, jaringan, riwayat clipboard, adzan.</summary>
public partial class IslandWindow
{
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);

    static IslandWindow() { TabMeta["clip"] = ("", "Clipboard"); }

    Action _alertAction;
    bool _dblSkip;
    readonly YieldService _yield = new();
    (int L, int T, int R, int B) _pillPx;   // ditulis thread UI, dibaca thread latar: salinan sesaat yang tidak konsisten tidak berbahaya
    readonly DispatcherTimer _osdT = new() { Interval = TimeSpan.FromMilliseconds(300) };
    Timer _slowT;
    readonly BluetoothWatch _bt = new();
    readonly DownloadWatch _dl = new();

    // keadaan terakhir untuk mendeteksi perubahan (null = belum dibaca: bacaan pertama tidak memunculkan popup)
    float? _lastVol; bool? _lastMute, _lastMic; bool? _lastCaps; int? _lastBright;
    HashSet<string> _lastPrivacy = new();
    bool _netWarned;

    void InitFeatures()
    {
        Pill.PreviewMouseWheel += OnWheel;
        Pill.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // klik dua kali pada pill idle = putar/jeda; klik kedua tidak boleh ikut membuka pill
            if (Settings.Cur.Gestures && e.ClickCount == 2 && !_open && MediaService.I.HasSession) { MediaService.I.PlayPause(); _dblSkip = true; e.Handled = true; }
        };
        _osdT.Tick += (_, _) => OsdPoll();
        _osdT.Start();
        _yield.Start(() => _pillPx);
    }

    /// <summary>Dipanggil dari hook jendela: pesan Windows yang dipakai fitur tambahan.</summary>
    void HookFeatures(int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == 0x031D) { ClipboardHistory.Capture(); handled = true; }   // WM_CLIPBOARDUPDATE
        else if (msg == 0x020E && Settings.Cur.Gestures && _open && Pill.IsMouseOver)   // WM_MOUSEHWHEEL: sapuan dua jari ke samping
        {
            int delta = (short)((wp.ToInt64() >> 16) & 0xFFFF);
            if (delta != 0) { ShiftTab(delta > 0 ? 1 : -1); handled = true; }
        }
    }

    void StartFeatureListeners(IntPtr hwnd)
    {
        try { AddClipboardFormatListener(hwnd); } catch (Exception ex) { App.Log(ex); }
        _bt.Changed += (name, on) => { if (Settings.Cur.OsdOn) Osd(L.F(on ? "Bluetooth tersambung: {0}" : "Bluetooth terputus: {0}", name)); };
        _bt.Start();
        _dl.Finished += path =>
        {
            if (!Settings.Cur.DownloadAlert) return;
            Alert(L.F("Unduhan selesai: {0} · klik untuk menampilkan", System.IO.Path.GetFileName(path)), 8,
                () => { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { } });
        };
        _dl.Start();
        // pembacaan yang agak berat (WMI, registri, jaringan) di thread latar tiap 2 dtk
        _slowT = new Timer(_ => SlowPoll(), null, 4000, 2000);
    }

    // ---------- klik pada pill ----------
    bool HandleClickAction()
    {
        if (_dblSkip) { _dblSkip = false; return true; }
        if (AdhanPlayer.Playing) { AdhanPlayer.Stop(); _alert = null; RefreshIdle(); return true; }
        if (_alert != null && DateTime.Now < _alertUntil && _alertAction != null)
        {
            var a = _alertAction;
            _alertAction = null; _alertUntil = DateTime.MinValue;
            RefreshIdle();
            try { a(); } catch (Exception ex) { App.Log(ex); }
            return true;
        }
        return false;
    }

    // ---------- gestur ----------
    static bool InScrollable(DependencyObject d)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollViewer or TextBox or ComboBox or ListBox) return true;
        return false;
    }

    // scroll di atas island = volume (kecuali di atas daftar/kotak teks yang menggulir sendiri)
    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Settings.Cur.Gestures || OverNotifRows() || e.OriginalSource is DependencyObject d && InScrollable(d)) return;
        var v = AudioEndpoint.Volume();
        if (v == null) return;
        e.Handled = true;
        var nv = Math.Clamp(v.Value + Math.Sign(e.Delta) * 0.02f, 0f, 1f);
        AudioEndpoint.SetVolume(nv);
        _lastVol = nv; _lastMute = nv <= 0 ? (bool?)null : false;
        Osd(VolumeText(nv, false));
    }

    static string VolumeText(float v, bool muted)
    {
        int n = (int)Math.Round(v * 10);
        return $"{L.T("Volume")} {new string('█', n)}{new string('░', 10 - n)} {(muted ? L.T("bisu") : Math.Round(v * 100) + "%")}";
    }

    void ShiftTab(int d)
    {
        var keys = TabOrder.Where(k => k == "settings" || !Settings.Cur.HiddenTabs.Contains(k)).ToList();
        int i = keys.IndexOf(_cur?.Key ?? "");
        if (i < 0) return;
        Select(keys[(i + d + keys.Count) % keys.Count]);
    }

    // ---------- OSD ----------
    void Osd(string text) => Alert(text, 1.8, null, sound: false);

    void OsdPoll()
    {
        {   // persegi pill dalam piksel layar untuk YieldService
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double x0 = Left + (Width - Pill.ActualWidth) / 2;
            _pillPx = ((int)(x0 * dpi), (int)(Top * dpi), (int)((x0 + Pill.ActualWidth) * dpi), (int)((Top + Pill.ActualHeight) * dpi));
        }
        if (!Settings.Cur.OsdOn || _snooze) return;
        var v = AudioEndpoint.Volume();
        var m = AudioEndpoint.Muted();
        if (v != null && _lastVol != null && (Math.Abs(v.Value - _lastVol.Value) >= 0.005f || m != _lastMute) && m != null)
            Osd(VolumeText(v.Value, m == true));
        _lastVol = v ?? _lastVol; _lastMute = m ?? _lastMute;

        bool caps = (GetKeyState(0x14) & 1) != 0;
        if (_lastCaps != null && caps != _lastCaps) Osd(caps ? "Caps Lock ON" : "Caps Lock off");
        _lastCaps = caps;
    }

    // ---------- pembacaan latar ----------
    void SlowPoll()
    {
        try
        {
            var s = Settings.Cur;
            if (s.OsdOn)
            {
                var mic = AudioEndpoint.Muted(AudioEndpoint.Capture);
                if (mic != null && _lastMic != null && mic != _lastMic) Osd(mic == true ? L.T("Mikrofon dibisukan") : L.T("Mikrofon aktif"));
                if (mic != null) _lastMic = mic;
                var b = Brightness.Get();
                if (b != null && _lastBright != null && b != _lastBright) Osd($"{L.T("Kecerahan")} {b}%");
                if (b != null) _lastBright = b;
            }
            if (s.PrivacyOn)
            {
                var now = new HashSet<string>(PrivacyWatch.InUse("microphone").Select(a => "mic:" + a).Concat(PrivacyWatch.InUse("webcam").Select(a => "cam:" + a)));
                foreach (var n in now.Except(_lastPrivacy))
                    Alert(L.F(n.StartsWith("mic:") ? "Mikrofon dipakai: {0}" : "Kamera dipakai: {0}", n[4..]), 4, null, sound: false);
                _lastPrivacy = now;
            }
            if (ShareServer.I.On && !_netWarned)
            {
                var net = NetInfo.Current();
                if (net != null && net.Category == 0 && !NetInfo.FirewallOpenToPublic())
                {
                    _netWarned = true;
                    Alert(L.T("Wi-Fi ini Publik di Windows dan aturan firewall HP belum mencakupnya: HP tidak bisa masuk"), 12);
                }
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
