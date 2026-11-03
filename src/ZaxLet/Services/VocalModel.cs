using System;

namespace ZaxLet;

/// <summary>
/// Regresi logistik kecil yang belajar per lagu: ciri audio per bingkai -> "vokal" atau "instrumen". Label datang dari waktu lirik
/// (di dalam baris yang dinyanyikan = vokal; jeda panjang/intro = instrumen), jadi model menyesuaikan diri dengan mix lagu itu
/// (vokal keras, pad tebal, gitar di pita vokal, dll.) tanpa ambang tetap. Sampel disimpan dalam reservoir kecil per kelas.
/// </summary>
public sealed class VocalModel
{
    public const int Dim = 5, Cap = 500;
    readonly float[][][] _x = { new float[Cap][], new float[Cap][] };   // [kelas][sampel][ciri]
    readonly int[] _n = new int[2], _seen = new int[2];
    readonly double[] _w = new double[Dim], _mu = new double[Dim], _sd = new double[Dim];
    double _b;
    readonly Random _rnd = new(7);

    public bool Ready { get; private set; }
    public double Accuracy { get; private set; }
    public int Count0 => _n[0];
    public int Count1 => _n[1];

    public void Reset()
    {
        _n[0] = _n[1] = _seen[0] = _seen[1] = 0;
        Array.Clear(_w); _b = 0; Ready = false; Accuracy = 0;
    }

    public void Add(float[] x, bool vocal)
    {
        int c = vocal ? 1 : 0;
        _seen[c]++;
        var copy = (float[])x.Clone();
        if (_n[c] < Cap) _x[c][_n[c]++] = copy;
        else { int j = _rnd.Next(_seen[c]); if (j < Cap) _x[c][j] = copy; }   // reservoir: sampel lama dan baru sama peluangnya
    }

    public double Prob(float[] x)
    {
        double z = _b;
        for (int d = 0; d < Dim; d++) z += _w[d] * (x[d] - _mu[d]) / _sd[d];
        return 1 / (1 + Math.Exp(-z));
    }

    /// <summary>Latih ulang dari sampel yang ada (butuh ≥60 per kelas). Model dipakai hanya bila akurasinya ≥ 0,72.</summary>
    public void Train()
    {
        if (_n[0] < 60 || _n[1] < 60) { Ready = false; return; }
        int total = _n[0] + _n[1];
        for (int d = 0; d < Dim; d++)
        {
            double m = 0, v = 0;
            for (int c = 0; c < 2; c++) for (int i = 0; i < _n[c]; i++) m += _x[c][i][d];
            m /= total;
            for (int c = 0; c < 2; c++) for (int i = 0; i < _n[c]; i++) { double t = _x[c][i][d] - m; v += t * t; }
            _mu[d] = m; _sd[d] = Math.Max(Math.Sqrt(v / total), 1e-6);
        }
        var w = new double[Dim]; double b = 0;
        var g = new double[Dim];
        for (int it = 0; it < 200; it++)
        {
            Array.Clear(g); double gb = 0;
            for (int c = 0; c < 2; c++)
            {
                double cw = 0.5 / _n[c];   // kelas seimbang walau sampel timpang
                for (int i = 0; i < _n[c]; i++)
                {
                    double z = b;
                    for (int d = 0; d < Dim; d++) z += w[d] * (_x[c][i][d] - _mu[d]) / _sd[d];
                    double e = (1 / (1 + Math.Exp(-z)) - c) * cw;
                    for (int d = 0; d < Dim; d++) g[d] += e * (_x[c][i][d] - _mu[d]) / _sd[d];
                    gb += e;
                }
            }
            for (int d = 0; d < Dim; d++) w[d] -= 1.2 * (g[d] + 0.01 * w[d]);
            b -= 1.2 * gb;
        }
        Array.Copy(w, _w, Dim); _b = b;
        double ok = 0;
        for (int c = 0; c < 2; c++)
            for (int i = 0; i < _n[c]; i++) if ((Prob(_x[c][i]) > 0.5) == (c == 1)) ok += 0.5 / _n[c];
        Accuracy = ok;
        Ready = ok >= 0.72;
    }
}
