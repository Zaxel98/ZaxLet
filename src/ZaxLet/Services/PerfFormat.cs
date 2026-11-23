using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZaxLet;

/// <summary>Pure part of the performance overlay (no WPF/sensors): selectable metrics list, nvidia-smi output parser, and line builder. Tested in tests/.</summary>
public static class PerfFormat
{
    /// <summary>Key, name in Settings, row group in overlay. Order = display order.</summary>
    public static readonly (string Key, string Label, string Group)[] Metrics =
    {
        ("fps", "FPS (game di depan)", "FPS"),
        ("cpu", "Beban CPU", "CPU"), ("cpuclk", "Clock CPU", "CPU"), ("cpucore", "Inti CPU tersibuk", "CPU"), ("cputemp", "Suhu CPU", "CPU"), ("cpupwr", "Daya CPU (paket)", "CPU"), ("cpuvolt", "Tegangan CPU", "CPU"),
        ("ram", "RAM terpakai", "RAM"),
        ("gpu", "Beban GPU", "GPU"), ("gpuclk", "Clock GPU", "GPU"), ("gpumem", "Clock memori GPU", "GPU"), ("gpumemutil", "Beban bus memori GPU", "GPU"), ("gputemp", "Suhu GPU", "GPU"),
        ("gpupwr", "Daya GPU", "GPU"), ("gpuvolt", "Tegangan GPU", "GPU"), ("gpufan", "Kipas GPU", "GPU"), ("gpustate", "Status daya GPU (P-state)", "GPU"), ("gputhr", "Pembatasan GPU (daya/suhu)", "GPU"),
        ("vram", "VRAM terpakai", "VRAM"),
        ("disk", "Disk baca/tulis", "DISK"), ("net", "Jaringan unduh/unggah", "NET"),
    };

    public const string DefaultItems = "cpu,cputemp,gpu,gputemp,vram,ram";

    public static HashSet<string> Items(string csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => Metrics.Any(m => m.Key == k)).ToHashSet();

    public static string WithItem(string csv, string key, bool on)
    {
        var set = Items(csv);
        if (on) set.Add(key); else set.Remove(key);
        return string.Join(",", Metrics.Where(m => set.Contains(m.Key)).Select(m => m.Key));
    }

    public sealed record Smi(double? Util, double? Gr, double? Mem, double? Temp, double? MemUsedMb, double? MemTotalMb, double? Power,
        double? MemUtil = null, string PState = null, double? Fan = null, string Throttle = null);

    /// <summary>Columns requested from nvidia-smi; order matches <see cref="ParseSmi"/>.</summary>
    public const string SmiQuery = "utilization.gpu,clocks.gr,clocks.mem,temperature.gpu,memory.used,memory.total,power.draw,utilization.memory,pstate,fan.speed,clocks_throttle_reasons.active";

    /// <summary>One line of nvidia-smi output (<see cref="SmiQuery"/>, csv,noheader,nounits). "[N/A]" columns become null (laptop GPUs often lack power/fan data).
    /// First seven columns are required; the rest are optional.</summary>
    public static Smi ParseSmi(string line)
    {
        var f = (line ?? "").Split(',');
        if (f.Length < 7) return null;
        static double? N(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        string S(int i) => f.Length > i && !f[i].Contains("N/A") ? f[i].Trim() : null;
        return new Smi(N(f[0]), N(f[1]), N(f[2]), N(f[3]), N(f[4]), N(f[5]), N(f[6]), f.Length > 7 ? N(f[7]) : null, S(8), f.Length > 9 ? N(f[9]) : null, S(10));
    }

    /// <summary>GPU clock throttle reason from nvidia-smi bitmask: "suhu" (0x20/0x40), "daya" (0x4/0x80), "HW" (0x8), otherwise "tidak" (idle/app clocks are not throttling).</summary>
    public static string ThrottleText(string hex)
    {
        if (hex == null) return null;
        try
        {
            long m = Convert.ToInt64(hex.Replace("0x", "").Trim(), 16);
            if ((m & 0x60) != 0) return "suhu";
            if ((m & 0x84) != 0) return "daya";
            return (m & 0x8) != 0 ? "HW" : "tidak";
        }
        catch { return null; }
    }

    /// <summary>
    /// Voltages from LibreHardwareMonitor data.json (Remote Web Server, port 8085; LHM must be run as admin for voltage sensors to be read).
    /// Leaf nodes have values like "1,052 V" / "1.052 V"; hardware is identified from parent node name. CPU: "CPU Core"/"Vcore" first, then first CPU voltage; GPU: "GPU Core" first, then first GPU voltage.
    /// </summary>
    public static (double? Cpu, double? Gpu) ParseLhmVolts(string json)
    {
        double? cpu = null, gpu = null; int cpuRank = 9, gpuRank = 9;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            void Walk(System.Text.Json.JsonElement n, List<string> path)
            {
                string text = n.TryGetProperty("Text", out var t) ? t.GetString() ?? "" : "";
                var here = new List<string>(path) { text };
                if (n.TryGetProperty("Value", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String && v.GetString() is { } s && s.TrimEnd().EndsWith(" V")
                    && double.TryParse(s.TrimEnd()[..^2].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var volt) && volt > 0.1 && volt < 3)
                {
                    bool isGpu = here.Any(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"NVIDIA|GeForce|Radeon|Intel\(R\) (Arc|Iris|UHD|Graphics)|^GPU ", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                    bool isCpu = !isGpu && here.Any(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"Ryzen|Core\(TM\)|Core i\d|Xeon|Processor|^CPU", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                    if (isGpu) { int rank = text.Contains("GPU Core", StringComparison.OrdinalIgnoreCase) ? 0 : 5; if (rank < gpuRank) { gpu = volt; gpuRank = rank; } }
                    else if (isCpu) { int rank = text.Contains("Core", StringComparison.OrdinalIgnoreCase) || text.Contains("Vcore", StringComparison.OrdinalIgnoreCase) ? 0 : 5; if (rank < cpuRank) { cpu = volt; cpuRank = rank; } }
                }
                if (n.TryGetProperty("Children", out var ch) && ch.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var c in ch.EnumerateArray()) Walk(c, here);
            }
            Walk(doc.RootElement, new());
        }
        catch { }
        return (cpu, gpu);
    }

    public static string Rate(double kb) => kb >= 1024 ? $"{kb / 1024:0.0}M" : $"{kb:0}K";

    public static string Mhz(double? v) => v is null ? null : v >= 1000 ? $"{v / 1000:0.00} GHz" : $"{v:0} MHz";
    public static string Gb(double gb) => gb >= 10 ? $"{gb:0.0}" : $"{gb:0.00}";

    public static int? PingRound(double? ms) => ms is { } v ? (int)Math.Round(v) : null;
    public static string PingMs(double? ms) => ms is { } v ? $"{Math.Round(v):0} ms" : "—";

    /// <summary>Latency color level: -1 = none/off, 0 = normal (&lt;80 ms), 1 = yellow (80-200 ms), 2 = red (&gt;200 ms or lost).</summary>
    public static int PingTier(double? ms, bool hasSample) =>
        ms is not { } v ? (hasSample ? 2 : -1) : v < 80 ? 0 : v <= 200 ? 1 : 2;

    /// <summary>Small ring buffer (~20 samples) for average, simple jitter, and packet loss percentage.</summary>
    public sealed class PingRing
    {
        public const int Capacity = 20;
        readonly long[] _buf = new long[Capacity];
        int _head, _count;

        public int Count => _count;
        public long? Latest { get; private set; }
        public double? AvgMs { get; private set; }
        public double? JitterMs { get; private set; }
        public int LossPct { get; private set; }

        public void Reset()
        {
            _head = _count = 0;
            Latest = null;
            AvgMs = JitterMs = null;
            LossPct = 0;
        }

        public void Push(long? ms)
        {
            _buf[_head] = ms ?? -1;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
            Latest = ms;

            int valid = 0, lost = 0, jitCount = 0;
            long sum = 0, jitSum = 0, prev = -1;
            int start = (_head - _count + Capacity) % Capacity;
            for (int i = 0; i < _count; i++)
            {
                long v = _buf[(start + i) % Capacity];
                if (v < 0) { lost++; continue; }
                sum += v;
                valid++;
                if (prev >= 0) { jitSum += Math.Abs(v - prev); jitCount++; }
                prev = v;
            }
            AvgMs = valid > 0 ? (double)sum / valid : null;
            JitterMs = jitCount > 0 ? (double)jitSum / jitCount : (valid > 0 ? 0 : null);
            LossPct = _count > 0 ? (int)Math.Round(lost * 100.0 / _count) : 0;
        }
    }

    /// <summary>Overlay row: each group (CPU, RAM, GPU, VRAM) contains the selected and available metric values, separated by " · ". Empty groups are skipped.</summary>
    public static List<(string Group, string Text)> Compose(HashSet<string> items, IReadOnlyDictionary<string, string> v)
    {
        var r = new List<(string, string)>();
        foreach (var g in Metrics.Select(m => m.Group).Distinct())
        {
            var parts = Metrics.Where(m => m.Group == g && items.Contains(m.Key) && v.TryGetValue(m.Key, out var s) && s != null).Select(m => v[m.Key]).ToList();
            if (parts.Count > 0) r.Add((g, string.Join("  ", parts)));
        }
        return r;
    }
}
