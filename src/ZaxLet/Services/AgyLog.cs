using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;

namespace ZaxLet;

/// <summary>
/// Antigravity tokens from local conversation databases (app + CLI). Each model step stores
/// protobuf metadata: field 1.1 = epoch seconds, field 9 = usage (2 = new input, 3 = output).
/// Cache-read tokens (field 5) are excluded. Read via Windows' built-in winsqlite3.dll, with no extra dependencies.
/// </summary>
static class AgyLog
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly Dictionary<string, (DateTime Mt, long Len, List<(long Ts, long Tok)> Ev)> _cache = new();

    public static (long Today, long Week, long Month) Scan()
    {
        var now = DateTime.Now;
        long today = new DateTimeOffset(now.Date).ToUnixTimeSeconds(), week = new DateTimeOffset(UsagePeriod.WeekStart(now)).ToUnixTimeSeconds(),
             month = new DateTimeOffset(UsagePeriod.MonthStart(now)).ToUnixTimeSeconds(), oldest = Math.Min(week, month);
        long t = 0, w = 0, m = 0;
        var live = new HashSet<string>();
        foreach (var dir in new[] { Path.Combine(Home, ".gemini", "antigravity", "conversations"), Path.Combine(Home, ".gemini", "antigravity-cli", "conversations") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.db"))
            {
                if (Path.GetFileName(f).StartsWith("conversation_summaries")) continue;
                var fi = new FileInfo(f); live.Add(f);
                // files last modified before the oldest period cannot contain its steps
                if (new DateTimeOffset(fi.LastWriteTime).ToUnixTimeSeconds() < oldest) continue;
                if (!_cache.TryGetValue(f, out var c) || c.Mt != fi.LastWriteTimeUtc || c.Len != fi.Length)
                {
                    c = (fi.LastWriteTimeUtc, fi.Length, Read(f));
                    _cache[f] = c;
                }
                foreach (var (ts, tok) in c.Ev) { if (ts >= today) t += tok; if (ts >= week) w += tok; if (ts >= month) m += tok; }
            }
        }
        foreach (var k in _cache.Keys.Where(k => !live.Contains(k)).ToList()) _cache.Remove(k);
        return (t, w, m);
    }

    static List<(long, long)> Read(string path)
    {
        var ev = new List<(long, long)>();
        IntPtr db = IntPtr.Zero, st = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(new Uri(path).AbsoluteUri + "?mode=ro", out db, 0x41, null) != 0) return ev;
            sqlite3_busy_timeout(db, 500);
            if (sqlite3_prepare_v2(db, "select metadata from steps where metadata is not null", -1, out st, IntPtr.Zero) != 0) return ev;
            while (sqlite3_step(st) == 100)
            {
                int n = sqlite3_column_bytes(st, 0);
                var p = sqlite3_column_blob(st, 0);
                if (n <= 0 || p == IntPtr.Zero) continue;
                var b = new byte[n]; Marshal.Copy(p, b, 0, n);
                if (!Get(b, 9, out _, out var use) || !Get(b, 1, out _, out var tm) || !Get(tm, 1, out var sec, out _)) continue;
                Get(use, 2, out var inp, out _); Get(use, 3, out var outp, out _);
                if (inp + outp > 0) ev.Add((sec, inp + outp));
            }
        }
        catch (Exception ex) { App.Log(ex); }
        finally { if (st != IntPtr.Zero) sqlite3_finalize(st); if (db != IntPtr.Zero) sqlite3_close(db); }
        return ev;
    }

    /// <summary>First protobuf field numbered <paramref name="field"/>: varint value or submessage payload.</summary>
    static bool Get(byte[] b, int field, out long v, out byte[] sub)
    {
        v = 0; sub = Array.Empty<byte>();
        int i = 0;
        long Var() { long r = 0; int s = 0; while (i < b.Length) { byte c = b[i++]; r |= (long)(c & 127) << s; s += 7; if ((c & 128) == 0) break; } return r; }
        while (i < b.Length)
        {
            long tag = Var(); int f = (int)(tag >> 3), w = (int)(tag & 7);
            if (w == 0) { long x = Var(); if (f == field) { v = x; return true; } }
            else if (w == 2)
            {
                int l = (int)Var(); if (l < 0 || i + l > b.Length) return false;
                if (f == field) { sub = b[i..(i + l)]; i += l; return true; }
                i += l;
            }
            else if (w == 1) i += 8;
            else if (w == 5) i += 4;
            else return false;
        }
        return false;
    }

    const string Lib = "winsqlite3.dll";
    [DllImport(Lib)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string f, out IntPtr db, int flags, string vfs);
    [DllImport(Lib)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib)] static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int n, out IntPtr st, IntPtr tail);
    [DllImport(Lib)] static extern int sqlite3_step(IntPtr st);
    [DllImport(Lib)] static extern int sqlite3_column_bytes(IntPtr st, int i);
    [DllImport(Lib)] static extern IntPtr sqlite3_column_blob(IntPtr st, int i);
    [DllImport(Lib)] static extern int sqlite3_finalize(IntPtr st);
    [DllImport(Lib)] static extern int sqlite3_close(IntPtr db);
}
