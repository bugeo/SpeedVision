namespace TrafficSpeedAnalyzer.Core.Measurement;

public sealed record SpeedResult(double DurationSeconds, double SpeedMetersPerSecond, double UncertaintyKmh)
{
    public double SpeedKmh => SpeedMetersPerSecond * SpeedCalculator.MsToKmh;
}

public static class SpeedCalculator
{
    public const double MsToKmh = 3.6;

    /// <summary>
    /// Velocità media sul tratto: distanza / (t_uscita − t_ingresso).
    /// L'incertezza combina quelle dei due istanti (e facoltativamente della distanza).
    /// </summary>
    public static SpeedResult Compute(
        double distanceMeters,
        double entryTimeSeconds,
        double exitTimeSeconds,
        double entrySigmaSeconds = 0,
        double exitSigmaSeconds = 0,
        double distanceSigmaMeters = 0)
    {
        if (!double.IsFinite(distanceMeters) || distanceMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distanceMeters), "La distanza deve essere maggiore di zero.");
        }

        if (!double.IsFinite(entryTimeSeconds) || !double.IsFinite(exitTimeSeconds))
        {
            throw new ArgumentException("Tempi non validi.");
        }

        double duration = exitTimeSeconds - entryTimeSeconds;
        if (duration <= 0)
        {
            throw new ArgumentException("L'uscita deve avvenire dopo l'ingresso.");
        }

        double speed = distanceMeters / duration;
        double relTime = Math.Sqrt(entrySigmaSeconds * entrySigmaSeconds + exitSigmaSeconds * exitSigmaSeconds) / duration;
        double relDist = distanceSigmaMeters / distanceMeters;
        double sigmaKmh = speed * MsToKmh * Math.Sqrt(relTime * relTime + relDist * relDist);
        return new SpeedResult(duration, speed, sigmaKmh);
    }
}
