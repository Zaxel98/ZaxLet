using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace ZaxLet;

/// <summary>
/// Auto-update from GitHub Releases (repo in Settings.UpdateRepo). Flow: check latest release -> download ZaxLet-update.zip ->
/// verify SHA-256 -> extract to temp folder -> small script waits for ZaxLet to exit, copies files to app folder, and
/// reopens ZaxLet. User data (%LOCALAPPDATA%\ZaxLet) is untouched. Private repos cannot be accessed without a token, so
/// updates only work once the repo is public. ZAXLET_UPDATE_URL overrides the API address (testing).
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

    /// <summary>Check latest release. Result: <see cref="Available"/> (null if already up to date) and <see cref="Status"/>.</summary>
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

    /// <summary>Download, verify, then run copy script and exit. Fails at any stage: existing files are untouched.</summary>
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
            // wait for ZaxLet to exit (max ~20 s), copy (robocopy retries while files are still locked), then reopen
            File.WriteAllText(script, "@echo off\r\n" +
                "for /l %%i in (1,1,20) do (\r\n" +
                "  tasklist /FI \"IMAGENAME eq ZaxLet.exe\" /NH | find /I \"ZaxLet.exe\" >nul || goto :go\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                ")\r\n" +
                ":go\r\n" +
                $"robocopy \"{src}\" \"{dst}\" /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul\r\n" +
                $"start \"\" \"{exe}\" --updated\r\n");
            bool writable;   // install folders in Program Files etc. require admin to copy: request UAC, don't fail silently after ZaxLet exits
            try { var probe = Path.Combine(dst, ".zaxlet-write-test"); File.WriteAllText(probe, ""); File.Delete(probe); writable = true; }
            catch { writable = false; }
            Set(L.T(writable ? "Memasang pembaruan…" : "Folder ZaxLet butuh izin admin untuk pembaruan: pilih Ya di jendela Windows."));
            Process.Start(writable
                ? new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = work }
                : new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = work });
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
        catch (Exception ex) { App.Log(ex); Set(L.T("Pembaruan gagal: ") + ex.Message); }
        finally { Busy = false; Changed?.Invoke(); }
    }

    /// <summary>Auto-check at most once every ±20 hours; if a new version exists, the pill offers an update (click to install).</summary>
    public static async Task AutoCheckAsync()
    {
        if (!Settings.Cur.AutoUpdate || (DateTime.Now - Settings.Cur.UpdateCheckedAt).TotalHours < 20) return;
        await CheckAsync();
        if (Available != null)
            IslandWindow.Instance?.Alert(L.F("ZaxLet {0} tersedia · klik untuk memperbarui", Available.Version), 20, () => _ = ApplyAsync());
    }
}
