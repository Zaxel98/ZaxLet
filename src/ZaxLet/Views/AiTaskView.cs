using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ZaxLet;

public sealed class AiTaskView : TabView
{
    public override string Key => "aitask";
    public override string Glyph => "";
    public override string Label => "AI Task";
    public override double H => 188;

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    readonly StackPanel _claude = new(), _workers = new();
    readonly Border _sep = new() { Width = 1, Background = UI.Track, Margin = new Thickness(0, 2, 0, 2) };
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _busy;
    ClaudeTask _claudeTask;
    List<WorkerRow> _workerRows = new();

    public AiTaskView()
    {
        var g = UI.Cols(-1, 1, -1);
        g.Children.Add(_claude);
        g.Children.Add(_sep.At(0, 1));
        g.Children.Add(_workers.At(0, 2));
        _claude.Margin = new Thickness(0, 0, 14, 0);
        _workers.Margin = new Thickness(14, 0, 0, 0);
        Children.Add(g);
        _t.Tick += (_, _) => _ = RefreshAsync();
    }

    public override void OnShow()
    {
        _t.Start();
        Render();
        _ = RefreshAsync();
    }

    public override void OnHide() => _t.Stop();

    async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var workersDir = System.IO.Path.Combine(Home, ".config", "ponytail", "workers");
            var claudeDir = System.IO.Path.Combine(UsageService.ClaudeDir, "projects");
            var (claude, workers) = await Task.Run(() => (
                AiTaskScan.Claude(claudeDir, DateTime.Now),
                AiTaskScan.Workers(workersDir, DateTimeOffset.Now, AiTaskScan.PidAlive)
            ));
            _claudeTask = claude;
            _workerRows = workers;
            _ = Dispatcher.BeginInvoke(Render);
        }
        finally { _busy = false; }
    }

    void Render()
    {
        bool showClaude = Settings.Cur.ClaudeOn;
        _claude.Visibility = showClaude ? Visibility.Visible : Visibility.Collapsed;
        _sep.Visibility = showClaude ? Visibility.Visible : Visibility.Collapsed;
        _workers.Margin = showClaude ? new Thickness(14, 0, 0, 0) : new Thickness(0);

        if (showClaude) FillClaude();
        FillWorkers();
    }

    void FillClaude()
    {
        _claude.Children.Clear();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var logo = (FrameworkElement)Icons.ClaudeLogo(20);
        logo.Margin = new Thickness(0, 0, 8, 0);
        logo.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(logo);
        head.Children.Add(UI.T("Claude", 15, null, true));
        if (_claudeTask is { OtherLive: > 0 })
            head.Children.Add(Badge($"+{_claudeTask.OtherLive} {L.T("sesi")}"));
        _claude.Children.Add(head);

        if (_claudeTask == null || _claudeTask.Steps.Count == 0)
        {
            var empty = UI.T("tidak ada rencana aktif", 12, UI.Dim);
            empty.Margin = new Thickness(0, 12, 0, 0);
            empty.TextWrapping = TextWrapping.Wrap;
            _claude.Children.Add(empty);
            return;
        }

        var active = _claudeTask.Steps.FirstOrDefault(s => s.State == StepState.Active);
        var title = active != null
            ? UI.T(active.Text, 12.5, UI.Fg, true)
            : UI.T("selesai", 12.5, UI.Dim);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.Margin = new Thickness(0, 8, 0, 0);
        _claude.Children.Add(title);

        int done = _claudeTask.Steps.Count(s => s.State == StepState.Done);
        int total = _claudeTask.Steps.Count;
        var barRow = UI.Cols(-1, 0);
        barRow.Margin = new Thickness(0, 5, 0, 4);
        var bar = new Bar(4) { VerticalAlignment = VerticalAlignment.Center };
        bar.Set(total > 0 ? (double)done / total : 0, _claudeTask.Live ? UI.Accent : UI.Good);
        barRow.Children.Add(bar);
        var cnt = UI.T($"{done}/{total}", 11, UI.Dim);
        cnt.Margin = new Thickness(8, 0, 0, 0);
        barRow.Children.Add(cnt.At(0, 1));
        _claude.Children.Add(barRow);

        foreach (var p in _claudeTask.Steps.Where(s => s.State == StepState.Pending).Take(3))
        {
            var item = UI.T("· " + p.Text, 11, UI.Dim);
            item.TextTrimming = TextTrimming.CharacterEllipsis;
            item.Margin = new Thickness(0, 3, 0, 0);
            _claude.Children.Add(item);
        }
    }

    void FillWorkers()
    {
        _workers.Children.Clear();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var logo = (FrameworkElement)Icons.AntigravityLogo(20);
        logo.Margin = new Thickness(0, 0, 8, 0);
        logo.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(logo);
        head.Children.Add(UI.T("Worker", 15, null, true));
        _workers.Children.Add(head);

        if (_workerRows == null || _workerRows.Count == 0)
        {
            var empty = UI.T("belum ada worker", 12, UI.Dim);
            empty.Margin = new Thickness(0, 12, 0, 0);
            empty.TextWrapping = TextWrapping.Wrap;
            _workers.Children.Add(empty);
            return;
        }

        var now = DateTimeOffset.Now;
        foreach (var row in _workerRows.Take(4))
        {
            var w = row.Info;
            var line = UI.Cols(0, -1, 0);
            line.Margin = new Thickness(0, 4, 0, 0);

            var dot = new Ellipse
            {
                Width = 7, Height = 7, Fill = StateColor(row.State),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            line.Children.Add(dot);

            var left = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var label = string.IsNullOrEmpty(w.Repo) ? w.Name : $"{w.Repo} · {w.Name}";
            left.Inlines.Add(new Run(label) { FontSize = 11.5, Foreground = UI.Fg, FontWeight = FontWeights.SemiBold });
            var phase = row.State switch
            {
                "SELESAI" => L.T("selesai"),
                "MATI" => L.T("mati"),
                _ => w.Phase,
            };
            if (!string.IsNullOrEmpty(phase))
                left.Inlines.Add(new Run("  " + phase) { FontSize = 11, Foreground = UI.Dim });
            line.Children.Add(left.At(0, 1));

            var right = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            if (w.Started != DateTimeOffset.MinValue)
            {
                var end = row.State == "BERJALAN" ? now : (w.Updated != DateTimeOffset.MinValue ? w.Updated : now);
                var dur = end - w.Started;
                if (dur < TimeSpan.Zero) dur = TimeSpan.Zero;
                right.Inlines.Add(new Run(UI.Span(dur)) { FontSize = 11, Foreground = UI.Dim });
            }
            long tok = AiTaskParse.Tokens(w.Detail);
            if (tok > 0)
                right.Inlines.Add(new Run((right.Inlines.Count > 0 ? " · " : "") + UI.Tok(tok)) { FontSize = 11, Foreground = UI.Dim });
            var verdict = AiTaskParse.Verdict(w.Detail);
            if (verdict != null)
            {
                var vBrush = verdict == "OK" ? UI.Good : UI.Warn;
                right.Inlines.Add(new Run((right.Inlines.Count > 0 ? " · " : "") + $"VONIS: {verdict}") { FontSize = 10.5, Foreground = vBrush, FontWeight = FontWeights.SemiBold });
            }
            line.Children.Add(right.At(0, 2));
            _workers.Children.Add(line);

            if (row.State == "BERJALAN")
            {
                int stage = AiTaskParse.Stage(w.Phase);
                if (stage > 0)
                {
                    var bar = new Bar(3) { Margin = new Thickness(13, 2, 0, 1) };
                    bar.Set(stage / 4.0, UI.Accent);
                    _workers.Children.Add(bar);
                }
            }
        }

        if (_workerRows.Count > 4)
        {
            var more = UI.T($"+{_workerRows.Count - 4} {L.T("lainnya")}", 10.5, UI.Faint);
            more.Margin = new Thickness(13, 3, 0, 0);
            _workers.Children.Add(more);
        }
    }

    static Brush StateColor(string state) => state switch
    {
        "BERJALAN" => UI.Accent,
        "SELESAI" => UI.Good,
        "MASALAH" => UI.Bad,
        _ => UI.Warn,
    };

    static Border Badge(string text) => new()
    {
        BorderBrush = UI.Faint, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
        Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 1, 0, 0),
        Child = UI.T(text, 10.5, UI.Dim)
    };
}
