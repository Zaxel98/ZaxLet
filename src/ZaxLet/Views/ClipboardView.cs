using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZaxLet;

/// <summary>Riwayat clipboard: klik satu baris untuk menyalinnya lagi, ikon pin untuk menyematkan, silang untuk menghapus.</summary>
public sealed class ClipboardView : TabView
{
    public override string Key => "clip";
    public override string Glyph => "";
    public override string Label => "Clipboard";
    public override bool CenterV => false;

    readonly StackPanel _list = new();
    readonly TextBlock _hint = UI.T("", 11, UI.Faint);
    bool _dirty;

    public ClipboardView()
    {
        var root = UI.Rows(0, -1);
        var head = UI.Cols(-1, 0);
        head.Children.Add(UI.T("Riwayat clipboard", 15, null, true));
        head.Children.Add(UI.Btn(UI.T("Hapus semua", 11.5, UI.Dim), ClipboardHistory.Clear, UI.Card, 9, new Thickness(9, 3, 9, 3)).At(0, 1));
        root.Children.Add(head);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list, Margin = new Thickness(0, 8, 0, 0) };
        root.Children.Add(scroll.At(1));
        Children.Add(root);
    }

    public override void OnShow() { ClipboardHistory.Changed += OnChanged; Render(); }
    public override void OnHide() => ClipboardHistory.Changed -= OnChanged;

    void OnChanged() { if (_dirty) return; _dirty = true; Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _dirty = false; Render(); }); }

    void Render()
    {
        _list.Children.Clear();
        if (!Settings.Cur.ClipboardOn || ClipboardHistory.Items.Count == 0)
        {
            var e = UI.T(Settings.Cur.ClipboardOn ? "Belum ada yang disalin. Teks yang kamu salin muncul di sini (hanya di memori)." : "Riwayat clipboard dimatikan di Pengaturan > Alat.", 12, UI.Dim);
            e.TextWrapping = TextWrapping.Wrap; e.Margin = new Thickness(0, 14, 0, 0);
            _list.Children.Add(e);
            return;
        }
        foreach (var it in ClipboardHistory.Items.ToArray())
        {
            var item = it;
            var row = UI.Cols(-1, 0, 0);
            row.Margin = new Thickness(0, 0, 0, 4);
            var text = UI.T(ClipboardHistory.OneLine(item.Text), 12.5, UI.Fg);
            var cell = new Border { Background = UI.Card, CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 6, 10, 6), Cursor = System.Windows.Input.Cursors.Hand, Child = text };
            cell.ToolTip = item.Text.Length > 400 ? item.Text[..400] + "…" : item.Text;
            cell.MouseLeftButtonUp += (_, _) => { ClipboardHistory.CopyBack(item); text.Text = L.T("Disalin ✓"); };
            row.Children.Add(cell);
            var pin = UI.IconBtn("", () => ClipboardHistory.TogglePin(item), 11, null);
            ((TextBlock)pin.Child).Foreground = item.Pinned ? UI.Accent : UI.Faint;
            row.Children.Add(pin.At(0, 1));
            row.Children.Add(UI.IconBtn("", () => ClipboardHistory.Remove(item), 11).At(0, 2));
            _list.Children.Add(row);
        }
    }
}
