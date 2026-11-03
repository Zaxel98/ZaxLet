using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ZaxLet;

/// <summary>Unduhan selesai di folder Downloads: berkas sementara peramban (.crdownload, .part…) diabaikan; dilaporkan setelah ukurannya stabil.</summary>
public sealed class DownloadWatch
{
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static readonly string[] Temp = { ".crdownload", ".part", ".tmp", ".download", ".opdownload", ".partial", ".!ut", ".aria2" };
    readonly HashSet<string> _seen = new();
    FileSystemWatcher _fw;
    public event Action<string> Finished;

    public static string DownloadsDir()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public static bool IsTemp(string path) => Temp.Contains(Path.GetExtension(path).ToLowerInvariant());

    public void Start(string dirOverride = null)
    {
        try
        {
            var dir = dirOverride ?? DownloadsDir();
            if (!Directory.Exists(dir)) return;
            _fw = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            _fw.Created += (_, e) => _ = Check(e.FullPath);
            _fw.Renamed += (_, e) => _ = Check(e.FullPath);
        }
        catch (Exception) { /* folder tidak bisa diawasi: fitur ini diam saja */ }
    }

    async Task Check(string path)
    {
        if (IsTemp(path) || Directory.Exists(path)) return;
        lock (_seen) { if (!_seen.Add(path)) return; if (_seen.Count > 200) _seen.Clear(); }
        long last = -1;
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(700);
            try
            {
                var len = new FileInfo(path).Length;
                if (len > 0 && len == last) { Finished?.Invoke(path); return; }
                last = len;
            }
            catch (IOException) { last = -1; }   // masih dikunci penulisnya
            catch { return; }
        }
    }
}

