using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;

namespace TrafficSpeedAnalyzer.Core.Tests;

/// <summary>
/// Genera traiettorie sintetiche a velocità nota per verificare i calcoli contro una verità esatta.
/// Non simula il rilevamento: produce solo posizioni ideali (eventualmente con rumore) di un punto.
/// Scena: linea A verticale a x = 200, linea B verticale a x = 600, 400 px = 20 m (0,05 m/px).
/// </summary>
internal static class SyntheticTrack
{
    public const double LineAX = 200;
    public const double LineBX = 600;
    public const double DistanceMeters = 20;
    public const double MetersPerPixel = DistanceMeters / (LineBX - LineAX);

    public static readonly LineSegment LineA = new(new Point2(LineAX, 0), new Point2(LineAX, 1000));
    public static readonly LineSegment LineB = new(new Point2(LineBX, 0), new Point2(LineBX, 1000));

    public static Calibration.CalibrationSettings TestCalibration => new(LineA, LineB, DistanceMeters);

    /// <summary>Timestamp a frame rate costante con fase iniziale arbitraria.</summary>
    public static List<double> ConstantFrameTimes(double fps, double start, double end, double phase = 0)
    {
        var times = new List<double>();
        double dt = 1.0 / fps;
        for (double t = start + phase; t <= end; t += dt)
        {
            times.Add(t);
        }

        return times;
    }

    /// <summary>Timestamp a frame rate variabile: ogni intervallo varia casualmente di ±jitter.</summary>
    public static List<double> VariableFrameTimes(Random rng, double fps, double start, double end, double jitter)
    {
        var times = new List<double>();
        double t = start;
        while (t <= end)
        {
            times.Add(t);
            t += (1.0 / fps) * (1 + jitter * (2 * rng.NextDouble() - 1));
        }

        return times;
    }

    /// <summary>
    /// Posizioni di un punto che si muove di moto rettilineo uniforme.
    /// Il punto si trova in <paramref name="p0"/> all'istante 0 e si muove con velocità <paramref name="velocityPxPerS"/>.
    /// </summary>
    public static List<TrackObservation> Linear(
        IReadOnlyList<double> times,
        Point2 p0,
        Point2 velocityPxPerS,
        Random? noiseRng = null,
        double noiseSigmaPx = 0)
    {
        var list = new List<TrackObservation>(times.Count);
        for (int i = 0; i < times.Count; i++)
        {
            Point2 p = p0 + velocityPxPerS * times[i];
            if (noiseRng is not null && noiseSigmaPx > 0)
            {
                p += new Point2(Gaussian(noiseRng) * noiseSigmaPx, Gaussian(noiseRng) * noiseSigmaPx);
            }

            list.Add(new TrackObservation(i, times[i], p));
        }

        return list;
    }

    public static double KmhToPxPerS(double kmh) => kmh / 3.6 / MetersPerPixel;

    public static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
