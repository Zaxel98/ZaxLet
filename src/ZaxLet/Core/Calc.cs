using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZaxLet;

/// <summary>
/// Kalkulator dan konversi satuan untuk Aksi cepat. Murni, bisa dites.
/// "12*5+3", "2^10", "(1+2)*3" dan "5 km mi", "100 c f", "2 gb mb".
/// shortcut: titik sebagai desimal saja dan satuan terbatas pada tabel di bawah; tambah baris bila perlu.
/// </summary>
public static class Calc
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // satuan: kelompok + faktor ke satuan dasar kelompoknya (suhu ditangani terpisah)
    static readonly Dictionary<string, (string Grp, double F)> U = new()
    {
        ["mm"] = ("len", 0.001), ["cm"] = ("len", 0.01), ["m"] = ("len", 1), ["km"] = ("len", 1000), ["in"] = ("len", 0.0254), ["ft"] = ("len", 0.3048), ["mi"] = ("len", 1609.344),
        ["mg"] = ("mass", 1e-6), ["g"] = ("mass", 0.001), ["kg"] = ("mass", 1), ["lb"] = ("mass", 0.45359237), ["oz"] = ("mass", 0.028349523125),
        ["ml"] = ("vol", 0.001), ["l"] = ("vol", 1), ["gal"] = ("vol", 3.785411784),
        ["b"] = ("data", 1), ["kb"] = ("data", 1024), ["mb"] = ("data", 1048576), ["gb"] = ("data", 1073741824), ["tb"] = ("data", 1099511627776),
        ["c"] = ("temp", 0), ["f"] = ("temp", 0), ["k"] = ("temp", 0),
    };

    public static (string Text, double Value)? Run(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return null;
        if (Convert(s) is { } c) return c;
        return Eval(s, out var v) ? (Fmt(v), v) : null;
    }

    static string Fmt(double v) => v.ToString("0.######", Inv);

    static (string, double)? Convert(string s)
    {
        var t = s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // "5 km mi" atau "5 km to mi" / "ke" / "in"
        if (t.Length == 4 && (t[2] == "to" || t[2] == "ke" || t[2] == "in")) t = new[] { t[0], t[1], t[3] };
        if (t.Length != 3 || !double.TryParse(t[0], NumberStyles.Float, Inv, out var n) || !U.TryGetValue(t[1], out var a) || !U.TryGetValue(t[2], out var b) || a.Grp != b.Grp) return null;
        double r;
        if (a.Grp == "temp")
        {
            double c = t[1] == "c" ? n : t[1] == "f" ? (n - 32) * 5 / 9 : n - 273.15;
            r = t[2] == "c" ? c : t[2] == "f" ? c * 9 / 5 + 32 : c + 273.15;
        }
        else r = n * a.F / b.F;
        return ($"{Fmt(n)} {t[1]} = {Fmt(r)} {t[2]}", r);
    }

    // pemisah: + - * / ^ dan kurung; unary minus. Kiri-ke-kanan dengan prioritas biasa.
    static bool Eval(string s, out double v)
    {
        int i = 0; v = 0;
        try { v = Sum(s, ref i); }
        catch (FormatException) { return false; }
        return i == s.Length && !double.IsNaN(v) && !double.IsInfinity(v);
    }

    static void Skip(string s, ref int i) { while (i < s.Length && s[i] == ' ') i++; }

    static double Sum(string s, ref int i)
    {
        double v = Prod(s, ref i);
        for (Skip(s, ref i); i < s.Length && (s[i] == '+' || s[i] == '-'); Skip(s, ref i))
        {
            char op = s[i++]; double r = Prod(s, ref i);
            v = op == '+' ? v + r : v - r;
        }
        return v;
    }

    static double Prod(string s, ref int i)
    {
        double v = Pow(s, ref i);
        for (Skip(s, ref i); i < s.Length && (s[i] == '*' || s[i] == '/' || s[i] == 'x'); Skip(s, ref i))
        {
            char op = s[i++]; double r = Pow(s, ref i);
            v = op == '/' ? v / r : v * r;
        }
        return v;
    }

    static double Pow(string s, ref int i)
    {
        double v = Unary(s, ref i);
        Skip(s, ref i);
        if (i < s.Length && s[i] == '^') { i++; v = Math.Pow(v, Pow(s, ref i)); }   // kanan-ke-kiri
        return v;
    }

    static double Unary(string s, ref int i)
    {
        Skip(s, ref i);
        if (i < s.Length && s[i] == '-') { i++; return -Unary(s, ref i); }
        if (i < s.Length && s[i] == '(')
        {
            i++; double v = Sum(s, ref i); Skip(s, ref i);
            if (i >= s.Length || s[i] != ')') throw new FormatException();
            i++; return v;
        }
        int st = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (st == i || !double.TryParse(s.AsSpan(st, i - st), NumberStyles.Float, Inv, out var n)) throw new FormatException();
        return n;
    }
}
