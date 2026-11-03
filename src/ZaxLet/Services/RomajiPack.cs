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
/// Fitur romaji untuk kanji, diunduh hanya bila pengguna memintanya (Pengaturan > Musik): pustaka MeCab (LibNMeCab) dan kamus IPAdic
/// dari nuget.org, ±14 MB, disimpan di %LOCALAPPDATA%\ZaxLet\romaji. Sidik jari paket dipaku agar berkas yang diunduh pasti sama
/// dengan yang diuji. Pustaka dimuat dalam konteks yang bisa dibuang, jadi kamus tidak menahan RAM setelah selesai dipakai.
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
    public static bool Installed => File.Exists(Path.Combine(Dir, "LibNMeCab.dll")) && File.Exists(Path.Combine(DicDir, "sys.dic"));
    public static bool Busy;
    public static string Status = "";

    /// <summary>Unduh dan pasang. progress dipanggil dari thread latar. Melempar bila gagal (berkas parsial dibersihkan).</summary>
    public static async Task DownloadAsync(Action progress)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            Directory.CreateDirectory(Dir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            foreach (var p in Pkgs)
            {
                Status = $"Mengunduh {p.Id}…"; progress();
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
                        if (pct != lastPct) { lastPct = pct; Status = $"Mengunduh {p.Id}… {pct}%"; progress(); }
                    }
                }
                ms.Position = 0;
                if (!Convert.ToHexString(SHA256.HashData(ms.ToArray())).Equals(p.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Sidik jari {p.Id} tidak cocok; unduhan dibatalkan.");
                ms.Position = 0;
                Status = $"Memasang {p.Id}…"; progress();
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                foreach (var e in zip.Entries)
                {
                    if (e.Length == 0 || !e.FullName.StartsWith(p.Entry, StringComparison.Ordinal)) continue;
                    var dest = p.Entry.EndsWith('/') ? Path.Combine(DicDir, e.FullName[p.Entry.Length..]) : Path.Combine(Dir, Path.GetFileName(e.FullName));
                    dest = Path.GetFullPath(dest);
                    if (!dest.StartsWith(Path.GetFullPath(Dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;   // jangan keluar folder
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }
            }
            if (!Installed) throw new FileNotFoundException("Berkas romaji tidak lengkap setelah dipasang.");
            Status = "";
        }
        catch
        {
            try { Directory.Delete(Dir, true); } catch { }
            Status = "";
            throw;
        }
        finally { Busy = false; }
    }

    public static void Remove()
    {
        try { Directory.Delete(Dir, true); } catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>
    /// Bacaan tiap baris (katakana hasil MeCab, kata dipisah spasi; null bila gagal). Dipanggil dari thread latar. Pustaka dimuat di
    /// AssemblyLoadContext yang dibuang sesudahnya supaya kamus ±50 MB keluar dari memori.
    /// </summary>
    public static List<(string Line, string Reading)> Read(IEnumerable<string> lines) => ReadCore(lines);

    [MethodImpl(MethodImplOptions.NoInlining)]   // semua objek konteks harus lepas sebelum Unload
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
                            if (string.IsNullOrEmpty(s)) continue;   // awal/akhir kalimat
                            static bool Ok(string x) => !string.IsNullOrEmpty(x) && x != "*";
                            // bacaan ejaan (オモウ -> omou), bukan bunyi (オモー -> omoo); partikel dibaca menurut bunyinya: は=wa, へ=e
                            var w = (string)read.GetValue(n); if (!Ok(w)) w = s;
                            if ((string)pos.GetValue(n) is { } p && p.StartsWith("助詞")) w = s switch { "は" => "ワ", "へ" => "エ", _ => w };
                            // imbuhan menempel ke kata sebelumnya (aishi+teru -> aishiteru, ik+ou -> ikou); kata lain dipisah spasi, tanda baca menempel
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
