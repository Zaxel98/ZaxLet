using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.VisualBasic.FileIO;

namespace ZaxLet;

public sealed record DupFile(string Path, long Size, DateTime Modified);

public sealed record DupGroup(long Size, string Hash, IReadOnlyList<DupFile> Files);

/// <summary>
/// Pure duplicate file finder (no UI) with staged filtering:
/// exact size -> quick hash (first 64 KB + last 64 KB + size) -> full streaming SHA-256.
/// </summary>
public static class DupFinder
{
    public const int MaxFiles = 200_000;
    public const long MinFileBytes = 1024;
    const int QuickChunkBytes = 64 * 1024;
    const int StreamBufferBytes = 81_920;
    const long ProgressIntervalMs = 200;

    static string Norm(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    static bool IsSameOrUnder(string path, string parent)
    {
        if (string.IsNullOrEmpty(parent)) return false;
        var p = Norm(path);
        var par = Norm(parent);
        return string.Equals(p, par, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(par + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ensures the folder is safe to scan (rejects drive roots, Windows, Program Files, ProgramData, user profile root, and AppData).
    /// Returns the normalized full path.
    /// </summary>
    public static string ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException(L.T("Folder tidak boleh kosong."));

        string full;
        try { full = Path.GetFullPath(folder); }
        catch (Exception ex) { throw new ArgumentException(L.T("Path folder tidak valid."), ex); }

        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(L.T("Folder tidak ditemukan."));

        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(Norm(full), Norm(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(L.T("Root drive (seperti C:\\) tidak diizinkan. Pilih subfolder yang lebih spesifik."));
        }

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (IsSameOrUnder(full, win))
            throw new InvalidOperationException(L.T("Folder sistem Windows tidak diizinkan."));

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (IsSameOrUnder(full, pf) || IsSameOrUnder(full, pf86))
            throw new InvalidOperationException(L.T("Folder Program Files tidak diizinkan."));

        var progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (IsSameOrUnder(full, progData))
            throw new InvalidOperationException(L.T("Folder ProgramData tidak diizinkan."));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile) &&
            string.Equals(Norm(full), Norm(userProfile), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(L.T("Root profil pengguna tidak diizinkan. Pilih subfolder seperti Documents atau Downloads."));
        }

        var appDataRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDataLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataRoot = string.IsNullOrEmpty(userProfile) ? "" : Path.Combine(userProfile, "AppData");
        if (IsSameOrUnder(full, appDataRoaming) || IsSameOrUnder(full, appDataLocal) || IsSameOrUnder(full, appDataRoot))
            throw new InvalidOperationException(L.T("Folder AppData tidak diizinkan."));

        return full;
    }

    public static List<DupGroup> Scan(string folder, IProgress<string> progress, CancellationToken ct)
    {
        string safeFolder = ValidateFolder(folder);
        ct.ThrowIfCancellationRequested();

        bool rootHidden = (File.GetAttributes(safeFolder) & FileAttributes.Hidden) != 0;
        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | (rootHidden ? 0 : FileAttributes.Hidden),
            RecurseSubdirectories = true
        };

        var sw = Stopwatch.StartNew();
        long lastReportMs = -ProgressIntervalMs;

        void Report(string msg, bool force = false)
        {
            if (progress == null) return;
            long now = sw.ElapsedMilliseconds;
            if (!force && now - lastReportMs < ProgressIntervalMs) return;
            lastReportMs = now;
            progress.Report(msg);
        }

        // Stage 1: enumerate & group by exact size
        var bySize = new Dictionary<long, List<DupFile>>();
        int enumerated = 0;

        Report(L.T("Mengumpulkan daftar berkas..."), force: true);
        foreach (var path in Directory.EnumerateFiles(safeFolder, "*", enumOpts))
        {
            ct.ThrowIfCancellationRequested();
            if (++enumerated > MaxFiles) break;

            if (enumerated % 250 == 0)
                Report(L.F("Memindai {0:N0} berkas...", enumerated));

            try
            {
                var fi = new FileInfo(path);
                long len = fi.Length;
                if (len < MinFileBytes) continue;

                if (!bySize.TryGetValue(len, out var list))
                {
                    list = new List<DupFile>(2);
                    bySize[len] = list;
                }
                list.Add(new DupFile(path, len, fi.LastWriteTime));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var sizeCandidates = bySize.Values.Where(g => g.Count >= 2).ToList();
        int totalCandidates = sizeCandidates.Sum(g => g.Count);
        if (totalCandidates == 0)
        {
            Report(L.T("Selesai: tidak ditemukan duplikat."), force: true);
            return new List<DupGroup>();
        }

        // Stage 2: quick hash (first 64 KB + last 64 KB + length)
        var quickCandidates = new List<(long Size, List<DupFile> Files)>();
        var quickBuf = new byte[QuickChunkBytes];
        int checkedCount = 0;

        foreach (var group in sizeCandidates)
        {
            ct.ThrowIfCancellationRequested();
            var byQuick = new Dictionary<string, List<DupFile>>(StringComparer.Ordinal);

            foreach (var file in group)
            {
                ct.ThrowIfCancellationRequested();
                checkedCount++;
                Report(L.F("Memeriksa {0:N0} / {1:N0} berkas", checkedCount, totalCandidates));

                var qh = ComputeQuickHash(file.Path, file.Size, quickBuf, ct);
                if (qh == null) continue;

                if (!byQuick.TryGetValue(qh, out var qList))
                {
                    qList = new List<DupFile>(2);
                    byQuick[qh] = qList;
                }
                qList.Add(file);
            }

            foreach (var qList in byQuick.Values)
            {
                if (qList.Count >= 2)
                    quickCandidates.Add((group[0].Size, qList));
            }
        }

        int fullTotal = quickCandidates.Sum(g => g.Files.Count);
        if (fullTotal == 0)
        {
            Report(L.T("Selesai: tidak ditemukan duplikat."), force: true);
            return new List<DupGroup>();
        }

        // Stage 3: full SHA-256 with 81920-byte streaming buffer
        var result = new List<DupGroup>();
        var streamBuf = new byte[StreamBufferBytes];
        int fullDone = 0;

        foreach (var (size, files) in quickCandidates)
        {
            ct.ThrowIfCancellationRequested();
            var byFull = new Dictionary<string, List<DupFile>>(StringComparer.Ordinal);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                fullDone++;
                Report(L.F("Menghitung SHA-256 {0:N0} / {1:N0} berkas", fullDone, fullTotal));

                var hash = ComputeFullSha256(file.Path, streamBuf, ct);
                if (hash == null) continue;

                if (!byFull.TryGetValue(hash, out var fList))
                {
                    fList = new List<DupFile>(2);
                    byFull[hash] = fList;
                }
                fList.Add(file);
            }

            foreach (var kv in byFull)
            {
                if (kv.Value.Count >= 2)
                {
                    var ordered = kv.Value.OrderByDescending(f => f.Modified).ToList();
                    result.Add(new DupGroup(size, kv.Key, ordered));
                }
            }
        }

        result.Sort((a, b) => ((b.Files.Count - 1) * b.Size).CompareTo((a.Files.Count - 1) * a.Size));
        Report(L.F("Selesai: {0:N0} grup duplikat ditemukan.", result.Count), force: true);
        return result;
    }

    static string ComputeQuickHash(string path, long size, byte[] buf, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, QuickChunkBytes, FileOptions.RandomAccess);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            Span<byte> lenBytes = stackalloc byte[8];
            BitConverter.TryWriteBytes(lenBytes, size);
            hasher.AppendData(lenBytes);

            int read1 = ReadAtMost(fs, buf, QuickChunkBytes, ct);
            if (read1 > 0) hasher.AppendData(buf.AsSpan(0, read1));

            if (size > QuickChunkBytes)
            {
                long tailPos = Math.Max(0, size - QuickChunkBytes);
                fs.Seek(tailPos, SeekOrigin.Begin);
                int read2 = ReadAtMost(fs, buf, QuickChunkBytes, ct);
                if (read2 > 0) hasher.AppendData(buf.AsSpan(0, read2));
            }

            return Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static string ComputeFullSha256(string path, byte[] buf, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, StreamBufferBytes, FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.AppendData(buf.AsSpan(0, n));
            }

            return Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static int ReadAtMost(FileStream fs, byte[] buf, int maxBytes, CancellationToken ct)
    {
        int total = 0;
        while (total < maxBytes)
        {
            ct.ThrowIfCancellationRequested();
            int n = fs.Read(buf, total, maxBytes - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Moves the file list to the Recycle Bin (never permanently deletes).</summary>
    public static (List<string> Moved, List<(string Path, string Error)> Failed) MoveToRecycleBin(IEnumerable<string> paths)
    {
        var moved = new List<string>();
        var failed = new List<(string, string)>();
        foreach (var path in paths)
        {
            try
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                moved.Add(path);
            }
            catch (Exception ex)
            {
                failed.Add((path, ex.Message));
            }
        }
        return (moved, failed);
    }

    public static string FmtSize(long bytes)
    {
        var ci = CultureInfo.CurrentCulture;
        if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.##", ci) + " GB";
        if (bytes >= 1024L * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.#", ci) + " MB";
        return Math.Max(1, (long)Math.Round(bytes / 1024.0)).ToString(ci) + " KB";
    }
}
