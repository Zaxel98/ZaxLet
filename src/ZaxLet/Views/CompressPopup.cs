using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZaxLet;

/// <summary>
/// Popup for selecting the target compression size for images / PDFs from Shelf.
/// Last choice is stored in a static field only (without modifying Settings).
/// </summary>
public sealed class CompressPopup : Window
{
    static CompressPopup _cur;
    static int _lastTargetKb = 200;
    static bool _lastKeepResolution = true;

    readonly string _src;
    readonly long _srcBytes;
    readonly bool _isPdf;
    readonly TextBox _kbBox;
    readonly Slider _slider;
    readonly CheckBox _keepResCheck;
    readonly TextBlock _sliderLabel, _liveLabel, _statusLabel;
    readonly Border _runBtn;
    readonly TextBlock _runBtnText;
    bool _syncing;
    bool _running;
    CancellationTokenSource _cts;

    public static void Show(string src)
    {
        if (_cur != null) { _cur.Close(); }
        _cur = new CompressPopup(src);
        _cur.Show();
        _cur.Activate();
    }

    CompressPopup(string src)
    {
        _src = src;
        _isPdf = FileCompress.IsPdfExt(Path.GetExtension(src));
        bool validFile = !string.IsNullOrWhiteSpace(src) && File.Exists(src);
        _srcBytes = validFile ? new FileInfo(src).Length : 0;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 390;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 64;

        IslandWindow.Instance?.KeepOpen(true);

        var root = new StackPanel();

        // Header: title + file name
        var head = UI.Cols(-1, 0);
        var title = UI.T("Kompres file", 14, UI.Fg, true);
        head.Children.Add(title);
        var closeBtn = UI.IconBtn("", CancelOrClose, 12, UI.Card);
        closeBtn.Padding = new Thickness(6);
        head.Children.Add(closeBtn.At(0, 1));
        root.Children.Add(head);

        var fileInfo = UI.T(
            validFile ? $"{Path.GetFileName(src)} · {FileCompress.FmtSize(_srcBytes)}" : L.T("File tidak ditemukan"),
            11.5, UI.Dim);
        fileInfo.Margin = new Thickness(0, 2, 0, 10);
        root.Children.Add(fileInfo);

        // Preset buttons: 200 KB, 500 KB, 1 MB, 2 MB
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (label, kb) in new[] { ("200 KB", 200), ("500 KB", 500), ("1 MB", 1024), ("2 MB", 2048) })
        {
            int targetKb = kb;
            var pb = UI.Btn(UI.T(label, 11.5, UI.Fg, true), () => SetTargetKb(targetKb, fromSlider: false), UI.CardOn, 8, new Thickness(10, 4, 10, 4));
            pb.Margin = new Thickness(0, 0, 6, 0);
            presets.Children.Add(pb);
        }
        root.Children.Add(presets);

        // Custom KB box
        var kbRow = UI.Cols(0, -1, 0);
        kbRow.Margin = new Thickness(0, 0, 0, 10);
        var kbLbl = UI.T("Target maks:", 12, UI.Dim);
        kbLbl.Margin = new Thickness(0, 0, 8, 0);
        kbRow.Children.Add(kbLbl);

        _kbBox = new TextBox
        {
            FontSize = 13,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            Background = UI.Card,
            BorderBrush = UI.Track,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        kbRow.Children.Add(_kbBox.At(0, 1));

        var kbUnit = UI.T("KB (min 20)", 11.5, UI.Dim);
        kbUnit.Margin = new Thickness(8, 0, 0, 0);
        kbRow.Children.Add(kbUnit.At(0, 2));
        root.Children.Add(kbRow);

        // Slider: Reduce size: N% (10-95%, step 5, default 70)
        _sliderLabel = UI.T("Kurangi ukuran: 70%", 12, UI.Fg);
        _sliderLabel.Margin = new Thickness(0, 0, 0, 4);
        root.Children.Add(_sliderLabel);

        _slider = new Slider
        {
            Minimum = 10,
            Maximum = 95,
            SmallChange = 5,
            LargeChange = 5,
            TickFrequency = 5,
            IsSnapToTickEnabled = true,
            Value = 70,
            Margin = new Thickness(0, 0, 0, 6)
        };
        root.Children.Add(_slider);

        // Live preview: e.g. "6,0 MB -> 800 KB (-87%)"
        _liveLabel = UI.T("", 12.5, UI.Accent, true);
        _liveLabel.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(_liveLabel);

        _keepResCheck = new CheckBox
        {
            Content = L.T("Pertahankan resolusi (bisa lebih buram)"),
            IsChecked = _lastKeepResolution,
            Foreground = UI.Fg,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = _isPdf ? Visibility.Collapsed : Visibility.Visible
        };
        _keepResCheck.Checked += (_, _) => _lastKeepResolution = true;
        _keepResCheck.Unchecked += (_, _) => _lastKeepResolution = false;
        root.Children.Add(_keepResCheck);

        // Honest limits
        var limitText = _isPdf
            ? "Gambar di dalam PDF dikompres ulang; teks dan vektor tetap tajam. Jika target tak tercapai, halaman diubah menjadi gambar (teks tidak bisa diseleksi). PDF berpassword ditolak. File asli tidak diubah."
            : "Resolusi piksel dipertahankan secara default dengan menurunkan kualitas JPEG dan penghalusan ringan bila perlu. Hapus centang di atas untuk mengizinkan penurunan resolusi. Keluaran selalu JPEG (transparansi diisi putih). File asli tidak diubah.";
        var limitLabel = UI.T(limitText, 11, UI.Dim);
        limitLabel.TextWrapping = TextWrapping.Wrap;
        limitLabel.TextTrimming = TextTrimming.None;
        limitLabel.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(limitLabel);

        // Status / error / progress
        _statusLabel = UI.T("", 11.5, UI.Warn);
        _statusLabel.TextWrapping = TextWrapping.Wrap;
        _statusLabel.TextTrimming = TextTrimming.None;
        _statusLabel.Margin = new Thickness(0, 0, 0, 8);
        _statusLabel.Visibility = Visibility.Collapsed;
        root.Children.Add(_statusLabel);

        // Action buttons
        var actions = UI.Cols(-1, 0, 0);
        var cancelBtn = UI.Btn(UI.T("Batal", 12, UI.Fg), CancelOrClose, UI.Card, 8, new Thickness(12, 6, 12, 6));
        cancelBtn.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(cancelBtn.At(0, 1));

        _runBtnText = UI.T("Kompres", 12, Brushes.Black, true);
        _runBtn = UI.Btn(_runBtnText, StartCompress, UI.Accent, 8, new Thickness(14, 6, 14, 6));
        actions.Children.Add(_runBtn.At(0, 2));
        root.Children.Add(actions);

        Content = new Border
        {
            Background = UI.B("#F20B0B0D"),
            BorderBrush = UI.Track,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 12, 16, 12),
            Child = root
        };

        // Events
        _slider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            int n = (int)Math.Round(_slider.Value);
            long targetBytes = _srcBytes > 0
                ? Math.Max(FileCompress.MinTargetBytes, (long)Math.Round(_srcBytes * (1.0 - n / 100.0)))
                : Math.Max(20, _lastTargetKb) * 1024L;
            int kb = (int)Math.Max(20, Math.Round(targetBytes / 1024.0));
            SetTargetKb(kb, fromSlider: true, explicitPct: n);
        };

        _kbBox.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            if (int.TryParse(_kbBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int kb) && kb > 0)
            {
                SetTargetKb(Math.Max(20, kb), fromSlider: false, updateBox: false);
            }
        };

        _kbBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; StartCompress(); }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; CancelOrClose(); }
        };

        Deactivated += (_, _) =>
        {
            if (_cur == this && !_running) Close();
        };

        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
            IslandWindow.Instance?.KeepOpen(false);
            if (_cur == this) _cur = null;
        };

        // Initialize from last choice
        SetTargetKb(Math.Max(20, _lastTargetKb), fromSlider: false);

        if (!validFile)
        {
            ShowError(L.T("Sumber bukan file yang valid atau tidak ditemukan."));
            _runBtn.IsEnabled = false;
            _runBtn.Opacity = 0.4;
        }
        else if (_srcBytes > FileCompress.MaxSourceBytes)
        {
            ShowError(L.T("Ukuran file melebihi batas 300 MB."));
            _runBtn.IsEnabled = false;
            _runBtn.Opacity = 0.4;
        }
    }

    void SetTargetKb(int kb, bool fromSlider, bool updateBox = true, int? explicitPct = null)
    {
        _syncing = true;
        try
        {
            kb = Math.Max(20, kb);
            _lastTargetKb = kb;
            if (updateBox) _kbBox.Text = kb.ToString(CultureInfo.InvariantCulture);

            long targetBytes = kb * 1024L;
            int actualPct = _srcBytes > 0
                ? (int)Math.Round((1.0 - (double)targetBytes / _srcBytes) * 100.0)
                : (explicitPct ?? 70);

            int sliderPct = explicitPct ?? Math.Clamp(actualPct, 10, 95);
            if (!fromSlider) _slider.Value = sliderPct;

            _sliderLabel.Text = L.F("Kurangi ukuran: {0}%", sliderPct);
            if (_srcBytes > 0)
            {
                string sign = actualPct >= 0 ? $"-{actualPct}%" : $"+{-actualPct}%";
                _liveLabel.Text = $"{FileCompress.FmtSize(_srcBytes)} -> {FileCompress.FmtSize(targetBytes)} ({sign})";
            }
            else
            {
                _liveLabel.Text = $"Target: {FileCompress.FmtSize(targetBytes)}";
            }
        }
        finally { _syncing = false; }
    }

    void ShowError(string msg)
    {
        _statusLabel.Foreground = UI.Bad;
        _statusLabel.Text = msg;
        _statusLabel.Visibility = Visibility.Visible;
    }

    void ShowProgress(string msg)
    {
        _statusLabel.Foreground = UI.Fg;
        _statusLabel.Text = msg;
        _statusLabel.Visibility = Visibility.Visible;
    }

    void CancelOrClose()
    {
        if (_running)
        {
            _cts?.Cancel();
            ShowProgress(L.T("Membatalkan..."));
            return;
        }
        Close();
    }

    async void StartCompress()
    {
        if (_running) return;
        if (!File.Exists(_src) || _srcBytes > FileCompress.MaxSourceBytes) return;

        if (!int.TryParse(_kbBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int kb) || kb < 20)
        {
            kb = Math.Max(20, _lastTargetKb);
            SetTargetKb(kb, fromSlider: false);
        }

        long targetBytes = kb * 1024L;
        bool keepRes = _keepResCheck.IsChecked == true;
        _running = true;
        _runBtn.IsEnabled = false;
        _runBtn.Opacity = 0.5;
        _kbBox.IsEnabled = false;
        _slider.IsEnabled = false;
        _keepResCheck.IsEnabled = false;
        _runBtnText.Text = L.T("Memproses...");
        ShowProgress(L.T("Mengompres..."));

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            bool usedRaster = false;
            string imgMethod = null;
            string outPath = await Task.Run(() =>
            {
                return _isPdf
                    ? FileCompress.PdfToTarget(_src, targetBytes, token, msg => Dispatcher.BeginInvoke(() => { if (_running) ShowProgress(msg); }), out usedRaster)
                    : FileCompress.ImageToTarget(_src, targetBytes, token, keepRes, out imgMethod);
            }, token);

            if (string.IsNullOrEmpty(outPath) || !File.Exists(outPath))
            {
                ShowError(L.T("Kompresi gagal."));
                return;
            }

            long outBytes = new FileInfo(outPath).Length;
            Shelf.Add(outPath);
            string modeNote = _isPdf
                ? (usedRaster ? " · Teks jadi gambar" : " · Teks tetap tajam")
                : (!string.IsNullOrEmpty(imgMethod) ? $" · {imgMethod}" : "");
            string resultMsg = L.F("Dikompres: {0} -> {1}", FileCompress.FmtSize(_srcBytes), FileCompress.FmtSize(outBytes)) + modeNote;
            ShowProgress(resultMsg);
            IslandWindow.Instance?.Alert(resultMsg, 6);
            _running = false;
            Close();
        }
        catch (OperationCanceledException)
        {
            ShowError(L.T("Kompresi dibatalkan."));
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ShowError(ex.Message);
        }
        finally
        {
            _running = false;
            _runBtn.IsEnabled = true;
            _runBtn.Opacity = 1.0;
            _kbBox.IsEnabled = true;
            _slider.IsEnabled = true;
            _keepResCheck.IsEnabled = true;
            _runBtnText.Text = L.T("Kompres");
        }
    }
}
