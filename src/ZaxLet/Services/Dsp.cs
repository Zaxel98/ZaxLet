using System;

namespace ZaxLet;

/// <summary>FFT kecil untuk analisis vokal (tanpa pustaka).</summary>
public static class Dsp
{
    /// <summary>FFT radix-2 di tempat; panjang harus pangkat dua.</summary>
    public static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    float tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    float nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    /// <summary>Daya tiap bin (0..n/2-1) dari satu bingkai yang di-window.</summary>
    public static double[] Power(float[] frame, float[] window)
    {
        int n = frame.Length;
        var re = new float[n]; var im = new float[n];
        for (int i = 0; i < n; i++) re[i] = frame[i] * window[i];
        Fft(re, im);
        var p = new double[n / 2];
        for (int k = 1; k < n / 2; k++) p[k] = (double)re[k] * re[k] + (double)im[k] * im[k];
        return p;
    }

    public static float[] Hann(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        return w;
    }

    /// <summary>Daya spektrum satu bingkai (sudah di-window) di pita [loHz, hiHz] dan di seluruh pita [allLo, allHi].</summary>
    public static (double Band, double All) BandPower(float[] frame, float[] window, int sampleRate, double loHz, double hiHz, double allLo, double allHi)
    {
        int n = frame.Length;
        var re = new float[n]; var im = new float[n];
        for (int i = 0; i < n; i++) re[i] = frame[i] * window[i];
        Fft(re, im);
        double hz = (double)sampleRate / n, band = 0, all = 0;
        for (int k = 1; k < n / 2; k++)
        {
            double f = k * hz, p = (double)re[k] * re[k] + (double)im[k] * im[k];
            if (f >= allLo && f <= allHi) all += p;
            if (f >= loHz && f <= hiHz) band += p;
        }
        return (band, all);
    }
}
