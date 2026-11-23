using System;
using System.Collections.Generic;
using System.Text;

namespace ZaxLet;

/// <summary>
/// Romaji (Hepburn) for Japanese lyrics, like Metrolist. Hiragana and katakana are converted locally without a dictionary.
/// Kanji requires a reading dictionary (RomajiPack addon, downloaded by the user): without it, lines with kanji get no romaji (null).
/// </summary>
public static class Romaji
{
    static readonly Dictionary<string, string> Cache = new();

#if !NOKANJI   // test project defines NOKANJI: without Kawazu and App.Log
    /// <summary>Called (background thread) when new romaji results are ready in cache; UI just needs to redraw.</summary>
    public static event Action Ready;
    static int _busy;

    /// <summary>
    /// Romaji for kanji lines via Kawazu (MeCab + IPAdic). The ~50 MB dictionary is only loaded during this process and then released so RAM is not
    /// held; results go into the cache and <see cref="Of"/> reads them without blocking the UI. Called when a song's lyrics finish loading.
    /// </summary>
    public static void Prefetch(IEnumerable<string> lines)
    {
        var todo = new List<string>();
        lock (Cache)
            foreach (var l in lines)
                if (IsJapanese(l) && !Cache.ContainsKey(l) && !todo.Contains(l)) todo.Add(l);
        if (todo.Count == 0 || !RomajiPack.KanjiInstalled || System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // RomajiPack reads kanji via MeCab in a collectible context; the result (katakana/kana) is converted to romaji by Kana()
                foreach (var (line, reading) in RomajiPack.Read(todo))
                {
                    var r = reading == null ? "" : Kana(reading);
                    lock (Cache) { if (Cache.Count > 600) Cache.Clear(); Cache[line] = r; }
                }
            }
            catch (Exception ex) { App.Log(ex); }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _busy, 0);
                GC.Collect(2, GCCollectionMode.Forced, true, true);   // release dictionary
                Ready?.Invoke();
            }
        });
    }
#endif

    static bool HasKanji(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s) if (IsKanji(c)) return true;
        return false;
    }

    const string Base =
        "あ,a,い,i,う,u,え,e,お,o,か,ka,き,ki,く,ku,け,ke,こ,ko,さ,sa,し,shi,す,su,せ,se,そ,so,た,ta,ち,chi,つ,tsu,て,te,と,to," +
        "な,na,に,ni,ぬ,nu,ね,ne,の,no,は,ha,ひ,hi,ふ,fu,へ,he,ほ,ho,ま,ma,み,mi,む,mu,め,me,も,mo,や,ya,ゆ,yu,よ,yo," +
        "ら,ra,り,ri,る,ru,れ,re,ろ,ro,わ,wa,ゐ,i,ゑ,e,を,o,ん,n,が,ga,ぎ,gi,ぐ,gu,げ,ge,ご,go,ざ,za,じ,ji,ず,zu,ぜ,ze,ぞ,zo," +
        "だ,da,ぢ,ji,づ,zu,で,de,ど,do,ば,ba,び,bi,ぶ,bu,べ,be,ぼ,bo,ぱ,pa,ぴ,pi,ぷ,pu,ぺ,pe,ぽ,po,ゔ,vu," +
        "ぁ,a,ぃ,i,ぅ,u,ぇ,e,ぉ,o,ゎ,wa";

    // digraphs (left) + small ya/yu/yo or small vowels
    const string Combo =
        "きゃ,kya,きゅ,kyu,きょ,kyo,しゃ,sha,しゅ,shu,しょ,sho,ちゃ,cha,ちゅ,chu,ちょ,cho,にゃ,nya,にゅ,nyu,にょ,nyo," +
        "ひゃ,hya,ひゅ,hyu,ひょ,hyo,みゃ,mya,みゅ,myu,みょ,myo,りゃ,rya,りゅ,ryu,りょ,ryo,ぎゃ,gya,ぎゅ,gyu,ぎょ,gyo," +
        "じゃ,ja,じゅ,ju,じょ,jo,ぢゃ,ja,ぢゅ,ju,ぢょ,jo,びゃ,bya,びゅ,byu,びょ,byo,ぴゃ,pya,ぴゅ,pyu,ぴょ,pyo," +
        "しぇ,she,じぇ,je,ちぇ,che,てぃ,ti,でぃ,di,とぅ,tu,どぅ,du,ふぁ,fa,ふぃ,fi,ふぇ,fe,ふぉ,fo,ゔぁ,va,ゔぃ,vi,ゔぇ,ve,ゔぉ,vo," +
        "うぃ,wi,うぇ,we,うぉ,wo,つぁ,tsa,つぃ,tsi,つぇ,tse,つぉ,tso,いぇ,ye,くぁ,kwa,ぐぁ,gwa";

    static readonly Dictionary<string, string> Map = BuildMap();

    static Dictionary<string, string> BuildMap()
    {
        var m = new Dictionary<string, string>();
        foreach (var src in new[] { Base, Combo })
        {
            var p = src.Split(',');
            for (int i = 0; i + 1 < p.Length; i += 2)
            {
                m[p[i]] = p[i + 1];
                m[ToKatakana(p[i])] = p[i + 1];   // katakana uses the same table
            }
        }
        return m;
    }

    static string ToKatakana(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c is >= 'ぁ' and <= 'ゖ' ? (char)(c + 0x60) : c);
        return sb.ToString();
    }

    static bool IsKana(char c) => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or 'ー' or '・';
    static bool IsKanji(char c) => c is >= '一' and <= '鿿' or '々' or '〆';

    /// <summary>True if text contains Japanese characters (kana or kanji).</summary>
    public static bool IsJapanese(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s) if (IsKana(c) || IsKanji(c)) return true;
        return false;
    }

    static readonly Dictionary<string, string> Local = new();   // kana-only romaji without addon (fallback until MeCab results are ready)

    /// <summary>
    /// Romaji for one line; null if not Japanese or contains kanji not yet read by the addon. MeCab results (Cache) take precedence because
    /// they recognize particles (は=wa, へ=e) and word boundaries; pure-kana lines without the addon use local conversion.
    /// </summary>
    public static string Of(string text)
    {
        if (!IsJapanese(text)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(text, out var hit)) return string.IsNullOrEmpty(hit) ? null : hit;
            if (HasKanji(text)) return null;   // waiting for Prefetch
            if (Local.TryGetValue(text, out var l)) return l;
            if (Local.Count > 400) Local.Clear();
            return Local[text] = Kana(text, true);
        }
    }

    /// <summary>Hiragana/katakana -> romaji; other characters (Latin, digits, punctuation) are left as-is.</summary>
    public static string Kana(string s, bool particles = false)
    {
        var sb = new StringBuilder();
        bool geminate = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is 'っ' or 'ッ') { geminate = true; continue; }
            if (c == 'ー') { if (sb.Length > 0 && "aiueo".IndexOf(sb[^1]) >= 0) sb.Append(sb[^1]); continue; }
            if (c == '・') { sb.Append(' '); continue; }
            if (c is '、' or '。') { sb.Append(c == '、' ? ',' : '.'); continue; }
            string r = null;
            if (i + 1 < s.Length && Map.TryGetValue(s.Substring(i, 2), out var two)) { r = two; i++; }
            else if (Map.TryGetValue(c.ToString(), out var one)) r = one;
            if (r == null) { geminate = false; sb.Append(c); continue; }
            if (geminate)
            {
                // っ: double the following initial consonant (chi -> tchi)
                if (r.StartsWith("ch")) sb.Append('t'); else if (r.Length > 0 && !"aiueo".Contains(r[0])) sb.Append(r[0]);
                geminate = false;
            }
            if (r == "n" && i + 1 < s.Length && Map.TryGetValue(s.Substring(i + 1, 1), out var nx) && (nx[0] is 'a' or 'i' or 'u' or 'e' or 'o' or 'y')) r = "n'";
            sb.Append(r);
        }
        // standalone particles (space-separated): は=wa, へ=e, を=o
        var words = sb.ToString().Split(' ');
        if (particles) for (int k = 0; k < words.Length; k++) words[k] = words[k] switch { "ha" when IsParticle(s, "は", "ハ") => "wa", "he" when IsParticle(s, "へ", "ヘ") => "e", _ => words[k] };
        return string.Join(" ", words).Trim();
    }

    static bool IsParticle(string s, params string[] kana)
    {
        foreach (var k in kana)
            if (s.Contains(" " + k + " ") || s.StartsWith(k + " ") || s.EndsWith(" " + k)) return true;
        return false;
    }
}
