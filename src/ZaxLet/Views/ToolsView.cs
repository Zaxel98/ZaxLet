using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ZaxLet;

public sealed class ToolsView : TabView
{
    public override string Key => "tools";
    public override string Glyph => "";
    public override string Label => "Tangkap";
    public override double H => 140;

    public ToolsView()
    {
        var root = UI.Rows(0, -1);
        var head = UI.Cols(-1, 0);
        head.Margin = new Thickness(0, 0, 0, 10);
        head.Children.Add(UI.T("Tangkap layar", 15, null, true));
        root.Children.Add(head);

        var row = new UniformGrid { Rows = 1, Columns = 4 };
        row.Children.Add(Tile("", "Layar penuh", CaptureService.Full, UI.Card));
        row.Children.Add(Tile("", "Area", CaptureService.Area, UI.Card));
        row.Children.Add(Tile("", "Jendela", CaptureService.Window, UI.Card));
        row.Children.Add(Tile("", "Rekam layar", () => { CaptureService.ToggleRecord(); }, UI.B("#CC9B1C24")));
        root.Children.Add(row.At(1));
        Children.Add(root);
    }

    static UIElement Tile(string glyph, string label, Action click, Brush bg)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(UI.Icon(glyph, 22));
        var t = UI.T(label, 12.5, null, true);
        t.Margin = new Thickness(0, 8, 0, 0);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(t);
        var b = UI.Btn(sp, click, bg, 14, new Thickness(6, 11, 6, 11));
        b.Margin = new Thickness(4, 0, 4, 0);
        return b;
    }
}
