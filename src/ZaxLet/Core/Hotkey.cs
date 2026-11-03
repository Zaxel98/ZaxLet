using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ZaxLet;

/// <summary>Kirim kombinasi tombol global ("ctrl+shift+m") lewat SendInput, mis. untuk keybind mic/kamera Discord dan pintasan Meet.</summary>
public static class Hotkey
{
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    const uint KEYUP = 2;

    static ushort Vk(string k) => k switch
    {
        "ctrl" => 0x11, "shift" => 0x10, "alt" => 0x12, "win" => 0x5B,
        "space" => 0x20, "enter" => 0x0D, "esc" => 0x1B, "tab" => 0x09,
        _ when k.Length == 1 && char.IsLetterOrDigit(k[0]) => (ushort)char.ToUpperInvariant(k[0]),
        _ when k.StartsWith('f') && int.TryParse(k[1..], out var n) && n is >= 1 and <= 24 => (ushort)(0x6F + n),
        _ => 0,
    };

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    /// <summary>Daftarkan pintasan global ("ctrl+alt+i") yang mengirim WM_HOTKEY ke jendela h. Mendaftar ulang bila id sama. False bila format salah / sudah dipakai aplikasi lain.</summary>
    public static bool Register(IntPtr h, int id, string combo)
    {
        UnregisterHotKey(h, id);
        uint mods = 0, vk = 0;
        foreach (var part in (combo ?? "").ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "alt": mods |= 1; break;
                case "ctrl": mods |= 2; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default: vk = Vk(part); if (vk == 0) return false; break;
            }
        }
        return vk != 0 && RegisterHotKey(h, id, mods | 0x4000, vk);   // 0x4000 = tanpa pengulangan saat ditahan
    }

    /// <summary>Tekan lalu lepas kombinasi. False bila format tidak dikenal.</summary>
    public static bool Send(string combo)
    {
        var keys = new System.Collections.Generic.List<ushort>();
        foreach (var part in (combo ?? "").ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var v = Vk(part);
            if (v == 0) return false;
            keys.Add(v);
        }
        if (keys.Count == 0) return false;
        var inputs = new INPUT[keys.Count * 2];
        for (int i = 0; i < keys.Count; i++)
        {
            inputs[i] = Key(keys[i], 0);
            inputs[inputs.Length - 1 - i] = Key(keys[i], KEYUP);
        }
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    static INPUT Key(ushort vk, uint flags) => new() { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };

    /// <summary>Kirim kombinasi ke jendela yang judulnya memuat teks tertentu (mis. tab Meet di Chrome), lalu kembalikan fokus.</summary>
    public static bool SendToWindow(string titlePart, string combo)
    {
        IntPtr target = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.ToString().Contains(titlePart, StringComparison.OrdinalIgnoreCase)) { target = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (target == IntPtr.Zero) return false;
        var prev = GetForegroundWindow();
        if (IsIconic(target)) ShowWindow(target, 9);
        Send("alt");                       // trik agar Windows mengizinkan SetForegroundWindow dari proses latar
        SetForegroundWindow(target);
        System.Threading.Thread.Sleep(180);
        var ok = Send(combo);
        System.Threading.Thread.Sleep(120);
        if (prev != IntPtr.Zero) SetForegroundWindow(prev);
        return ok;
    }
}
