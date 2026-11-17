using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ZaxLet;

static class WorkerRows
{
    public static void AddWorkerRows(StackPanel host, List<WorkerRow> rows, int max)
    {
        if (rows == null || rows.Count == 0 || max <= 0) return;

        var now = DateTimeOffset.Now;
        foreach (var row in rows.Take(max))
        {
            var w = row.Info;
            var line = UI.Cols(0, -1, 0);
            line.Margin = new Thickness(0, 3, 0, 0);

            var dot = new Ellipse
            {
                Width = 6, Height = 6, Fill = StateColor(row.State),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
            };
            line.Children.Add(dot);

            var left = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var label = string.IsNullOrEmpty(w.Repo) ? w.Name : $"{w.Repo} · {w.Name}";
            left.Inlines.Add(new Run(label) { FontSize = 11, Foreground = UI.Fg, FontWeight = FontWeights.SemiBold });
            var phase = row.State switch
            {
                "SELESAI" => L.T("selesai"),
                "MATI" => L.T("mati"),
                _ => w.Phase,
            };
            if (!string.IsNullOrEmpty(phase))
                left.Inlines.Add(new Run("  " + phase) { FontSize = 10.5, Foreground = UI.Dim });
            line.Children.Add(left.At(0, 1));

            var right = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            if (w.Started != DateTimeOffset.MinValue)
            {
                var end = row.State == "BERJALAN" ? now : (w.Updated != DateTimeOffset.MinValue ? w.Updated : now);
                var dur = end - w.Started;
                if (dur < TimeSpan.Zero) dur = TimeSpan.Zero;
                right.Inlines.Add(new Run(UI.Span(dur)) { FontSize = 10.5, Foreground = UI.Dim });
            }
            long tok = AiTaskParse.Tokens(w.Detail);
            if (tok > 0)
                right.Inlines.Add(new Run((right.Inlines.Count > 0 ? " · " : "") + UI.Tok(tok)) { FontSize = 10.5, Foreground = UI.Dim });
            var verdict = AiTaskParse.Verdict(w.Detail);
            if (verdict != null)
            {
                var vBrush = verdict == "OK" ? UI.Good : UI.Warn;
                right.Inlines.Add(new Run((right.Inlines.Count > 0 ? " · " : "") + $"VONIS: {verdict}") { FontSize = 10, Foreground = vBrush, FontWeight = FontWeights.SemiBold });
            }
            line.Children.Add(right.At(0, 2));
            host.Children.Add(line);

            if (row.State == "BERJALAN")
            {
                int stage = AiTaskParse.Stage(w.Phase);
                if (stage > 0)
                {
                    var bar = new Bar(2) { Margin = new Thickness(11, 1, 0, 0) };
                    bar.Set(stage / 4.0, UI.Accent);
                    host.Children.Add(bar);
                }
            }
        }
    }

    static Brush StateColor(string state) => state switch
    {
        "BERJALAN" => UI.Accent,
        "SELESAI" => UI.Good,
        "MASALAH" => UI.Bad,
        _ => UI.Warn,
    };
}
