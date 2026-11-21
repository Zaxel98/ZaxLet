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

namespace ZaxLet;

/// <summary>
/// Jendela hasil pemindaian file duplikat dengan pemindahan aman ke Recycle Bin.
/// </summary>
public sealed class DupWindow : Window
{
    static DupWindow _cur;

    sealed class FileEntry
    {
        public DupFile File;
        public CheckBox Box;
        public TextBlock KeepBadge;
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

    string _folder;
    CancellationTokenSource _cts;
    bool _busy;

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

        var root = UI.Rows(0, 0, -1, 0);

        // Header (bisa diseret untuk memindahkan jendela)
        var head = UI.Cols(-1, 0, 0);
        head.Background = Brushes.Transparent;
        head.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };

        var titleStack = new StackPanel();
        titleStack.Children.Add(UI.T("Pencari file duplikat", 14.5, UI.Fg, true));
        _folderLabel = UI.T(folder ?? "", 11.5, UI.Dim);
        _folderLabel.Margin = new Thickness(0, 2, 8, 0);
        titleStack.Children.Add(_folderLabel);
        head.Children.Add(titleStack);

        var pickBtn = UI.Btn(UI.T("Pilih folder", 11.5, UI.Fg), PickFolder, UI.CardOn, 8, new Thickness(10, 5, 10, 5));
        pickBtn.Margin = new Thickness(0, 0, 6, 0);
        pickBtn.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(pickBtn.At(0, 1));

        var closeBtn = UI.IconBtn("", Close, 12, UI.Card);
        closeBtn.Padding = new Thickness(6);
        closeBtn.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(closeBtn.At(0, 2));
        root.Children.Add(head);

        // Baris progres + tombol batal
        var progRow = UI.Cols(-1, 0);
        progRow.Margin = new Thickness(0, 10, 0, 8);
        _statusLabel = UI.T("", 12, UI.Dim);
        _statusLabel.TextWrapping = TextWrapping.Wrap;
        _statusLabel.TextTrimming = TextTrimming.None;
        progRow.Children.Add(_statusLabel);

        _cancelBtn = UI.Btn(UI.T("Batal", 11.5, UI.Fg), CancelScan, UI.CardOn, 8, new Thickness(10, 4, 10, 4));
        _cancelBtn.Visibility = Visibility.Collapsed;
        _cancelBtn.Margin = new Thickness(8, 0, 0, 0);
        progRow.Children.Add(_cancelBtn.At(0, 1));
        root.Children.Add(progRow);

        // Daftar grup duplikat
        var listBox = new Border
        {
            Background = UI.B("#12FFFFFF"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _groupsPanel
            }
        };
        root.Children.Add(listBox.At(2));

        // Footer: total hemat + tombol aksi
        var foot = UI.Cols(-1, 0, 0);
        foot.Margin = new Thickness(0, 10, 0, 0);
        _savingsLabel = UI.T("Bisa dihemat: 0 KB", 12.5, UI.Accent, true);
        foot.Children.Add(_savingsLabel);

        _recycleBtnText = UI.T("Pindahkan yang dicentang ke Recycle Bin", 12, Brushes.Black, true);
        _recycleBtn = UI.Btn(_recycleBtnText, RecycleSelected, UI.Accent, 8, new Thickness(12, 6, 12, 6));
        _recycleBtn.Margin = new Thickness(0, 0, 8, 0);
        SetRecycleEnabled(false);
        foot.Children.Add(_recycleBtn.At(0, 1));

        var closeFootBtn = UI.Btn(UI.T("Tutup", 12, UI.Fg), Close, UI.Card, 8, new Thickness(12, 6, 12, 6));
        foot.Children.Add(closeFootBtn.At(0, 2));
        root.Children.Add(foot.At(3));

        Content = new Border
        {
            Background = UI.B("#F20B0B0D"),
            BorderBrush = UI.Track,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 14, 16, 14),
            Child = root
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; if (_busy) CancelScan(); else Close(); }
        };

        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
            IslandWindow.Instance?.KeepOpen(false);
            if (_cur == this) _cur = null;
        };

        if (!string.IsNullOrWhiteSpace(folder))
            Loaded += (_, _) => StartScan(folder);
    }

    void PickFolder()
    {
        if (_busy) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L.T("Pilih folder untuk mencari file duplikat")
        };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            StartScan(dlg.FolderName);
    }

    void CancelScan()
    {
        _cts?.Cancel();
        _statusLabel.Foreground = UI.Warn;
        _statusLabel.Text = L.T("Membatalkan...");
    }

    async void StartScan(string folder)
    {
        if (_busy) return;
        _folder = folder;
        _folderLabel.Text = folder;
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
            _statusLabel.Foreground = UI.Bad;
            _statusLabel.Text = ex.Message;
            return;
        }

        _busy = true;
        _cancelBtn.Visibility = Visibility.Visible;
        _statusLabel.Foreground = UI.Fg;
        _statusLabel.Text = L.T("Memulai pemindaian...");
        SetRecycleEnabled(false);

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var progress = new Progress<string>(msg =>
        {
            if (_busy) _statusLabel.Text = msg;
        });

        try
        {
            var found = await Task.Run(() => DupFinder.Scan(safeFolder, progress, token), token);
            RenderGroups(found);
            _statusLabel.Foreground = found.Count > 0 ? UI.Good : UI.Dim;
            _statusLabel.Text = found.Count > 0
                ? L.F("Ditemukan {0:N0} grup file duplikat. Klik dua kali baris untuk membuka lokasi di Explorer.", found.Count)
                : L.T("Tidak ada file duplikat (>= 1 KB) yang ditemukan.");
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Foreground = UI.Warn;
            _statusLabel.Text = L.T("Pemindaian dibatalkan.");
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _statusLabel.Foreground = UI.Bad;
            _statusLabel.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            _cancelBtn.Visibility = Visibility.Collapsed;
            UpdateSavings();
        }
    }

    void RenderGroups(List<DupGroup> found)
    {
        _groupsPanel.Children.Clear();
        _groups.Clear();

        foreach (var g in found)
        {
            var gs = new GroupState { Group = g };
            var stack = new StackPanel();

            var hdr = UI.T(
                $"{g.Files.Count} berkas · {DupFinder.FmtSize(g.Size)} per berkas · {DupFinder.FmtSize((g.Files.Count - 1) * g.Size)} duplikat",
                12, UI.Fg, true);
            hdr.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(hdr);

            for (int i = 0; i < g.Files.Count; i++)
            {
                var file = g.Files[i];
                bool newest = i == 0;   // DupFinder mengurutkan terbaru lebih dulu

                var cb = new CheckBox
                {
                    IsChecked = !newest,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                var keepBadge = UI.T("Simpan", 10.5, UI.Good, true);
                keepBadge.Margin = new Thickness(0, 0, 6, 0);
                keepBadge.Visibility = newest ? Visibility.Visible : Visibility.Collapsed;

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

                var rowGrid = UI.Cols(0, 0, -1, 0);
                rowGrid.Children.Add(cb);
                rowGrid.Children.Add(keepBadge.At(0, 1));

                var pathTxt = UI.T(ShortenPath(file.Path, _folder), 11.5, UI.Fg);
                pathTxt.ToolTip = file.Path;
                rowGrid.Children.Add(pathTxt.At(0, 2));

                var metaTxt = UI.T($"{DupFinder.FmtSize(file.Size)} · {file.Modified:yyyy-MM-dd HH:mm}", 11, UI.Dim);
                metaTxt.Margin = new Thickness(10, 0, 0, 0);
                rowGrid.Children.Add(metaTxt.At(0, 3));

                var rowBorder = new Border
                {
                    Background = UI.Card,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 4),
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
                Background = UI.B("#14FFFFFF"),
                BorderBrush = UI.Track,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 6),
                Margin = new Thickness(0, 0, 0, 8),
                Child = stack
            };
            _groups.Add(gs);
            _groupsPanel.Children.Add(gs.Card);
            EnforceKeepOne(gs);
        }

        UpdateSavings();
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
            // Jaga agar minimal satu berkas selalu tersimpan
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
        SetRecycleEnabled(!_busy && count > 0);
    }

    void SetRecycleEnabled(bool enabled)
    {
        _recycleBtn.IsEnabled = enabled;
        _recycleBtn.Opacity = enabled ? 1.0 : 0.4;
    }

    async void RecycleSelected()
    {
        if (_busy) return;
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

        _busy = true;
        SetRecycleEnabled(false);
        _statusLabel.Foreground = UI.Fg;
        _statusLabel.Text = L.T("Memindahkan berkas ke Recycle Bin...");

        try
        {
            var paths = selected.Select(e => e.File.Path).ToList();
            var (moved, failed) = await Task.Run(() => DupFinder.MoveToRecycleBin(paths));
            var movedSet = new HashSet<string>(moved, StringComparer.OrdinalIgnoreCase);

            // Segarkan daftar grup yang tersisa
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
            App.Log(ex);
            _statusLabel.Foreground = UI.Bad;
            _statusLabel.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateSavings();
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
