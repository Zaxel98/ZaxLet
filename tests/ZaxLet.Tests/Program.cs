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

// Cek mandiri pembaca halaman produk Wishlist: meta og (urutan atribut bebas), harga gaya Indonesia, cadangan JSON-LD, sumber dari domain, judul dari link
var wp = WishParse.Meta("<html><head><title>Cadangan</title><meta content=\"Fuwa &amp; Petit\" property=\"og:title\"><meta property='og:image' content='/img/a.jpg'><meta property=\"product:price:amount\" content=\"149.000\"><meta property=\"product:price:currency\" content=\"idr\"></head></html>");
var wj = WishParse.Meta("<title>Plush</title><script type=\"application/ld+json\">{\"priceCurrency\":\"IDR\",\"price\":\"129000.00\"}</script>");
bool wishOk = wp.Title == "Fuwa & Petit" && wp.Image == "/img/a.jpg" && wp.Price == "IDR 149,000"
              && wj.Title == "Plush" && wj.Price == "IDR 129,000" && WishParse.Meta("").Price == "" && WishParse.Meta("<meta content=\"x\"").Title == ""
              && WishParse.Source("https://shopee.co.id/x") == "Shopee" && WishParse.Source("https://www.tokopedia.com/a/b") == "Tokopedia"
              && WishParse.Source("https://kyou-hobby.com/p") == "Kyou Hobby" && WishParse.Source("https://toko.example.co.id/p") == "Example" && WishParse.Source("bukan link") == ""
              && WishParse.SlugTitle("https://shopee.co.id/Fuwa-Petit-Mascot-Akiyama-Mizuki-i.123.456?sp=1") == "Fuwa Petit Mascot Akiyama Mizuki";
wishOk = wishOk && WishParse.PriceFromText("Rp149.000") == "IDR 149,000" && WishParse.PriceFromText("Harga: IDR 1,250,000.") == "IDR 1,250,000"
         && WishParse.PriceFromText("149000") == "IDR 149,000" && WishParse.PriceFromText("Rp 75.500,00") == "IDR 75.500,00" && WishParse.PriceFromText("tidak ada harga") == "" && WishParse.PriceFromText(null) == "";
Console.WriteLine(wishOk ? "WISH OK" : $"WISH GAGAL {wp} {wj} {WishParse.SlugTitle("https://shopee.co.id/Fuwa-Petit-Mascot-Akiyama-Mizuki-i.123.456?sp=1")}");
if (!wishOk) fail++;

// Cek mandiri pengenal kode OTP di notifikasi: perlu kata kunci + angka 4-8 digit; harga, tahun-tanpa-kunci, dan teks biasa tidak dianggap kode
bool otpOk = Otp.Find("Bank Rey", "Kode OTP kamu 482913. Jangan bagikan.") == "482913"
             && Otp.Find("Google", "G-123 456 is your verification code") == "123456"
             && Otp.Find("Shopee", "Gunakan 5821 sebagai kode verifikasi") == "5821"
             && Otp.Find("Toko", "Total pembayaran Rp 150.000 sudah diterima") == null
             && Otp.Find("Kakak", "Nanti jam 19.30 ya, bawa 12345") == null
             && Otp.Find(null, null) == null;
Console.WriteLine(otpOk ? "OTP OK" : "OTP GAGAL");
if (!otpOk) fail++;

// Cek mandiri pengenal rapat: aplikasi rapat asli cukup dari mikrofon; peramban perlu judul tab rapat; tab biasa dan tanpa mikrofon bukan rapat
bool meetOk = MeetingDetect.Classify(new[] { "Zoom" }, new string[0]) == "Zoom"
              && MeetingDetect.Classify(new[] { "ms-teams" }, null) == "Teams"
              && MeetingDetect.Classify(new[] { "chrome" }, new[] { "Meet - abc-defg-hij - Google Chrome" }) == "Meet"
              && MeetingDetect.Classify(new[] { "chrome" }, new[] { "Perekam suara - Google Chrome" }) == null
              && MeetingDetect.Classify(new string[0], new[] { "Meet - abc-defg-hij" }) == null
              && MeetingDetect.Classify(new[] { "discord" }, new string[0]) == null
              && MeetingDetect.Classify(new[] { "WhatsApp.Root" }, null) == "WhatsApp";
Console.WriteLine(meetOk ? "MEETING OK" : "MEETING GAGAL");
if (!meetOk) fail++;

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

// Cek mandiri jembatan ekstensi Wishlist: kunci link, asal permintaan, token, dan validasi kiriman halaman (masukan tidak tepercaya)
var png = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
var okPage = WishBridgeCore.ParsePage("{\"url\":\"https://shopee.co.id/Sepatu-i.1.2?xptdk=abc\",\"title\":\"Sepatu Bristol | Shopee Indonesia\",\"price\":\"\",\"priceText\":\"Rp149.000\",\"image\":\"data:image/png;base64," + png + "\"}");
bool bridgeOk = WishBridgeCore.Key("https://www.Shopee.co.id/Sepatu-i.1.2/?xptdk=zzz&utm_source=a#x") == WishBridgeCore.Key("https://shopee.co.id/Sepatu-i.1.2")
                && WishBridgeCore.Key("https://a.com/p?id=1") != WishBridgeCore.Key("https://a.com/p?id=2")
                && WishBridgeCore.Key("ftp://a.com/x") == null && WishBridgeCore.Key("bukan link") == null
                && WishBridgeCore.OriginAllowed("chrome-extension://abcdef") && !WishBridgeCore.OriginAllowed("https://evil.example") && !WishBridgeCore.OriginAllowed(null)
                && WishBridgeCore.TokenOk("abc123", "abc123") && !WishBridgeCore.TokenOk("abc124", "abc123") && !WishBridgeCore.TokenOk("", "") && !WishBridgeCore.TokenOk("abc", "abc123")
                && WishBridgeCore.CleanTitle("Jual Ortuseight Sepatu Sneakers Bristol 1.2 - Ash Grey | Shopee Indonesia") == "Ortuseight Sepatu Sneakers Bristol 1.2 - Ash Grey"
                && WishBridgeCore.CleanTitle("Jual Aku") == "Jual Aku"
                && okPage != null && okPage.Title == "Sepatu Bristol" && okPage.Price == "IDR 149,000" && okPage.Image != null && WishBridgeCore.ImageExt(okPage.Image) == ".png"
                && WishBridgeCore.ParsePage("{\"url\":\"javascript:alert(1)\",\"title\":\"x\"}") == null
                && WishBridgeCore.ParsePage("{\"url\":\"https://a.com/p\",\"title\":\"\"}") == null
                && WishBridgeCore.ParsePage("bukan json") == null
                && WishBridgeCore.ParsePage("{\"url\":\"https://a.com/p\",\"title\":\"Ok\",\"image\":\"data:image/png;base64,AAAA\"}").Image == null   // bukan gambar sungguhan: dibuang
                && WishBridgeCore.ParsePage("{\"url\":\"https://a.com/p\",\"title\":\"Ok\",\"price\":\"149000\",\"currency\":\"IDR\"}").Price == "IDR 149,000";
bridgeOk = bridgeOk && WishBridgeCore.ParseNotify("{\"text\":\"Build selesai\",\"seconds\":6}") is { Text: "Build selesai", Seconds: 6 }
           && WishBridgeCore.ParseNotify("{\"text\":\"a\\nb\",\"seconds\":999}") is { Text: "a b", Seconds: 30 }
           && WishBridgeCore.ParseNotify("{\"text\":\"x\",\"seconds\":0}") is { Seconds: 2 }
           && WishBridgeCore.ParseNotify("{\"text\":\"\"}") == null && WishBridgeCore.ParseNotify("bukan json") == null && WishBridgeCore.ParseNotify("{\"seconds\":5}") == null;
Console.WriteLine(bridgeOk ? "BRIDGE OK" : "BRIDGE GAGAL");

// Cek mandiri filter sumber musik dan warna aksen Windows
bool miscOk = MediaFilter.Ignored("Discord.exe", "discord, zoom") && MediaFilter.Ignored("us.zoom.Zoom", "Zoom") && !MediaFilter.Ignored("Spotify.exe", "discord, zoom")
              && !MediaFilter.Ignored("Spotify.exe", "") && !MediaFilter.Ignored("Spotify.exe", "s") && !MediaFilter.Ignored(null, "discord")
              && AccentColor.FromAbgr(unchecked((int)0xFFD77800)) != "0078D7"          // biru Windows (R0 G120 B215) gelap: dicerahkan
              && AccentColor.FromAbgr(unchecked((int)0xFFFFFF00)) == "00FFFF"          // sudah terang (cyan): tidak berubah
              && AccentColor.FromAbgr(unchecked((int)0xFF000000)).Length == 6;
var qc = new[] { QuickCmd.Parse("t 10m"), QuickCmd.Parse("timer 90"), QuickCmd.Parse("fokus"), QuickCmd.Parse("fokus 25"), QuickCmd.Parse("catat beli susu"), QuickCmd.Parse("w https://kyou.id/items/1"), QuickCmd.Parse("minum"), QuickCmd.Parse("minum 250"), QuickCmd.Parse("tema Neon"), QuickCmd.Parse("sembunyi") };
miscOk = miscOk && qc[0] is { Kind: QuickCmd.Kind.Timer, Value: 600 } && qc[1] is { Kind: QuickCmd.Kind.Timer, Value: 5400 } && qc[2] is { Kind: QuickCmd.Kind.Focus, Value: 0 } && qc[3] is { Kind: QuickCmd.Kind.Focus, Value: 25 }
          && qc[4] is { Kind: QuickCmd.Kind.Note, Arg: "beli susu" } && qc[5].Kind == QuickCmd.Kind.Wish && qc[6] is { Kind: QuickCmd.Kind.Water, Value: 0 } && qc[7] is { Kind: QuickCmd.Kind.Water, Value: 250 }
          && qc[8] is { Kind: QuickCmd.Kind.Theme, Arg: "neon" } && qc[9].Kind == QuickCmd.Kind.Hide
          && QuickCmd.Parse("").Kind == QuickCmd.Kind.None && QuickCmd.Parse("t abc").Kind == QuickCmd.Kind.Unknown && QuickCmd.Parse("fokus 999").Kind == QuickCmd.Kind.Unknown
          && QuickCmd.Parse("w bukan link").Kind == QuickCmd.Kind.Unknown && QuickCmd.Parse("catat").Kind == QuickCmd.Kind.Unknown && QuickCmd.Parse("minum 5").Kind == QuickCmd.Kind.Unknown && QuickCmd.Parse("tema pelangi").Kind == QuickCmd.Kind.Unknown && QuickCmd.Parse("xyz").Kind == QuickCmd.Kind.Unknown;
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
if (!bridgeOk) fail++;

return fail;
