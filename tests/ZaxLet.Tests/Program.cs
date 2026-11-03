using System;
using System.IO;
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

return fail;
