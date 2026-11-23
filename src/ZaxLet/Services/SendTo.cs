using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ZaxLet;

/// <summary>
/// Send files to phone from the Explorer right-click menu (SendTo): creates/removes the <c>ZaxLet HP.lnk</c> shortcut,
/// forwards arguments from a second instance via named pipe, and adds files to Shelf + sends a command to the phone.
/// </summary>
public static class SendTo
{
    const string PipeName = "ZaxLet.SendPhone";
    const long MaxFileBytes = 200L * 1024 * 1024;
    static Thread _server;

    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "ZaxLet HP.lnk");

    public static void Sync(bool on)
    {
        try
        {
            var lnk = ShortcutPath;
            if (!on)
            {
                if (File.Exists(lnk)) File.Delete(lnk);
                return;
            }
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var dir = Path.GetDirectoryName(lnk);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t);
            try
            {
                dynamic sc = shell.CreateShortcut(lnk);
                try
                {
                    sc.TargetPath = exe;
                    sc.Arguments = "--send-phone";
                    sc.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
                    sc.Description = "Kirim ke HP lewat ZaxLet";
                    sc.Save();
                }
                finally { if (Marshal.IsComObject(sc)) Marshal.FinalReleaseComObject(sc); }
            }
            finally { if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public static bool ForwardToRunning(IEnumerable<string> paths)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            using var w = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
            foreach (var p in paths)
            {
                if (!string.IsNullOrWhiteSpace(p)) w.WriteLine(p.Trim());
            }
            return true;
        }
        catch { return false; }
    }

    public static void StartPipeServer()
    {
        if (_server != null) return;
        _server = new Thread(PipeLoop) { IsBackground = true, Name = "send-phone-pipe" };
        _server.Start();
    }

    static void PipeLoop()
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                pipe.WaitForConnection();
                using var r = new StreamReader(pipe, Encoding.UTF8);
                var list = new List<string>();
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
                }
                if (list.Count > 0) HandleFiles(list);
            }
            catch { Thread.Sleep(250); }
        }
    }

    public static void HandleFiles(IEnumerable<string> paths)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = raw.Trim();
            if (Directory.Exists(path) || !File.Exists(path)) continue;
            long len;
            try { len = new FileInfo(path).Length; } catch { continue; }
            if (len > MaxFileBytes) continue;

            Shelf.Add(path);
            if (ShareServer.I.AppCount > 0)
            {
                var items = Shelf.Snapshot();
                int idx = items.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    var name = Path.GetFileName(path);
                    var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
                    if (ShareServer.I.Command($"file {idx} {b64}") > 0)
                    {
                        IslandWindow.Instance?.Alert(L.F("Dikirim ke HP: {0}", name), 5);
                        continue;
                    }
                }
            }
            IslandWindow.Instance?.Alert(L.T("Berkas masuk Shelf (HP belum tersambung)"), 5);
        }
    }
}
