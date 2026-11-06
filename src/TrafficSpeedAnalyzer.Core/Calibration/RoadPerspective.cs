using System.Globalization;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Tracking;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Calibration;

/// <summary>Giudizio sulla ripresa: la calibrazione con un'auto di lunghezza nota è possibile?</summary>
public sealed record ShotAssessment(bool Suitable, int UsableVehicles, double MedianSizeChange, string Message);

/// <summary>Da dove viene l'orizzonte usato per orientare le linee.</summary>
public enum HorizonSource
{
    /// <summary>Una sola corsia (o stima non possibile): linee perpendicolari alla strada nell'immagine.</summary>
    None,

    /// <summary>Stimato dalla grandezza di molte auto su corsie diverse.</summary>
    Statistical,

    /// <summary>Calcolato da due auto riconosciute su corsie diverse.</summary>
    TwoReferences,
}

/// <summary>
/// Orizzonte della strada nell'immagine, in coordinate «strada»: u lungo la direzione di marcia,
/// v perpendicolare (cresce verso il basso, verso la camera). Le linee trasversali a terra
/// convergono nel punto di fuga sull'orizzonte, alla u del centro dell'immagine.
/// </summary>
public sealed record HorizonEstimate(
    double RoadAngle,
    double HorizonV,
    double VanishingU,
    HorizonSource Source,
    int Vehicles,
    double LaneSpreadPixels,
    double LaneErrorFraction)
{
    /// <summary>Punto di fuga delle linee trasversali, in coordinate dell'immagine.</summary>
    public Point2 VanishingPoint => RoadPerspective.FromRoad(VanishingU, HorizonV, RoadAngle);

    public bool HasPerspective => Source != HorizonSource.None;
}

/// <summary>
/// Prospettiva della strada per la calibrazione con un'auto di lunghezza nota, con la camera
/// perpendicolare alla strada (la strada è parallela al piano dell'immagine).
/// <para>
/// Sul piano stradale la scala lungo la strada è proporzionale alla distanza dall'orizzonte
/// nell'immagine: s(v) = k·(v − v_h). Un'auto sulla corsia lontana appare più piccola e più in alto.
/// Le linee A e B tracciate a terra perpendicolarmente alla strada convergono nel punto di fuga
/// sull'orizzonte: con linee così la distanza reale tra A e B è la stessa su tutte le corsie,
/// mentre con linee parallele (verticali) cresce sulle corsie più lontane.
/// </para>
/// </summary>
public static class RoadPerspective
{
    /// <summary>Variazione mediana di grandezza oltre la quale la camera non è perpendicolare.</summary>
    public const double MaxSizeChange = VehicleScaleEstimator.MaxSizeChange;

    /// <summary>Errore massimo tra le corsie accettato per usare l'orizzonte stimato statisticamente.</summary>
    public const double MaxStatisticalLaneError = 0.05;

    /// <summary>Auto minime per la stima statistica dell'orizzonte.</summary>
    public const int MinStatisticalVehicles = 6;

    private static CultureInfo It => Loc.Culture;

    public static (double U, double V) ToRoad(Point2 p, double roadAngle)
    {
        double c = Math.Cos(roadAngle), s = Math.Sin(roadAngle);
        return ((c * p.X) + (s * p.Y), (-s * p.X) + (c * p.Y));
    }

    public static Point2 FromRoad(double u, double v, double roadAngle)
    {
        double c = Math.Cos(roadAngle), s = Math.Sin(roadAngle);
        return new Point2((c * u) - (s * v), (s * u) + (c * v));
    }

    /// <summary>Veicoli utilizzabili: passano per il centro dell'immagine e sono misurabili per intero.</summary>
    public static List<CenterFrameMeasurement> UsableVehicles(IEnumerable<IReadOnlyList<TrackPoint>> tracks, double imageWidth, double imageHeight)
    {
        var list = new List<CenterFrameMeasurement>();
        foreach (IReadOnlyList<TrackPoint> t in tracks)
        {
            if (t.Count > 0
                && VehicleScaleEstimator.CrossesImageCenter(t, imageWidth, imageHeight)
                && VehicleScaleEstimator.MeasureAtCenter(t, imageWidth, imageHeight, out _) is { } m)
            {
                list.Add(m);
            }
        }

        return list;
    }

    /// <summary>
    /// La ripresa è adatta se almeno un'auto attraversa per intero il centro dell'immagine e le auto
    /// non cambiano grandezza da un lato all'altro (camera perpendicolare alla strada).
    /// </summary>
    public static ShotAssessment Assess(IReadOnlyList<CenterFrameMeasurement> vehicles)
    {
        if (vehicles.Count == 0)
        {
            return new ShotAssessment(false, 0, 0,
                Loc.T("Nessuna auto attraversa per intero il centro dell'immagine: non c'è un'auto da proporre."));
        }

        double change = Median(vehicles.Select(v => Math.Abs(v.SizeChangeFraction)));
        if (change > MaxSizeChange)
        {
            return new ShotAssessment(false, vehicles.Count, change, Loc.S($"Attenzione: le auto cambiano grandezza del {change * 100:0}% attraversando l'immagine, la camera potrebbe non essere perpendicolare alla strada e le velocità essere meno precise."));
        }

        return new ShotAssessment(true, vehicles.Count, change, Loc.S($"Ripresa adatta: le auto hanno la stessa grandezza in tutta l'immagine (variazione {change * 100:0}%), {vehicles.Count} {(vehicles.Count == 1 ? Loc.T("auto utilizzabile") : Loc.T("auto utilizzabili"))}."));
    }

    /// <summary>Direzione della strada nell'immagine (radianti): mediana delle traiettorie, normalizzata a ±90°.</summary>
    public static double RoadAngle(IEnumerable<IReadOnlyList<TrackPoint>> tracks)
    {
        var angles = new List<double>();
        foreach (IReadOnlyList<TrackPoint> t in tracks)
        {
            List<Point2> pts = t.Where(p => !p.Truncated).Select(p => p.Box.BottomCenter).ToList();
            if (pts.Count >= 3 && VehicleScaleEstimator.FitLine(pts, out LineSegment line))
            {
                double a = line.Angle;
                while (a > Math.PI / 2) a -= Math.PI;
                while (a <= -Math.PI / 2) a += Math.PI;
                angles.Add(a);
            }
        }

        return angles.Count == 0 ? 0 : Median(angles);
    }

    /// <summary>
    /// Stima l'orizzonte dalla grandezza delle auto su corsie diverse: lunghezza in pixel = k·(v − v_h),
    /// con retta robusta (Theil–Sen). Le auto hanno lunghezze diverse, quindi serve un numero sufficiente
    /// di auto su almeno due corsie. Restituisce una stima con <see cref="HorizonSource.None"/> se c'è una
    /// sola corsia, null se le corsie sono più di una ma la stima non è affidabile.
    /// </summary>
    public static HorizonEstimate? EstimateHorizon(IReadOnlyList<CenterFrameMeasurement> vehicles, double roadAngle, double imageWidth, double imageHeight)
    {
        double uc = ToRoad(new Point2(imageWidth / 2, imageHeight / 2), roadAngle).U;
        List<(double V, double L, double H)> s = vehicles
            .Where(m => m.Horizontal && m.Best.Class == VehicleClass.Car)
            .Select(m => (ToRoad(m.Best.Box.BottomCenter, roadAngle).V, m.LengthPixels, m.Best.Box.Height))
            .ToList();
        if (s.Count == 0)
        {
            return new HorizonEstimate(roadAngle, double.NegativeInfinity, uc, HorizonSource.None, 0, 0, 0);
        }

        double medianHeight = Median(s.Select(x => x.H));
        double spread = Percentile(s.Select(x => x.V), 0.9) - Percentile(s.Select(x => x.V), 0.1);
        if (spread < 0.3 * medianHeight)
        {
            return new HorizonEstimate(roadAngle, double.NegativeInfinity, uc, HorizonSource.None, s.Count, spread, 0);
        }

        if (s.Count < MinStatisticalVehicles || FitHorizon(s, medianHeight) is not { } vh)
        {
            return null;
        }

        // Incertezza: ricampionamento (deterministico) e scarto sul rapporto di scala tra le corsie estreme.
        double vLo = Percentile(s.Select(x => x.V), 0.1), vHi = Percentile(s.Select(x => x.V), 0.9);
        double Ratio(double h) => (vHi - h) / (vLo - h);
        double ratio = Ratio(vh);
        var rng = new Random(20261102);
        var errors = new List<double>();
        for (int b = 0; b < 200; b++)
        {
            var sample = new List<(double V, double L, double H)>(s.Count);
            for (int i = 0; i < s.Count; i++)
            {
                sample.Add(s[rng.Next(s.Count)]);
            }

            errors.Add(FitHorizon(sample, medianHeight) is { } hb ? Math.Abs((Ratio(hb) / ratio) - 1) : 1);
        }

        double laneError = Percentile(errors, 0.9);
        return laneError > MaxStatisticalLaneError
            ? null
            : new HorizonEstimate(roadAngle, vh, uc, HorizonSource.Statistical, s.Count, spread, laneError);
    }

    /// <summary>
    /// Orizzonte da due auto riconosciute su corsie diverse: s_i = L_i / lunghezza_i = k·(v_i − v_h).
    /// Null se le due auto sono sulla stessa corsia o i dati non sono coerenti.
    /// </summary>
    public static HorizonEstimate? FromTwoReferences(
        CenterFrameMeasurement m1, double lengthMeters1,
        CenterFrameMeasurement m2, double lengthMeters2,
        double roadAngle, double imageWidth, double imageHeight)
    {
        double uc = ToRoad(new Point2(imageWidth / 2, imageHeight / 2), roadAngle).U;
        double v1 = ToRoad(m1.Best.Box.BottomCenter, roadAngle).V, v2 = ToRoad(m2.Best.Box.BottomCenter, roadAngle).V;
        double s1 = m1.LengthPixels / lengthMeters1, s2 = m2.LengthPixels / lengthMeters2;
        if (Math.Abs(v1 - v2) < 0.3 * Math.Min(m1.Best.Box.Height, m2.Best.Box.Height) || Math.Abs(s2 - s1) < 1e-9)
        {
            return null;
        }

        double vh = ((s2 * v1) - (s1 * v2)) / (s2 - s1);

        // La corsia più vicina (v maggiore) deve apparire più grande, e l'orizzonte stare sopra le auto.
        if ((v2 - v1) * (s2 - s1) <= 0 || vh >= Math.Min(v1, v2))
        {
            return null;
        }

        double u1 = VehicleScaleEstimator.RelativeUncertainty(m1), u2 = VehicleScaleEstimator.RelativeUncertainty(m2);
        return new HorizonEstimate(roadAngle, vh, uc, HorizonSource.TwoReferences, 2, Math.Abs(v1 - v2), Math.Sqrt((u1 * u1) + (u2 * u2)));
    }

    /// <summary>
    /// Linee A e B sulle estremità a terra del veicolo di riferimento (spigoli bassi del riquadro, nel
    /// fotogramma in cui è al centro), dirette verso il punto di fuga. Si estendono sotto il veicolo e
    /// verso l'orizzonte per coprire le altre corsie. Senza prospettiva sono perpendicolari alla strada.
    /// </summary>
    public static (LineSegment A, LineSegment B) LinesAtVehicleEnds(
        CenterFrameMeasurement m, HorizonEstimate? horizon, double imageWidth, double imageHeight)
    {
        if (horizon is not { HasPerspective: true } h || !m.Horizontal)
        {
            return VehicleScaleEstimator.LinesAtVehicleEnds(m, imageWidth, imageHeight);
        }

        BoundingBox box = m.Best.Box;
        double half = m.LengthPixels / 2;
        Point2 vp = h.VanishingPoint;

        LineSegment Toward(Point2 p)
        {
            Point2 d = vp - p;
            double dist = d.Length;
            Point2 dir = d * (1 / dist);
            double forward = Math.Min(3 * box.Height, 0.85 * dist);
            double back = 1.2 * box.Height;
            return new LineSegment(ClampInside(p, p - (dir * back), imageWidth, imageHeight), ClampInside(p, p + (dir * forward), imageWidth, imageHeight));
        }

        return (Toward(new Point2(box.CenterX - half, box.Bottom)), Toward(new Point2(box.CenterX + half, box.Bottom)));
    }

    /// <summary>Descrizione per l'utente e per info.txt.</summary>
    public static string Describe(HorizonEstimate? h) => h switch
    {
        null => Loc.T("Prospettiva non stimabile con le auto disponibili: le linee valgono per la corsia dell'auto di riferimento."),
        { Source: HorizonSource.None } => Loc.T("Una sola corsia: linee perpendicolari alla strada."),
        { Source: HorizonSource.TwoReferences } => Loc.S($"Linee verso il punto di fuga, ricavato da due auto riconosciute su corsie diverse (scarto tra le corsie circa ±{h.LaneErrorFraction * 100:0.0}%)."),
        _ => Loc.S($"Linee verso il punto di fuga, stimato dalla grandezza di {h.Vehicles} auto su più corsie (scarto tra le corsie fino a circa {h.LaneErrorFraction * 100:0.0}%)."),
    };

    private static double? FitHorizon(IReadOnlyList<(double V, double L, double H)> s, double medianHeight)
    {
        double minDv = 0.25 * medianHeight;
        var slopes = new List<double>();
        for (int i = 0; i < s.Count; i++)
        {
            for (int j = i + 1; j < s.Count; j++)
            {
                double dv = s[j].V - s[i].V;
                if (Math.Abs(dv) >= minDv)
                {
                    slopes.Add((s[j].L - s[i].L) / dv);
                }
            }
        }

        if (slopes.Count == 0)
        {
            return null;
        }

        double a = Median(slopes);
        if (a <= 0)
        {
            return null;
        }

        double b = Median(s.Select(x => x.L - (a * x.V)));
        double vh = -b / a;
        return vh < s.Min(x => x.V) - (0.3 * medianHeight) ? vh : null;
    }

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

    private static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

    private static double Percentile(IEnumerable<double> values, double q)
    {
        double[] v = values.Order().ToArray();
        if (v.Length == 0)
        {
            return 0;
        }

        double pos = q * (v.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, v.Length - 1);
        return v[lo] + ((v[hi] - v[lo]) * (pos - lo));
    }
}
