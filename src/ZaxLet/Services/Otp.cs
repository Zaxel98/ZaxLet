using System.Text.RegularExpressions;

namespace ZaxLet;

/// <summary>One-time code (OTP) in notification text: recognized by keyword + 4-8 digit number. Copying it: ClipboardHistory.CopySecret.</summary>
public static class Otp
{
    static readonly Regex Kw = new(@"\b(otp|kode|code|verifikasi|verification|verify|sandi|password|passcode|pin|token|one[- ]time|sekali pakai|autentikasi|authentication)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Num = new(@"(?<!\d)(?<!\d[.,])(\d{3}[ -]\d{3}|\d{4,8})(?!\d)(?![.,]\d)", RegexOptions.Compiled);

    /// <summary>First code found, without spaces/hyphens; null if the text is not a code message.</summary>
    public static string Find(string title, string text)
    {
        var s = (title ?? "") + " " + (text ?? "");
        if (!Kw.IsMatch(s)) return null;
        var m = Num.Match(s);
        return m.Success ? m.Groups[1].Value.Replace(" ", "").Replace("-", "") : null;
    }
}
