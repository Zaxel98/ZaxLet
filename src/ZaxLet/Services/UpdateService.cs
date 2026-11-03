using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace ZaxLet;

/// <summary>
/// Pembaruan otomatis dari GitHub Releases (repo di Settings.UpdateRepo). Alurnya: cek rilis terbaru -> unduh ZaxLet-update.zip ->
/// cocokkan SHA-256 -> ekstrak ke folder sementara -> skrip kecil menunggu ZaxLet keluar, menyalin berkasnya ke folder aplikasi, dan
/// membuka ZaxLet lagi. Data pengguna (%LOCALAPPDATA%\ZaxLet) tidak disentuh. Repo privat tidak bisa diakses tanpa token, jadi
/// pembaruan baru berfungsi setelah repo publik. ZAXLET_UPDATE_URL mengganti alamat API (uji).
/// </summary>
public static class UpdateService
{
    public static string Status = "";
    public static UpdateCheck.Release Available;
    public static bool Busy;
    public static event Action Changed;

    public static Version Current => typeof(UpdateService).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);

    static string ApiUrl => Environment.GetEnvironmentVariable("ZAXLET_UPDATE_URL") is { Length: > 0 } u ? u
        : $"https://api.github.com/repos/{Settings.Cur.UpdateRepo}/releases/latest";

    static HttpClient Http()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ZaxLet-updater/1.0");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    static void Set(string s) { Status = s; Changed?.Invoke(); }

    /// <summary>Periksa rilis terbaru. Hasil: <see cref="Available"/> (null bila sudah terbaru) dan <see cref="Status"/>.</summary>
    public static async Task CheckAsync()
    {
        if (Busy) return;
        Busy = true; Set(L.T("Memeriksa pembaruan…"));
        try
        {
            using var http = Http();
            using var resp = await http.GetAsync(ApiUrl);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { Available = null; Set(L.T("Belum ada rilis yang bisa diunduh (repo privat atau belum ada rilis)")); return; }
            resp.EnsureSuccessStatusCode();
            var rel = UpdateCheck.ParseRelease(await resp.Content.ReadAsStringAsync());
            Settings.Cur.UpdateCheckedAt = DateTime.Now; Settings.Save();
            if (rel == null) { Available = null; Set(L.T("Rilis terbaru tidak memuat berkas pembaruan")); return; }
            if (!UpdateCheck.IsNewer(rel.Version, Current)) { Available = null; Set(L.T("ZaxLet sudah versi terbaru")); return; }
            if (rel.Sha256.Length != 64 && rel.ShaUrl != null && UpdateCheck.HostAllowed(rel.ShaUrl))
                rel = rel with { Sha256 = UpdateCheck.ReadShaFile(await http.GetStringAsync(rel.ShaUrl)) };
            if (rel.Sha256.Length != 64) { Available = null; Set(L.T("Rilis tidak menyertakan SHA-256: pembaruan ditolak")); return; }
            Available = rel;
            Set(L.F("Versi {0} tersedia", rel.Version));
        }
        catch (Exception ex) { App.Log(ex); Available = null; Set(L.T("Tidak bisa terhubung ke GitHub. Periksa internet lalu coba lagi.")); }
        finally { Busy = false; Changed?.Invoke(); }
    }

    /// <summary>Unduh, verifikasi, lalu jalankan skrip penyalin dan keluar. Gagal di tahap mana pun: berkas lama tidak disentuh.</summary>
    public static async Task ApplyAsync()
    {
        var rel = Available;
        if (rel == null || Busy) return;
        Busy = true;
        try
        {
            if (!UpdateCheck.HostAllowed(rel.Url)) { Set(L.T("Alamat unduhan tidak diizinkan")); return; }
            var work = Path.Combine(Path.GetTempPath(), "zaxlet-update-" + rel.Version);
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, UpdateCheck.AssetName);
            Set(L.T("Mengunduh pembaruan…"));
            using (var http = Http())
            {
                http.Timeout = TimeSpan.FromMinutes(10);
                using var resp = await http.GetAsync(rel.Url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                if (!UpdateCheck.HostAllowed(resp.RequestMessage?.RequestUri?.ToString() ?? rel.Url)) { Set(L.T("Alamat unduhan tidak diizinkan")); return; }
                await using var fs = File.Create(zip);
                await resp.Content.CopyToAsync(fs);
            }
            if (!UpdateCheck.Verify(zip, rel.Sha256)) { File.Delete(zip); Set(L.T("File unduhan rusak (SHA-256 tidak cocok)")); return; }
            var src = Path.Combine(work, "files");
            UpdateCheck.ExtractSafe(zip, src);

            var dst = AppContext.BaseDirectory.TrimEnd('\\');
            var exe = Path.Combine(dst, "ZaxLet.exe");
            var script = Path.Combine(work, "apply.cmd");
            // tunggu ZaxLet keluar, salin (robocopy mencoba ulang selagi berkas masih terkunci), lalu buka lagi
            File.WriteAllText(script, "@echo off\r\ntimeout /t 2 /nobreak >nul\r\n" +
                $"robocopy \"{src}\" \"{dst}\" /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul\r\n" +
                $"start \"\" \"{exe}\" --updated\r\n");
            Set(L.T("Memasang pembaruan…"));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = work });
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
        catch (Exception ex) { App.Log(ex); Set(L.T("Pembaruan gagal: ") + ex.Message); }
        finally { Busy = false; Changed?.Invoke(); }
    }

    /// <summary>Periksa otomatis paling sering tiap ±20 jam; bila ada versi baru, pill menawarkan pembaruan (klik untuk memasang).</summary>
    public static async Task AutoCheckAsync()
    {
        if (!Settings.Cur.AutoUpdate || (DateTime.Now - Settings.Cur.UpdateCheckedAt).TotalHours < 20) return;
        await CheckAsync();
        if (Available != null)
            IslandWindow.Instance?.Alert(L.F("ZaxLet {0} tersedia · klik untuk memperbarui", Available.Version), 20, () => _ = ApplyAsync());
    }
}
