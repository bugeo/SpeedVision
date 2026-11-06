using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Calibration;

public enum DetectionLine
{
    A,
    B,
}

/// <summary>
/// Configurazione della misura: due linee nel fotogramma e la distanza reale tra loro,
/// misurata dall'utente sul campo.
/// </summary>
public sealed record CalibrationSettings(LineSegment LineA, LineSegment LineB, double DistanceMeters)
{
    public LineSegment GetLine(DetectionLine line) => line == DetectionLine.A ? LineA : LineB;

    /// <summary>
    /// Distanza media in pixel tra le due linee, misurata perpendicolarmente
    /// da ciascun punto medio all'altra linea.
    /// </summary>
    public double MeanPixelSeparation =>
        (Math.Abs(LineB.SignedDistance(LineA.Midpoint)) + Math.Abs(LineA.SignedDistance(LineB.Midpoint))) / 2.0;

    /// <summary>
    /// Scala media metri/pixel nella zona tra le linee. È solo indicativa: per effetto della
    /// prospettiva la scala reale cambia con la distanza dalla camera.
    /// </summary>
    public double ApproxMetersPerPixel
    {
        get
        {
            double sep = MeanPixelSeparation;
            return sep > 1e-9 ? DistanceMeters / sep : double.NaN;
        }
    }

    /// <summary>
    /// Lato della linea <paramref name="line"/> su cui si trova l'altra linea (+1 o -1).
    /// Serve a capire il verso di attraversamento.
    /// </summary>
    public int SideTowardsOther(DetectionLine line)
    {
        LineSegment self = GetLine(line);
        LineSegment other = GetLine(line == DetectionLine.A ? DetectionLine.B : DetectionLine.A);
        return self.SideOf(other.Midpoint);
    }
}
