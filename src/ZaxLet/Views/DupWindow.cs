using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>
/// Duplicate file scan results window with safe move to Recycle Bin.
/// </summary>
public sealed class DupWindow : Window
{
    static DupWindow _cur;

    static readonly Brush
        WindowBg = UI.WinBg,
        CardBg = UI.WinCard,
        TextFg = UI.Fg,
        CaptionFg = UI.Dim;

    static TextBlock RawText(string text, double size, Brush fg, bool bold = false)
    {
        var t = UI.T("", size, fg, bold);
        t.Text = text;
        return t;
    }

    /// <summary>Dark checkbox: 16px thin-bordered box, filled with accent + checkmark when checked (Windows default: white box that clashes with dark windows).</summary>
    static ControlTemplate DarkCheckTemplate()
    {
        var box = new FrameworkElementFactory(typeof(Border), "box");
        box.SetValue(FrameworkElement.WidthProperty, 16.0);
        box.SetValue(FrameworkElement.HeightProperty, 16.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        box.SetValue(Border.BorderBrushProperty, UI.B("#66FFFFFF"));
        box.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var tick = new FrameworkElementFactory(typeof(TextBlock), "tick");
        tick.SetValue(TextBlock.TextProperty, "");
        tick.SetValue(TextBlock.FontFamilyProperty, UI.IconFont);
        tick.SetValue(TextBlock.FontSizeProperty, 11.0);
        tick.SetValue(TextBlock.ForegroundProperty, Brushes.Black);
        tick.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        tick.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        tick.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        box.AppendChild(tick);
        var tpl = new ControlTemplate(typeof(CheckBox)) { VisualTree = box };
        var on = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BackgroundProperty, UI.Accent, "box"));
        on.Setters.Add(new Setter(Border.BorderBrushProperty, UI.Accent, "box"));
        on.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "tick"));
        tpl.Triggers.Add(on);
        var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "box"));
        tpl.Triggers.Add(off);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.White, "box"));
        tpl.Triggers.Add(focus);
        return tpl;
    }

    readonly ControlTemplate _checkTpl = DarkCheckTemplate();

    sealed class FileEntry
    {
        public DupFile File;
        public CheckBox Box;
        public FrameworkElement KeepBadge;
        public GroupState Group;
    }

    sealed class GroupState
    {
        public DupGroup Group;
        public Border Card;
        public List<FileEntry> Entries = new();
    }

    readonly TextBlock _folderLabel, _statusLabel, _savingsLabel, _recycleBtnText;
    readonly Border _cancelBtn, _recycleBtn;
    readonly StackPanel _groupsPanel = new();
    readonly List<GroupState> _groups = new();

    const int RenderChunkSize = 20;
    const double PathColW = 300;   // approximate path column width (680 window - padding - check/Keep/size/date columns)

    string _folder;
    CancellationTokenSource _cts;
    bool _scanning, _recycling, _rendering, _closed;
    int _scanId, _renderVersion;

    /// <summary>For <c>--ui-test</c>: populate window (without showing) with sample groups, then hand off its content for PNG rendering.</summary>
    internal static FrameworkElement Preview(string baseFolder, List<DupGroup> groups)
    {
        var w = new DupWindow(null) { _folder = baseFolder };
        w._folderLabel.Text = baseFolder;
        w.RenderGroups(groups);
        w._statusLabel.Foreground = UI.Good;
        w._statusLabel.Text = $"Ditemukan {groups.Count} grup file duplikat.";
        var c = (FrameworkElement)w.Content;
        w.Content = null;
        return c;
    }

    public static void Show(string folder)
    {
        if (_cur != null) _cur.Close();
        _cur = new DupWindow(folder);
        _cur.Show();
        _cur.Activate();
    }

    DupWindow(string folder)
    {
        _folder = folder;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 680;
        Height = 520;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = Math.Max(40, (SystemParameters.PrimaryScreenHeight - Height) / 2);

        IslandWindow.Instance?.KeepOpen(true);

        var root = UI.Rows(0, -1, 0);

        // Header (draggable to move window): title, status, and path in 3 Auto rows
        var head = UI.Cols(-1, 0, 0);
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.Margin = new Thickness(0, 0, 0, 10);
        head.Background = Brushes.Transparent;
        head.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };

        head.Children.Add(UI.T("Pencari file duplikat", 14.5, TextFg, true).At(0, 0));

        var statusRow = UI.Cols(-1, 0);
        statusRow.Margin = new Thickness(0, 3, 8, 0);
        _statusLabel = UI.T("", 12, CaptionFg);
        statusRow.Children.Add(_statusLabel);

        _cancelBtn = UI.Btn(UI.T("Batal", 11.5, TextFg), CancelScan, UI.CardOn, 8, new Thickness(10, 4, 10, 4));
        _cancelBtn.Visibility = Visibility.Collapsed;
        _cancelBtn.Margin = new Thickness(8, 0, 0, 0);
        _cancelBtn.MouseLeftButtonDown += (_, e) => e.Handled = true;
        statusRow.Children.Add(_cancelBtn.At(0, 1));
        head.Children.Add(statusRow.At(1, 0));

        _folderLabel = RawText(folder ?? "", 11.5, CaptionFg);
        _folderLabel.Margin = new Thickness(0, 3, 8, 0);
        _folderLabel.ToolTip = folder ?? "";
        head.Children.Add(_folderLabel.At(2, 0));

        var pickBtn = UI.Btn(UI.T("Pilih folder", 11.5, TextFg), PickFolder, UI.CardOn, 8, new Thickness(10, 5, 10, 5));
        pickBtn.Margin = new Thickness(0, 0, 6, 0);
        pickBtn.VerticalAlignment = VerticalAlignment.Center;
        pickBtn.MouseLeftButtonDown += (_, e) => e.Handled = true;
        Grid.SetRowSpan(pickBtn, 3);
        head.Children.Add(pickBtn.At(0, 1));

        var closeBtn = UI.IconBtn("", Close, 12, CardBg);
        closeBtn.Padding = new Thickness(6);
        closeBtn.VerticalAlignment = VerticalAlignment.Center;
        closeBtn.MouseLeftButtonDown += (_, e) => e.Handled = true;
        Grid.SetRowSpan(closeBtn, 3);
        head.Children.Add(closeBtn.At(0, 2));
        root.Children.Add(head.At(0));

        // Duplicate group list
        var listBox = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _groupsPanel
            }
        };
        root.Children.Add(listBox.At(1));

        // Footer: total savings + action buttons
        var foot = UI.Cols(-1, 0, 0);
        foot.Margin = new Thickness(0, 10, 0, 0);
        _savingsLabel = UI.T("Bisa dihemat: 0 KB", 12.5, UI.Accent, true);
        foot.Children.Add(_savingsLabel);

        _recycleBtnText = UI.T("Pindahkan yang dicentang ke Recycle Bin", 12, Brushes.Black, true);
        _recycleBtn = UI.Btn(_recycleBtnText, RecycleSelected, UI.Accent, 8, new Thickness(12, 6, 12, 6));
        _recycleBtn.Margin = new Thickness(0, 0, 8, 0);
        SetRecycleEnabled(false);
        foot.Children.Add(_recycleBtn.At(0, 1));

        var closeFootBtn = UI.Btn(UI.T("Tutup", 12, TextFg), Close, CardBg, 8, new Thickness(12, 6, 12, 6));
        foot.Children.Add(closeFootBtn.At(0, 2));
        root.Children.Add(foot.At(2));

        Content = new Border
        {
            Background = WindowBg,
            BorderBrush = UI.Track,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 14, 16, 14),
            Child = root
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };

        Closed += (_, _) =>
        {
            _closed = true;
            _renderVersion++;
            _rendering = false;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _cts?.Dispose();
            _cts = null;
            IslandWindow.Instance?.KeepOpen(false);
            if (_cur == this) _cur = null;
        };

        if (!string.IsNullOrWhiteSpace(folder))
            Loaded += (_, _) => StartScan(folder);
    }

    void PickFolder()
    {
        if (_recycling) return;
        if (_scanning) CancelScan();
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L.T("Pilih folder untuk mencari file duplikat")
        };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            StartScan(dlg.FolderName);
    }

    void CancelScan()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _statusLabel.Foreground = UI.Warn;
        _statusLabel.Text = L.T("Membatalkan...");
    }

    async void StartScan(string folder)
    {
        if (_recycling || _closed) return;
        int myScanId = ++_scanId;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts?.Dispose();
        _cts = null;

        _folder = folder;
        _folderLabel.Text = folder;
        _folderLabel.ToolTip = folder;
        _renderVersion++;
        _rendering = false;
        _groupsPanel.Children.Clear();
        _groups.Clear();
        UpdateSavings();

        string safeFolder;
        try
        {
            safeFolder = DupFinder.ValidateFolder(folder);
        }
        catch (Exception ex)
        {
            _scanning = false;
            _cancelBtn.Visibility = Visibility.Collapsed;
            _statusLabel.Foreground = UI.Bad;
            _statusLabel.Text = ex.Message;
            UpdateSavings();
            return;
        }

        _scanning = true;
        _cancelBtn.Visibility = Visibility.Visible;
        _statusLabel.Foreground = UI.Fg;
        _statusLabel.Text = L.T("Memulai pemindaian...");
        SetRecycleEnabled(false);

        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        var progress = new Progress<string>(msg =>
        {
            if (!_closed && _scanId == myScanId && _scanning) _statusLabel.Text = msg;
        });

        try
        {
            var found = await Task.Run(() => DupFinder.Scan(safeFolder, progress, token), token);
            if (_closed || _scanId != myScanId) return;
            RenderGroups(found);
            _statusLabel.Foreground = found.Count > 0 ? UI.Good : UI.Dim;
            _statusLabel.Text = found.Count > 0
                ? L.F("Ditemukan {0:N0} grup file duplikat. Klik dua kali baris untuk membuka lokasi di Explorer.", found.Count)
                : L.T("Tidak ada file duplikat (>= 1 KB) yang ditemukan.");
        }
        catch (OperationCanceledException)
        {
            if (_closed || _scanId != myScanId) return;
            _statusLabel.Foreground = UI.Warn;
            _statusLabel.Text = L.T("Pemindaian dibatalkan.");
        }
        catch (Exception ex)
        {
            if (_closed || _scanId != myScanId) return;
            App.Log(ex);
            _statusLabel.Foreground = UI.Bad;
            _statusLabel.Text = ex.Message;
        }
        finally
        {
            if (!_closed && _scanId == myScanId)
            {
                _scanning = false;
                _cancelBtn.Visibility = Visibility.Collapsed;
                UpdateSavings();
            }
        }
    }

    void RenderGroups(List<DupGroup> found)
    {
        int version = ++_renderVersion;
        _groupsPanel.Children.Clear();
        _groups.Clear();

        if (found == null || found.Count == 0)
        {
            _rendering = false;
            UpdateSavings();
            return;
        }

        _rendering = true;
        UpdateSavings();

        int index = 0;
        void RenderChunk()
        {
            if (_closed || version != _renderVersion) return;

            int end = Math.Min(index + RenderChunkSize, found.Count);
            for (; index < end; index++)
            {
                var gs = BuildGroupCard(found[index]);
                _groups.Add(gs);
                _groupsPanel.Children.Add(gs.Card);
                EnforceKeepOne(gs);
            }

            if (index < found.Count)
            {
                UpdateSavings();
                Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)RenderChunk);
            }
            else
            {
                _rendering = false;
                UpdateSavings();
            }
        }

        RenderChunk();
    }

    GroupState BuildGroupCard(DupGroup g)
    {
        var gs = new GroupState { Group = g };
        var stack = new StackPanel();

        var hdrGrid = UI.Cols(-1, 0);
        hdrGrid.Margin = new Thickness(0, 0, 0, 6);
        var hdrLeft = RawText(L.F("{0} berkas · {1} per berkas", g.Files.Count, DupFinder.FmtSize(g.Size)), 12.5, CaptionFg, true);
        var hdrRight = RawText(L.F("{0} duplikat", DupFinder.FmtSize((g.Files.Count - 1) * g.Size)), 12.5, CaptionFg);
        hdrRight.Margin = new Thickness(8, 0, 0, 0);
        hdrGrid.Children.Add(hdrLeft);
        hdrGrid.Children.Add(hdrRight.At(0, 1));
        stack.Children.Add(hdrGrid);

        for (int i = 0; i < g.Files.Count; i++)
        {
            var file = g.Files[i];
            bool newest = i == 0;   // DupFinder sorts newest first

            var cb = new CheckBox
            {
                IsChecked = !newest,
                Template = _checkTpl,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // "Simpan" badge as a chip (previously 10.5px accent text that was barely readable)
            var keepBadge = new Border
            {
                Background = UI.AccentBg, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Child = UI.T("Simpan", 10.5, UI.Accent, true),
                Visibility = newest ? Visibility.Visible : Visibility.Collapsed
            };

            var entry = new FileEntry
            {
                File = file,
                Box = cb,
                KeepBadge = keepBadge,
                Group = gs
            };
            gs.Entries.Add(entry);

            cb.Checked += (_, _) => OnEntryToggled(gs);
            cb.Unchecked += (_, _) => OnEntryToggled(gs);

            var rowGrid = UI.Cols(24, 60, -1, 80, 130);
            rowGrid.Children.Add(cb);
            rowGrid.Children.Add(keepBadge.At(0, 1));

            // folder (dim, truncated at end) + filename (bright, always visible): long paths no longer hide the filename
            var rel = ShortenPath(file.Path, _folder);
            var dirPart = Path.GetDirectoryName(rel);
            var pathBox = UI.Cols(0, 0);
            pathBox.Margin = new Thickness(0, 0, 8, 0);
            pathBox.ToolTip = file.Path;
            var nameTxt = RawText(Path.GetFileName(rel), 11.5, TextFg);
            double nameW = Math.Min(PathColW, UI.TextWidth(nameTxt.Text, 11.5, FontWeights.Normal) + 2);
            nameTxt.MaxWidth = PathColW;
            if (!string.IsNullOrEmpty(dirPart))
            {
                // fixed path column width: folder fills remaining space after filename, so both sit side by side and only folder is truncated
                var dirTxt = RawText(dirPart + Path.DirectorySeparatorChar, 11.5, CaptionFg);
                dirTxt.MaxWidth = Math.Max(36, PathColW - nameW - 4);
                pathBox.Children.Add(dirTxt);
            }
            nameTxt.HorizontalAlignment = HorizontalAlignment.Left;
            pathBox.Children.Add(nameTxt.At(0, 1));
            rowGrid.Children.Add(pathBox.At(0, 2));

            var sizeTxt = RawText(DupFinder.FmtSize(file.Size), 11, CaptionFg);
            sizeTxt.HorizontalAlignment = HorizontalAlignment.Right;
            sizeTxt.TextAlignment = TextAlignment.Right;
            rowGrid.Children.Add(sizeTxt.At(0, 3));

            var dateTxt = RawText($"{file.Modified:yyyy-MM-dd HH:mm}", 11, CaptionFg);
            dateTxt.HorizontalAlignment = HorizontalAlignment.Right;
            dateTxt.TextAlignment = TextAlignment.Right;
            rowGrid.Children.Add(dateTxt.At(0, 4));

            var rowBorder = new Border
            {
                Height = 32,
                Background = Brushes.Transparent,
                Padding = new Thickness(4, 0, 4, 0),
                Child = rowGrid,
                ToolTip = file.Path + L.T("\nKlik dua kali untuk membuka lokasi di Explorer")
            };
            rowBorder.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) OpenInExplorer(file.Path);
            };

            stack.Children.Add(rowBorder);
        }

        gs.Card = new Border
        {
            Background = CardBg,
            BorderBrush = UI.Track,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack
        };
        return gs;
    }

    static string ShortenPath(string fullPath, string baseFolder)
    {
        if (string.IsNullOrEmpty(baseFolder)) return fullPath;
        try
        {
            var rel = Path.GetRelativePath(baseFolder, fullPath);
            return rel.StartsWith("..", StringComparison.Ordinal) ? fullPath : rel;
        }
        catch { return fullPath; }
    }

    void OnEntryToggled(GroupState gs)
    {
        EnforceKeepOne(gs);
        UpdateSavings();
    }

    static void EnforceKeepOne(GroupState gs)
    {
        var kept = gs.Entries.Where(e => e.Box.IsChecked != true).ToList();
        if (kept.Count == 0 && gs.Entries.Count > 0)
        {
            // Ensure at least one file is always kept
            gs.Entries[0].Box.IsChecked = false;
            kept.Add(gs.Entries[0]);
        }

        bool lockLast = kept.Count == 1;
        foreach (var e in gs.Entries)
        {
            bool isKept = e.Box.IsChecked != true;
            e.KeepBadge.Visibility = isKept ? Visibility.Visible : Visibility.Collapsed;
            e.Box.IsEnabled = !(lockLast && isKept);
        }
    }

    void UpdateSavings()
    {
        long bytes = 0;
        int count = 0;
        foreach (var gs in _groups)
        {
            foreach (var e in gs.Entries)
            {
                if (e.Box.IsChecked == true)
                {
                    bytes += e.File.Size;
                    count++;
                }
            }
        }
        _savingsLabel.Text = L.F("Bisa dihemat: {0}", DupFinder.FmtSize(bytes));
        _recycleBtnText.Text = count > 0
            ? L.F("Pindahkan yang dicentang ke Recycle Bin ({0:N0})", count)
            : L.T("Pindahkan yang dicentang ke Recycle Bin");
        SetRecycleEnabled(!_scanning && !_recycling && !_rendering && count > 0);
    }

    void SetRecycleEnabled(bool enabled)
    {
        _recycleBtn.IsEnabled = enabled;
        _recycleBtn.Opacity = enabled ? 1.0 : 0.4;
    }

    async void RecycleSelected()
    {
        if (_scanning || _recycling || _rendering) return;
        var selected = _groups.SelectMany(g => g.Entries).Where(e => e.Box.IsChecked == true).ToList();
        if (selected.Count == 0) return;

        long totalBytes = selected.Sum(e => e.File.Size);
        var confirm = MessageBox.Show(
            this,
            L.F("Pindahkan {0:N0} berkas ({1}) ke Recycle Bin?\nBerkas yang dipindahkan masih bisa dipulihkan dari Recycle Bin.", selected.Count, DupFinder.FmtSize(totalBytes)),
            L.T("Konfirmasi Recycle Bin"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _recycling = true;
        SetRecycleEnabled(false);
        _statusLabel.Foreground = UI.Fg;
        _statusLabel.Text = L.T("Memindahkan berkas ke Recycle Bin...");

        try
        {
            var paths = selected.Select(e => e.File.Path).ToList();
            var (moved, failed) = await Task.Run(() => DupFinder.MoveToRecycleBin(paths));
            if (_closed) return;
            var movedSet = new HashSet<string>(moved, StringComparer.OrdinalIgnoreCase);

            // Refresh remaining group list
            var remainingGroups = new List<DupGroup>();
            foreach (var gs in _groups)
            {
                var remFiles = gs.Entries.Where(e => !movedSet.Contains(e.File.Path)).Select(e => e.File).ToList();
                if (remFiles.Count >= 2)
                    remainingGroups.Add(new DupGroup(gs.Group.Size, gs.Group.Hash, remFiles));
            }
            RenderGroups(remainingGroups);

            if (failed.Count == 0)
            {
                _statusLabel.Foreground = UI.Good;
                _statusLabel.Text = L.F("{0:N0} berkas dipindahkan ke Recycle Bin.", moved.Count);
            }
            else
            {
                _statusLabel.Foreground = UI.Warn;
                _statusLabel.Text = L.F("{0:N0} dipindahkan, {1:N0} gagal (dilewati): {2}", moved.Count, failed.Count, failed[0].Error);
            }
        }
        catch (Exception ex)
        {
            if (_closed) return;
            App.Log(ex);
            _statusLabel.Foreground = UI.Bad;
            _statusLabel.Text = ex.Message;
        }
        finally
        {
            if (!_closed)
            {
                _recycling = false;
                UpdateSavings();
            }
        }
    }

    static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
