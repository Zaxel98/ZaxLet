using System;
using System.IO;
using System.Linq;
using ZaxLet;

// Cek mandiri PrayerCalc: jadwal Bandung 2 Nov 2026 (WIB) dari foto ReyLand: 04.04, 11.37, 14.52, 17.47, 18.59.
// Toleransi 5 menit untuk koordinat Bandung kota; kandidat lain hanya dicetak sebagai pembanding.
var expect = new[] { "04:04", "11:37", "14:52", "17:47", "18:59" };
var candidates = new (string Name, double Lat, double Lon)[]
{
    ("Bandung kota", -6.9175, 107.6191), ("Coblong", -6.8915, 107.6107), ("Jakarta", -6.2088, 106.8456),
};

int fail = 0;
foreach (var c in candidates)
{
    var t = PrayerCalc.Compute(new DateTime(2026, 11, 2), c.Lat, c.Lon, 7);
    Console.Write($"{c.Name,-14}");
    var all = t.All;
    for (int i = 0; i < 5; i++)
    {
        var got = all[i].At;
        var diff = (got.TimeOfDay - TimeSpan.Parse(expect[i])).TotalMinutes;
        Console.Write($" {all[i].Name} {got:HH:mm} ({diff:+0;-0;0})");
        if (c.Name == "Bandung kota" && Math.Abs(diff) > 5) fail++;
    }
    Console.WriteLine();
}

// Cek mandiri parser lirik LRC (format LRCLIB) dan respons kuota Claude
var lrc = "[00:12.50] Baris satu\n[00:20.00][01:05.25] Ulang\n[00:30.00] \nbukan baris";
var lines = Lrc.Parse(lrc);
bool lrcOk = lines.Count == 4 && lines[0].At == TimeSpan.FromSeconds(12.5) && lines[1].Text == "Ulang" && lines[3].At == TimeSpan.FromSeconds(65.25);
Console.WriteLine(lrcOk ? "LRC OK" : "LRC GAGAL");
if (!lrcOk) fail++;


// Cek mandiri teks durasi bebas
bool D(string t, double bare, double want) => DurText.Parse(t, bare, out var s) && Math.Abs(s - want) < 1e-6;
bool durOk = D("10m", 1, 600) && D("1.5 jam", 1, 5400) && D("1j30m", 1, 5400) && D("30", 1, 30) && D("25", 60, 1500) && D("500ms", 1, 0.5)
             && D("1,5 mnt", 1, 90) && !DurText.Parse("abc", 1, out _) && DurText.Fmt(5400) == "1 jam 30 mnt" && DurText.Fmt(90) == "1 mnt 30 dtk";
Console.WriteLine(durOk ? "DUR OK" : "DUR GAGAL");
if (!durOk) fail++;


// Cek mandiri romaji (kana lokal; baris berkanji tanpa pembaca = null)
bool R(string t, string want) => Romaji.Of(t) == want;
bool romOk = R("くるくるまわる", "kurukurumawaru") && R("クローバー・クラブ", "kuroobaa kurabu") && R("きっと", "kitto") && R("こんにちは", "konnichiha") && R("しんあい", "shin'ai") && R("ちょっと", "chotto") && R("まっちゃ", "matcha") && R("わたし は", "watashi wa") && Romaji.Of("世界が") == null && Romaji.Of("la la la") == null;
Console.WriteLine(romOk ? "ROMAJI OK" : "ROMAJI GAGAL");
if (!romOk) fail++;


// Cek mandiri pengenalan judul YouTube dan tulisan Latin (pinyin, Hangul, Kiril, Yunani, Arab); bahasa Han ditentukan per lagu
var tid = TrackId.Parse("Tame Impala - New Person, Same Old Mistakes (Audio)", "tameimpalaVEVO");
var tid2 = TrackId.Parse("Bohemian Rhapsody - Remastered 2011", "Queen");
var tid3 = TrackId.Parse("YOASOBI「夜に駆ける」 Official Music Video", "YOASOBI");
var tid4 = TrackId.Parse("Linkin Park - Numb [Official Music Video] [4K Upgrade]", "Linkin Park");
var tid5 = TrackId.Parse("Joji - Slow Dancing in the Dark", "Lofi Girl");
var tid6 = TrackId.Parse("Numb", "Linkin Park - Topic");
bool idOk = tid.Show == new TrackId.Track("New Person, Same Old Mistakes", "Tame Impala")
            && tid2.Show.Title == "Bohemian Rhapsody - Remastered 2011" && tid2.Search[0] == new TrackId.Track("Bohemian Rhapsody", "Queen")
            && tid3.Show == new TrackId.Track("夜に駆ける", "YOASOBI")
            && tid4.Show == new TrackId.Track("Numb", "Linkin Park")
            && tid5.Search[0] == new TrackId.Track("Slow Dancing in the Dark", "Joji") && tid5.Search[1].Title == "Joji"
            && tid6.Show == new TrackId.Track("Numb", "Linkin Park");
Console.WriteLine(idOk ? "TRACKID OK" : "TRACKID GAGAL " + tid.Show + " | " + tid2.Show + " | " + tid3.Show + " | " + tid4.Show + " | " + tid5.Search[0] + " | " + tid6.Show);
if (!idOk) fail++;

// tabel pinyin sekarang berasal dari Paket tulisan Latin: buat dari cuplikan format asli (pinyin-data) untuk uji
var pyTxt = "# version: 0.16.0\n# source: x\nU+3007: líng,yuán,xīng  # 〇\nU+4E2D: zhōng,zhòng  # 中\nU+56FD: guó  # 国\nU+6211: wǒ  # 我\nU+7231: ài  # 爱\nU+4F60: nǐ,nì  # 你\nU+7684: de,dī,dí  # 的\nU+4E16: shì  # 世\nU+754C: jiè  # 界\nU+9F9C: guī,jūn,qiū  # 龜\nU+1F600: x  # di luar BMP\nU+0041: a  # bukan CJK\nU+4E2E: \n";
var pyGz = PinyinPack.Convert(pyTxt);
var pyFile = Path.Combine(Path.GetTempPath(), "zx-pinyin-test.gz"); File.WriteAllBytes(pyFile, pyGz);
bool noPackOk = Translit.PinyinFile == null && !Translit.PinyinReady;
Translit.PinyinFile = pyFile; Translit.ResetPinyin();
using (var chk = new System.IO.Compression.GZipStream(new MemoryStream(pyGz), System.IO.Compression.CompressionMode.Decompress)) using (var pyRd = new StreamReader(chk))
{ var body = pyRd.ReadToEnd(); noPackOk &= body.Contains("中zhōng\n") && !body.Contains("zhòng") && !body.Contains("x\n") && body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 10; }
Console.WriteLine(noPackOk ? "PINYIN PACK OK" : "PINYIN PACK GAGAL");
if (!noPackOk) fail++;
Translit.Begin(new[] { "我爱你中国" });
Translit.PinyinFile = Path.Combine(Path.GetTempPath(), "tidak-ada.gz"); Translit.ResetPinyin();
bool absent = Translit.Of("我爱你中国") == null;   // tanpa paket: tidak diubah, bukan rusak
Translit.PinyinFile = pyFile; Translit.ResetPinyin();
Console.WriteLine(absent ? "PINYIN TANPA PAKET OK" : "PINYIN TANPA PAKET GAGAL");
if (!absent) fail++;
var tl = new[] { "我爱你中国", "愿你平安 快乐", "音乐 的 世界", "다시 만나요 한국어", "Я люблю тебя, мой друг", "Привіт, як справи", "Καλημέρα κόσμε", "حبيبي تعال", "الكتاب", "la la la" };
Translit.Begin(new[] { "我爱你", "愿你平安" });
bool zhOk = Translit.Lang == "zh" && Translit.Of("我爱你中国") == "wǒ ài nǐ zhōng guó" && Translit.Of("音乐 的 世界") == "yīn yuè de shì jiè";
Translit.Begin(new[] { "世界が", "愛してる", "夜" });
bool jaOk = Translit.Lang == "ja" && Translit.Of("愛してる") == null;   // kanji butuh addon; kana-saja diurus Romaji
Translit.Begin(Enumerable.Repeat("我爱你中国愿你平安快乐", 20).Append("の"));   // satu kana nyasar tidak mengubah lagu Mandarin
bool strayOk = Translit.Lang == "zh";
Translit.Begin(new[] { "夜" }, "夜に駆ける", "YOASOBI");                // lirik Han-saja, judul berkana = Jepang
bool hintOk = Translit.Lang == "ja";
Translit.Begin(new string[0]);
bool otherOk = Translit.Of("다시 만나요") == "dasi mannayo" && Translit.Of("한국어") == "hangugeo" && Translit.Of("Я люблю тебя, мой друг") == "Ya lyublyu tebya, moy drug"
               && Translit.Of("Привіт, як справи") == "Pryvit, yak spravy" && Translit.Of("Καλημέρα κόσμε") == "Kalimera kosme" && Translit.Of("حبيبي") == "habibi" && Translit.Of("la la la") == null;
foreach (var l in tl) Console.WriteLine("  " + l + " => " + Translit.Of(l));
bool trOk = zhOk && jaOk && strayOk && hintOk && otherOk;
Console.WriteLine(trOk ? "TRANSLIT OK" : $"TRANSLIT GAGAL zh={zhOk} ja={jaOk} stray={strayOk} hint={hintOk} other={otherOk}");
if (!trOk) fail++;


// Cek mandiri FFT: sinus 1 kHz dominan di pita vokal, sinus 100 Hz tidak
var fw = Dsp.Hann(1024);
(double, double) Sine(double hz) { var f = new float[1024]; for (int i = 0; i < 1024; i++) f[i] = (float)Math.Sin(2 * Math.PI * hz * i / 48000); var r = Dsp.BandPower(f, fw, 48000, 300, 3400, 60, 12000); return (r.Band, r.All); }
var (b1, a1) = Sine(1000); var (b2, a2) = Sine(100);
bool dspOk = b1 / a1 > 0.9 && b2 / a2 < 0.1;
Console.WriteLine(dspOk ? "DSP OK" : "DSP GAGAL");
if (!dspOk) fail++;


// Cek mandiri model vokal: dua kelompok terpisah harus terpelajari
var vm = new VocalModel(); var rr = new Random(3);
for (int i = 0; i < 300; i++) { vm.Add(new float[] { (float)(rr.NextDouble() + 2), (float)(rr.NextDouble() - 3), (float)rr.NextDouble(), 1f + (float)rr.NextDouble(), 1f }, true); vm.Add(new float[] { (float)(rr.NextDouble() - 1), (float)(rr.NextDouble() - 5), (float)rr.NextDouble(), (float)rr.NextDouble() * 0.5f, 1f }, false); }
vm.Train();
bool vmOk = vm.Ready && vm.Accuracy > 0.95;
Console.WriteLine(vmOk ? "VOCAL MODEL OK" : "VOCAL MODEL GAGAL " + vm.Accuracy);
if (!vmOk) fail++;

// Cek mandiri daftar lagu YouTube (deskripsi kompilasi) dan pemilihan video dari hasil pencarian
var chs = YtChapters.ParseChapters("Compilation!\n\n00:00:00 Tsukino Mito · 浮遊感UFO\n00:04:55 Tokyo Shoegazer · Constellations\n[11:13] 3. CONDOR44 - 00203\n1:20:59 Erio wo Kamattechan · Os\nlink: https://x.y/1:2");
var searchHtml = "<script>var ytInitialData = {\"a\":[{\"videoRenderer\":{\"videoId\":\"bad1\",\"title\":{\"runs\":[{\"text\":\"Other\"}]},\"lengthText\":{\"simpleText\":\"4:37\"}}},{\"videoRenderer\":{\"videoId\":\"good\",\"title\":{\"runs\":[{\"text\":\"THE SHOES ARE GAZING\"}]},\"lengthText\":{\"simpleText\":\"2:55:18\"}}}]};</script>";
bool chOk = chs != null && chs.Count == 4 && chs[1].At == new TimeSpan(0, 4, 55) && chs[2].Text == "CONDOR44 - 00203" && chs[3].At == new TimeSpan(1, 20, 59)
            && YtChapters.ParseChapters("00:00 a\n00:30 b") == null && YtChapters.ParseChapters("1:00 a\n2:00 b\n3:00 c") == null
            && YtChapters.PickVideo(searchHtml, "THE SHOES ARE GAZING", 10516) == "good" && YtChapters.PickVideo(searchHtml, "THE SHOES ARE GAZING", 600) == null
            && TrackId.Parse("Coaltar of the Deepers · DEAR FUTURE", "").Show == new TrackId.Track("DEAR FUTURE", "Coaltar of the Deepers");
Console.WriteLine(chOk ? "CHAPTERS OK" : "CHAPTERS GAGAL");
if (!chOk) fail++;

// Cek mandiri pengenal lagu: WAV 16 kHz dari 48 kHz, kode waktu, dan respons AudD (dikenali, tidak dikenali, ditolak)
var wav = SongId.ToWav16k(new float[48000], 48000);
bool idsOk = wav.Length == 44 + 16000 * 2 && System.Text.Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BitConverter.ToInt32(wav, 24) == 16000
             && SongId.ParseTimecode("01:23") == 83 && SongId.ParseTimecode("1:02:03") == 3723 && SongId.ParseTimecode("x") == -1
             && SongId.ParseResponse("{\"status\":\"success\",\"result\":{\"artist\":\"Cymbals\",\"title\":\"Higher than the Sun\",\"timecode\":\"00:35\"}}") is { Title: "Higher than the Sun", At: 35 }
             && SongId.ParseResponse("{\"status\":\"success\",\"result\":null}") == null;
try { SongId.ParseResponse("{\"status\":\"error\",\"error\":{\"error_code\":901,\"error_message\":\"bad token\"}}"); idsOk = false; } catch (InvalidOperationException e) { idsOk &= e.Message == "bad token"; }
Console.WriteLine(idsOk ? "SONGID OK" : "SONGID GAGAL");
if (!idsOk) fail++;

// Uji langsung ke youtube.com (opsional): dotnet run -- --live "judul video" "kanal" detik
if (args.Length == 4 && args[0] == "--live")
{
    var live = YtChapters.FetchAsync(args[1], args[2], double.Parse(args[3])).GetAwaiter().GetResult();
    Console.WriteLine(live == null ? "LIVE: tidak ada daftar lagu" : $"LIVE: {live.Count} segmen, pertama '{live[0].Text}', ke-4 '{live[3].At} {live[3].Text}'");
}

// Cek mandiri kuota dari aplikasi Claude Desktop: cuplikan terbaru yang berisi angka menang, cuplikan kosong dilewati
var du = DesktopUsage.ParseLatest("{\"version\":2,\"samples\":[{\"t\":100,\"org\":\"a\",\"u\":{\"fh\":10,\"sd\":2}},{\"t\":300,\"org\":\"a\",\"u\":{\"fh\":32,\"sd\":66}},{\"t\":400,\"org\":\"a\",\"u\":{}}]}");
bool duOk = du is { T: 300, FiveHour: 32, Weekly: 66 } && DesktopUsage.ParseLatest("{\"samples\":[]}") == null && DesktopUsage.ParseLatest("{\"x\":1}") == null
            && DesktopUsage.ParseLatest("{\"samples\":[{\"t\":5,\"u\":{\"sd\":7}}]}") is { FiveHour: -1, Weekly: 7 };
Console.WriteLine(duOk ? "DESKTOP USAGE OK" : "DESKTOP USAGE GAGAL");
if (!duOk) fail++;

// Cek mandiri kalender iCal: acara sekali, seharian, mingguan BYDAY+COUNT, tahunan 29 Feb, dibatalkan, EXDATE
var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20261104T120000Z\r\nSUMMARY:Rapat\\, tim\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20261105\r\nSUMMARY:Libur\r\n  kantor\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nDTSTART:20261102T090000\r\nRRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=4\r\nEXDATE:20261109T090000\r\nSUMMARY:Kuliah\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20200229\r\nRRULE:FREQ=YEARLY\r\nSUMMARY:Ultah\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nDTSTART:20261110T100000\r\nSTATUS:CANCELLED\r\nSUMMARY:Batal\r\nEND:VEVENT\r\nEND:VCALENDAR";
var evs = Ical.Parse(ics, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
var evs28 = Ical.Parse(ics, new DateOnly(2028, 2, 1), new DateOnly(2028, 3, 1));
bool icalOk = evs.Any(e => e.Title == "Rapat, tim" && e.Date == new DateOnly(2026, 11, 4)) && evs.Any(e => e.Title == "Libur kantor" && e.Date == new DateOnly(2026, 11, 5) && e.Time == "")
              && evs.Where(e => e.Title == "Kuliah").Select(e => e.Date.Day).OrderBy(x => x).SequenceEqual(new[] { 2, 4, 11 })   // 4 kali (2,4,9,11) dikurangi EXDATE 9
              && evs.All(e => e.Title != "Batal" && e.Title != "Ultah") && evs28.Any(e => e.Title == "Ultah" && e.Date == new DateOnly(2028, 2, 29))
              && Ical.NormalizeUrl("webcal://x.y/c.ics") == "https://x.y/c.ics" && !Ical.LooksValidUrl("http://x.y") && Ical.LooksLikeIcal(ics) && !Ical.LooksLikeIcal("<html>");
Console.WriteLine(icalOk ? "ICAL OK" : "ICAL GAGAL: " + string.Join(" | ", evs.Select(e => $"{e.Date:MM-dd} {e.Time} {e.Title}")) + " || 2028: " + string.Join(" | ", evs28.Select(e => $"{e.Date:yy-MM-dd} {e.Title}")));
if (!icalOk) fail++;

// Cek mandiri pembaruan: versi, rilis GitHub (digest dan berkas .sha256), hash, host, dan ekstraksi aman (zip-slip)
var rel = UpdateCheck.ParseRelease("{\"tag_name\":\"v1.1.0\",\"body\":\"catatan\",\"assets\":[{\"name\":\"ZaxLetSetup.exe\",\"browser_download_url\":\"https://github.com/a/b/x.exe\"},{\"name\":\"ZaxLet-update.zip\",\"browser_download_url\":\"https://github.com/a/b/u.zip\",\"digest\":\"sha256:" + new string('A', 64) + "\"}]}");
var rel2 = UpdateCheck.ParseRelease("{\"tag_name\":\"1.2\",\"assets\":[{\"name\":\"ZaxLet-update.zip\",\"browser_download_url\":\"https://github.com/a/b/u.zip\"},{\"name\":\"ZaxLet-update.zip.sha256\",\"browser_download_url\":\"https://github.com/a/b/u.sha\"}]}");
var tmpU = Path.Combine(Path.GetTempPath(), "zx-upd-test"); if (Directory.Exists(tmpU)) Directory.Delete(tmpU, true); Directory.CreateDirectory(tmpU);
var okZip = Path.Combine(tmpU, "ok.zip"); var badZip = Path.Combine(tmpU, "bad.zip");
void MakeZip(string path, params string[] names)
{
    using var z = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
    foreach (var n in names) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write("x"); }   // satu entri ditutup sebelum yang berikutnya dibuat
}
MakeZip(okZip, "ZaxLet.exe", "sub/a.txt");
MakeZip(badZip, "ZaxLet.exe", "../evil.txt");
var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(okZip))).ToLowerInvariant();
bool slip = false; try { UpdateCheck.ExtractSafe(badZip, Path.Combine(tmpU, "out-bad")); } catch (InvalidDataException) { slip = true; }
UpdateCheck.ExtractSafe(okZip, Path.Combine(tmpU, "out-ok"));
bool updOk = rel is { Tag: "v1.1.0", Sha256: var h1, Url: "https://github.com/a/b/u.zip" } && h1 == new string('a', 64) && UpdateCheck.IsNewer(rel.Version, new Version(1, 0, 0)) && !UpdateCheck.IsNewer(new Version(1, 0, 0), new Version(1, 0, 0))
             && rel2 is { Sha256: "", ShaUrl: "https://github.com/a/b/u.sha" } && UpdateCheck.IsNewer(rel2.Version, new Version(1, 1, 0)) && UpdateCheck.ParseRelease("{\"tag_name\":\"v2\",\"assets\":[]}") == null
             && UpdateCheck.ReadShaFile(sha + "  ZaxLet-update.zip\n") == sha && UpdateCheck.ReadShaFile("zzz") == "" && UpdateCheck.Verify(okZip, sha) && !UpdateCheck.Verify(okZip, new string('0', 64)) && !UpdateCheck.Verify(okZip, "")
             && UpdateCheck.HostAllowed("https://github.com/x") && UpdateCheck.HostAllowed("https://objects.githubusercontent.com/x") && !UpdateCheck.HostAllowed("https://evilgithub.com.evil.io/x") && !UpdateCheck.HostAllowed("http://github.com/x")
             && slip && File.Exists(Path.Combine(tmpU, "out-ok", "sub", "a.txt")) && !File.Exists(Path.Combine(tmpU, "evil.txt"));
Directory.Delete(tmpU, true);
Console.WriteLine(updOk ? "UPDATE OK" : "UPDATE GAGAL");
if (!updOk) fail++;

// Cek mandiri cadangan pengaturan: rahasia tidak ikut diekspor, dipertahankan saat memulihkan, berkas asing ditolak
var cur = "{\"Accent\":\"pink\",\"ShareToken\":\"RAHASIA\",\"SongIdToken\":\"T\",\"DiscordSecret\":\"\",\"HoverOpen\":true}";
var exp = SettingsBackup.Export(cur);
var imp = SettingsBackup.Import(exp.Replace("\"pink\"", "\"blue\"").Replace("true", "false"), cur);
bool badRej = false; try { SettingsBackup.Import("{\"a\":1}", cur); } catch (FormatException) { badRej = true; }
bool bkOk = !exp.Contains("RAHASIA") && exp.Contains("\"zaxlet\": 1") && imp.Contains("\"blue\"") && imp.Contains("\"RAHASIA\"") && imp.Contains("\"HoverOpen\":false") && badRej;
Console.WriteLine(bkOk ? "BACKUP OK" : "BACKUP GAGAL " + imp);
if (!bkOk) fail++;

// Cek mandiri unduhan selesai: berkas sementara peramban diabaikan, yang sudah di-rename ke nama akhir dilaporkan setelah ukurannya stabil
var dlDir = Path.Combine(Path.GetTempPath(), "zx-dl-test"); if (Directory.Exists(dlDir)) Directory.Delete(dlDir, true); Directory.CreateDirectory(dlDir);
var dlGot = new System.Collections.Concurrent.ConcurrentBag<string>();
var dw = new DownloadWatch(); dw.Finished += p => dlGot.Add(Path.GetFileName(p)); dw.Start(dlDir);
File.WriteAllText(Path.Combine(dlDir, "lagu.crdownload"), "setengah");
File.WriteAllText(Path.Combine(dlDir, "catatan.txt"), "langsung jadi");
System.Threading.Thread.Sleep(300);
File.Move(Path.Combine(dlDir, "lagu.crdownload"), Path.Combine(dlDir, "lagu.mp3"));
System.Threading.Thread.Sleep(2600);
bool dlOk = dlGot.Contains("lagu.mp3") && dlGot.Contains("catatan.txt") && !dlGot.Any(n => n.EndsWith(".crdownload")) && DownloadWatch.IsTemp("x.PART") && !DownloadWatch.IsTemp("x.zip");
Console.WriteLine(dlOk ? "DOWNLOAD OK" : "DOWNLOAD GAGAL: " + string.Join(",", dlGot));
if (!dlOk) fail++;
Directory.Delete(dlDir, true);

// Cek mandiri bahasa: Inggris menerjemahkan kunci yang ada, kunci asing tetap Indonesia, format dan satuan durasi ikut berubah
L.Init("en");
bool langOk = L.T("Kirim") == "Send" && L.T("teks yang tidak ada") == "teks yang tidak ada" && L.F("Shelf · {0} file", 3) == "Shelf · 3 files" && DurText.Fmt(5400) == "1 h 30 min";
L.Init("id");
langOk = langOk && L.T("Kirim") == "Kirim" && DurText.Fmt(5400) == "1 jam 30 mnt";
Console.WriteLine(langOk ? "LANG OK" : "LANG GAGAL");
if (!langOk) fail++;

return fail;
