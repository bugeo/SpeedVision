using System.Globalization;
using System.Text.RegularExpressions;

namespace TrafficSpeedAnalyzer.Core.Calibration;

/// <summary>Punto sulla Terra in gradi decimali (WGS84, come GPS e OpenStreetMap).</summary>
public readonly record struct GeoPosition(double Latitude, double Longitude)
{
    public bool IsValid =>
        double.IsFinite(Latitude) && double.IsFinite(Longitude) && Math.Abs(Latitude) <= 90 && Math.Abs(Longitude) <= 180;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.000000}, {Longitude:0.000000}");

    /// <summary>
    /// Posizione nel formato ISO 6709 dei metadati dei video (iPhone: <c>+45.4642+009.1900+120.000/</c>,
    /// Android: <c>+45.4642+009.1900/</c>). Null se assente o non valida.
    /// </summary>
    public static GeoPosition? ParseIso6709(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match m = Iso6709().Match(text.Trim());
        if (!m.Success
            || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
            || !double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
        {
            return null;
        }

        var p = new GeoPosition(lat, lon);
        return p.IsValid && !(lat == 0 && lon == 0) ? p : null;
    }

    private static Regex Iso6709() => new(@"^([+-]\d{1,2}(?:\.\d+)?)([+-]\d{1,3}(?:\.\d+)?)", RegexOptions.CultureInvariant);
}

/// <summary>Distanze sulla Terra (ellissoide WGS84).</summary>
public static class GeoMath
{
    private const double A = 6378137.0;                 // semiasse maggiore
    private const double F = 1 / 298.257223563;         // schiacciamento
    private const double B = A * (1 - F);

    /// <summary>
    /// Distanza in metri tra due punti, con la formula inversa di Vincenty sull'ellissoide WGS84
    /// (precisione sub-millimetrica). Per punti quasi agli antipodi, dove Vincenty non converge,
    /// si usa la formula sulla sfera.
    /// </summary>
    public static double DistanceMeters(GeoPosition p, GeoPosition q)
    {
        double l = ToRad(q.Longitude - p.Longitude);
        double u1 = Math.Atan((1 - F) * Math.Tan(ToRad(p.Latitude)));
        double u2 = Math.Atan((1 - F) * Math.Tan(ToRad(q.Latitude)));
        double sinU1 = Math.Sin(u1), cosU1 = Math.Cos(u1), sinU2 = Math.Sin(u2), cosU2 = Math.Cos(u2);

        double lambda = l, sinSigma, cosSigma, sigma, cos2Alpha, cos2SigmaM;
        for (int i = 0; ; i++)
        {
            double sinLambda = Math.Sin(lambda), cosLambda = Math.Cos(lambda);
            double t1 = cosU2 * sinLambda, t2 = (cosU1 * sinU2) - (sinU1 * cosU2 * cosLambda);
            sinSigma = Math.Sqrt((t1 * t1) + (t2 * t2));
            if (sinSigma == 0)
            {
                return 0;   // stesso punto
            }

            cosSigma = (sinU1 * sinU2) + (cosU1 * cosU2 * cosLambda);
            sigma = Math.Atan2(sinSigma, cosSigma);
            double sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cos2Alpha = 1 - (sinAlpha * sinAlpha);
            cos2SigmaM = cos2Alpha == 0 ? 0 : cosSigma - (2 * sinU1 * sinU2 / cos2Alpha);
            double c = F / 16 * cos2Alpha * (4 + (F * (4 - (3 * cos2Alpha))));
            double previous = lambda;
            lambda = l + ((1 - c) * F * sinAlpha * (sigma + (c * sinSigma * (cos2SigmaM + (c * cosSigma * (-1 + (2 * cos2SigmaM * cos2SigmaM)))))));
            if (Math.Abs(lambda - previous) < 1e-12)
            {
                break;
            }

            if (i > 200)
            {
                return HaversineMeters(p, q);
            }
        }

        double uSq = cos2Alpha * ((A * A) - (B * B)) / (B * B);
        double bigA = 1 + (uSq / 16384 * (4096 + (uSq * (-768 + (uSq * (320 - (175 * uSq)))))));
        double bigB = uSq / 1024 * (256 + (uSq * (-128 + (uSq * (74 - (47 * uSq))))));
        double deltaSigma = bigB * sinSigma * (cos2SigmaM + (bigB / 4 * ((cosSigma * (-1 + (2 * cos2SigmaM * cos2SigmaM)))
            - (bigB / 6 * cos2SigmaM * (-3 + (4 * sinSigma * sinSigma)) * (-3 + (4 * cos2SigmaM * cos2SigmaM))))));
        return B * bigA * (sigma - deltaSigma);
    }

    private static double HaversineMeters(GeoPosition p, GeoPosition q)
    {
        const double r = 6371008.8;
        double dLat = ToRad(q.Latitude - p.Latitude), dLon = ToRad(q.Longitude - p.Longitude);
        double h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) + (Math.Cos(ToRad(p.Latitude)) * Math.Cos(ToRad(q.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180;
}
