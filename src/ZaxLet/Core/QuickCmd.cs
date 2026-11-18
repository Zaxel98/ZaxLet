using System;

namespace ZaxLet;

/// <summary>
/// Perintah singkat untuk kotak Aksi cepat (pintasan keyboard, gaya rofi/Spotlight): satu baris teks jadi satu aksi, tanpa membuka tab.
/// Murni (tanpa WPF), jadi bisa dites. Contoh: "t 10m", "fokus 25", "catat beli susu", "w https://...", "minum 250", "tema neon", "sembunyi".
/// </summary>
public static class QuickCmd
{
    public enum Kind { None, Timer, Focus, Note, Wish, Water, Theme, Hide, Perf, Bt, Calc, Open, Lock, Sleep, MusicSleep, Dark, Light, Wifi, Bluetooth, Power, Awake, Unknown }

    public sealed record Cmd(Kind Kind, string Arg = "", double Value = 0);

    public const string Help = "t 10m  ·  fokus 25  ·  catat beli susu  ·  w <link>  ·  minum 250  ·  tema neon  ·  = 12*5  ·  5 km mi  ·  buka monitor  ·  perf  ·  bt  ·  sembunyi  ·  kunci  ·  tidur  ·  tidur musik 30  ·  gelap  ·  terang  ·  wifi off  ·  bluetooth on  ·  daya hemat/seimbang/performa/kembali  ·  jaga [60/on/off]";

    static readonly string[] Themes = { "black", "aurora", "matcha", "senja", "neon" };

    public static Cmd Parse(string input)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0) return new Cmd(Kind.None);
        char c0 = input[0];
        if (c0 == '=' || char.IsDigit(c0) || c0 == '(' || c0 == '-' || c0 == '.')
        {
            var calc = Calc.Run(c0 == '=' ? input[1..] : input);
            return calc != null ? new Cmd(Kind.Calc, calc.Value.Text, calc.Value.Value) : new Cmd(Kind.Unknown, "= 12*5  ·  5 km mi  ·  100 c f");
        }
        int sp = input.IndexOf(' ');
        string verb = (sp < 0 ? input : input[..sp]).ToLowerInvariant(), arg = sp < 0 ? "" : input[(sp + 1)..].Trim();
        switch (verb)
        {
            case "t": case "timer":
                return DurText.Parse(arg, 60, out var sec) && sec >= 1 ? new Cmd(Kind.Timer, arg, sec) : new Cmd(Kind.Unknown, "t 10m = timer 10 menit");   // angka tanpa satuan = menit
            case "fokus": case "focus":
                if (arg.Length == 0) return new Cmd(Kind.Focus, "", 0);   // 0 = lama bawaan dari Pengaturan
                return double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= 1 && m <= 240 ? new Cmd(Kind.Focus, arg, m) : new Cmd(Kind.Unknown, "fokus 25 = 25 menit (1-240)");
            case "catat": case "todo": case "note":
                return arg.Length > 0 ? new Cmd(Kind.Note, arg.Length > 200 ? arg[..200] : arg) : new Cmd(Kind.Unknown, "catat <teks>");
            case "w": case "wish": case "wishlist":
                return Uri.TryCreate(arg, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? new Cmd(Kind.Wish, arg) : new Cmd(Kind.Unknown, "w <link produk>");
            case "minum": case "air": case "drink":
                if (arg.Length == 0) return new Cmd(Kind.Water, "", 0);   // 0 = ukuran gelas bawaan
                return int.TryParse(arg, out var ml) && ml is >= 10 and <= 2000 ? new Cmd(Kind.Water, arg, ml) : new Cmd(Kind.Unknown, "minum 250 = 250 ml (10-2000)");
            case "tema": case "theme":
                var th = arg.ToLowerInvariant();
                if (th == "hitam") th = "black";
                return Array.IndexOf(Themes, th) >= 0 ? new Cmd(Kind.Theme, th) : new Cmd(Kind.Unknown, "tema: hitam, aurora, matcha, senja, neon");
            case "sembunyi": case "hide":
                return new Cmd(Kind.Hide);
            case "perf": case "fps": case "performa":
                return new Cmd(Kind.Perf);
            case "kunci": case "lock": return new Cmd(Kind.Lock);
            case "tidur": case "sleep":
                if (arg.Length == 0) return new Cmd(Kind.Sleep);   // pemanggil wajib meminta konfirmasi
                return ParseMusicSleep(arg);
            case "gelap": case "dark": return new Cmd(Kind.Dark);
            case "terang": case "light": return new Cmd(Kind.Light);
            case "wifi": case "bluetooth":
                var k = verb == "wifi" ? Kind.Wifi : Kind.Bluetooth;   // Value: 1 nyala, 0 mati, -1 balik
                return arg.ToLowerInvariant() switch { "" => new Cmd(k, "", -1), "on" or "nyala" => new Cmd(k, arg, 1), "off" or "mati" => new Cmd(k, arg, 0), _ => new Cmd(Kind.Unknown, verb + " on / off") };
            case "daya": case "power":
                var pm = arg.ToLowerInvariant() switch
                {
                    "hemat" or "saver" => "hemat",
                    "seimbang" or "balanced" => "seimbang",
                    "performa" or "performance" or "high" => "performa",
                    "kembali" or "restore" or "back" => "kembali",
                    _ => null
                };
                return pm != null ? new Cmd(Kind.Power, pm) : new Cmd(Kind.Unknown, "daya hemat / seimbang / performa / kembali");
            case "jaga": case "awake":
                return ParseAwake(arg);
            case "bt": case "baterai": case "battery":
                return new Cmd(Kind.Bt);
            case "buka": case "open": case "ke":
                return arg.Length > 0 ? new Cmd(Kind.Open, arg.ToLowerInvariant()) : new Cmd(Kind.Unknown, "buka <nama tab>");
            default:
                return new Cmd(Kind.Unknown, Help);
        }
    }

    static Cmd ParseMusicSleep(string arg)
    {
        var lo = arg.ToLowerInvariant();
        if (lo is not ("musik" or "music") && !lo.StartsWith("musik ") && !lo.StartsWith("music "))
            return new Cmd(Kind.Unknown, "tidur  ·  tidur musik 30 / akhir / mati");
        int sp = lo.IndexOf(' ');
        string sub = sp < 0 ? "" : lo[(sp + 1)..].Trim();
        if (sub is "akhir" or "akhir lagu" or "end") return new Cmd(Kind.MusicSleep, sub, -1);
        if (sub is "mati" or "off" or "batal" or "0") return new Cmd(Kind.MusicSleep, sub, 0);
        if (sub.EndsWith("m") && sub.Length > 1) sub = sub[..^1].Trim();
        return int.TryParse(sub, out var mins) && mins is >= 1 and <= 480
            ? new Cmd(Kind.MusicSleep, sub, mins)
            : new Cmd(Kind.Unknown, "tidur musik 30 / akhir / mati (1-480 menit)");
    }

    static Cmd ParseAwake(string arg)
    {
        var lo = arg.ToLowerInvariant();
        if (lo.Length == 0) return new Cmd(Kind.Awake, "toggle");
        if (lo is "on" or "nyala") return new Cmd(Kind.Awake, "on");
        if (lo is "off" or "mati") return new Cmd(Kind.Awake, "off");
        if (lo is "status" or "cek" or "?") return new Cmd(Kind.Awake, "status");
        if (lo.EndsWith("m") && lo.Length > 1) lo = lo[..^1].Trim();
        return int.TryParse(lo, out var mins) && mins is >= 1 and <= 1440
            ? new Cmd(Kind.Awake, "timer", mins)
            : new Cmd(Kind.Unknown, "jaga [menit / on / off / status] (1-1440 menit)");
    }
}
