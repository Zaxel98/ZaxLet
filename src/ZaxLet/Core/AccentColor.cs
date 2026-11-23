namespace ZaxLet;

/// <summary>Converts Windows accent color (DWM registry, DWORD in A-B-G-R order) to "RRGGBB" bright enough on the black pill.</summary>
public static class AccentColor
{
    public static string FromAbgr(int v)
    {
        int r = v & 255, g = (v >> 8) & 255, b = (v >> 16) & 255;
        double lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
        double t = System.Math.Clamp((0.55 - lum) * 1.6, 0, 0.65);   // dark accents are mixed with white; already bright ones are left unchanged
        r += (int)((255 - r) * t); g += (int)((255 - g) * t); b += (int)((255 - b) * t);
        return $"{r:X2}{g:X2}{b:X2}";
    }
}
