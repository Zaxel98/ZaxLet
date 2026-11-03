using System;

namespace ZaxLet;

/// <summary>
/// Waktu sholat dihitung lokal (algoritma PrayTimes.org). Default Kemenag RI: Subuh 20°, Isya 18°, Asr Syafi'i, ihtiyat +2 menit.
/// </summary>
public static class PrayerCalc
{
    public sealed record Times(DateTime Fajr, DateTime Sunrise, DateTime Dhuhr, DateTime Asr, DateTime Maghrib, DateTime Isha)
    {
        public (string Name, DateTime At)[] All => new[]
        {
            ("Subuh", Fajr), ("Dzuhur", Dhuhr), ("Ashar", Asr), ("Maghrib", Maghrib), ("Isya", Isha)
        };
    }

    static double Rad(double d) => d * Math.PI / 180;
    static double Deg(double r) => r * 180 / Math.PI;
    static double Sin(double d) => Math.Sin(Rad(d));
    static double Cos(double d) => Math.Cos(Rad(d));
    static double Fix(double a, double n) { a %= n; return a < 0 ? a + n : a; }

    static double Julian(int y, int m, int d)
    {
        if (m <= 2) { y--; m += 12; }
        double a = Math.Floor(y / 100.0), b = 2 - a + Math.Floor(a / 4);
        return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + b - 1524.5;
    }

    static (double Decl, double Eqt) Sun(double jd)
    {
        double d = jd - 2451545.0;
        double g = Fix(357.529 + 0.98560028 * d, 360);
        double q = Fix(280.459 + 0.98564736 * d, 360);
        double l = Fix(q + 1.915 * Sin(g) + 0.020 * Sin(2 * g), 360);
        double e = 23.439 - 0.00000036 * d;
        double ra = Fix(Deg(Math.Atan2(Cos(e) * Sin(l), Cos(l))) / 15, 24);
        double eqt = q / 15 - ra;
        if (eqt > 12) eqt -= 24;
        if (eqt < -12) eqt += 24;
        return (Deg(Math.Asin(Sin(e) * Sin(l))), eqt);
    }

    public static Times Compute(DateTime date, double lat, double lon, double tz, double fajrDeg = 20, double ishaDeg = 18, int ihtiyatMin = 2, double asrFactor = 1)
    {
        double jd = Julian(date.Year, date.Month, date.Day) - lon / (15 * 24);

        double Mid(double t) => Fix(12 - Sun(jd + t).Eqt, 24);
        double Angle(double angle, double t, bool ccw)
        {
            var decl = Sun(jd + t).Decl;
            double x = (-Sin(angle) - Sin(decl) * Sin(lat)) / (Cos(decl) * Cos(lat));
            double dt = Deg(Math.Acos(Math.Clamp(x, -1, 1))) / 15;
            return Mid(t) + (ccw ? -dt : dt);
        }
        double Asr(double t)
        {
            var decl = Sun(jd + t).Decl;
            double ang = -Deg(Math.Atan(1 / (asrFactor + Math.Tan(Rad(Math.Abs(lat - decl))))));
            return Angle(ang, t, false);
        }

        // dua putaran: perkiraan awal lalu diperhalus
        double fajr = 5, rise = 6, dhuhr = 12, asr = 13, set = 18, isha = 18;
        for (int i = 0; i < 2; i++)
        {
            fajr = Angle(fajrDeg, fajr / 24, true);
            rise = Angle(0.833, rise / 24, true);
            dhuhr = Mid(dhuhr / 24);
            asr = Asr(asr / 24);
            set = Angle(0.833, set / 24, false);
            isha = Angle(ishaDeg, isha / 24, false);
        }
        double shift = tz - lon / 15;
        DateTime At(double h, int extraMin) => date.Date.AddMinutes(Math.Ceiling((h + shift) * 60 - 1e-6) + extraMin);
        return new Times(At(fajr, ihtiyatMin), At(rise, -ihtiyatMin), At(dhuhr, ihtiyatMin), At(asr, ihtiyatMin), At(set, ihtiyatMin), At(isha, ihtiyatMin));
    }
}
