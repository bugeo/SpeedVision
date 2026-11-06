using System.Globalization;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Tracking;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Calibration;

/// <summary>Distanza tra le linee stimata da un veicolo di lunghezza nota.</summary>
public sealed record VehicleScaleEstimate(
    ReferenceVehicle Vehicle,
    double DistanceMeters,
    double UncertaintyMeters,
    double MetersPerPixel,
    double VehicleLengthPixels,
    double LinesDistancePixels,
    long BestFrameIndex,
    BoundingBox BestBox,
    double SizeChangeFraction,
    int FramesUsed,
    IReadOnlyList<string> Warnings);

/// <summary>Misura del veicolo nei fotogrammi in cui è più vicino al centro dell'immagine.</summary>
public sealed record CenterFrameMeasurement(
    TrackPoint Best,
    double LengthPixels,
    double RelativeSpread,
    bool Horizontal,
    double SizeChangeFraction,
    int FramesUsed,
    IReadOnlyList<string> Warnings);

/// <summary>Stima combinata da uno o più veicoli.</summary>
public sealed record CombinedScaleEstimate(double DistanceMeters, double UncertaintyMeters, int Vehicles, bool Consistent, string? Warning);

/// <summary>
/// Stima la distanza reale tra le linee A e B da un veicolo di cui l'utente indica il modello.
/// Presuppone la camera circa perpendicolare alla strada: nel fotogramma in cui il veicolo è al centro
/// dell'immagine la sua estensione lungo la direzione di marcia (in pixel) corrisponde alla sua lunghezza.
/// La scala (m/px) vale alla profondità del veicolo, quindi la distanza tra le linee si misura lungo
/// la sua traiettoria (centro del lato inferiore del riquadro), cioè nella sua corsia.
/// </summary>
public static class VehicleScaleEstimator
{
    /// <summary>Fotogrammi più vicini al centro usati per la media.</summary>
    public const int CenterFrames = 7;

    /// <summary>Incertezza sistematica del riquadro rispetto alla sagoma reale (frazione).</summary>
    public const double BoxTightnessUncertainty = 0.02;

    /// <summary>Incertezza della lunghezza del modello (allestimenti, paraurti; frazione).</summary>
    public const double LengthUncertainty = 0.01;

    /// <summary>Oltre questa variazione di dimensione tra i due lati dell'immagine la camera non è perpendicolare.</summary>
    public const double MaxSizeChange = 0.08;

    private static CultureInfo It => Loc.Culture;

    /// <summary>
    /// Trova i fotogrammi in cui il veicolo è intero e più vicino al centro dell'immagine e ne misura
    /// l'estensione lungo la direzione di marcia (media robusta). Null se il veicolo non è mai intero.
    /// </summary>
    public static CenterFrameMeasurement? MeasureAtCenter(
        IReadOnlyList<TrackPoint> points, double imageWidth, double imageHeight, out string? failure)
    {
        failure = null;
        var warnings = new List<string>();

        // Riquadri interi: non tagliati dalla zona di analisi né dai bordi dell'immagine.
        const double edge = 2;
        List<TrackPoint> usable = points
            .Where(p => !p.Truncated && p.Box.X > edge && p.Box.Y > edge && p.Box.Right < imageWidth - edge && p.Box.Bottom < imageHeight - edge)
            .OrderBy(p => p.FrameIndex)
            .ToList();
        if (usable.Count < 3)
        {
            failure = Loc.T("L'auto non è mai inquadrata per intero abbastanza a lungo.");
            return null;
        }

        // Direzione di marcia dominante: orizzontale (ripresa laterale) o verticale.
        Point2 first = usable[0].Box.BottomCenter;
        Point2 last = usable[^1].Box.BottomCenter;
        bool horizontal = Math.Abs(last.X - first.X) >= Math.Abs(last.Y - first.Y);
        double Along(BoundingBox b) => horizontal ? b.Width : b.Height;
        double Position(BoundingBox b) => horizontal ? b.CenterX : b.CenterY;
        double center = horizontal ? imageWidth / 2 : imageHeight / 2;
        double span = horizontal ? imageWidth : imageHeight;

        // Fotogrammi più vicini al centro dell'immagine; scarto di quelli con dimensione anomala.
        List<TrackPoint> nearest = usable.OrderBy(p => Math.Abs(Position(p.Box) - center)).Take(CenterFrames).ToList();
        if (Math.Abs(Position(nearest[0].Box) - center) > 0.25 * span)
        {
            warnings.Add(Loc.T("L'auto non passa vicino al centro dell'immagine: la stima è meno affidabile."));
        }

        double median = Median(nearest.Select(p => Along(p.Box)));
        List<double> lengths = nearest.Select(p => Along(p.Box)).Where(l => Math.Abs(l - median) <= 0.05 * median).ToList();
        if (lengths.Count == 0)
        {
            lengths = [median];
        }

        double lengthPx = lengths.Average();
        double spread = lengths.Count > 1
            ? Math.Sqrt(lengths.Sum(l => (l - lengthPx) * (l - lengthPx)) / (lengths.Count - 1)) / lengthPx / Math.Sqrt(lengths.Count)
            : 0.02;

        // Variazione della dimensione attraversando l'immagine (prospettiva = camera non perpendicolare).
        double sizeChange = SizeChange(usable.Select(p => (Position(p.Box), Along(p.Box))).ToList(), center, span);
        if (Math.Abs(sizeChange) > MaxSizeChange)
        {
            warnings.Add(Loc.S($"L'auto cambia dimensione del {Math.Abs(sizeChange) * 100:0}% attraversando l'immagine: la camera probabilmente non è perpendicolare alla strada."));
        }

        return new CenterFrameMeasurement(nearest[0], lengthPx, spread, horizontal, sizeChange, lengths.Count, warnings);
    }

    /// <summary>
    /// Vero se il veicolo è transitato attraverso la parte centrale dell'immagine: si è spostato per
    /// almeno un quarto dell'inquadratura e il suo percorso comprende la fascia centrale. Esclude le
    /// auto parcheggiate o che si fermano ai lati.
    /// </summary>
    public static bool CrossesImageCenter(IReadOnlyList<TrackPoint> points, double imageWidth, double imageHeight)
    {
        List<Point2> pts = points.Where(p => !p.Truncated).OrderBy(p => p.FrameIndex).Select(p => p.Box.BottomCenter).ToList();
        if (pts.Count < 3)
        {
            return false;
        }

        bool horizontal = Math.Abs(pts[^1].X - pts[0].X) >= Math.Abs(pts[^1].Y - pts[0].Y);
        double[] along = pts.Select(p => horizontal ? p.X : p.Y).ToArray();
        double span = horizontal ? imageWidth : imageHeight;
        double center = span / 2;
        double min = along.Min(), max = along.Max();
        return max - min >= 0.25 * span && min <= center - (0.05 * span) && max >= center + (0.05 * span);
    }

    /// <summary>Incertezza relativa della scala ricavata da una misura al centro.</summary>
    public static double RelativeUncertainty(CenterFrameMeasurement m) =>
        Math.Sqrt((m.RelativeSpread * m.RelativeSpread) + (BoxTightnessUncertainty * BoxTightnessUncertainty) + (LengthUncertainty * LengthUncertainty));

    /// <summary>
    /// Linee A e B sulle due estremità del veicolo (paraurti) nel fotogramma in cui è al centro: la loro
    /// distanza reale è la lunghezza del veicolo. Sono perpendicolari alla direzione di marcia nell'immagine
    /// (verticali in una ripresa laterale), passano per i punti a terra delle estremità e si estendono
    /// sopra il veicolo per coprire anche le corsie più lontane. Restano parallele, quindi non si incrociano;
    /// l'utente può inclinarle sui riferimenti a terra se la prospettiva lo richiede.
    /// </summary>
    public static (LineSegment A, LineSegment B) LinesAtVehicleEnds(CenterFrameMeasurement m, double imageWidth, double imageHeight)
    {
        BoundingBox box = m.Best.Box;
        double half = m.LengthPixels / 2;

        LineSegment Through(Point2 p, Point2 dir, double back, double forward) =>
            new(ClampInside(p, p - (dir * back), imageWidth, imageHeight), ClampInside(p, p + (dir * forward), imageWidth, imageHeight));

        if (m.Horizontal)
        {
            double y = box.Bottom;
            var up = new Point2(0, -1);
            return (Through(new Point2(box.CenterX - half, y), up, 0.6 * box.Height, 2 * box.Height),
                    Through(new Point2(box.CenterX + half, y), up, 0.6 * box.Height, 2 * box.Height));
        }

        var right = new Point2(1, 0);
        return (Through(new Point2(box.CenterX, box.CenterY - half), right, 1.5 * box.Width, 1.5 * box.Width),
                Through(new Point2(box.CenterX, box.CenterY + half), right, 1.5 * box.Width, 1.5 * box.Width));
    }

    /// <summary>Accorcia il segmento da <paramref name="from"/> a <paramref name="to"/> perché resti nell'immagine.</summary>
    private static Point2 ClampInside(Point2 from, Point2 to, double w, double h)
    {
        double t = 1;
        Point2 d = to - from;
        if (d.X > 0) t = Math.Min(t, (w - 1 - from.X) / d.X);
        if (d.X < 0) t = Math.Min(t, (1 - from.X) / d.X);
        if (d.Y > 0) t = Math.Min(t, (h - 1 - from.Y) / d.Y);
        if (d.Y < 0) t = Math.Min(t, (1 - from.Y) / d.Y);
        return from + (d * Math.Max(0, t));
    }

    public static VehicleScaleEstimate? Estimate(
        IReadOnlyList<TrackPoint> points,
        LineSegment lineA,
        LineSegment lineB,
        double imageWidth,
        double imageHeight,
        ReferenceVehicle vehicle,
        out string? failure)
    {
        if (MeasureAtCenter(points, imageWidth, imageHeight, out failure) is not { } m)
        {
            return null;
        }

        var warnings = new List<string>(m.Warnings);
        List<TrackPoint> usable = points.Where(p => !p.Truncated).OrderBy(p => p.FrameIndex).ToList();
        double center = m.Horizontal ? imageWidth / 2 : imageHeight / 2;
        double span = m.Horizontal ? imageWidth : imageHeight;

        // Traiettoria (retta dei punti di riferimento) e sue intersezioni con le linee A e B.
        if (!FitLine(usable.Select(p => p.Box.BottomCenter).ToList(), out LineSegment path))
        {
            failure = Loc.T("La traiettoria dell'auto non è utilizzabile.");
            return null;
        }

        if (!GeometryMath.TryIntersectLines(path, lineA, out double ta, out _)
            || !GeometryMath.TryIntersectLines(path, lineB, out double tb, out _))
        {
            failure = Loc.T("La traiettoria dell'auto non attraversa le linee A e B.");
            return null;
        }

        Point2 pa = path.PointAt(ta);
        Point2 pb = path.PointAt(tb);
        double linesPx = Point2.Distance(pa, pb);
        if (linesPx < 10)
        {
            failure = Loc.T("Le linee sono troppo vicine lungo la traiettoria dell'auto.");
            return null;
        }

        double midAlong = m.Horizontal ? (pa.X + pb.X) / 2 : (pa.Y + pb.Y) / 2;
        if (Math.Abs(midAlong - center) > 0.15 * span)
        {
            warnings.Add(Loc.T("Le linee A e B non sono simmetriche rispetto al centro dell'immagine."));
        }

        double mpp = vehicle.LengthMeters / m.LengthPixels;
        double distance = linesPx * mpp;
        return new VehicleScaleEstimate(
            vehicle, distance, distance * RelativeUncertainty(m), mpp, m.LengthPixels, linesPx,
            m.Best.FrameIndex, m.Best.Box, m.SizeChangeFraction, m.FramesUsed, warnings);
    }

    /// <summary>Media pesata delle stime; segnala se non concordano entro le incertezze.</summary>
    public static CombinedScaleEstimate? Combine(IReadOnlyList<VehicleScaleEstimate> estimates)
    {
        if (estimates.Count == 0)
        {
            return null;
        }

        double sw = 0, swx = 0;
        foreach (VehicleScaleEstimate e in estimates)
        {
            double w = 1 / Math.Max(1e-9, e.UncertaintyMeters * e.UncertaintyMeters);
            sw += w;
            swx += w * e.DistanceMeters;
        }

        double mean = swx / sw;
        // L'errore del riquadro è in parte sistematico: non si riduce oltre metà dell'incertezza minima.
        double u = Math.Max(1 / Math.Sqrt(sw), 0.5 * estimates.Min(e => e.UncertaintyMeters));
        bool consistent = estimates.All(e => Math.Abs(e.DistanceMeters - mean) <= 2 * e.UncertaintyMeters);
        if (estimates.Count > 1)
        {
            double sd = Math.Sqrt(estimates.Sum(e => (e.DistanceMeters - mean) * (e.DistanceMeters - mean)) / (estimates.Count - 1));
            u = Math.Max(u, sd / Math.Sqrt(estimates.Count));
        }

        string? warning = consistent
            ? null
            : Loc.T("Le stime delle diverse auto non concordano: controlla i modelli indicati e che la camera sia perpendicolare alla strada.");
        return new CombinedScaleEstimate(mean, u, estimates.Count, consistent, warning);
    }

    /// <summary>
    /// Variazione relativa dell'estensione per metà immagine, dovuta all'inclinazione della camera rispetto
    /// alla strada: termine lineare di una parabola dimensione(posizione) centrata nell'immagine.
    /// <para>
    /// Il termine quadratico assorbe l'allargamento simmetrico ai lati (ai bordi si vede anche il muso o il
    /// retro dell'auto, tanto più quanto la camera è vicina). Con una semplice retta questo allargamento,
    /// su un tratto non simmetrico rispetto al centro, sembrerebbe una camera storta. Riquadri anomali
    /// (auto coperta da un palo, fusa con un'altra) vengono scartati e la parabola ricalcolata.
    /// </para>
    /// </summary>
    internal static double SizeChange(IReadOnlyList<(double Pos, double Size)> samples, double center, double span)
    {
        if (samples.Count < 3)
        {
            return 0;
        }

        double min = samples.Min(s => s.Pos), max = samples.Max(s => s.Pos);
        if (max - min < 0.1 * span)
        {
            return 0; // il veicolo si è spostato troppo poco per giudicare
        }

        double half = span / 2;
        var pts = samples.Select(s => ((s.Pos - center) / half, s.Size)).ToList();
        bool quadratic = pts.Count >= 6 && max - min >= 0.3 * span;
        if (!FitPolynomial(pts, quadratic ? 2 : 1, out double[] c))
        {
            return 0;
        }

        // Scarto dei riquadri anomali (oltre 3 deviazioni robuste) e nuovo calcolo.
        double[] residuals = pts.Select(p => p.Item2 - Evaluate(c, p.Item1)).ToArray();
        double mad = 1.4826 * Median(residuals.Select(r => Math.Abs(r)));
        if (mad > 1e-9)
        {
            List<(double, double)> kept = pts.Where((p, i) => Math.Abs(residuals[i]) <= 3 * mad).ToList();
            if (kept.Count >= (quadratic ? 6 : 3) && FitPolynomial(kept, quadratic ? 2 : 1, out double[] c2))
            {
                c = c2;
            }
        }

        return c[0] <= 0 ? 0 : c[1] / c[0];
    }

    private static double Evaluate(double[] c, double x) => c.Length == 3 ? c[0] + (c[1] * x) + (c[2] * x * x) : c[0] + (c[1] * x);

    /// <summary>Polinomio di grado 1 o 2 ai minimi quadrati (equazioni normali, eliminazione di Gauss).</summary>
    private static bool FitPolynomial(IReadOnlyList<(double X, double Y)> pts, int degree, out double[] coefficients)
    {
        int n = degree + 1;
        var a = new double[n, n + 1];
        foreach ((double x, double y) in pts)
        {
            var powers = new double[(2 * degree) + 1];
            powers[0] = 1;
            for (int k = 1; k < powers.Length; k++)
            {
                powers[k] = powers[k - 1] * x;
            }

            for (int r = 0; r < n; r++)
            {
                for (int col = 0; col < n; col++)
                {
                    a[r, col] += powers[r + col];
                }

                a[r, n] += powers[r] * y;
            }
        }

        coefficients = new double[n];
        for (int p = 0; p < n; p++)
        {
            int best = p;
            for (int r = p + 1; r < n; r++)
            {
                if (Math.Abs(a[r, p]) > Math.Abs(a[best, p]))
                {
                    best = r;
                }
            }

            if (Math.Abs(a[best, p]) < 1e-12)
            {
                return false;
            }

            for (int col = 0; col <= n; col++)
            {
                (a[p, col], a[best, col]) = (a[best, col], a[p, col]);
            }

            for (int r = 0; r < n; r++)
            {
                if (r == p)
                {
                    continue;
                }

                double f = a[r, p] / a[p, p];
                for (int col = p; col <= n; col++)
                {
                    a[r, col] -= f * a[p, col];
                }
            }
        }

        for (int r = 0; r < n; r++)
        {
            coefficients[r] = a[r, n] / a[r, r];
        }

        return true;
    }

    /// <summary>Retta ai minimi quadrati totali (direzione principale dei punti).</summary>
    internal static bool FitLine(IReadOnlyList<Point2> pts, out LineSegment line)
    {
        line = default;
        if (pts.Count < 2)
        {
            return false;
        }

        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double sxx = 0, syy = 0, sxy = 0;
        foreach (Point2 p in pts)
        {
            sxx += (p.X - mx) * (p.X - mx);
            syy += (p.Y - my) * (p.Y - my);
            sxy += (p.X - mx) * (p.Y - my);
        }

        double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var dir = new Point2(Math.Cos(angle), Math.Sin(angle));
        if (sxx + syy < 1e-9)
        {
            return false;
        }

        var c = new Point2(mx, my);
        line = new LineSegment(c - (dir * 100), c + (dir * 100));
        return true;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] v = values.Order().ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : 0.5 * (v[(v.Length / 2) - 1] + v[v.Length / 2]);
    }
}
