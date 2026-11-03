using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ZaxLet;

/// <summary>
/// Pembaca penghitung kinerja Windows (PDH) dengan karakter pengganti, tanpa paket tambahan dan tanpa admin. Dipakai untuk suhu zona
/// termal ACPI dan beban GPU semua merek ("GPU Engine"). Hanya 64-bit (tata letak struktur hasil dibaca langsung dari memori).
/// Penghitung laju (persen GPU) baru bernilai pada pembacaan kedua, jadi query dipertahankan antar pemanggilan.
/// </summary>
public sealed class PdhQuery : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

    const uint FmtDouble = 0x200, FmtNoCap100 = 0x8000, MoreData = 0x800007D2;
    IntPtr _q;
    readonly List<IntPtr> _c = new();

    public PdhQuery()
    {
        if (IntPtr.Size != 8 || PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) _q = IntPtr.Zero;
    }

    /// <summary>Nomor penghitung untuk <see cref="Read"/>, atau -1 bila kategorinya tidak ada di PC ini.</summary>
    public int Add(string path)
    {
        if (_q == IntPtr.Zero || PdhAddEnglishCounterW(_q, path, IntPtr.Zero, out var c) != 0) return -1;
        _c.Add(c);
        return _c.Count - 1;
    }

    public bool Collect() => _q != IntPtr.Zero && PdhCollectQueryData(_q) == 0;

    /// <summary>Semua instance penghitung bernilai sah sejak <see cref="Collect"/> terakhir (nama instance, nilai).</summary>
    public List<(string Name, double Value)> Read(int idx)
    {
        var r = new List<(string, double)>();
        if (idx < 0 || idx >= _c.Count) return r;
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(_c[idx], FmtDouble | FmtNoCap100, ref size, ref count, IntPtr.Zero) != MoreData || size == 0) return r;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(_c[idx], FmtDouble | FmtNoCap100, ref size, ref count, buf) != 0) return r;
            for (int i = 0; i < count; i++)
            {
                var item = buf + i * 24;   // PDH_FMT_COUNTERVALUE_ITEM (x64): nama (8) + status (4, +4 isi) + nilai (8)
                int status = Marshal.ReadInt32(item, 8);
                if (status != 0 && status != 1) continue;   // PDH_CSTATUS_VALID_DATA / NEW_DATA
                r.Add((Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "", BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16))));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return r;
    }

    public void Dispose() { if (_q != IntPtr.Zero) { PdhCloseQuery(_q); _q = IntPtr.Zero; } }
}
