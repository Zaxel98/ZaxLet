using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZaxLet;

/// <summary>
/// Display language (Indonesian / English). Source strings in code are in Indonesian and act as keys:
/// <see cref="T"/> returns them as-is, or the English translation from <see cref="Strings.En"/> when English is selected.
/// Untranslated keys fall back to Indonesian, never empty.
/// </summary>
public static class L
{
    /// <summary>"auto" (follow Windows language), "id" or "en". Called once at startup; changes take effect after ZaxLet restarts.</summary>
    public static void Init(string pref)
    {
        pref = Pref = (pref ?? "auto").ToLowerInvariant();
        English = pref == "en" || pref != "id" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "id";
    }

    public static bool English { get; private set; }

    /// <summary>Currently active language choice (not a newly picked value in Settings).</summary>
    public static string Pref { get; private set; } = "auto";

    /// <summary>Keys looked up without a translation (recorded by <c>--ui-test</c> so missing ones stand out).</summary>
    public static readonly HashSet<string> Misses = new();

    public static string T(string id)
    {
        if (!English) return id;
        if (Strings.En.TryGetValue(id, out var en)) return en;
        lock (Misses) Misses.Add(id);
        return id;
    }

    /// <summary>Text with {0}, {1}... placeholders: the key is the Indonesian format string.</summary>
    public static string F(string id, params object[] args) => string.Format(T(id), args);
}
