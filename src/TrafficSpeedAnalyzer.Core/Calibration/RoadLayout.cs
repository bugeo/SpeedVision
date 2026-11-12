using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Calibration;

/// <summary>Linea tra due corsie: posizione delle due estremità sul bordo sinistro e destro della strada (0 = bordo lontano, 1 = bordo vicino).</summary>
public readonly record struct LaneDivider(double Left, double Right)
{
    public double At(double u) => Left + ((Right - Left) * u);
}

/// <summary>
/// La strada indicata dall'utente: un quadrilatero sui bordi della carreggiata (i quattro vertici si spostano
/// liberamente, per seguire la prospettiva) e le linee tra le corsie, ognuna con le due estremità sui lati
/// sinistro e destro.
/// <para>
/// Le posizioni sono in coordinate «della strada»: (u, v) con u da 0 (lato sinistro) a 1 (lato destro) e v da 0
/// (bordo lontano, in alto) a 1 (bordo vicino, in basso). Il passaggio dall'immagine alla strada è
/// un'omografia, quindi le linee restano diritte e seguono il quadrilatero quando lo si deforma.
/// </para>
/// <para>Le corsie si contano dal basso: la corsia 0 (mostrata come «corsia 1») è la più vicina alla camera.</para>
/// </summary>
public sealed record RoadLayout
{
    public const int MaxLanes = 4;

    /// <summary>Distanza minima tra due linee, o tra una linea e un bordo (frazione della larghezza della strada).</summary>
    public const double MinLaneFraction = 0.06;

    /// <summary>Tolleranza «appena fuori»: un quarto di corsia.</summary>
    public const double OutsideToleranceLanes = 0.25;

    /// <summary>Lato minimo del quadrilatero, in pixel.</summary>
    public const double MinSide = 20;

    private readonly double[] _toUnit;
    private readonly double[] _fromUnit;

    /// <param name="corners">Vertici: alto-sinistra, alto-destra, basso-destra, basso-sinistra (bordo lontano in alto).</param>
    /// <param name="dividers">Linee tra le corsie, dall'alto (bordo lontano) verso il basso.</param>
    public RoadLayout(IReadOnlyList<Point2> corners, IReadOnlyList<LaneDivider> dividers)
    {
        if (corners.Count != 4)
        {
            throw new ArgumentException("Servono quattro vertici.", nameof(corners));
        }

        Corners = corners.ToArray();
        Dividers = dividers.ToArray();
        _fromUnit = SquareToQuad(Corners);
        _toUnit = Invert(_fromUnit);
    }

    public IReadOnlyList<Point2> Corners { get; }

    public IReadOnlyList<LaneDivider> Dividers { get; init; }

    public Point2 TopLeft => Corners[0];

    public Point2 TopRight => Corners[1];

    public Point2 BottomRight => Corners[2];

    public Point2 BottomLeft => Corners[3];

    public int Lanes => Dividers.Count + 1;

    /// <summary>Riquadro che contiene la strada.</summary>
    public BoundingBox Bounds
    {
        get
        {
            double x0 = Corners.Min(c => c.X), x1 = Corners.Max(c => c.X);
            double y0 = Corners.Min(c => c.Y), y1 = Corners.Max(c => c.Y);
            return new BoundingBox(x0, y0, x1 - x0, y1 - y0);
        }
    }

    // ───────────── Creazione e modifica ─────────────

    /// <summary>Strada iniziale al centro dell'immagine (80 % × 30 %) con le corsie uguali.</summary>
    public static RoadLayout CreateCentered(int lanes, double imageWidth, double imageHeight)
    {
        double x0 = 0.1 * imageWidth, x1 = 0.9 * imageWidth, y0 = 0.35 * imageHeight, y1 = 0.65 * imageHeight;
        return new RoadLayout([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], EvenDividers(lanes));
    }

    /// <summary>Stessa strada con il numero di corsie indicato e linee equidistanti.</summary>
    public RoadLayout WithLanes(int lanes) => new(Corners, EvenDividers(lanes));

    /// <summary>
    /// Per ciascun vertice: è ancora nella posizione proposta da <see cref="CreateCentered"/> (entro mezzo pixel),
    /// cioè l'utente non l'ha ancora spostato.
    /// </summary>
    public bool[] CornersAtStart(double imageWidth, double imageHeight)
    {
        IReadOnlyList<Point2> start = CreateCentered(1, imageWidth, imageHeight).Corners;
        return [.. Corners.Select((c, i) => Point2.Distance(c, start[i]) <= 0.5)];
    }

    /// <summary>Le linee tra le corsie sono ancora equidistanti come all'inizio (nessuna spostata); falso con una sola corsia.</summary>
    public bool DividersAtStart =>
        Lanes > 1 &&
        Dividers.Zip(EvenDividers(Lanes)).All(d => Math.Abs(d.First.Left - d.Second.Left) <= 0.002 && Math.Abs(d.First.Right - d.Second.Right) <= 0.002);

    public static IReadOnlyList<LaneDivider> EvenDividers(int lanes)
    {
        int n = Math.Clamp(lanes, 1, MaxLanes);
        return Enumerable.Range(1, n - 1).Select(i => new LaneDivider(i / (double)n, i / (double)n)).ToArray();
    }

    /// <summary>
    /// Nuovi vertici, se il quadrilatero resta valido (convesso, nell'immagine allargata di poco, lati di almeno
    /// <see cref="MinSide"/> px); altrimenti la strada resta com'era. Le linee tra le corsie lo seguono.
    /// </summary>
    public RoadLayout WithCorners(IReadOnlyList<Point2> corners, double imageWidth, double imageHeight)
    {
        Point2[] c = corners.Select(p => new Point2(Math.Clamp(p.X, 0, imageWidth), Math.Clamp(p.Y, 0, imageHeight))).ToArray();
        return IsValidQuad(c) ? new RoadLayout(c, Dividers) : this;
    }

    /// <summary>Sposta un vertice (0 alto-sinistra, 1 alto-destra, 2 basso-destra, 3 basso-sinistra).</summary>
    public RoadLayout MoveCorner(int index, Point2 to, double imageWidth, double imageHeight)
    {
        Point2[] c = Corners.ToArray();
        c[index] = to;
        return WithCorners(c, imageWidth, imageHeight);
    }

    /// <summary>Sposta un lato (0 alto, 1 destra, 2 basso, 3 sinistra) con i suoi due vertici.</summary>
    public RoadLayout MoveEdge(int edge, Point2 delta, double imageWidth, double imageHeight)
    {
        Point2[] c = Corners.ToArray();
        c[edge] += delta;
        c[(edge + 1) % 4] += delta;
        return WithCorners(c, imageWidth, imageHeight);
    }

    /// <summary>Sposta tutta la strada, restando nell'immagine.</summary>
    public RoadLayout Move(Point2 delta, double imageWidth, double imageHeight)
    {
        double dx = Math.Clamp(delta.X, -Corners.Min(p => p.X), imageWidth - Corners.Max(p => p.X));
        double dy = Math.Clamp(delta.Y, -Corners.Min(p => p.Y), imageHeight - Corners.Max(p => p.Y));
        var d = new Point2(dx, dy);
        return new RoadLayout(Corners.Select(p => p + d).ToArray(), Dividers);
    }

    /// <summary>
    /// Sposta la linea tra le corsie <paramref name="index"/>: <paramref name="side"/> −1 solo l'estremità sinistra,
    /// +1 solo la destra, 0 tutta la linea. La posizione viene dal punto <paramref name="to"/> dell'immagine;
    /// la linea resta tra le vicine.
    /// </summary>
    public RoadLayout MoveDivider(int index, int side, Point2 to, Point2 from)
    {
        (double _, double vTo) = ToUnit(to);
        (double _, double vFrom) = ToUnit(from);
        LaneDivider d = Dividers[index];
        double dv = vTo - vFrom;
        double left = side <= 0 ? d.Left + dv : d.Left;
        double right = side >= 0 ? d.Right + dv : d.Right;
        if (side == 0)
        {
            // Tutta la linea: stesso spostamento alle due estremità, limitato da entrambe.
            double lo = Math.Max(LowerLimit(index, true) - d.Left, LowerLimit(index, false) - d.Right);
            double hi = Math.Min(UpperLimit(index, true) - d.Left, UpperLimit(index, false) - d.Right);
            dv = Math.Clamp(dv, lo, Math.Max(lo, hi));
            left = d.Left + dv;
            right = d.Right + dv;
        }

        left = Math.Clamp(left, LowerLimit(index, true), UpperLimit(index, true));
        right = Math.Clamp(right, LowerLimit(index, false), UpperLimit(index, false));
        LaneDivider[] all = Dividers.ToArray();
        all[index] = new LaneDivider(left, right);
        return this with { Dividers = all };
    }

    private double LowerLimit(int index, bool left) =>
        (index == 0 ? 0 : (left ? Dividers[index - 1].Left : Dividers[index - 1].Right)) + MinLaneFraction;

    private double UpperLimit(int index, bool left) =>
        (index == Dividers.Count - 1 ? 1 : (left ? Dividers[index + 1].Left : Dividers[index + 1].Right)) - MinLaneFraction;

    // ───────────── Geometria ─────────────

    /// <summary>Estremità nell'immagine della linea tra le corsie <paramref name="index"/> (dall'alto, 0-based).</summary>
    public (Point2 Left, Point2 Right) DividerEnds(int index) =>
        (FromUnit(0, Dividers[index].Left), FromUnit(1, Dividers[index].Right));

    /// <summary>Confini (v) della corsia <paramref name="lane"/> alla posizione u (0 = la più vicina, in basso).</summary>
    private (double Top, double Bottom) LaneV(int lane, double u)
    {
        int fromTop = Lanes - 1 - lane;
        double top = fromTop == 0 ? 0 : Dividers[fromTop - 1].At(u);
        double bottom = fromTop == Lanes - 1 ? 1 : Dividers[fromTop].At(u);
        return (top, bottom);
    }

    /// <summary>Tolleranza «appena fuori» nelle coordinate della strada.</summary>
    public double ToleranceV => OutsideToleranceLanes / Lanes;

    /// <summary>
    /// Righe dell'immagine (alto, basso) della corsia <paramref name="lane"/> alla colonna <paramref name="x"/>,
    /// allargate della tolleranza «appena fuori».
    /// </summary>
    public (double Top, double Bottom) LaneSpanAt(int lane, double x, bool withTolerance = true)
    {
        (double u, double _) = ToUnit(new Point2(x, Bounds.CenterY));
        u = Math.Clamp(double.IsFinite(u) ? u : 0.5, 0, 1);
        (double top, double bottom) = LaneV(lane, u);
        double t = withTolerance ? ToleranceV : 0;
        double yTop = RowAt(x, u, top - t), yBottom = RowAt(x, u, bottom + t);
        return (Math.Min(yTop, yBottom), Math.Max(yTop, yBottom));
    }

    /// <summary>Riga dell'immagine, alla colonna x, della linea v = costante.</summary>
    private double RowAt(double x, double u, double v)
    {
        // La linea v = costante è diritta nell'immagine: si interseca con la verticale in x.
        Point2 a = FromUnit(0, v), b = FromUnit(1, v);
        if (Math.Abs(b.X - a.X) < 1e-9)
        {
            return FromUnit(u, v).Y;
        }

        double t = (x - a.X) / (b.X - a.X);
        return a.Y + ((b.Y - a.Y) * t);
    }

    /// <summary>
    /// Tratto della retta per <paramref name="p"/> con direzione <paramref name="direction"/> che attraversa la
    /// strada: dal bordo vicino (in basso) al bordo lontano, allungato del margine «appena fuori» da entrambe
    /// le parti. Null se la retta è parallela ai bordi.
    /// </summary>
    public (Point2 Near, Point2 Far)? SpanAcross(Point2 p, Point2 direction)
    {
        if (Intersect(p, direction, BottomLeft, BottomRight - BottomLeft) is not { } near
            || Intersect(p, direction, TopLeft, TopRight - TopLeft) is not { } far)
        {
            return null;
        }

        Point2 across = far - near;
        double t = OutsideToleranceLanes / Lanes;
        return (near - (across * t), far + (across * t));
    }

    private static Point2? Intersect(Point2 p, Point2 d, Point2 q, Point2 e)
    {
        double den = Point2.Cross(d, e);
        if (Math.Abs(den) < 1e-9 * Math.Max(1, d.Length * e.Length))
        {
            return null;
        }

        double t = Point2.Cross(q - p, e) / den;
        return p + (d * t);
    }

    /// <summary>Centro della corsia nell'immagine (per l'etichetta).</summary>
    public Point2 LaneCenter(int lane)
    {
        (double top, double bottom) = LaneV(lane, 0.5);
        return FromUnit(0.5, (top + bottom) / 2);
    }

    /// <summary>Vero se il punto a terra è sulla strada o appena fuori.</summary>
    public bool Contains(Point2 p)
    {
        (double u, double v) = ToUnit(p);
        double t = ToleranceV;
        return double.IsFinite(u) && u >= -t && u <= 1 + t && v >= -t && v <= 1 + t;
    }

    /// <summary>Corsia del punto a terra (0 = la più vicina), oppure −1 se è fuori dalla strada.</summary>
    public int LaneOf(Point2 p)
    {
        if (!Contains(p))
        {
            return -1;
        }

        (double u, double v) = ToUnit(p);
        double uc = Math.Clamp(u, 0, 1);
        int above = Dividers.Count(d => v > d.At(uc));   // linee sopra il punto, contate dall'alto
        return Lanes - 1 - above;
    }

    /// <summary>
    /// Corsia di un veicolo dai punti a terra della sua traccia: serve che almeno metà dei punti sia sulla strada;
    /// la corsia è quella più frequente tra i punti sulla strada (a parità, quella del punto mediano).
    /// −1 se il veicolo non è sulla strada.
    /// </summary>
    public int LaneOfTrack(IReadOnlyList<Point2> groundPoints)
    {
        if (groundPoints.Count == 0)
        {
            return -1;
        }

        int[] lanes = groundPoints.Select(LaneOf).Where(l => l >= 0).ToArray();
        if (lanes.Length * 2 < groundPoints.Count)
        {
            return -1;
        }

        int median = lanes[lanes.Length / 2];
        return lanes.GroupBy(l => l)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key == median)
            .First().Key;
    }

    /// <summary>
    /// Zona da analizzare: la strada con un margine sotto e, sopra, abbastanza spazio per i veicoli della corsia
    /// lontana (il riquadro di un'auto sale ben oltre la sua corsia). In larghezza tutta l'immagine, dove i
    /// veicoli entrano ed escono.
    /// </summary>
    public BoundingBox AnalysisRegion(double imageWidth, double imageHeight)
    {
        BoundingBox b = Bounds;
        double tolerancePx = OutsideToleranceLanes * b.Height / Lanes;
        double top = Math.Max(0, b.Y - Math.Max(1.5 * b.Height, 0.2 * imageHeight));
        double bottom = Math.Min(imageHeight, b.Bottom + Math.Max(tolerancePx, 0.05 * imageHeight));
        return new BoundingBox(0, top, imageWidth, bottom - top);
    }

    // ───────────── Omografia ─────────────

    /// <summary>Dall'immagine alla strada; (NaN, NaN) per i punti oltre l'orizzonte del quadrilatero.</summary>
    public (double U, double V) ToUnit(Point2 p)
    {
        double[] h = _toUnit;
        double w = (h[6] * p.X) + (h[7] * p.Y) + h[8];
        if (Math.Abs(w) < 1e-12)
        {
            return (double.NaN, double.NaN);
        }

        return (((h[0] * p.X) + (h[1] * p.Y) + h[2]) / w, ((h[3] * p.X) + (h[4] * p.Y) + h[5]) / w);
    }

    /// <summary>Dalla strada all'immagine.</summary>
    public Point2 FromUnit(double u, double v)
    {
        double[] h = _fromUnit;
        double w = (h[6] * u) + (h[7] * v) + h[8];
        return new Point2(((h[0] * u) + (h[1] * v) + h[2]) / w, ((h[3] * u) + (h[4] * v) + h[5]) / w);
    }

    /// <summary>Quadrilatero convesso, con i vertici in senso orario (y verso il basso) e lati non troppo corti.</summary>
    public static bool IsValidQuad(IReadOnlyList<Point2> c)
    {
        for (int i = 0; i < 4; i++)
        {
            Point2 a = c[i], b = c[(i + 1) % 4], d = c[(i + 2) % 4];
            if (Point2.Distance(a, b) < MinSide || Point2.Cross(b - a, d - b) <= 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Omografia dal quadrato unitario (0,0)(1,0)(1,1)(0,1) al quadrilatero (Heckbert).</summary>
    private static double[] SquareToQuad(IReadOnlyList<Point2> q)
    {
        double x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y, x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;
        double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
        double g, h;
        if (Math.Abs(sx) < 1e-12 && Math.Abs(sy) < 1e-12)
        {
            g = 0;
            h = 0;
        }
        else
        {
            double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
            double den = (dx1 * dy2) - (dx2 * dy1);
            g = ((sx * dy2) - (dx2 * sy)) / den;
            h = ((dx1 * sy) - (sx * dy1)) / den;
        }

        // (u, v) → ((a u + b v + c) / w, (d u + e v + f) / w), w = g u + h v + 1
        double a = x1 - x0 + (g * x1), b = x3 - x0 + (h * x3), cc = x0;
        double d = y1 - y0 + (g * y1), e = y3 - y0 + (h * y3), f = y0;
        return [a, b, cc, d, e, f, g, h, 1];
    }

    private static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double A = (e * i) - (f * h), B = -((d * i) - (f * g)), C = (d * h) - (e * g);
        double det = (a * A) + (b * B) + (c * C);
        if (Math.Abs(det) < 1e-18)
        {
            return [double.NaN, 0, 0, 0, double.NaN, 0, 0, 0, 1];
        }

        return
        [
            A / det, -((b * i) - (c * h)) / det, ((b * f) - (c * e)) / det,
            B / det, ((a * i) - (c * g)) / det, -((a * f) - (c * d)) / det,
            C / det, -((a * h) - (b * g)) / det, ((a * e) - (b * d)) / det,
        ];
    }
}
