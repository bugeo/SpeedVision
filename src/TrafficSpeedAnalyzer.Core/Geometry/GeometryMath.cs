namespace TrafficSpeedAnalyzer.Core.Geometry;

public static class GeometryMath
{
    /// <summary>
    /// Intersezione tra due segmenti. Restituisce i parametri t (lungo <paramref name="a"/>)
    /// e u (lungo <paramref name="b"/>) del punto di incontro delle due rette.
    /// Restituisce false se le rette sono parallele.
    /// </summary>
    public static bool TryIntersectLines(LineSegment a, LineSegment b, out double t, out double u)
    {
        Point2 r = a.Direction;
        Point2 s = b.Direction;
        double denom = Point2.Cross(r, s);
        if (Math.Abs(denom) < 1e-12 * Math.Max(1.0, r.Length * s.Length))
        {
            t = u = double.NaN;
            return false;
        }

        Point2 qp = b.Start - a.Start;
        t = Point2.Cross(qp, s) / denom;
        u = Point2.Cross(qp, r) / denom;
        return true;
    }

    /// <summary>Vero se i due segmenti si toccano o si incrociano (esclusi i casi collineari).</summary>
    public static bool SegmentsIntersect(LineSegment a, LineSegment b)
    {
        if (!TryIntersectLines(a, b, out double t, out double u))
        {
            return false;
        }

        const double eps = 1e-9;
        return t >= -eps && t <= 1 + eps && u >= -eps && u <= 1 + eps;
    }

    /// <summary>Angolo acuto tra le direzioni di due segmenti, in gradi (0–90).</summary>
    public static double AcuteAngleDegrees(LineSegment a, LineSegment b)
    {
        double diff = Math.Abs(a.Angle - b.Angle) % Math.PI;
        if (diff > Math.PI / 2)
        {
            diff = Math.PI - diff;
        }

        return diff * 180.0 / Math.PI;
    }
}
