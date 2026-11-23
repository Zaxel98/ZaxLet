using System;
using System.Collections.Generic;

namespace ZaxLet;

public readonly record struct BurnSample(DateTimeOffset At, double Pct);

public readonly record struct BurnProjection(bool HasData, bool WillExhaust, TimeSpan? TimeToExhaust, double RatePerHour);

/// <summary>Estimates quota burn rate (%/hour) and projects exhaustion before the reset window.</summary>
public static class BurnRate
{
    public const int MaxSamples = 30;
    public const int MinSamples = 3;
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);
    public static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(10);
    const double ResetSlackMin = 2;
    const double DropThresholdPct = 5;

    public static void Record(List<BurnSample> samples, DateTimeOffset at, double pct, DateTimeOffset? reset, ref DateTimeOffset? lastReset)
    {
        if (samples == null || double.IsNaN(pct) || pct < 0) return;
        bool resetChanged = reset != null && lastReset != null
            ? Math.Abs((reset.Value - lastReset.Value).TotalMinutes) > ResetSlackMin
            : reset != lastReset;
        if (resetChanged || (samples.Count > 0 && samples[^1].Pct - pct >= DropThresholdPct))
            samples.Clear();
        lastReset = reset;

        var cutoff = at - MaxAge;
        samples.RemoveAll(s => s.At < cutoff);
        if (samples.Count > 0 && (at - samples[^1].At).TotalSeconds < 5)
            samples[^1] = new BurnSample(at, pct);
        else
            samples.Add(new BurnSample(at, pct));
        while (samples.Count > MaxSamples)
            samples.RemoveAt(0);
    }

    public static BurnProjection Project(IReadOnlyList<BurnSample> samples, TimeSpan? timeToReset)
    {
        if (samples == null || samples.Count < MinSamples) return default;
        if (timeToReset is not { } resetIn || resetIn <= TimeSpan.Zero) return default;
        var t0 = samples[0].At;
        if ((samples[^1].At - t0) < MinSpan) return default;

        int n = samples.Count;
        double sumT = 0, sumY = 0;
        for (int i = 0; i < n; i++)
        {
            sumT += (samples[i].At - t0).TotalHours;
            sumY += samples[i].Pct;
        }
        double meanT = sumT / n, meanY = sumY / n;
        double stt = 0, sty = 0;
        for (int i = 0; i < n; i++)
        {
            double dt = (samples[i].At - t0).TotalHours - meanT;
            stt += dt * dt;
            sty += dt * (samples[i].Pct - meanY);
        }
        if (stt <= 0) return default;
        double rate = sty / stt;
        if (rate <= 0 || double.IsNaN(rate) || double.IsInfinity(rate)) return default;

        double remPct = Math.Max(0, 100.0 - samples[^1].Pct);
        // very small rate makes hours infinite: TimeSpan.FromHours overflows (OverflowException) and breaks AI tab rendering; cap at 1 million hours
        var exhaustIn = TimeSpan.FromHours(Math.Min(remPct / rate, 1e6));
        return new BurnProjection(true, exhaustIn < resetIn, exhaustIn, rate);
    }

    public static string Format(BurnProjection p)
    {
        if (!p.HasData) return "";
        if (!p.WillExhaust || p.TimeToExhaust is not { } t) return L.T("Aman");
        return L.F("Habis ±{0} lagi", DurText.Fmt(Math.Max(1, t.TotalSeconds)));
    }
}
