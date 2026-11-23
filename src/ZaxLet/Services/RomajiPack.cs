using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ZaxLet;

/// <summary>
/// Romaji feature for kanji, downloaded only if the user requests it (Settings > Music): MeCab library (LibNMeCab) and IPAdic dictionary
/// from nuget.org, ~14 MB, stored in %LOCALAPPDATA%\ZaxLet\romaji. Package hashes are pinned so downloaded files match what was tested.
/// The library is loaded in a collectible context, so the dictionary does not hold RAM after use.
/// </summary>
public static class RomajiPack
{
    const string Feed = "https://api.nuget.org/v3-flatcontainer/";
    static readonly (string Id, string Ver, string Sha256, string Entry)[] Pkgs =
    {
        ("libnmecab", "0.10.1", "f9945667086bb4db1a711d255f98287dd810ad0890191812b364337c01e01013", "lib/netstandard2.0/LibNMeCab.dll"),
        ("libnmecab.ipadicbin", "0.10.0", "275b9b15a7191b639c7f20fac93b3a8fc6af1c300a8d596a8396c6d6aa396a83", "content/IpaDic/"),
    };

    public static string Dir => Path.Combine(Settings.Dir, "romaji");
    static string DicDir => Path.Combine(Dir, "IpaDic");
    public static string PinyinFile => Path.Combine(Dir, "pinyin.gz");
    public static bool KanjiInstalled => File.Exists(Path.Combine(Dir, "LibNMeCab.dll")) && File.Exists(Path.Combine(DicDir, "sys.dic"));
    public static bool PinyinInstalled => File.Exists(PinyinFile);
    /// <summary>Full pack: Japanese kanji dictionary and Mandarin pinyin table. The kanji dictionary alone is used for reading Japanese lyrics (<see cref="KanjiInstalled"/>).</summary>
    public static bool Installed => KanjiInstalled && PinyinInstalled;
    public static bool Busy;
    public static string Status = "";

    /// <summary>Download and install missing parts. progress is called from a background thread. Throws on failure (partial files are cleaned up).</summary>
    public static async Task DownloadAsync(Action progress)
    {
        if (Busy) return;
        Busy = true;
        bool hadKanji = KanjiInstalled;
        try
        {
            Directory.CreateDirectory(Dir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            if (!PinyinInstalled)
            {
                Status = L.F("Mengunduh {0}…", "pinyin"); progress();
                var txt = await http.GetByteArrayAsync(PinyinPack.Url);
                if (!Convert.ToHexString(SHA256.HashData(txt)).Equals(PinyinPack.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Sidik jari pinyin tidak cocok; unduhan dibatalkan.");
                Status = L.F("Memasang {0}…", "pinyin"); progress();
                File.WriteAllBytes(PinyinFile, PinyinPack.Convert(Encoding.UTF8.GetString(txt)));
                Translit.ResetPinyin();
            }
            foreach (var p in hadKanji ? Array.Empty<(string Id, string Ver, string Sha256, string Entry)>() : Pkgs)
            {
                Status = L.F("Mengunduh {0}…", p.Id); progress();
                using var resp = await http.GetAsync($"{Feed}{p.Id}/{p.Ver}/{p.Id}.{p.Ver}.nupkg", HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;
                var ms = new MemoryStream();
                using (var s = await resp.Content.ReadAsStreamAsync())
                {
                    var buf = new byte[81920]; int n; int lastPct = -1;
                    while ((n = await s.ReadAsync(buf)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        int pct = total > 0 ? (int)(ms.Length * 100 / total) : 0;
                        if (pct != lastPct) { lastPct = pct; Status = L.F("Mengunduh {0}… {1}%", p.Id, pct); progress(); }
                    }
                }
                ms.Position = 0;
                if (!Convert.ToHexString(SHA256.HashData(ms.ToArray())).Equals(p.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Sidik jari {p.Id} tidak cocok; unduhan dibatalkan.");
                ms.Position = 0;
                Status = L.F("Memasang {0}…", p.Id); progress();
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                foreach (var e in zip.Entries)
                {
                    if (e.Length == 0 || !e.FullName.StartsWith(p.Entry, StringComparison.Ordinal)) continue;
                    var dest = p.Entry.EndsWith('/') ? Path.Combine(DicDir, e.FullName[p.Entry.Length..]) : Path.Combine(Dir, Path.GetFileName(e.FullName));
                    dest = Path.GetFullPath(dest);
                    if (!dest.StartsWith(Path.GetFullPath(Dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;   // do not escape directory
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }
            }
            if (!Installed) throw new FileNotFoundException("Berkas paket tulisan Latin tidak lengkap setelah dipasang.");
            Status = "";
        }
        catch
        {
            // previously installed parts are not deleted
            try { if (!hadKanji) Directory.Delete(Dir, true); else File.Delete(PinyinFile); } catch { }
            Translit.ResetPinyin();
            Status = "";
            throw;
        }
        finally { Busy = false; }
    }

    public static void Remove()
    {
        try { Directory.Delete(Dir, true); Translit.ResetPinyin(); } catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>
    /// Reading of each line (MeCab katakana output, words separated by spaces; null on failure). Called from a background thread. Library is loaded in
    /// an AssemblyLoadContext that is unloaded afterwards so the ~50 MB dictionary leaves memory.
    /// </summary>
    public static List<(string Line, string Reading)> Read(IEnumerable<string> lines) => ReadCore(lines);

    [MethodImpl(MethodImplOptions.NoInlining)]   // all context objects must be released before Unload
    static List<(string, string)> ReadCore(IEnumerable<string> lines)
    {
        var res = new List<(string, string)>();
        var alc = new AssemblyLoadContext("romaji", true);
        try
        {
            var asm = alc.LoadFromAssemblyPath(Path.Combine(Dir, "LibNMeCab.dll"));
            var tt = asm.GetType("NMeCab.Specialized.MeCabIpaDicTagger", true);
            var tagger = tt.GetMethod("Create", new[] { typeof(string), typeof(string[]) }).Invoke(null, new object[] { DicDir, null });
            var parse = tt.GetMethod("Parse", new[] { typeof(string) });
            PropertyInfo surf = null, pos = null, sec1 = null, read = null;
            try
            {
                foreach (var line in lines)
                {
                    string reading = null;
                    try
                    {
                        var sb = new StringBuilder();
                        foreach (var n in (Array)parse.Invoke(tagger, new object[] { line }))
                        {
                            if (n == null) continue;
                            if (surf == null)
                            {
                                var t = n.GetType();
                                surf = t.GetProperty("Surface"); pos = t.GetProperty("PartsOfSpeech"); sec1 = t.GetProperty("PartsOfSpeechSection1"); read = t.GetProperty("Reading");
                            }
                            var s = (string)surf.GetValue(n);
                            if (string.IsNullOrEmpty(s)) continue;   // start/end of sentence
                            static bool Ok(string x) => !string.IsNullOrEmpty(x) && x != "*";
                            // orthographic reading (オモウ -> omou), not phonetic (オモー -> omoo); particles are read by sound: は=wa, へ=e
                            var w = (string)read.GetValue(n); if (!Ok(w)) w = s;
                            if ((string)pos.GetValue(n) is { } p && p.StartsWith("助詞")) w = s switch { "は" => "ワ", "へ" => "エ", _ => w };
                            // affixes attach to the previous word (aishi+teru -> aishiteru, ik+ou -> ikou); other words are space-separated, punctuation attaches
                            var pp = (string)pos.GetValue(n) ?? ""; var sec = (string)sec1.GetValue(n) ?? "";
                            bool glue = pp == "助動詞" || pp == "記号" || (pp == "助詞" && sec == "接続助詞") || (pp == "動詞" && sec is "接尾" or "非自立") || (pp == "名詞" && sec == "接尾");
                            if (sb.Length > 0 && !glue) sb.Append(' ');
                            sb.Append(w);
                        }
                        reading = sb.ToString();
                    }
                    catch (Exception ex) { App.Log(ex); }
                    res.Add((line, reading));
                }
            }
            finally { (tagger as IDisposable)?.Dispose(); }
        }
        finally { alc.Unload(); }
        return res;
    }
}
