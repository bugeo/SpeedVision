using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Geometry;

/// <summary>Segmento tra due punti del fotogramma: rappresenta una linea di rilevamento.</summary>
public readonly record struct LineSegment(Point2 Start, Point2 End)
{
    public Point2 Direction => End - Start;

    public double Length => Direction.Length;

    public Point2 Midpoint => Point2.Lerp(Start, End, 0.5);

    public bool IsDegenerate => Length < 1e-9;

    /// <summary>
    /// Distanza con segno del punto dalla retta del segmento, in pixel.
    /// Il segno indica il lato; il modulo è la distanza perpendicolare.
    /// </summary>
    public double SignedDistance(Point2 p)
    {
        double len = Length;
        if (len < 1e-12)
        {
            throw new InvalidOperationException(Loc.T("Segmento degenere: i due estremi coincidono."));
        }

        return Point2.Cross(Direction, p - Start) / len;
    }

    /// <summary>Lato del punto rispetto alla retta: +1, -1 oppure 0 se sulla retta.</summary>
    public int SideOf(Point2 p) => Math.Sign(SignedDistance(p));

    /// <summary>Parametro (0 = Start, 1 = End) della proiezione del punto sulla retta del segmento.</summary>
    public double ProjectParameter(Point2 p)
    {
        double len2 = Point2.Dot(Direction, Direction);
        return len2 < 1e-24 ? 0 : Point2.Dot(p - Start, Direction) / len2;
    }

    public Point2 PointAt(double t) => Point2.Lerp(Start, End, t);

    /// <summary>Angolo della direzione del segmento rispetto all'asse x, in radianti.</summary>
    public double Angle => Math.Atan2(Direction.Y, Direction.X);
}
