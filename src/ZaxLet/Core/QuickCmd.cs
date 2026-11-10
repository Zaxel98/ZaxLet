using System;

namespace ZaxLet;

/// <summary>
/// Perintah singkat untuk kotak Aksi cepat (pintasan keyboard, gaya rofi/Spotlight): satu baris teks jadi satu aksi, tanpa membuka tab.
/// Murni (tanpa WPF), jadi bisa dites. Contoh: "t 10m", "fokus 25", "catat beli susu", "w https://...", "minum 250", "tema neon", "sembunyi".
/// </summary>
public static class QuickCmd
{
    public enum Kind { None, Timer, Focus, Note, Wish, Water, Theme, Hide, Perf, Bt, Calc, Open, Unknown }

    public sealed record Cmd(Kind Kind, string Arg = "", double Value = 0);

    public const string Help = "t 10m  ·  fokus 25  ·  catat beli susu  ·  w <link>  ·  minum 250  ·  tema neon  ·  = 12*5  ·  5 km mi  ·  buka monitor  ·  perf  ·  bt  ·  sembunyi";

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
            case "bt": case "baterai": case "battery":
                return new Cmd(Kind.Bt);
            case "buka": case "open": case "ke":
                return arg.Length > 0 ? new Cmd(Kind.Open, arg.ToLowerInvariant()) : new Cmd(Kind.Unknown, "buka <nama tab>");
            default:
                return new Cmd(Kind.Unknown, Help);
        }
    }
}
