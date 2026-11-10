using System;
using System.Collections.Generic;
using System.Linq;

namespace ZaxLet;

/// <summary>
/// Mengenali rapat dari dua petunjuk: aplikasi yang sedang memakai mikrofon (daftar privasi Windows) dan judul jendela.
/// Aplikasi rapat asli cukup dikenali dari nama prosesnya; di peramban perlu judul tab rapat, supaya tab biasa yang
/// memakai mikrofon (mis. perekam suara) tidak dianggap rapat. Murni: tanpa WPF dan tanpa akses sistem, jadi bisa dites.
/// </summary>
public static class MeetingDetect
{
    static readonly (string Proc, string Label)[] Native =
    {
        ("zoom", "Zoom"), ("teams", "Teams"), ("ms-teams", "Teams"), ("msteams", "Teams"), ("webex", "Webex"), ("ciscocollabhost", "Webex"),
        ("skype", "Skype"), ("slack", "Slack"), ("whatsapp", "WhatsApp"),
    };
    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "arc" };

    /// <summary>Nama rapat ("Zoom", "Meet", ...) atau null bila tidak ada rapat.</summary>
    public static string Classify(IEnumerable<string> micApps, IEnumerable<string> windowTitles)
    {
        var apps = (micApps ?? Enumerable.Empty<string>()).Select(a => (a ?? "").ToLowerInvariant()).ToList();
        foreach (var (proc, label) in Native)
            if (apps.Any(a => a == proc || a.StartsWith(proc + ".") || a.StartsWith(proc + "_"))) return label;
        if (!apps.Any(a => Browsers.Any(b => a.StartsWith(b)))) return null;
        foreach (var t in windowTitles ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrEmpty(t)) continue;
            if (t.StartsWith("Meet - ", StringComparison.Ordinal) || t.StartsWith("Meet – ", StringComparison.Ordinal) || t.Contains("meet.google.com", StringComparison.OrdinalIgnoreCase)) return "Meet";
            if (t.Contains("| Microsoft Teams", StringComparison.OrdinalIgnoreCase)) return "Teams";
            if (t.Contains("Zoom Meeting", StringComparison.OrdinalIgnoreCase) || t.Contains("- Zoom", StringComparison.OrdinalIgnoreCase) && t.Contains("zoom.us", StringComparison.OrdinalIgnoreCase)) return "Zoom";
        }
        return null;
    }
}
