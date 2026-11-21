using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ZaxLet;

public sealed class ToolsView : TabView
{
    const string RecBgHex = "#CC9B1C24";

    public override string Key => "tools";
    public override string Glyph => "";
    public override string Label => "Tangkap";
    public override double H => 210;

    readonly Action _paintMic;

    public ToolsView()
    {
        var root = UI.Rows(0, -1);
        var head = UI.Cols(-1, 0);
        head.Margin = new Thickness(0, 0, 0, 10);
        head.Children.Add(UI.T("Tangkap layar", 15, null, true));

        var micRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var micIcon = UI.Icon("", 13, UI.Dim);
        micIcon.Margin = new Thickness(0, 0, 5, 0);
        var micLabel = UI.T("Mikrofon", 12, UI.Dim);
        micLabel.Margin = new Thickness(0, 0, 8, 0);
        var knob = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var track = new Border
        {
            Width = 40, Height = 22, CornerRadius = new CornerRadius(11), Cursor = Cursors.Hand,
            Padding = new Thickness(3, 0, 3, 0), Child = knob, Focusable = true,
            ToolTip = L.T("Rekam suara mikrofon bersama layar (sesi ini)")
        };
        _paintMic = () =>
        {
            bool on = CaptureService.RecMic;
            track.Background = on ? UI.Accent : UI.CardOn;
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        };
        void ToggleMic() { CaptureService.RecMic = !CaptureService.RecMic; _paintMic(); }
        track.MouseLeftButtonUp += (_, _) => ToggleMic();
        track.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { e.Handled = true; ToggleMic(); } };
        _paintMic();
        micRow.Children.Add(micIcon);
        micRow.Children.Add(micLabel);
        micRow.Children.Add(track);
        head.Children.Add(micRow.At(0, 1));
        root.Children.Add(head);

        var recBg = UI.B(RecBgHex);
        var row = new UniformGrid { Rows = 2, Columns = 4 };
        row.Children.Add(Tile("", "Layar penuh", CaptureService.Full, UI.Card));
        row.Children.Add(Tile("", "Area", CaptureService.Area, UI.Card));
        row.Children.Add(Tile("", "Jendela", CaptureService.Window, UI.Card));
        row.Children.Add(Tile("", "Sematkan area", CaptureService.PinArea, UI.Card));
        row.Children.Add(Tile("", "OCR area", ScreenOcr.PickAndCopy, UI.Card));
        row.Children.Add(Tile("", "Pipet warna", ColorPick.Pick, UI.Card));
        row.Children.Add(Tile("", "Rekam layar", CaptureService.ToggleRecord, recBg));
        row.Children.Add(Tile("", "Rekam area", CaptureService.ToggleRecordArea, recBg));
        root.Children.Add(row.At(1));
        Children.Add(root);
    }

    public override void OnShow() => _paintMic();

    static UIElement Tile(string glyph, string label, Action click, Brush bg)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(UI.Icon(glyph, 22));
        var t = UI.T(label, 12.5, null, true);
        t.Margin = new Thickness(0, 8, 0, 0);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(t);
        var b = UI.Btn(sp, click, bg, 14, new Thickness(6, 11, 6, 11));
        b.Margin = new Thickness(4);
        return b;
    }
}
