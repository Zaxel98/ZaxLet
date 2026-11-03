using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ZaxLet;

public sealed record LyricLine(TimeSpan At, string Text);

/// <summary>Parser format LRC (lirik tersinkron LRCLIB): "[mm:ss.xx] teks", boleh beberapa stempel waktu per baris.</summary>
public static class Lrc
{
    static readonly Regex Stamp = new(@"\[(\d+):(\d+(?:\.\d+)?)\]", RegexOptions.Compiled);

    public static List<LyricLine> Parse(string lrc)
    {
        var list = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var ms = Stamp.Matches(raw);
            if (ms.Count == 0) continue;
            var text = Stamp.Replace(raw, "").Trim();
            foreach (Match m in ms)
            {
                var t = TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                list.Add(new LyricLine(t, text));
            }
        }
        return list.OrderBy(l => l.At).ToList();
    }
}
