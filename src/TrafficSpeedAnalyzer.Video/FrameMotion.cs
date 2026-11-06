namespace TrafficSpeedAnalyzer.Video;

/// <summary>
/// Posizione di un fotogramma rispetto al primo: trasformazione (3 × 3, riga per riga) che porta un punto
/// fermo dal primo fotogramma a questo, in pixel del video. Con FFmpeg vidstab è uno spostamento.
/// </summary>
public sealed record FrameMotion(int Frame, double[] H, int Inliers, int Tracked, bool Reliable, bool Projective = false)
{
    public static FrameMotion Identity(int frame, int points) => new(frame, [1, 0, 0, 0, 1, 0, 0, 0, 1], points, points, true);

    /// <summary>Similitudine x' = a·x − b·y + tx, y' = b·x + a·y + ty.</summary>
    public static FrameMotion FromSimilarity(int frame, double a, double b, double tx, double ty, int inliers, int tracked, bool reliable) =>
        new(frame, [a, -b, tx, b, a, ty, 0, 0, 1], inliers, tracked, reliable);

    public (double X, double Y) Apply(double x, double y)
    {
        double w = (H[6] * x) + (H[7] * y) + H[8];
        return (((H[0] * x) + (H[1] * y) + H[2]) / w, ((H[3] * x) + (H[4] * y) + H[5]) / w);
    }

    public double DisplacementAt(double x, double y)
    {
        (double px, double py) = Apply(x, y);
        return Math.Sqrt(((px - x) * (px - x)) + ((py - y) * (py - y)));
    }

    /// <summary>
    /// Spostamento massimo tra gli angoli e il centro della fascia dell'immagine che va da
    /// <paramref name="topFraction"/> dell'altezza fino in fondo (0 = tutta l'immagine).
    /// </summary>
    public double MaxDisplacement(double width, double height, double topFraction = 0)
    {
        double top = topFraction * height;
        return new[] { (width / 2, (top + height) / 2), (0.0, top), (width, top), (0.0, height), (width, height) }
            .Max(p => DisplacementAt(p.Item1, p.Item2));
    }

    public double AngleDegrees => Math.Atan2(H[3], H[0]) * 180 / Math.PI;

    public double Scale => Math.Sqrt((H[0] * H[0]) + (H[3] * H[3]));
}
