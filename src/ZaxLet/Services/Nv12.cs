using System;

namespace ZaxLet;

/// <summary>BGRA -> NV12 (BT.601, rentang terbatas), diletakkan di tengah kanvas W×H; sisanya hitam.</summary>
public static class Nv12
{
    public static void Compose(byte[] bgra, int tw, int th, byte[] dst, int W, int H)
    {
        Array.Fill(dst, (byte)16, 0, W * H);
        Array.Fill(dst, (byte)128, W * H, W * H / 2);
        int ox = (W - tw) / 2 & ~1, oy = (H - th) / 2 & ~1, uv = W * H;
        for (int y = 0; y < th; y += 2)
            for (int x = 0; x < tw; x += 2)
            {
                int r = 0, g = 0, b = 0;
                for (int j = 0; j < 2; j++)
                    for (int i = 0; i < 2; i++)
                    {
                        int s = ((y + j) * tw + x + i) * 4, B = bgra[s], G = bgra[s + 1], R = bgra[s + 2];
                        dst[(oy + y + j) * W + ox + x + i] = (byte)(((66 * R + 129 * G + 25 * B + 128) >> 8) + 16);
                        r += R; g += G; b += B;
                    }
                r >>= 2; g >>= 2; b >>= 2;
                int o = uv + (oy + y) / 2 * W + ox + x;
                dst[o] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                dst[o + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
    }
}
