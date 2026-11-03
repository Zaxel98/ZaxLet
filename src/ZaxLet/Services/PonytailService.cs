using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ZaxLet;

/// <summary>Ringkasan sesi Claude Code yang dicatat Ponytail (~/.config/ponytail/band/*.csv, satu baris per sesi).</summary>
public sealed class PonytailStats
{
    public double Cost, AgentMin;
    public long Tokens;
    public int Turns, Sessions;
}

public static class PonytailService
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ponytail", "band");
    record Row(DateTime Started, double Cost, double AgentMin, long Tokens, int Turns);

    // cache per file: hanya dibaca ulang bila waktu ubah berbeda
    static readonly Dictionary<string, (DateTime Stamp, List<Row> Rows)> Cache = new();

    public static bool Available => Directory.Exists(Dir);

    /// <summary>Total sesi yang mulai hari ini dan 7 hari terakhir. Biaya sesi dicatat penuh pada hari mulainya.</summary>
    public static (PonytailStats Today, PonytailStats Week) Read()
    {
        var today = new PonytailStats(); var week = new PonytailStats();
        if (!Available) return (today, week);
        var d0 = DateTime.Today;
        foreach (var f in Directory.EnumerateFiles(Dir, "*.csv"))
        {
            foreach (var r in Load(f))
            {
                if (r.Started < d0.AddDays(-6)) continue;
                Add(week, r);
                if (r.Started >= d0) Add(today, r);
            }
        }
        return (today, week);
    }

    static void Add(PonytailStats s, Row r) { s.Cost += r.Cost; s.AgentMin += r.AgentMin; s.Tokens += r.Tokens; s.Turns += r.Turns; s.Sessions++; }

    static List<Row> Load(string path)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (Cache.TryGetValue(path, out var c) && c.Stamp == stamp) return c.Rows;
            var rows = new List<Row>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            sr.ReadLine();   // header: key,started,project,name,cost_usd,turns,agent_minutes,tokens_total,...
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                var f = Split(line);
                if (f.Count < 8 || !DateTime.TryParseExact(f[1], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
                rows.Add(new Row(at, Num(f[4]), Num(f[6]), (long)Num(f[7]), (int)Num(f[5])));
            }
            Cache[path] = (stamp, rows);
            return rows;
        }
        catch (Exception ex) { App.Log(ex); return new List<Row>(); }
    }

    static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // CSV sederhana: kolom bertanda kutip boleh berisi koma (judul sesi)
    static List<string> Split(string line)
    {
        var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
        foreach (var ch in line)
        {
            if (ch == '"') q = !q;
            else if (ch == ',' && !q) { r.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        r.Add(sb.ToString());
        return r;
    }
}
