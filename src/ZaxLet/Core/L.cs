using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZaxLet;

/// <summary>
/// Bahasa tampilan (Indonesia / Inggris). Teks sumber di kode berbahasa Indonesia dan berfungsi sebagai kunci:
/// <see cref="T"/> mengembalikannya apa adanya, atau terjemahan Inggris dari <see cref="Strings.En"/> bila bahasa Inggris dipilih.
/// Kunci yang belum diterjemahkan tampil dalam bahasa Indonesia, tidak pernah kosong.
/// </summary>
public static class L
{
    /// <summary>"auto" (ikut bahasa Windows), "id" atau "en". Dipanggil sekali saat mulai; pergantian berlaku setelah ZaxLet dimulai ulang.</summary>
    public static void Init(string pref)
    {
        pref = Pref = (pref ?? "auto").ToLowerInvariant();
        English = pref == "en" || pref != "id" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "id";
    }

    public static bool English { get; private set; }

    /// <summary>Pilihan bahasa yang sedang berlaku (bukan yang baru dipilih di Pengaturan).</summary>
    public static string Pref { get; private set; } = "auto";

    /// <summary>Kunci yang dicari tapi belum ada terjemahannya (dicatat oleh <c>--ui-test</c> agar kelihatan mana yang kurang).</summary>
    public static readonly HashSet<string> Misses = new();

    public static string T(string id)
    {
        if (!English) return id;
        if (Strings.En.TryGetValue(id, out var en)) return en;
        lock (Misses) Misses.Add(id);
        return id;
    }

    /// <summary>Teks bertanda {0}, {1}…: kuncinya adalah format Indonesianya.</summary>
    public static string F(string id, params object[] args) => string.Format(T(id), args);
}
