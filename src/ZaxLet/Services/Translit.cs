using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ZaxLet;

/// <summary>
/// Latin script for non-Latin lyrics: pinyin (Mandarin), Hangul romanization (Korean), Cyrillic (Russian, Ukrainian, Serbian, etc.),
/// Greek, and Arabic. Japanese still goes through <see cref="Romaji"/>. Han characters are shared by Japanese and Mandarin, so the language
/// is determined per song (<see cref="Begin"/>) from kana present in the lyrics, not per line.
/// Arabic is written without vowel marks so the output is approximate; pinyin uses the most common reading of each character (common polyphones
/// are handled via a small word list).
/// </summary>
public static class Translit
{
    /// <summary>Han-script language of the currently playing song: "ja", "zh", "ko", or "" (no East Asian script).</summary>
    public static string Lang { get; private set; } = "";

    /// <summary>Determine the song's language from all lyric lines (and title/artist). Kana = Japanese; Han without kana = Mandarin.</summary>
    public static void Begin(IEnumerable<string> lines, params string[] hints)
    {
        int kana = 0, han = 0, hangul = 0;
        foreach (var s in lines)
            foreach (var c in s ?? "") { if (IsKana(c)) kana++; else if (Script(c) == 1) han++; else if (Script(c) == 2) hangul++; }
        bool hintKana = hints.Any(h => (h ?? "").Any(IsKana));
        // stray kana in a Mandarin song (names, quotes) does not make it Japanese: require at least 10% of CJK chars, or kana in title/artist
        Lang = kana > 0 && kana >= (kana + han) * 0.1 || hintKana && kana + han > 0 ? "ja" : hangul > 0 && hangul >= han ? "ko" : han > 0 ? "zh" : "";
    }

    static bool IsKana(char c) => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or >= 'ㇰ' and <= 'ㇿ' or >= 'ｦ' and <= 'ﾟ';

    // 1 Han, 2 Hangul, 3 Cyrillic, 4 Greek, 5 Arabic, 0 other
    static int Script(char c) => c switch
    {
        >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or '〇' => 1,
        >= '가' and <= '힣' => 2,
        >= 'Ѐ' and <= 'ԯ' => 3,
        >= 'Ͱ' and <= 'Ͽ' or >= 'ἀ' and <= '῿' => 4,
        >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿' => 5,
        _ => 0
    };

    /// <summary>Latin transliteration of one line; null if the line has no convertible script (or Japanese that is not ready yet).</summary>
    public static string Of(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool hasHan = text.Any(c => Script(c) == 1);
        if (text.Any(IsKana) || hasHan && Lang == "ja") return Romaji.Of(text);

        bool uk = text.IndexOfAny("іїєґІЇЄҐ".ToCharArray()) >= 0;
        var sb = new StringBuilder();
        bool any = false, afterRun = false;
        for (int i = 0; i < text.Length;)
        {
            int sc = Script(text[i]);
            if (sc == 0)
            {
                var c = text[i++];
                var p = Punct(c);
                if (afterRun && char.IsLetterOrDigit(p) && sb.Length > 0 && char.IsLetterOrDigit(sb[^1])) sb.Append(' ');
                sb.Append(p);
                afterRun = false;
                continue;
            }
            int j = i;
            while (j < text.Length && Script(text[j]) == sc) j++;
            var run = text[i..j];
            i = j;
            var piece = sc switch { 1 => Pinyin(run), 2 => Korean(run), 3 => Cyrillic(run, uk), 4 => Greek(run), _ => Arabic(run) };
            if (string.IsNullOrEmpty(piece)) { sb.Append(run); afterRun = false; continue; }
            if (sb.Length > 0 && char.IsLetterOrDigit(sb[^1])) sb.Append(' ');
            sb.Append(piece);
            any = true; afterRun = true;
        }
        return any ? sb.ToString().Trim() : null;
    }

    static char Punct(char c) => c switch
    {
        '，' or '、' => ',', '。' => '.', '！' => '!', '？' => '?', '：' => ':', '；' => ';', '（' => '(', '）' => ')', '　' => ' ',
        '“' or '”' or '「' or '」' or '『' or '』' or '《' or '》' => '"', _ => c
    };

    static string Cap(string s, bool upper) => upper && s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    // ---------- Mandarin: pinyin ----------
    static Dictionary<char, string> _py;
    static readonly Dictionary<char, string> NoTable = new();

    /// <summary>Pinyin table file from the Latin script pack (populated by the app at startup). Without this file Mandarin Han characters are not converted.</summary>
    public static string PinyinFile { get; set; }

    /// <summary>Reload the table (after the pack is installed or removed).</summary>
    public static void ResetPinyin() => _py = null;

    public static bool PinyinReady => !string.IsNullOrEmpty(PinyinFile) && File.Exists(PinyinFile);

    public static bool HasHan(string s) => !string.IsNullOrEmpty(s) && s.Any(c => Script(c) == 1);

    static Dictionary<char, string> Table()
    {
        if (_py != null) return _py;
        if (!PinyinReady) return NoTable;   // not downloaded yet: not cached, so it is picked up immediately once the pack is installed
        var d = new Dictionary<char, string>(27000);
        try
        {
            using var gz = new GZipStream(File.OpenRead(PinyinFile), CompressionMode.Decompress);
            using var rd = new StreamReader(gz, Encoding.UTF8);
            for (string l; (l = rd.ReadLine()) != null;) if (l.Length > 1) d[l[0]] = l[1..];
        }
        catch { return NoTable; /* corrupt table: leave Han characters as-is */ }
        foreach (var (c, r) in Over) d[c] = r;
        return _py = d;
    }

    // most common reading in lyrics when different from the first reading in the table
    static readonly (char, string)[] Over = { ('长', "cháng"), ('得', "de"), ('地', "dì"), ('乐', "lè"), ('了', "le"), ('着', "zhe") };

    // polyphonic-character words whose reading differs from the single character's common reading
    static readonly Dictionary<string, string> Words = new()
    {
        ["银行"] = "yín háng", ["行业"] = "háng yè", ["重复"] = "chóng fù", ["重新"] = "chóng xīn", ["重来"] = "chóng lái", ["重逢"] = "chóng féng",
        ["长大"] = "zhǎng dà", ["成长"] = "chéng zhǎng", ["生长"] = "shēng zhǎng", ["增长"] = "zēng zhǎng",
        ["音乐"] = "yīn yuè", ["乐队"] = "yuè duì", ["乐曲"] = "yuè qǔ", ["乐器"] = "yuè qì",
        ["得到"] = "dé dào", ["获得"] = "huò dé", ["不得"] = "bù dé", ["值得"] = "zhí dé", ["取得"] = "qǔ dé", ["赢得"] = "yíng dé", ["得意"] = "dé yì", ["得罪"] = "dé zuì",
        ["不了"] = "bù liǎo", ["了解"] = "liǎo jiě", ["了不起"] = "liǎo bu qǐ",
        ["着急"] = "zháo jí", ["睡着"] = "shuì zháo", ["睡觉"] = "shuì jiào", ["头发"] = "tóu fà", ["的确"] = "dí què", ["目的"] = "mù dì",
        ["什么"] = "shén me", ["还债"] = "huán zhài", ["还给"] = "huán gěi", ["归还"] = "guī huán", ["爱好"] = "ài hào", ["喜好"] = "xǐ hào",
        ["为难"] = "wéi nán", ["成为"] = "chéng wéi", ["作为"] = "zuò wéi", ["认为"] = "rèn wéi", ["以为"] = "yǐ wéi", ["行为"] = "xíng wéi",
    };

    static string Pinyin(string run)
    {
        var py = Table();
        var parts = new List<string>();
        bool hit = false;
        for (int i = 0; i < run.Length; i++)
        {
            string w;
            if (i + 3 <= run.Length && Words.TryGetValue(run.Substring(i, 3), out w)) { parts.AddRange(w.Split(' ')); hit = true; i += 2; continue; }
            if (i + 2 <= run.Length && Words.TryGetValue(run.Substring(i, 2), out w)) { parts.AddRange(w.Split(' ')); hit = true; i += 1; continue; }
            char c = run[i];
            if (c == '地' && i >= 2 && run[i - 1] == run[i - 2]) { parts.Add("de"); hit = true; continue; }   // 慢慢地, 静静地: particle
            if (py.TryGetValue(c, out var r)) { parts.Add(r); hit = true; }
            else parts.Add(c.ToString());
        }
        return hit ? string.Join(" ", parts) : null;
    }

    // ---------- Korean: Revised Romanization ----------
    static readonly string[] KoInit = { "g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h" };
    static readonly string[] KoMed = { "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i" };
    // final: (sound when not linked, remainder in this syllable when next syllable starts with a vowel, sound that carries over to next syllable)
    static readonly (string End, string Keep, string Carry)[] KoFin =
    {
        ("", "", ""), ("k", "", "g"), ("k", "", "kk"), ("k", "k", "s"), ("n", "", "n"), ("n", "n", "j"), ("n", "", "n"), ("t", "", "d"), ("l", "", "r"),
        ("k", "l", "g"), ("m", "l", "m"), ("l", "l", "b"), ("l", "l", "s"), ("l", "l", "t"), ("p", "l", "p"), ("l", "", "r"), ("m", "", "m"), ("p", "", "b"),
        ("p", "p", "s"), ("t", "", "s"), ("t", "", "ss"), ("ng", "ng", ""), ("t", "", "j"), ("t", "", "ch"), ("k", "", "k"), ("t", "", "t"), ("p", "", "p"), ("t", "", ""),
    };

    static string Korean(string run)
    {
        var sb = new StringBuilder();
        string carry = null;
        for (int i = 0; i < run.Length; i++)
        {
            int n = run[i] - 0xAC00;
            int ini = n / 588, med = n % 588 / 28, fin = n % 28;
            string onset = KoInit[ini];
            if (carry != null) { onset = carry; carry = null; }
            else if (ini == 5 && sb.Length > 0 && sb[^1] == 'l') onset = "l";   // 달라: dalla
            sb.Append(onset).Append(KoMed[med]);
            var f = KoFin[fin];
            bool link = fin > 0 && i + 1 < run.Length && (run[i + 1] - 0xAC00) / 588 == 11;   // next syllable starts with ㅇ: final sound carries over
            if (link) { sb.Append(f.Keep); carry = f.Carry; }
            else sb.Append(f.End);
        }
        return sb.ToString();
    }

    // ---------- Cyrillic ----------
    static readonly Dictionary<char, string> Cy = Pairs("а=a б=b в=v г=g д=d е=e ё=yo ж=zh з=z и=i й=y к=k л=l м=m н=n о=o п=p р=r с=s т=t у=u ф=f х=kh ц=ts ч=ch ш=sh щ=shch ъ= ы=y ь= э=e ю=yu я=ya і=i ї=yi є=ye ґ=g ў=u ј=j љ=lj њ=nj ђ=dj ћ=c џ=dz ѝ=i");

    static Dictionary<char, string> Pairs(string spec)
    {
        var d = new Dictionary<char, string>();
        foreach (var p in spec.Split(' ')) d[p[0]] = p[2..];
        return d;
    }

    static string Cyrillic(string run, bool uk)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < run.Length; i++)
        {
            char c = run[i], lc = char.ToLowerInvariant(c);
            string r;
            if (!Cy.TryGetValue(lc, out r)) { sb.Append(c); continue; }
            if (uk) r = lc switch { 'г' => "h", 'и' => "y", _ => r };
            else if (lc == 'е' && (i == 0 || "аеёиоуыэюяъьй".IndexOf(char.ToLowerInvariant(run[i - 1])) >= 0)) r = "ye";   // BGN/PCGN: word-initial and after vowel
            sb.Append(Cap(r, char.IsUpper(c)));
        }
        return sb.ToString();
    }

    // ---------- Greek ----------
    static readonly Dictionary<char, string> Gr = Pairs("α=a β=v γ=g δ=d ε=e ζ=z η=i θ=th ι=i κ=k λ=l μ=m ν=n ξ=x ο=o π=p ρ=r σ=s ς=s τ=t υ=y φ=f χ=ch ψ=ps ω=o");

    static string Greek(string run)
    {
        // strip accents without Normalize (unreliable when invariant globalization is enabled)
        var d = new string(run.Select(c => "άέήίόύώΆΈΉΊΌΎΏϊϋΐΰ".IndexOf(c) is var k and >= 0 ? "αεηιουωΑΕΗΙΟΥΩιυιυ"[k] : c).ToArray());
        var sb = new StringBuilder();
        for (int i = 0; i < d.Length; i++)
        {
            char c = d[i], lc = char.ToLowerInvariant(c);
            char nx = i + 1 < d.Length ? char.ToLowerInvariant(d[i + 1]) : '\0';
            string r = null; int skip = 0;
            switch (lc, nx)
            {
                case ('ο', 'υ'): r = "ou"; skip = 1; break;
                case ('α', 'ι'): r = "e"; skip = 1; break;
                case ('ε', 'ι'): case ('ο', 'ι'): r = "i"; skip = 1; break;
                case ('μ', 'π'): r = i == 0 ? "b" : "mb"; skip = 1; break;
                case ('ν', 'τ'): r = i == 0 ? "d" : "nd"; skip = 1; break;
                case ('γ', 'κ'): r = i == 0 ? "g" : "ng"; skip = 1; break;
                case ('γ', 'γ'): r = "ng"; skip = 1; break;
                case ('α', 'υ'): case ('ε', 'υ'): case ('η', 'υ'):
                    // υ = v, but f before voiceless consonants
                    char after = i + 2 < d.Length ? char.ToLowerInvariant(d[i + 2]) : '\0';
                    r = (lc == 'α' ? "a" : lc == 'ε' ? "e" : "i") + ("θκξπστφχψ".IndexOf(after) >= 0 || after == '\0' ? "f" : "v"); skip = 1; break;
            }
            if (r == null && !Gr.TryGetValue(lc, out r)) { sb.Append(c); continue; }
            sb.Append(Cap(r, char.IsUpper(c)));
            i += skip;
        }
        return sb.ToString();
    }

    // ---------- Arabic (abjad: consonants + vowel letters; vowel marks if present) ----------
    // kind: C consonant, V vowel, W semivowel (consonant|vowel), A vowel mark, S shadda/gemination, N ignored
    static readonly Dictionary<char, (char Kind, string P)> Ar = BuildAr();

    static Dictionary<char, (char, string)> BuildAr()
    {
        var m = new Dictionary<char, (char, string)>();
        foreach (var p in "ب=b پ=p ت=t ث=th ج=j چ=ch ح=h خ=kh د=d ذ=dh ر=r ز=z ژ=zh س=s ش=sh ص=s ض=d ط=t ظ=z ع=' غ=gh ف=f ق=q ك=k ک=k گ=g ل=l م=m ن=n ه=h ء=' ؤ=' ئ='".Split(' ')) m[p[0]] = ('C', p[2..]);
        foreach (var p in "ا=a أ=a إ=i آ=aa ى=a ة=a".Split(' ')) m[p[0]] = ('V', p[2..]);
        foreach (var c in "يی") m[c] = ('W', "y|i");
        m['و'] = ('W', "w|u");
        m['َ'] = ('A', "a"); m['ُ'] = ('A', "u"); m['ِ'] = ('A', "i"); m['ً'] = ('A', "an"); m['ٌ'] = ('A', "un"); m['ٍ'] = ('A', "in");
        m['ّ'] = ('S', ""); m['ْ'] = ('N', ""); m['ـ'] = ('N', ""); m['ٰ'] = ('V', "a");
        return m;
    }

    static string Arabic(string word)
    {
        var map = Ar;
        string prefix = "";
        if (word.Length > 3 && word.StartsWith("ال")) { prefix = "al-"; word = word[2..]; }   // article: al-kitab
        var pcs = new List<(string S, bool Vowel)>();
        bool explicitVowels = false;
        for (int i = 0; i < word.Length; i++)
        {
            if (!map.TryGetValue(word[i], out var e)) continue;
            switch (e.Kind)
            {
                case 'C': pcs.Add((e.P, false)); break;
                case 'V': if (!(pcs.Count > 0 && pcs[^1].Vowel && pcs[^1].S == e.P)) pcs.Add((e.P, true)); break;   // long alif after fatha is not doubled
                case 'A': pcs.Add((e.P, true)); explicitVowels = true; break;
                case 'S': if (pcs.Count > 0 && !pcs[^1].Vowel) pcs.Add(pcs[^1]); break;
                case 'W':
                    var form = e.P.Split('|');
                    bool next = i + 1 < word.Length && map.TryGetValue(word[i + 1], out var ne) && ne.Kind == 'A';
                    bool cons = pcs.Count == 0 || pcs[^1].Vowel && pcs[^1].S.Length > 0 || next;
                    pcs.Add(cons ? (form[0], false) : (form[1], true));
                    break;
            }
        }
        if (pcs.Count == 0) return null;
        var sb = new StringBuilder(prefix);
        for (int k = 0; k < pcs.Count; k++)
        {
            // without vowel marks, two consecutive consonants are separated by "a" (approximation: حبيبي -> habibi)
            if (!explicitVowels && k > 0 && !pcs[k].Vowel && !pcs[k - 1].Vowel) sb.Append('a');
            sb.Append(pcs[k].S);
        }
        return sb.ToString();
    }
}
