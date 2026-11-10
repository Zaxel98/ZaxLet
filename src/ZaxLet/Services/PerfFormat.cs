using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZaxLet;

/// <summary>Bagian murni overlay performa (tanpa WPF/sensor): daftar metrik yang bisa dipilih, pembaca keluaran nvidia-smi, dan penyusun baris. Dites di tests/.</summary>
public static class PerfFormat
{
    /// <summary>Kunci, nama di Pengaturan, kelompok baris di overlay. Urutan = urutan tampil.</summary>
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

    /// <summary>Kolom yang diminta ke nvidia-smi, urutannya = urutan <see cref="ParseSmi"/>.</summary>
    public const string SmiQuery = "utilization.gpu,clocks.gr,clocks.mem,temperature.gpu,memory.used,memory.total,power.draw,utilization.memory,pstate,fan.speed,clocks_throttle_reasons.active";

    /// <summary>Satu baris keluaran nvidia-smi (<see cref="SmiQuery"/>, csv,noheader,nounits). Kolom "[N/A]" menjadi null (GPU laptop sering tanpa data daya/kipas).
    /// Tujuh kolom pertama wajib; sisanya opsional.</summary>
    public static Smi ParseSmi(string line)
    {
        var f = (line ?? "").Split(',');
        if (f.Length < 7) return null;
        static double? N(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        string S(int i) => f.Length > i && !f[i].Contains("N/A") ? f[i].Trim() : null;
        return new Smi(N(f[0]), N(f[1]), N(f[2]), N(f[3]), N(f[4]), N(f[5]), N(f[6]), f.Length > 7 ? N(f[7]) : null, S(8), f.Length > 9 ? N(f[9]) : null, S(10));
    }

    /// <summary>Alasan pembatasan clock GPU dari bitmask nvidia-smi: "suhu" (0x20/0x40), "daya" (0x4/0x80), "HW" (0x8), selain itu "tidak" (idle/app clocks bukan pembatasan).</summary>
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
    /// Tegangan dari data.json LibreHardwareMonitor (Remote Web Server, port 8085; LHM perlu dijalankan sebagai admin supaya sensor tegangan terbaca).
    /// Simpul daun bernilai "1,052 V" / "1.052 V"; perangkat keras dikenali dari nama simpul induk. CPU: "CPU Core"/"Vcore" dulu, lalu tegangan CPU pertama; GPU: "GPU Core" dulu, lalu tegangan GPU pertama.
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

    /// <summary>Baris overlay: tiap kelompok (CPU, RAM, GPU, VRAM) berisi nilai metrik yang dipilih dan tersedia, dipisah " · ". Kelompok kosong dilewati.</summary>
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
