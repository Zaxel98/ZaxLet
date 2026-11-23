using System;
using System.Collections.Generic;
using System.Linq;

namespace ZaxLet;

/// <summary>
/// Detects meetings from two signals: apps currently using the microphone (Windows privacy list) and window titles.
/// Native meeting apps are identified by process name; in browsers a meeting tab title is required so ordinary tabs
/// using the mic (e.g., voice recorder) are not treated as a meeting. Pure: no WPF and no system access, so it is testable.
/// </summary>
public static class MeetingDetect
{
    static readonly (string Proc, string Label)[] Native =
    {
        ("zoom", "Zoom"), ("teams", "Teams"), ("ms-teams", "Teams"), ("msteams", "Teams"), ("webex", "Webex"), ("ciscocollabhost", "Webex"),
        ("skype", "Skype"), ("slack", "Slack"), ("whatsapp", "WhatsApp"),
    };
    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "arc" };

    /// <summary>Meeting name ("Zoom", "Meet", ...) or null if no meeting is active.</summary>
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
