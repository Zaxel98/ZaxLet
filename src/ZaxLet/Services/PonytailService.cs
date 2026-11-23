using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ZaxLet;

/// <summary>Claude Code session summary recorded by Ponytail (~/.config/ponytail/band/*.csv, one line per session).</summary>
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

    // per-file cache: only re-read when modification time differs
    static readonly Dictionary<string, (DateTime Stamp, List<Row> Rows)> Cache = new();

    public static bool Available => Directory.Exists(Dir);

    /// <summary>Session totals started today, since Monday, and since the 1st. Session cost is recorded in full on its start day.</summary>
    public static (PonytailStats Today, PonytailStats Week, PonytailStats Month) Read()
    {
        var today = new PonytailStats(); var week = new PonytailStats(); var month = new PonytailStats();
        if (!Available) return (today, week, month);
        var d0 = DateTime.Today; var wk = UsagePeriod.WeekStart(d0); var mo = UsagePeriod.MonthStart(d0);
        foreach (var f in Directory.EnumerateFiles(Dir, "*.csv"))
        {
            foreach (var r in Load(f))
            {
                if (r.Started >= mo) Add(month, r);
                if (r.Started >= wk) Add(week, r);
                if (r.Started >= d0) Add(today, r);
            }
        }
        return (today, week, month);
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

    // simple CSV: quoted columns may contain commas (session title)
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
