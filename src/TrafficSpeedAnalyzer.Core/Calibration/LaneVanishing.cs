using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Calibration;

/// <summary>
/// Punto di fuga delle linee trasversali alla strada (come A e B), stimato dai bordi della carreggiata e dalle linee
/// tra le corsie, supponendo le corsie larghe uguali. Serve almeno 2 corsie: su una retta che attraversa la strada i
/// bordi e le linee tra le corsie sono equidistanti sul terreno, ma nell'immagine si stringono verso il punto di fuga di
/// quella retta. Con due corsie, se la corsia vicina appare larga d₁ e la seconda d₂ = r·d₁, il punto di fuga sta a
/// d₁·(1 + r)/(1 − r) dal bordo vicino.
/// <para>
/// Lo si calcola sui due lati della strada (sinistro e destro). Se i due punti coincidono, i lati sono già trasversali e
/// quello è il punto cercato. Altrimenti i due punti danno l'orizzonte della strada, e il punto di fuga trasversale è
/// stimato come il punto dell'orizzonte più vicino al centro dell'immagine: esatto per una camera che guarda la strada
/// di fronte, approssimato se è ruotata.
/// </para>
/// </summary>
public static class LaneVanishing
{
    /// <summary>Oltre questa distanza (in larghezze della strada) il punto di fuga è considerato all'infinito.</summary>
    private const double MaxDistanceSpans = 500;

    /// <summary>Stima del punto di fuga trasversale; null con una sola corsia o senza prospettiva misurabile.</summary>
    public static Point2? Estimate(RoadLayout road, double imageWidth, double imageHeight)
    {
        if (road.Lanes < 2)
        {
            return null;
        }

        // Punti su ciascun lato dal bordo vicino (in basso) al lontano: le linee tra le corsie sono elencate dall'alto.
        var left = new List<Point2> { road.BottomLeft };
        var right = new List<Point2> { road.BottomRight };
        for (int i = road.Dividers.Count - 1; i >= 0; i--)
        {
            (Point2 l, Point2 r) = road.DividerEnds(i);
            left.Add(l);
            right.Add(r);
        }

        left.Add(road.TopLeft);
        right.Add(road.TopRight);

        Point2? vl = AlongTransversal(left);
        Point2? vr = AlongTransversal(right);
        if (vl is not { } a || vr is not { } b)
        {
            return vl ?? vr;
        }

        // Lati già trasversali: i due punti coincidono (entro il 5 % della loro distanza dalla strada).
        Point2 center = Point2.Lerp(Point2.Lerp(road.TopLeft, road.BottomRight, 0.5), Point2.Lerp(road.TopRight, road.BottomLeft, 0.5), 0.5);
        double reach = (Point2.Distance(a, center) + Point2.Distance(b, center)) / 2;
        if (Point2.Distance(a, b) < 0.05 * reach)
        {
            return Point2.Lerp(a, b, 0.5);
        }

        // Orizzonte per i due punti: si prende il suo punto più vicino al centro dell'immagine.
        Point2 d = b - a;
        var c = new Point2(imageWidth / 2, imageHeight / 2);
        double t = Point2.Dot(c - a, d) / Point2.Dot(d, d);
        return a + (d * t);
    }

    /// <summary>
    /// Punto di fuga di una retta su cui i punti dati (almeno 3, in ordine) sono equidistanti sul terreno.
    /// Con la posizione x lungo la retta e s = 0, 1, 2… sul terreno vale x = a·s / (1 + b·s): a e b ai minimi quadrati,
    /// punto di fuga in x = a / b. Null se i punti sono allineati male o la prospettiva è trascurabile.
    /// </summary>
    public static Point2? AlongTransversal(IReadOnlyList<Point2> points)
    {
        if (points.Count < 3)
        {
            return null;
        }

        Point2 p0 = points[0];
        Point2 span = points[^1] - p0;
        double length = span.Length;
        if (length < 1e-6)
        {
            return null;
        }

        Point2 dir = span * (1 / length);
        // x = a·s − b·(s·x): minimi quadrati con le colonne s e −s·x.
        double s11 = 0, s12 = 0, s22 = 0, t1 = 0, t2 = 0;
        for (int k = 1; k < points.Count; k++)
        {
            double x = Point2.Dot(points[k] - p0, dir);
            double c1 = k, c2 = -k * x;
            s11 += c1 * c1;
            s12 += c1 * c2;
            s22 += c2 * c2;
            t1 += c1 * x;
            t2 += c2 * x;
        }

        double det = (s11 * s22) - (s12 * s12);
        if (Math.Abs(det) < 1e-12)
        {
            return null;
        }

        double a = ((t1 * s22) - (t2 * s12)) / det;
        double b = ((s11 * t2) - (s12 * t1)) / det;
        if (Math.Abs(b) < 1e-12 || Math.Abs(a / b) > MaxDistanceSpans * length)
        {
            return null;
        }

        return p0 + (dir * (a / b));
    }
}
