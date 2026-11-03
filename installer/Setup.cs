// Pemasang ZaxLet (per-pengguna, tanpa admin). Dikompilasi dengan csc .NET Framework (C# 5) supaya jalan di Windows 10/11 tanpa .NET apa pun.
// Isi aplikasi ada di sumber daya "payload.zip". "Setup.exe /uninstall" (salinan: Uninstall.exe di folder pemasangan) mencopot.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup
{
    const string AppName = "ZaxLet";   // folder pasang dan nilai autostart (harus sama dengan App.xaml.cs)
    const string Title = AppName;        // nama yang tampil
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZaxLet";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RuntimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        foreach (string a in args)
            if (a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)) return Uninstall();
        Application.Run(new InstallForm());
        return 0;
    }

    public static string DefaultDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    }

    /// <summary>Folder pemasangan yang sudah ada (dari catatan pencopot), atau null bila belum terpasang: pemasang beralih ke mode perbarui.</summary>
    public static string ExistingDir()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstKey))
            {
                string d = k == null ? null : k.GetValue("InstallLocation") as string;
                return d != null && File.Exists(Path.Combine(d, "ZaxLet.exe")) ? d : null;
            }
        }
        catch { return null; }
    }

    public static bool AutostartOn()
    {
        try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(AppName) != null; }
        catch { return false; }
    }

    public static bool DesktopLinkExists() { return File.Exists(DesktopLink()); }

    public static bool HasRuntime()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(root)) return false;
        foreach (string d in Directory.GetDirectories(root))
            if (Path.GetFileName(d).StartsWith("8.")) return true;
        return false;
    }

    static void KillRunning()
    {
        foreach (Process p in Process.GetProcessesByName("ZaxLet"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }

    static void Shortcut(string lnk, string target, string dir)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object sh = Activator.CreateInstance(t);
        object s = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
        Type st = s.GetType();
        st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, s, new object[] { target });
        st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, s, new object[] { dir });
        st.InvokeMember("Description", BindingFlags.SetProperty, null, s, new object[] { "Dynamic island untuk Windows" });
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, s, null);
    }

    static string StartMenuLink()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Title + ".lnk");
    }

    static string DesktopLink()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Title + ".lnk");
    }

    /// <summary>Pasang. report(persen, teks) dipanggil dari thread pemasang.</summary>
    public static void Install(string dir, bool autostart, bool desktop, Action<int, string> report)
    {
        report(5, "Menutup ZaxLet yang sedang berjalan…");
        KillRunning();
        Directory.CreateDirectory(dir);

        report(15, "Menyalin berkas…");
        using (Stream res = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (ZipArchive zip = new ZipArchive(res, ZipArchiveMode.Read))
        {
            int n = 0;
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                string dest = Path.GetFullPath(Path.Combine(dir, e.FullName));
                if (!dest.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;   // jangan keluar dari folder tujuan
                if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                e.ExtractToFile(dest, true);
                n++;
                report(15 + 60 * n / Math.Max(1, zip.Entries.Count), "Menyalin berkas…");
            }
        }

        report(80, "Membuat pintasan…");
        string exe = Path.Combine(dir, "ZaxLet.exe");
        string uninst = Path.Combine(dir, "Uninstall.exe");
        File.Copy(Application.ExecutablePath, uninst, true);
        Shortcut(StartMenuLink(), exe, dir);
        if (desktop) Shortcut(DesktopLink(), exe, dir);
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (autostart) k.SetValue(AppName, "\"" + exe + "\"");
            else k.DeleteValue(AppName, false);
        }

        report(90, "Mendaftarkan pencopot…");
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstKey))
        {
            k.SetValue("DisplayName", Title);
            k.SetValue("DisplayIcon", exe);
            k.SetValue("InstallLocation", dir);
            k.SetValue("Publisher", "ZaxLet");
            k.SetValue("DisplayVersion", "1.1.0");
            k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        report(100, "Selesai");
    }

    /// <summary>Unduh dan jalankan pemasang resmi .NET 8 Desktop Runtime (Microsoft). Meminta izin admin (UAC) sendiri.</summary>
    public static bool InstallRuntime(Action<int, string> report)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "windowsdesktop-runtime-8-win-x64.exe");
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768;   // TLS 1.2 / 1.1
        report(2, "Mengunduh .NET 8 Desktop Runtime dari Microsoft…");
        using (WebClient wc = new WebClient()) wc.DownloadFile(RuntimeUrl, tmp);
        report(4, "Memasang .NET 8 Desktop Runtime…");
        ProcessStartInfo psi = new ProcessStartInfo(tmp, "/install /quiet /norestart");
        psi.UseShellExecute = true;
        psi.Verb = "runas";
        using (Process p = Process.Start(psi))
        {
            p.WaitForExit();
            try { File.Delete(tmp); } catch { }
            return p.ExitCode == 0 || p.ExitCode == 3010;
        }
    }

    static int Uninstall()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        if (MessageBox.Show("Copot ZaxLet dari komputer ini?", "Copot ZaxLet", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
        bool wipe = MessageBox.Show("Hapus juga pengaturan dan data ZaxLet (tugas, Shelf, lirik tersimpan)?", "Copot ZaxLet", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        KillRunning();
        try { File.Delete(StartMenuLink()); } catch { }
        try { File.Delete(DesktopLink()); } catch { }
        try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)) k.DeleteValue(AppName, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
        if (wipe)
        {
            try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZaxLet"), true); } catch { }
        }
        // Uninstall.exe sedang berjalan dari folder ini: hapus folder lewat cmd setelah proses ini selesai
        ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"");
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        Process.Start(psi);
        MessageBox.Show("ZaxLet sudah dicopot.", "Copot ZaxLet", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}

sealed class InstallForm : Form
{
    readonly TextBox _dir = new TextBox();
    readonly CheckBox _auto = new CheckBox();
    readonly CheckBox _desk = new CheckBox();
    readonly CheckBox _run = new CheckBox();
    readonly Button _go = new Button();
    readonly ProgressBar _bar = new ProgressBar();
    readonly Label _msg = new Label();
    bool _update;

    public InstallForm()
    {
        Text = "Pasang ZaxLet";
        ClientSize = new Size(480, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Label title = new Label { Text = "ZaxLet", Font = new Font("Segoe UI", 18f, FontStyle.Bold), Location = new Point(20, 14), AutoSize = true };
        Label sub = new Label { Text = "Dynamic island untuk Windows 11: musik, usage AI, notifikasi HP, dan lainnya.", Location = new Point(22, 54), Size = new Size(440, 20) };
        Label l1 = new Label { Text = "Folder pemasangan", Location = new Point(22, 90), AutoSize = true };
        string old = Setup.ExistingDir(); _update = old != null;   // sudah terpasang: mode perbarui (folder dan pilihan lama dipertahankan, pengaturan dan data tidak disentuh)
        _dir.Text = old ?? Setup.DefaultDir();
        _dir.Location = new Point(22, 110);
        _dir.Width = 360;
        Button browse = new Button { Text = "Pilih…", Location = new Point(388, 108), Size = new Size(70, 27) };
        browse.Click += delegate
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
                if (d.ShowDialog() == DialogResult.OK) _dir.Text = Path.Combine(d.SelectedPath, "ZaxLet");
        };
        _auto.Text = "Mulai bersama Windows";
        _auto.Checked = true;
        _auto.Location = new Point(22, 148);
        _auto.AutoSize = true;
        _desk.Text = "Buat pintasan di desktop";
        _desk.Location = new Point(22, 174);
        _desk.AutoSize = true;
        _run.Text = "Jalankan ZaxLet setelah selesai";
        _run.Checked = true;
        _run.Location = new Point(22, 200);
        _run.AutoSize = true;
        if (old != null)
        {
            Text = "Perbarui ZaxLet";
            sub.Text = "ZaxLet sudah terpasang. Berkas aplikasi diganti ke versi ini; pengaturan dan datamu tetap.";
            l1.Text = "Terpasang di";
            _dir.ReadOnly = true; browse.Enabled = false;
            _auto.Checked = Setup.AutostartOn();
            _desk.Checked = Setup.DesktopLinkExists();
        }
        _bar.Location = new Point(22, 232);
        _bar.Size = new Size(436, 8);
        _msg.Location = new Point(22, 246);
        _msg.Size = new Size(300, 36);
        _go.Text = old != null ? "Perbarui" : "Pasang";
        _go.Location = new Point(368, 250);
        _go.Size = new Size(90, 32);
        _go.Click += OnGo;
        Controls.AddRange(new Control[] { title, sub, l1, _dir, browse, _auto, _desk, _run, _bar, _msg, _go });
        AcceptButton = _go;
    }

    void OnGo(object s, EventArgs e)
    {
        if (!Setup.HasRuntime())
        {
            DialogResult r = MessageBox.Show(
                "ZaxLet memerlukan .NET 8 Desktop Runtime (gratis dari Microsoft) dan belum terpasang.\n\nUnduh dan pasang sekarang? (perlu internet dan izin administrator)",
                "Pasang ZaxLet", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
        }
        string dir = _dir.Text.Trim();
        if (dir.Length < 4) { MessageBox.Show("Folder pemasangan tidak valid."); return; }
        bool auto = _auto.Checked, desk = _desk.Checked, run = _run.Checked;
        _go.Enabled = false;
        _dir.Enabled = false;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { Work(dir, auto, desk, run); });
    }

    void Report(int pct, string text)
    {
        BeginInvoke(new Action(delegate { _bar.Value = Math.Max(0, Math.Min(100, pct)); _msg.Text = text; }));
    }

    void Work(string dir, bool auto, bool desk, bool run)
    {
        try
        {
            if (!Setup.HasRuntime() && !Setup.InstallRuntime(Report))
                throw new Exception("Pemasangan .NET 8 Desktop Runtime tidak berhasil. Pasang manual dari dotnet.microsoft.com lalu ulangi.");
            Setup.Install(dir, auto, desk, Report);
            if (run) Process.Start(Path.Combine(dir, "ZaxLet.exe"));
            BeginInvoke(new Action(delegate
            {
                MessageBox.Show(_update ? "ZaxLet sudah diperbarui." : "ZaxLet terpasang. Arahkan kursor ke bagian tengah atas layar. Ikon tray: Buka, Pengaturan, Keluar.", "Pasang ZaxLet", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }));
        }
        catch (Exception ex)
        {
            BeginInvoke(new Action(delegate
            {
                MessageBox.Show("Gagal memasang: " + ex.Message, "Pasang ZaxLet", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _go.Enabled = true;
                _dir.Enabled = true;
            }));
        }
    }
}
