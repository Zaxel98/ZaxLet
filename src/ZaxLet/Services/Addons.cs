using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace ZaxLet;

/// <summary>
/// Fitur tambahan yang butuh unduhan eksternal. Pemasang tetap kecil: tidak ada yang diunduh sampai pengguna menekan tombol di
/// Pengaturan > Addon. Setiap unduhan diperiksa sidik jarinya (SHA-256) sebelum dipasang ke %LOCALAPPDATA%\ZaxLet.
/// </summary>
public sealed class Addon
{
    public string Id, Name, Desc, Size;
    public Func<bool> Installed;
    public Func<string> Detail;                // keterangan tambahan (mis. terdeteksi di PATH)
    public Func<Action, Task> Install;         // parameter: pemanggil progres (thread latar)
    public Action Remove;
    public Func<string> Progress = () => "";
    public bool Busy;
    public string Error = "";
}

public static class Addons
{
    public static readonly Addon[] All =
    {
        new Addon
        {
            Id = "romaji", Name = "Paket tulisan Latin (semua bahasa)", Size = "±15 MB",
            Desc = "Satu-satunya unduhan untuk tulisan Latin: kamus kanji Jepang (MeCab + IPAdic) dan tabel pinyin Mandarin. Kana, Korea, Rusia/Kiril, Yunani, dan Arab sudah ada di aplikasi tanpa unduhan.",
            Installed = () => RomajiPack.Installed,
            Install = async p => { await RomajiPack.DownloadAsync(p); IslandWindow.Instance?.Dispatcher.BeginInvoke(() => { Translit.ResetPinyin(); MediaService.I.RefreshRomaji(); }); },
            Remove = RomajiPack.Remove,
            Progress = () => RomajiPack.Status,
        },
        new Addon
        {
            Id = "ffmpeg", Name = "ffmpeg (rekam layar)", Size = "±100 MB",
            Desc = "Dibutuhkan tombol Rekam di tab Tangkap. Tangkapan layar biasa tidak memerlukannya.",
            Installed = () => CaptureService.FindFfmpeg() != null,
            Detail = () => File.Exists(FfmpegPack.Exe) ? "" : CaptureService.FindFfmpeg() is { } p ? "terdeteksi: " + p : "",
            Install = FfmpegPack.DownloadAsync,
            Remove = FfmpegPack.Remove,
            Progress = () => FfmpegPack.Status,
        },
    };
}

/// <summary>ffmpeg dari gyan.dev (build "essentials", yang juga ditautkan situs resmi ffmpeg.org). Hash dibaca dari berkas .sha256 di server yang sama.</summary>
public static class FfmpegPack
{
    const string Url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    public static string Exe => Path.Combine(Settings.Dir, "ffmpeg.exe");
    public static string Status = "";

    public static async Task DownloadAsync(Action progress)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "zaxlet-ffmpeg.zip");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            Status = L.T("Mengambil sidik jari…"); progress();
            var want = (await http.GetStringAsync(Url + ".sha256")).Trim().Split(' ', '\n', '\r')[0];
            if (want.Length != 64) throw new InvalidDataException("Sidik jari ffmpeg tidak terbaca.");

            using (var resp = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0, got = 0;
                int last = -1;
                using var s = await resp.Content.ReadAsStreamAsync();
                using var f = File.Create(tmp);
                var buf = new byte[81920]; int n;
                while ((n = await s.ReadAsync(buf)) > 0)
                {
                    f.Write(buf, 0, n); got += n;
                    int pct = total > 0 ? (int)(got * 100 / total) : 0;
                    if (pct != last) { last = pct; Status = L.F("Mengunduh ffmpeg… {0}%", pct); progress(); }
                }
            }
            Status = L.T("Memeriksa berkas…"); progress();
            string have;
            using (var f = File.OpenRead(tmp)) have = Convert.ToHexString(await SHA256.HashDataAsync(f));
            if (!have.Equals(want, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Sidik jari ffmpeg tidak cocok; unduhan dibatalkan.");

            Status = L.T("Memasang ffmpeg…"); progress();
            using var zip = ZipFile.OpenRead(tmp);
            var e = zip.Entries.FirstOrDefault(x => x.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException("ffmpeg.exe tidak ada di arsip.");
            Directory.CreateDirectory(Settings.Dir);
            e.ExtractToFile(Exe, true);
        }
        finally
        {
            Status = "";
            try { File.Delete(tmp); } catch { }
        }
    }

    public static void Remove() { try { File.Delete(Exe); } catch (Exception ex) { App.Log(ex); } }
}
