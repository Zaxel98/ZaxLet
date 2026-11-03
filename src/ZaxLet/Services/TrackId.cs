using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ZaxLet;

/// <summary>
/// Mengenali judul dan artis dari metadata yang tidak rapi, terutama video YouTube di browser: "Artis - Judul (Audio)" dengan nama
/// kanal "artisVEVO" / "Artis - Topic". Menghasilkan pasangan untuk ditampilkan dan daftar kandidat pencarian lirik (berurutan dari
/// yang paling mungkin). Metadata pemutar musik biasa (judul dan artis terpisah) lolos tanpa perubahan berarti.
/// </summary>
public static class TrackId
{
    public sealed record Track(string Title, string Artist);
    public sealed record Parsed(Track Show, List<Track> Search);

    const string Noise = @"official|video|audio|lyrics?|lirik|mv|m/v|visuali[sz]er|hd|hq|4k|remaster(?:ed)?|music|clip|subtitles?|terjemahan|karaoke|explicit|provided|topic|full|歌詞|歌词|公式|ost|opening|ending|theme";
    static readonly Regex Brackets = new(@"[\(\[\{（【「『]([^\)\]\}）】」』]*)[\)\]\}）】」』]", RegexOptions.Compiled);
    static readonly Regex NoiseWord = new(@"(?<![\p{L}\p{N}])(?:" + Noise + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // keterangan versi setelah " - " pada pemutar biasa ("Judul - Remastered 2011"): bagian dari judul, bukan nama artis
    static readonly Regex SuffixLike = new(@"^\s*(?:remaster(?:ed)?|\d{4}|live|remix|version|ver\.?|edit|mono|stereo|deluxe|radio|single|acoustic|demo|instrumental|bonus|mix|original|album|extended|sped|slowed|reverb|cover|expanded|anniversary)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Quoted = new(@"^(?<a>[^「『“""]+?)\s*[「『“""](?<t>[^」』”""]+)[」』”""]", RegexOptions.Compiled);
    static readonly Regex Seps = new(@"\s+[-–—·•]\s+|\s*[|｜]\s*", RegexOptions.Compiled);
    static readonly Regex Feat = new(@"\s+(?:feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string Key(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", "");

    static bool Same(string a, string b)
    {
        string x = Key(a), y = Key(b);
        return x.Length >= 3 && y.Length >= 3 && (x == y || x.Contains(y) || y.Contains(x));
    }

    static bool OnlyNoise(string piece) => Key(NoiseWord.Replace(piece, "")).Length == 0;

    static string StripNoise(string s) => Regex.Replace(Brackets.Replace(s ?? "", m => NoiseWord.IsMatch(m.Groups[1].Value) ? "" : m.Value), @"\s{2,}", " ").Trim();

    /// <summary>Nama kanal YouTube menjadi nama artis: "tameimpalaVEVO", "Queen - Topic", "Adele Official" -> nama polos.</summary>
    public static string CleanChannel(string a)
    {
        var s = (a ?? "").Trim();
        string prev;
        do
        {
            prev = s;
            s = Regex.Replace(s, @"\s*[-–]\s*Topic$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*VEVO$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+(?:Official(?:\s+(?:Channel|Music|YouTube))?|Music|Records|Channel)$", "", RegexOptions.IgnoreCase).Trim();
        } while (s != prev);
        return s.Length >= 2 ? s : (a ?? "").Trim();
    }

    public static Parsed Parse(string title, string artist)
    {
        title ??= ""; artist ??= "";
        var chan = CleanChannel(artist);
        var s = StripNoise(title);
        var search = new List<Track>();
        void Add(string t, string a)
        {
            t = Feat.Replace(t ?? "", "").Trim(); a = (a ?? "").Trim();
            if (t.Length > 0 && !search.Any(x => x.Title == t && x.Artist == a)) search.Add(new Track(t, a));
        }

        Track show;
        var q = Quoted.Match(s);
        if (q.Success)   // Artis 「Judul」
        {
            show = new Track(q.Groups["t"].Value.Trim(), q.Groups["a"].Value.Trim());
            Add(show.Title, show.Artist);
        }
        else
        {
            var pieces = Seps.Split(s).Select(p => p.Trim()).Where(p => p.Length > 0 && !OnlyNoise(p)).ToList();
            if (pieces.Count >= 2 && SuffixLike.IsMatch(pieces[^1])) pieces.RemoveAt(pieces.Count - 1);   // "Judul - Remastered 2011" = satu judul
            if (pieces.Count < 2)
            {
                show = new Track(s, chan);
                Add(pieces.Count == 1 ? pieces[0] : s, chan);
            }
            else
            {
                string a = pieces[0], b = string.Join(" - ", pieces.Skip(1));
                if (Same(a, chan)) { show = new Track(b, a); Add(b, a); }
                else if (Same(b, chan)) { show = new Track(a, b); Add(a, b); }   // "Judul - Artis"
                else { show = new Track(b, a); Add(b, a); Add(a, b); }          // kanal tidak dikenali: anggap "Artis - Judul", coba terbalik juga
                // pencarian bebas dengan seluruh judul: katalog yang pandai mencocokkan teks panjang tetap menemukannya
                Add(string.Join(" ", pieces), "");
            }
        }
        if (Key(s) != Key(title)) Add(title, artist);   // metadata asli sebagai pilihan terakhir bila pembersihan terlalu agresif
        return new Parsed(show, search);
    }
}
