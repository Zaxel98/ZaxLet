using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace ZaxLet;

public sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public string Date { get; set; } = "";   // yyyy-MM-dd
}

/// <summary>Libur nasional dari iCal publik Google (di-cache seminggu) + to-do per tanggal di tasks.json.</summary>
public sealed class CalendarService
{
    public static readonly CalendarService I = new();
    const string HolidayIcs = "https://calendar.google.com/calendar/ical/id.indonesian%23holiday%40group.v.calendar.google.com/public/basic.ics";

    public event Action Changed;
    public Dictionary<DateOnly, string> Holidays = new();
    public List<TaskItem> Tasks = Store.Load<List<TaskItem>>("tasks.json") ?? new();
    bool _loading;

    static string CachePath => Path.Combine(Settings.Dir, "holidays.ics");

    public async Task LoadHolidaysAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            if (File.Exists(CachePath) && Holidays.Count == 0) Holidays = ParseIcs(File.ReadAllText(CachePath));
            if (!File.Exists(CachePath) || (DateTime.Now - File.GetLastWriteTime(CachePath)).TotalDays > 7)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var ics = await http.GetStringAsync(HolidayIcs);
                Directory.CreateDirectory(Settings.Dir);
                File.WriteAllText(CachePath, ics);
                Holidays = ParseIcs(ics);
            }
            Changed?.Invoke();
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _loading = false; }
    }

    public static Dictionary<DateOnly, string> ParseIcs(string ics)
    {
        var result = new Dictionary<DateOnly, string>();
        // unfold: baris lanjutan diawali spasi/tab
        var lines = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && lines.Count > 0) lines[^1] += raw[1..];
            else lines.Add(raw);
        }
        DateOnly? date = null;
        string name = null;
        foreach (var l in lines)
        {
            if (l.StartsWith("BEGIN:VEVENT")) { date = null; name = null; }
            else if (l.StartsWith("DTSTART") && l.Contains(':'))
            {
                var v = l[(l.LastIndexOf(':') + 1)..];
                if (v.Length >= 8 && DateTime.TryParseExact(v[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)) date = DateOnly.FromDateTime(d);
            }
            else if (l.StartsWith("SUMMARY:")) name = l[8..].Replace("\\,", ",").Trim();
            else if (l.StartsWith("END:VEVENT") && date != null && name != null) result.TryAdd(date.Value, name);
        }
        return result;
    }

    public (DateOnly Date, string Name)? NextHoliday(DateOnly after) =>
        Holidays.Where(h => h.Key > after && !h.Value.StartsWith("Cuti Bersama")).OrderBy(h => h.Key).Select(h => ((DateOnly, string)?)(h.Key, h.Value)).FirstOrDefault();

    public IEnumerable<TaskItem> On(DateOnly d) => Tasks.Where(t => t.Date == d.ToString("yyyy-MM-dd"));
    public bool HasTasks(DateOnly d) => On(d).Any(t => !t.Done);

    public void Add(DateOnly d, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Tasks.Add(new TaskItem { Text = text.Trim(), Date = d.ToString("yyyy-MM-dd") });
        Save();
    }

    public void Toggle(TaskItem t) { t.Done = !t.Done; Save(); }
    public void Remove(TaskItem t) { Tasks.Remove(t); Save(); }

    void Save()
    {
        // buang tugas selesai yang lebih tua dari 30 hari
        var cut = DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd");
        Tasks.RemoveAll(t => t.Done && string.CompareOrdinal(t.Date, cut) < 0);
        Store.Save("tasks.json", Tasks);
        Changed?.Invoke();
    }
}
