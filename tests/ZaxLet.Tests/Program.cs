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
             && UpdateCheck.HostAllowed("https://github.com/x") && UpdateCheck.HostAllowed("https://api.github.com/x") && UpdateCheck.HostAllowed("https://objects.githubusercontent.com/x")
             && !UpdateCheck.HostAllowed("https://evilgithub.com/x") && !UpdateCheck.HostAllowed("https://github.com.evil.io/x") && !UpdateCheck.HostAllowed("https://evil-githubusercontent.com/x") && !UpdateCheck.HostAllowed("http://github.com/x")
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

// Cek mandiri pengenal kode OTP di notifikasi: perlu kata kunci + angka 4-8 digit; harga, tahun-tanpa-kunci, dan teks biasa tidak dianggap kode
bool otpOk = Otp.Find("Bank Rey", "Kode OTP kamu 482913. Jangan bagikan.") == "482913"
             && Otp.Find("Google", "G-123 456 is your verification code") == "123456"
             && Otp.Find("Shopee", "Gunakan 5821 sebagai kode verifikasi") == "5821"
             && Otp.Find("Toko", "Total pembayaran Rp 150.000 sudah diterima") == null
             && Otp.Find("Kakak", "Nanti jam 19.30 ya, bawa 12345") == null
             && Otp.Find(null, null) == null;
Console.WriteLine(otpOk ? "OTP OK" : "OTP GAGAL");
if (!otpOk) fail++;

// Cek mandiri jadwal ibadah mingguan dan pembaca cuaca open-meteo
var wl = Worship.Parse("min 08:00 Kebaktian; jum,sab 18.30 Latihan koor; tiap 21:00 Doa\nrusak; sen 25:00 salah");
var due = new System.Collections.Generic.List<Worship.Item>(Worship.Due(wl, new DateTime(2026, 11, 8, 8, 1, 0)));   // Minggu 08:01
var wj2 = "{\"current\":{\"temperature_2m\":27.4,\"weather_code\":61},\"hourly\":{\"time\":[\"2026-11-06T09:00\",\"2026-11-06T10:00\",\"2026-11-06T11:00\",\"2026-11-06T15:00\"],\"precipitation_probability\":[10,70,20,95]}}";
var wr = WeatherParse.Read(wj2, new DateTime(2026, 11, 6, 9, 30, 0));
bool worshipOk = wl.Count == 1 + 2 + 7                  // Min | Jum+Sab | tiap (7 hari); baris rusak dan jam 25:00 dilewati
            && due.Count == 1 && due[0].Label == "Kebaktian"
            && Worship.Parse("jum 12.00").Count == 1 && Worship.Parse("jum 12.00")[0].Label == ""
            && wr != null && wr.Temp == 27.4 && wr.Code == 61 && wr.RainNext2h == 70   // jam 15:00 di luar jendela 2 jam
            && WeatherParse.Read("bukan json", DateTime.Now) == null && WeatherParse.Describe(95) == "Badai petir";
Console.WriteLine(worshipOk ? "WORSHIP/WEATHER OK" : $"WORSHIP/WEATHER GAGAL {wl.Count} {due.Count} {wr}");
if (!worshipOk) fail++;

// Cek mandiri filter sumber musik dan warna aksen Windows
bool miscOk = MediaFilter.Ignored("Discord.exe", "discord, zoom") && MediaFilter.Ignored("us.zoom.Zoom", "Zoom") && !MediaFilter.Ignored("Spotify.exe", "discord, zoom")
              && !MediaFilter.Ignored("Spotify.exe", "") && !MediaFilter.Ignored("Spotify.exe", "s") && !MediaFilter.Ignored(null, "discord")
              && AccentColor.FromAbgr(unchecked((int)0xFFD77800)) != "0078D7"          // biru Windows (R0 G120 B215) gelap: dicerahkan
              && AccentColor.FromAbgr(unchecked((int)0xFFFFFF00)) == "00FFFF"          // sudah terang (cyan): tidak berubah
              && AccentColor.FromAbgr(unchecked((int)0xFF000000)).Length == 6;
var qc = new[] { QuickCmd.Parse("t 10m"), QuickCmd.Parse("timer 90"), QuickCmd.Parse("catat beli susu"), QuickCmd.Parse("minum"), QuickCmd.Parse("minum 250"), QuickCmd.Parse("tema Neon"), QuickCmd.Parse("sembunyi") };
miscOk = miscOk && qc[0] is { Kind: QuickCmd.Kind.Timer, Value: 600 } && qc[1] is { Kind: QuickCmd.Kind.Timer, Value: 5400 }
          && qc[2] is { Kind: QuickCmd.Kind.Note, Arg: "beli susu" } && qc[3] is { Kind: QuickCmd.Kind.Water, Value: 0 } && qc[4] is { Kind: QuickCmd.Kind.Water, Value: 250 }
          && qc[5] is { Kind: QuickCmd.Kind.Theme, Arg: "neon" } && qc[6].Kind == QuickCmd.Kind.Hide
          && QuickCmd.Parse("").Kind == QuickCmd.Kind.None && QuickCmd.Parse("t abc").Kind == QuickCmd.Kind.Unknown
          && QuickCmd.Parse("catat").Kind == QuickCmd.Kind.Unknown && QuickCmd.Parse("minum 5").Kind == QuickCmd.Kind.Unknown && QuickCmd.Parse("tema pelangi").Kind == QuickCmd.Kind.Unknown && QuickCmd.Parse("xyz").Kind == QuickCmd.Kind.Unknown;
// overlay performa: baris nvidia-smi nyata dari RTX 3050 laptop (daya [N/A] di data lain), pilihan metrik, penyusun baris
var smi = PerfFormat.ParseSmi("32, 405, 810, 50, 645, 6144, 8.86, 20, P5, [N/A], 0x0000000000000001");
var smiNa = PerfFormat.ParseSmi("5, 210, 405, 41, 300, 6144, [N/A]");
var vals = new System.Collections.Generic.Dictionary<string, string> { ["cpu"] = "12%", ["cputemp"] = "56°C", ["gpu"] = "32%", ["gputemp"] = "50°C", ["vram"] = "0.65/6.00 GB", ["ram"] = "9.1/15.7 GB" };
var composed = PerfFormat.Compose(PerfFormat.Items("cpu,cputemp,gpu,vram,bogus"), vals);
miscOk = miscOk && smi is { Util: 32, Gr: 405, Mem: 810, Temp: 50, MemUsedMb: 645, MemTotalMb: 6144, Power: 8.86 } && smiNa is { Power: null, Util: 5 } && PerfFormat.ParseSmi("rusak") == null && PerfFormat.ParseSmi(null) == null
          && smi.MemUtil == 20 && smi.PState == "P5" && smi.Fan == null && PerfFormat.ThrottleText(smi.Throttle) == "tidak" && PerfFormat.ThrottleText("0x4") == "daya" && PerfFormat.ThrottleText("0x44") == "suhu" && PerfFormat.ThrottleText("zz") == null && smiNa.PState == null
          && PerfFormat.Mhz(2020.4) == "2.02 GHz" && PerfFormat.Mhz(810) == "810 MHz" && PerfFormat.Mhz(null) == null
          && composed.Count == 3 && composed[0] == ("CPU", "12%  56°C") && composed[1] == ("GPU", "32%") && composed[2] == ("VRAM", "0.65/6.00 GB")
          && PerfFormat.Compose(new System.Collections.Generic.HashSet<string>(), vals).Count == 0 && PerfFormat.WithItem("cpu", "gpu", true) == "cpu,gpu" && PerfFormat.WithItem("cpu,gpu", "cpu", false) == "gpu"
          && QuickCmd.Parse("perf").Kind == QuickCmd.Kind.Perf && QuickCmd.Parse("bt").Kind == QuickCmd.Kind.Bt;
miscOk = miscOk && QuickCmd.Parse("= 12*5").Value == 60 && QuickCmd.Parse("2+3*4").Value == 14 && QuickCmd.Parse("(1+2)*3").Value == 9 && QuickCmd.Parse("2^3^2").Value == 512
          && QuickCmd.Parse("-4+10").Value == 6 && QuickCmd.Parse("10/4").Arg == "2.5" && QuickCmd.Parse("1/0").Kind == QuickCmd.Kind.Unknown && QuickCmd.Parse("(1+2").Kind == QuickCmd.Kind.Unknown
          && QuickCmd.Parse("5 km mi") is { Kind: QuickCmd.Kind.Calc } km && km.Arg == "5 km = 3.106856 mi" && Math.Abs(QuickCmd.Parse("100 c f").Value - 212) < 1e-9 && Math.Abs(QuickCmd.Parse("2 gb to mb").Value - 2048) < 1e-9
          && QuickCmd.Parse("5 km kg").Kind == QuickCmd.Kind.Unknown && QuickCmd.Parse("5 km").Kind == QuickCmd.Kind.Unknown
          && QuickCmd.Parse("buka Monitor") is { Kind: QuickCmd.Kind.Open, Arg: "monitor" } && QuickCmd.Parse("buka").Kind == QuickCmd.Kind.Unknown;
// tegangan dari data.json LibreHardwareMonitor: pohon Sensor > komputer > perangkat keras > grup "Voltages" > daun "1,052 V" (koma desimal lokal ID)
var lhm = ("{'Text':'Sensor','Children':[{'Text':'NITRO','Children':["
  + "{'Text':'Intel Core 5 210H','Children':[{'Text':'Voltages','Children':[{'Text':'Core #1 VID','Value':'0,850 V','Children':[]},{'Text':'CPU Core','Value':'0,912 V','Children':[]}]}]},"
  + "{'Text':'NVIDIA GeForce RTX 3050 6GB Laptop GPU','Children':[{'Text':'Voltages','Children':[{'Text':'GPU Core','Value':'0.725 V','Children':[]}]},{'Text':'Clocks','Children':[{'Text':'GPU Core','Value':'405 MHz','Children':[]}]}]}]}]}").Replace('\'', '"');
var lv = PerfFormat.ParseLhmVolts(lhm);
miscOk = miscOk && lv.Cpu is { } cvv && Math.Abs(cvv - 0.912) < 1e-9 && lv.Gpu is { } gvv && Math.Abs(gvv - 0.725) < 1e-9
          && PerfFormat.ParseLhmVolts("bukan json") == (null, null) && PerfFormat.ParseLhmVolts("{'Text':'x','Children':[]}".Replace('\'', '"')) == (null, null)
          && PerfFormat.Compose(PerfFormat.Items("fps,cpu"), new System.Collections.Generic.Dictionary<string, string> { ["fps"] = "143", ["cpu"] = "12%" }) is [("FPS", "143"), ("CPU", "12%")];
{
    var now0 = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Local);
    long U(int daysAgo) => new DateTimeOffset(now0.ToUniversalTime()).ToUnixTimeSeconds() - daysAgo * 86400L;
    var log = new[] { new PlayEntry(U(1), "A", "X", 100), new PlayEntry(U(2), "a", "x", 50), new PlayEntry(U(3), "B", "X", 400), new PlayEntry(U(10), "C", "Y", 900), new PlayEntry(U(20), "C", "Y", 900) };
    var s7 = PlayStats.TopSongs(log, now0, 7, 5); var s30 = PlayStats.TopSongs(log, now0, 30, 5); var a7 = PlayStats.TopArtists(log, now0, 7, 5);
    var rt = PlayStats.FromLine(PlayStats.ToLine(new PlayEntry(5, "Ju\tdul", "Ar\ntis", 31)));
    miscOk = miscOk && s7.Count == 2 && s7[0].Name == "A" && s7[0].Plays == 2 && s7[1].Name == "B"   // A diputar 2x (huruf besar/kecil sama), B 1x
        && s30[0].Name == "C" && s30[0].Plays == 2 && s30[1].Name == "A" && a7.Count == 1 && a7[0].Plays == 3 && a7[0].Sec == 550
        && rt != null && rt.Title == "Ju dul" && rt.Artist == "Ar tis" && rt.Sec == 31 && PlayStats.FromLine("rusak") == null && PlayStats.FromLine("1\t2\tx\t") == null;
}
// iklan Spotify: templat di slot judul + artis "Spotify" (data nyata di playlog), lagu asli tidak ikut
miscOk = miscOk && MediaFilter.IsAd("Spotify.exe", "Dengarkan musik bebas iklan.", "Spotify") && MediaFilter.IsAd(null, "Dengarkan musik bebas iklan.", "Spotify")
        && MediaFilter.IsAd("Spotify.exe", "Advertisement", "") && MediaFilter.IsAd("Spotify.exe", "Spotify", "")
        && !MediaFilter.IsAd("Spotify.exe", "Hello!My World!!", "fhána") && !MediaFilter.IsAd("chrome.exe", "Advertisement", "") && !MediaFilter.IsAd("Spotify.exe", "Advertisement", "Some Band") && !MediaFilter.IsAd(null, "", "");
// baris pill menurut waktu
{
    var pr = ("Ashar", new DateTime(2026, 11, 7, 15, 12, 0));
    miscOk = miscOk && IdleContext.Compose(new DateTime(2026, 11, 7, 7, 0, 0), "27°C · Cerah", pr, 500, 2, "x") == "27°C · Cerah · Ashar 15.12"
        && IdleContext.Compose(new DateTime(2026, 11, 7, 7, 0, 0), null, null, 500, 2, "x") == null
        && IdleContext.Compose(new DateTime(2026, 11, 7, 11, 0, 0), "c", pr, 500, 2, "x") == "2 tugas hari ini"
        && IdleContext.Compose(new DateTime(2026, 11, 7, 11, 0, 0), "c", pr, 500, 0, "x") == null
        && IdleContext.Compose(new DateTime(2026, 11, 7, 16, 0, 0), "c", pr, 750, 2, "x") == "Ashar 15.12 · Minum 750 ml lagi"
        && IdleContext.Compose(new DateTime(2026, 11, 7, 21, 0, 0), "c", pr, 750, 2, "Besok: Libur") == "Besok: Libur"
        && IdleContext.Compose(new DateTime(2026, 11, 7, 21, 0, 0), "c", pr, 750, 2, null) == null;
}
// tampil/sembunyi pill: Want(fs, snooze, snoozePeek, peek, mini, ghost, notifForced, fsMusic, activityHide)
miscOk = miscOk && PillVisibility.Want(false, false, false, false, false, false, false, false, false)   // keadaan biasa: tampil
    && !PillVisibility.Want(true, false, false, false, false, false, false, false, false)               // fullscreen: sembunyi
    && PillVisibility.Want(true, false, false, true, false, false, false, false, false)                 // fullscreen + mengintip: tampil
    && PillVisibility.Want(true, false, false, false, true, false, false, false, false)                 // fullscreen dengan jam kecil: tampil
    && PillVisibility.Want(true, false, false, false, false, false, true, false, false)                 // notifikasi memaksa tampil
    && PillVisibility.Want(true, false, false, false, false, false, false, true, false)                 // musik di layar penuh
    && !PillVisibility.Want(false, true, false, false, false, false, false, false, false)              // disembunyikan pengguna
    && !PillVisibility.Want(false, true, false, true, false, false, false, false, false)               // sembunyi tanpa snoozePeek: intipan tidak menampilkan
    && PillVisibility.Want(false, true, true, true, false, false, false, false, false)                 // sembunyi dengan intipan (klik kanan): tampil saat mengintip
    && !PillVisibility.Want(false, false, false, false, false, false, false, false, true)              // mode aktivitas tanpa aktivitas: sembunyi
    && PillVisibility.Want(false, false, false, true, false, false, false, false, true)                // ...tapi tampil saat mengintip
    && !PillVisibility.Want(false, false, false, false, false, true, true, true, false)                // Alt ditahan mengalahkan semuanya
    && PillVisibility.Want(false, true, false, false, false, false, true, false, false);               // disembunyikan tapi ada notifikasi: tampil
miscOk = miscOk && QuickCmd.Parse("kunci").Kind == QuickCmd.Kind.Lock && QuickCmd.Parse("Tidur").Kind == QuickCmd.Kind.Sleep && QuickCmd.Parse("gelap").Kind == QuickCmd.Kind.Dark && QuickCmd.Parse("light").Kind == QuickCmd.Kind.Light
          && QuickCmd.Parse("wifi off") is { Kind: QuickCmd.Kind.Wifi, Value: 0 } && QuickCmd.Parse("wifi") is { Kind: QuickCmd.Kind.Wifi, Value: -1 } && QuickCmd.Parse("bluetooth nyala") is { Kind: QuickCmd.Kind.Bluetooth, Value: 1 }
          && QuickCmd.Parse("wifi maybe").Kind == QuickCmd.Kind.Unknown && QuickCmd.Parse("bt").Kind == QuickCmd.Kind.Bt;
miscOk = miscOk && ProcGuard.CanKill("notepad++", @"C:\Program Files\Notepad++\notepad++.exe") && !ProcGuard.CanKill("svchost", @"C:\Windows\System32\svchost.exe") && !ProcGuard.CanKill("explorer", @"c:\windows\explorer.exe")
          && !ProcGuard.CanKill("zaxlet", @"D:\x\ZaxLet.exe") && !ProcGuard.CanKill("System", null) && !ProcGuard.CanKill("x", "");
Console.WriteLine(miscOk ? "MISC OK" : $"MISC GAGAL {AccentColor.FromAbgr(unchecked((int)0xFFD77800))}");
if (!miscOk) fail++;

// Cek mandiri parser AiTaskParse: worker JSON (BOM/terpotong), status MATI, stage, vonis, dan token
{
    var wJson = "\uFEFF{\"name\":\"scope1\",\"repo\":\"toy\",\"state\":\"SELESAI\",\"phase\":\"selesai\",\"model\":\"m\",\"timeoutMin\":8,\"startedAt\":\"2026-11-17T10:00:00+07:00\",\"updatedAt\":\"2026-11-17T10:02:00+07:00\",\"pid\":21092,\"detail\":[\"VONIS: OK\"]}";
    var w = AiTaskParse.ParseWorker(wJson);
    var wTitle = AiTaskParse.ParseWorker("{\"name\":\"aud-x\",\"state\":\"BERJALAN\",\"title\":\"Ringkas fitur\",\"group\":\"Audit ZaxLet\"}");
    bool wrkOk = w != null && w.Name == "scope1" && w.Repo == "toy" && w.State == "SELESAI" && w.TimeoutMin == 8 && w.Pid == 21092 && w.Title == "" && w.Group == ""
        && wTitle != null && wTitle.Title == "Ringkas fitur" && wTitle.Group == "Audit ZaxLet"
        && AiTaskParse.ParseWorker("{\"name\":\"x\",\"sta") == null
        && AiTaskParse.ParseWorker("{\"name\":\"x\"}") == null;

    var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.FromHours(7));
    var wRun = new WorkerInfo("a", "r", "BERJALAN", "Hydrogen bekerja", "m", 8, now.AddMinutes(-10), now.AddMinutes(-5), 123, Array.Empty<string>());
    var wStale = wRun with { Updated = now.AddMinutes(-19) };
    var wDone = wRun with { State = "SELESAI" };
    var wBad = wRun with { State = "MASALAH" };
    bool effOk = AiTaskParse.Effective(wRun, now, _ => false) == "MATI"
        && AiTaskParse.Effective(wRun, now, _ => true) == "BERJALAN"
        && AiTaskParse.Effective(wStale, now, _ => true) == "MATI"
        && AiTaskParse.Effective(wDone, now, _ => false) == "SELESAI"
        && AiTaskParse.Effective(wBad, now, _ => false) == "MASALAH";

    bool stgOk = AiTaskParse.Stage("menyiapkan worktree") == 1
        && AiTaskParse.Stage("Hydrogen bekerja") == 2
        && AiTaskParse.Stage("memeriksa hasil (build dan tes)") == 3
        && AiTaskParse.Stage("reviewer (gemini-4-argon-medium) membaca diff") == 4
        && AiTaskParse.Stage("apa saja") == 0;

    bool verOk = AiTaskParse.Verdict(new[] { "x", "VONIS: OK" }) == "OK"
        && AiTaskParse.Verdict(new[] { "VONIS: PERIKSA (reviewer gagal)" }) == "PERIKSA"
        && AiTaskParse.Verdict(new[] { "tidak ada" }) == null
        && AiTaskParse.Verdict(new[] { "1. a\n2. b\nVONIS: OK" }) == "OK"
        && AiTaskParse.Verdict(new[] { "x\nVONIS: PERIKSA (reviewer gagal)" }) == "PERIKSA"
        && AiTaskParse.Verdict(new[] { "kalimat bukan VONIS: OK di tengah" }) == null;

    bool tokOk = AiTaskParse.Tokens(new[] { "File berubah:", " M clamp.go", "Durasi: 42 s; token: 33610", "Reviewer (x) - durasi: 54 s; token: 22136" }) == 33610
        && AiTaskParse.Tokens(new[] { "File berubah:" }) == 0;

    bool kindOk = AiTaskParse.Kind("aud-app-svc") == "audit"
        && AiTaskParse.Kind("audit-x") == "audit"
        && AiTaskParse.Kind("ver-capture") == "verify"
        && AiTaskParse.Kind("verify-x") == "verify"
        && AiTaskParse.Kind("zax-aiusage") == "other"
        && AiTaskParse.Kind("Aud-X") == "audit";

    bool descOk = AiTaskParse.Describe(wRun with { Title = "  Judul kustom  " }) == "Judul kustom"
        && AiTaskParse.Describe(wRun with { Name = "aud-app-svc", Title = "" }) == "Audit · app svc"
        && AiTaskParse.Describe(wRun with { Name = "ver-capture", Title = "" }) == "Verify · capture"
        && AiTaskParse.Describe(wRun with { Name = "zax-aiusage", Title = "" }) == "zax aiusage";

    bool gkOk = AiTaskParse.GroupKey(wRun with { Group = "  Tugas A  ", Repo = "repo1", Name = "n1" }) == "Tugas A"
        && AiTaskParse.GroupKey(wRun with { Group = "  ", Repo = "repo1", Name = "n1" }) == "repo1"
        && AiTaskParse.GroupKey(wRun with { Group = "", Repo = "", Name = "n1" }) == "n1";

    bool aiOk = wrkOk && effOk && stgOk && verOk && tokOk && kindOk && descOk && gkOk;
    Console.WriteLine(aiOk ? "AITASK OK" : $"AITASK GAGAL wrk={wrkOk} eff={effOk} stg={stgOk} ver={verOk} tok={tokOk} kind={kindOk} desc={descOk} gk={gkOk}");
    if (!aiOk) fail++;
}

// Cek mandiri pemindai AiTaskScan: worker JSON (urutan, BOM, file rusak, jendela 1 jam)
{
    var tmp = Path.Combine(Path.GetTempPath(), "zx-aiscan-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try
    {
        var utf8Bom = new System.Text.UTF8Encoding(true);
        var nowOff = new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.FromHours(7));
        string WJson(string name, string state, DateTimeOffset upd, int pid) =>
            $"{{\"name\":\"{name}\",\"repo\":\"r\",\"state\":\"{state}\",\"phase\":\"p\",\"model\":\"m\",\"timeoutMin\":8,\"startedAt\":\"{upd.AddMinutes(-5):O}\",\"updatedAt\":\"{upd:O}\",\"pid\":{pid},\"detail\":[]}}";

        var wDir = Path.Combine(tmp, "workers");
        Directory.CreateDirectory(wDir);
        File.WriteAllText(Path.Combine(wDir, "a.json"), WJson("A", "BERJALAN", nowOff.AddMinutes(-2), 111), utf8Bom);
        File.WriteAllText(Path.Combine(wDir, "b.json"), WJson("B", "SELESAI", nowOff.AddMinutes(-10), 112), utf8Bom);
        File.WriteAllText(Path.Combine(wDir, "c.json"), WJson("C", "SELESAI", nowOff.AddHours(-2), 113), utf8Bom);
        File.WriteAllText(Path.Combine(wDir, "d.json"), WJson("D", "BERJALAN", nowOff.AddMinutes(-1), 222), utf8Bom);
        File.WriteAllText(Path.Combine(wDir, "bad.json"), "{\"name\":\"x\",\"sta", utf8Bom);
        File.WriteAllText(Path.Combine(wDir, "note.txt"), "abaikan");

        var rows = AiTaskScan.Workers(wDir, nowOff, pid => pid == 111);
        var emptyRows = AiTaskScan.Workers(Path.Combine(tmp, "tidak-ada"), nowOff, _ => false);
        bool workersOk = rows.Count == 3
            && rows[0].Info.Name == "A" && rows[0].State == "BERJALAN"
            && rows[1].Info.Name == "D" && rows[1].State == "MATI"
            && rows[2].Info.Name == "B" && rows[2].State == "SELESAI"
            && emptyRows.Count == 0;

        WorkerRow MkRow(string name, string state, string phase, DateTimeOffset started) =>
            new(new WorkerInfo(name, "r", state, phase, "m", 8, started, started, 1, Array.Empty<string>()), state);

        var p1 = AiTaskScan.PageOne(new System.Collections.Generic.List<WorkerRow>
        {
            MkRow("s2", "BERJALAN", "Hydrogen bekerja", nowOff),
            MkRow("s4", "BERJALAN", "reviewer membaca diff", nowOff),
            MkRow("s3", "BERJALAN", "memeriksa hasil", nowOff),
            MkRow("done", "SELESAI", "selesai", nowOff),
        }, 2);
        var p2 = AiTaskScan.PageOne(new System.Collections.Generic.List<WorkerRow>
        {
            MkRow("run1", "BERJALAN", "Hydrogen bekerja", nowOff),
            MkRow("d1", "SELESAI", "selesai", nowOff),
            MkRow("d2", "SELESAI", "selesai", nowOff),
            MkRow("d3", "SELESAI", "selesai", nowOff),
        }, 2);
        var p3 = AiTaskScan.PageOne(new System.Collections.Generic.List<WorkerRow>
        {
            MkRow("late", "BERJALAN", "Hydrogen bekerja", nowOff.AddMinutes(-1)),
            MkRow("early", "BERJALAN", "Hydrogen bekerja", nowOff.AddMinutes(-5)),
        }, 2);
        bool pageOneOk = p1.Shown.Count == 2 && p1.Shown[0].Info.Name == "s4" && p1.Shown[1].Info.Name == "s3" && p1.MoreRunning == 1
            && p2.Shown.Count == 2 && p2.Shown[0].Info.Name == "run1" && p2.Shown[1].Info.Name == "d1" && p2.MoreRunning == 0
            && p3.Shown.Count == 2 && p3.Shown[0].Info.Name == "early" && p3.Shown[1].Info.Name == "late";

        WorkerRow MkGrpRow(string name, string repo, string group, string state, DateTimeOffset started) =>
            new(new WorkerInfo(name, repo, state, "p", "m", 8, started, started, 1, Array.Empty<string>(), "", group), state);

        var grps = AiTaskScan.Groups(new System.Collections.Generic.List<WorkerRow>
        {
            MkGrpRow("aud-1", "repo-a", "Audit ZaxLet", "SELESAI", nowOff.AddMinutes(-30)),
            MkGrpRow("ver-1", "repo-a", "audit zaxlet", "BERJALAN", nowOff.AddMinutes(-20)),
            MkGrpRow("aud-2", "repo-a", "Audit ZaxLet", "BERJALAN", nowOff.AddMinutes(-10)),
            MkGrpRow("zax-1", "repo-b", "", "SELESAI", nowOff.AddMinutes(-2)),
            MkGrpRow("zax-2", "repo-b", "", "MATI", nowOff.AddMinutes(-5)),
            MkGrpRow("solo", "", "", "SELESAI", DateTimeOffset.MinValue),
        });
        bool groupsOk = grps.Count == 3
            && grps[0].Title == "repo-b" && grps[0].Rows.Count == 2 && grps[0].Rows[0].Info.Name == "zax-1" && grps[0].Rows[1].Info.Name == "zax-2"
            && grps[1].Title == "Audit ZaxLet" && grps[1].Rows.Count == 3 && grps[1].Rows[0].Info.Name == "aud-2" && grps[1].Rows[1].Info.Name == "ver-1" && grps[1].Rows[2].Info.Name == "aud-1"
            && grps[2].Title == "solo" && grps[2].Rows.Count == 1 && grps[2].Rows[0].Info.Name == "solo";

        bool scanOk = workersOk && pageOneOk && groupsOk;
        Console.WriteLine(scanOk ? "AISCAN OK" : $"AISCAN GAGAL w={workersOk} p1={pageOneOk} grp={groupsOk}");
        if (!scanOk) fail++;
    }
    finally
    {
        try { Directory.Delete(tmp, true); } catch { }
    }
}

// Cek mandiri proyeksi habis kuota (BurnRate)
{
    var t0 = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.FromHours(7));
    var pExhaust = BurnRate.Project(new[] { new BurnSample(t0, 20), new BurnSample(t0.AddMinutes(10), 30), new BurnSample(t0.AddMinutes(20), 40) }, TimeSpan.FromHours(2));
    bool exhaustOk = pExhaust.HasData && pExhaust.WillExhaust && Math.Abs(pExhaust.RatePerHour - 60) < 1e-6 && BurnRate.Format(pExhaust) == "Habis ±1 jam lagi";

    var pSafe = BurnRate.Project(new[] { new BurnSample(t0, 10), new BurnSample(t0.AddMinutes(15), 15), new BurnSample(t0.AddMinutes(30), 20) }, TimeSpan.FromHours(2));
    bool safeOk = pSafe.HasData && !pSafe.WillExhaust && BurnRate.Format(pSafe) == "Aman";

    var pFew = BurnRate.Project(new[] { new BurnSample(t0, 10), new BurnSample(t0.AddMinutes(20), 30) }, TimeSpan.FromHours(2));
    var pShort = BurnRate.Project(new[] { new BurnSample(t0, 10), new BurnSample(t0.AddMinutes(4), 20), new BurnSample(t0.AddMinutes(8), 30) }, TimeSpan.FromHours(2));
    var pFlat = BurnRate.Project(new[] { new BurnSample(t0, 20), new BurnSample(t0.AddMinutes(10), 20), new BurnSample(t0.AddMinutes(20), 20) }, TimeSpan.FromHours(2));
    // laju mendekati nol: dulu TimeSpan.FromHours meluap dan merusak render tab AI
    var pTiny = BurnRate.Project(new[] { new BurnSample(t0, 20), new BurnSample(t0.AddMinutes(10), 20.0000000001), new BurnSample(t0.AddMinutes(20), 20.0000000002) }, TimeSpan.FromHours(2));
    bool fewOk = !pFew.HasData && !pShort.HasData && !pFlat.HasData && BurnRate.Format(pFew) == "" && !pTiny.WillExhaust;

    var samples = new System.Collections.Generic.List<BurnSample>();
    DateTimeOffset? lastReset = null;
    var r1 = t0.AddHours(2);
    BurnRate.Record(samples, t0, 20, r1, ref lastReset);
    BurnRate.Record(samples, t0.AddMinutes(10), 35, r1, ref lastReset);
    BurnRate.Record(samples, t0.AddMinutes(20), 50, r1, ref lastReset);
    bool beforeDrop = BurnRate.Project(samples, TimeSpan.FromHours(2)).HasData;
    BurnRate.Record(samples, t0.AddMinutes(30), 10, r1, ref lastReset); // Pct turun tajam -> kosongkan
    bool afterDrop = samples.Count == 1 && !BurnRate.Project(samples, TimeSpan.FromHours(2)).HasData;
    BurnRate.Record(samples, t0.AddMinutes(40), 15, r1, ref lastReset);
    BurnRate.Record(samples, t0.AddMinutes(50), 20, r1.AddHours(5), ref lastReset); // jendela reset berubah -> kosongkan
    bool afterReset = samples.Count == 1;
    bool dropOk = beforeDrop && afterDrop && afterReset;

    bool burnOk = exhaustOk && safeOk && fewOk && dropOk;
    Console.WriteLine(burnOk ? "BURN OK" : $"BURN GAGAL exh={exhaustOk} safe={safeOk} few={fewOk} drop={dropOk}");
    if (!burnOk) fail++;
}

// ---- pengingat berlabel ----
{
    var now = new DateTime(2026, 11, 21, 14, 0, 0);
    bool a = ReminderLogic.TryParse("15:30 rapat tim", now, out var d1, out var l1) && d1 == new DateTime(2026, 11, 21, 15, 30, 0) && l1 == "rapat tim";
    bool b = ReminderLogic.TryParse("13.05 makan", now, out var d2, out _) && d2 == new DateTime(2026, 11, 22, 13, 5, 0);   // sudah lewat = besok
    bool c = ReminderLogic.TryParse("20m jemuran", now, out var d3, out var l3) && d3 == now.AddMinutes(20) && l3 == "jemuran";
    bool bad = !ReminderLogic.TryParse("25:00 x", now, out _, out _) && !ReminderLogic.TryParse("15:30", now, out _, out _) && !ReminderLogic.TryParse("abc x", now, out _, out _);
    var items = new System.Collections.Generic.List<ReminderItem> {
        new() { Label = "depan", DueAt = now.AddMinutes(5) }, new() { Label = "baru lewat", DueAt = now.AddSeconds(-30) },
        new() { Label = "terlambat", DueAt = now.AddHours(-3) }, new() { Label = "basi", DueAt = now.AddHours(-20) } };
    var (rem, dueList) = ReminderLogic.EvaluateDue(items, now, TimeSpan.FromHours(12), TimeSpan.FromMinutes(2));
    bool ev = rem.Count == 1 && dueList.Count == 2 && !dueList[0].IsLate && dueList[1].IsLate;   // "basi" dibuang tanpa bunyi
    bool ok = a && b && c && bad && ev;
    Console.WriteLine(ok ? "REMIND OK" : $"REMIND GAGAL a={a} b={b} c={c} bad={bad} ev={ev}");
    if (!ok) fail++;
}

return fail;
