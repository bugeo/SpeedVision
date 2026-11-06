namespace TrafficSpeedAnalyzer.Core.Geometry;

/// <summary>Punto o vettore 2D in coordinate pixel del fotogramma (origine in alto a sinistra).</summary>
public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 operator *(Point2 a, double k) => new(a.X * k, a.Y * k);
    public static Point2 operator *(double k, Point2 a) => new(a.X * k, a.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>Componente z del prodotto vettoriale (a × b).</summary>
    public static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;

    public static double Distance(Point2 a, Point2 b) => (a - b).Length;

    public static Point2 Lerp(Point2 a, Point2 b, double t) => a + (b - a) * t;

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public override string ToString() => FormattableString.Invariant($"({X:0.##}, {Y:0.##})");
}
