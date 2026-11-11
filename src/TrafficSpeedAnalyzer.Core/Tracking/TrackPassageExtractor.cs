using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Tracking;

/// <summary>Punto del veicolo usato per la misura automatica.</summary>
public enum ReferencePoint
{
    /// <summary>Vertice in basso sul davanti del riquadro (paraurti).</summary>
    BoxCorner,

    /// <summary>Punto in cui la ruota anteriore tocca l'asfalto.</summary>
    FrontWheel,
}

/// <summary>Esito della conversione di una traccia automatica in passaggio.</summary>
public sealed record TrackPassageCandidate(
    FinishedTrack Track,
    IReadOnlyList<TrackObservation> Observations,
    VehicleClass Class,
    float MeanScore,
    PassageResult? Result,
    string? RejectionReason,
    ReferencePoint Reference = ReferencePoint.BoxCorner)
{
    public bool IsPassage => Result?.Success == true;
}

public sealed record TrackPassageOptions
{
    public static TrackPassageOptions Default { get; } = new();

    /// <summary>Posizioni utilizzabili minime nella traccia.</summary>
    public int MinObservations { get; init; } = 4;

    /// <summary>Confidenza media minima della traccia.</summary>
    public float MinMeanScore { get; init; } = 0.3f;

    /// <summary>Usa il contatto a terra della ruota anteriore quando è trovato con coerenza.</summary>
    public bool UseWheels { get; init; } = true;

    /// <summary>Scarto minimo tollerato, in altezze del riquadro, della ruota dalla sua posizione tipica rispetto al muso.</summary>
    public double WheelOffsetTolerance { get; init; } = 0.08;

    /// <summary>Quota minima dei fotogrammi utilizzabili in cui la ruota anteriore dev'essere trovata.</summary>
    public double MinWheelFraction { get; init; } = 0.5;

    public PassageOptions Passage { get; init; } = PassageOptions.Default with
    {
        // Con il rilevamento automatico niente ricostruzione all'indietro: se il veicolo compare
        // già oltre la linea, il passaggio non è misurabile con certezza.
        AllowExtrapolatedEntry = false,
    };
}

/// <summary>
/// Trasforma una traccia del tracker in un passaggio misurato. Il punto di riferimento preferito è il contatto
/// a terra della ruota anteriore: sta sul piano della strada come le linee (niente parallasse) ed è sempre lo
/// stesso punto del veicolo. Si usa quando la ruota è trovata nella maggior parte dei fotogrammi e sempre nella
/// stessa posizione rispetto al veicolo; altrimenti si usa il vertice in basso del riquadro rilevato (non
/// filtrato) sul davanti del veicolo, cioè quello che taglia per primo le linee: il destro per i veicoli che
/// vanno da sinistra a destra, il sinistro per gli altri. Il punto è lo stesso per l'ingresso e per l'uscita.
/// Esclude i riquadri tagliati dal bordo, nei quali vertici e ruote non corrispondono al veicolo.
/// </summary>
public static class TrackPassageExtractor
{
    /// <param name="calibration">Senza calibrazione si prepara solo la traccia (nessun passaggio): le linee arriveranno dopo.</param>
    public static TrackPassageCandidate Extract(FinishedTrack track, CalibrationSettings? calibration, TrackPassageOptions? options = null)
    {
        options ??= TrackPassageOptions.Default;

        List<TrackPoint> usable = track.Points.Where(p => !p.Truncated).ToList();
        Func<BoundingBox, Point2> corner = LeadingCorner(usable);
        var observations = usable
            .Select(p => new TrackObservation(p.FrameIndex, p.TimeSeconds, corner(p.Box)))
            .ToList();

        VehicleClass cls = track.Points.Count == 0
            ? VehicleClass.Other
            : track.Points.GroupBy(p => p.Class).OrderByDescending(g => g.Sum(p => p.Score)).First().Key;
        float meanScore = track.Points.Count == 0 ? 0 : track.Points.Average(p => p.Score);

        TrackPassageCandidate Reject(string reason, PassageResult? r = null) => new(track, observations, cls, meanScore, r, reason);

        if (observations.Count < options.MinObservations)
        {
            return Reject(Loc.T("Traccia troppo breve."));
        }

        if (meanScore < options.MinMeanScore)
        {
            return Reject(Loc.T("Rilevamento poco affidabile."));
        }

        List<TrackObservation>? wheelObservations = options.UseWheels ? FrontWheelObservations(usable, options) : null;

        if (calibration is null)
        {
            return wheelObservations is not null
                ? new TrackPassageCandidate(track, wheelObservations, cls, meanScore, null, null, ReferencePoint.FrontWheel)
                : new TrackPassageCandidate(track, observations, cls, meanScore, null, null);
        }

        if (wheelObservations is not null)
        {
            PassageResult wheelResult = PassageAnalyzer.Analyze(0, wheelObservations, calibration, options.Passage);
            if (wheelResult.Success)
            {
                return new TrackPassageCandidate(track, wheelObservations, cls, meanScore, wheelResult, null, ReferencePoint.FrontWheel);
            }
        }

        PassageResult result = PassageAnalyzer.Analyze(0, observations, calibration, options.Passage);
        return result.Success
            ? new TrackPassageCandidate(track, observations, cls, meanScore, result, null)
            : Reject(result.FailureReason ?? Loc.T("Passaggio non valido."), result);
    }

    /// <summary>
    /// Contatto a terra della ruota anteriore in ogni fotogramma in cui è trovata con coerenza: la ruota sul davanti
    /// rispetto al centro del riquadro, alla solita distanza dal muso e con il solito raggio (in proporzione
    /// all'altezza del riquadro). Null se il verso non è noto o se la ruota è trovata in troppo pochi fotogrammi.
    /// </summary>
    public static List<TrackObservation>? FrontWheelObservations(IReadOnlyList<TrackPoint> usable, TrackPassageOptions? options = null)
    {
        options ??= TrackPassageOptions.Default;
        int dir = TravelDirectionSign(usable);
        if (dir == 0)
        {
            return null;
        }

        var found = new List<(TrackPoint Point, WheelCandidate Wheel, double Offset, double Size)>();
        foreach (TrackPoint p in usable)
        {
            if (p.Wheels is not { Count: > 0 } wheels || p.Box.Height <= 0)
            {
                continue;
            }

            WheelCandidate? front = wheels
                .Where(w => dir > 0 ? w.Center.X >= p.Box.CenterX : w.Center.X <= p.Box.CenterX)
                .MaxBy(w => w.Score);
            if (front is not null)
            {
                double nose = dir > 0 ? p.Box.Right - front.Center.X : front.Center.X - p.Box.X;
                found.Add((p, front, nose / p.Box.Height, front.Radius / p.Box.Height));
            }
        }

        int needed = Math.Max(options.MinObservations, (int)Math.Ceiling(options.MinWheelFraction * usable.Count));
        if (found.Count < needed)
        {
            return null;
        }

        // La distanza tra muso e ruota cambia poco (il bordo del riquadro «balla»): si tollera lo scarto tipico,
        // ma non una «ruota» lontana dalle altre (ombra, ruota posteriore, cerchio scambiato per ruota).
        double offset = Median(found.Select(f => f.Offset));
        double size = Median(found.Select(f => f.Size));
        double spread = 1.4826 * Median(found.Select(f => Math.Abs(f.Offset - offset)));
        double tolerance = Math.Max(options.WheelOffsetTolerance, 3 * spread);
        var consistent = found
            .Where(f => Math.Abs(f.Offset - offset) <= tolerance && Math.Abs(f.Size - size) <= 0.15 * size)
            .Select(f => (Obs: new TrackObservation(f.Point.FrameIndex, f.Point.TimeSeconds, f.Wheel.Contact), Height: f.Point.Box.Height))
            .ToList();
        List<TrackObservation> forward = MovingForward(consistent, dir);
        return forward.Count >= needed ? forward : null;
    }

    /// <summary>Arretramento massimo tollerato tra due punti della ruota, in altezze del riquadro.</summary>
    public const double MaxBackwardStep = 0.15;

    /// <summary>
    /// La ruota di un veicolo in marcia non torna indietro: si tiene la sequenza più lunga di punti che avanzano nel verso
    /// di marcia (con una piccola tolleranza). Un salto all'indietro vuol dire che in quei fotogrammi è stata presa la
    /// ruota posteriore o un'altra cosa, per esempio quando il veicolo esce dall'inquadratura.
    /// </summary>
    private static List<TrackObservation> MovingForward(List<(TrackObservation Obs, double Height)> points, int dir)
    {
        var p = points.OrderBy(x => x.Obs.TimeSeconds).ToList();
        int n = p.Count;
        if (n < 2)
        {
            return [.. p.Select(x => x.Obs)];
        }

        var length = new int[n];
        var previous = new int[n];
        int best = 0;
        for (int i = 0; i < n; i++)
        {
            length[i] = 1;
            previous[i] = -1;
            for (int j = 0; j < i; j++)
            {
                double step = dir * (p[i].Obs.Position.X - p[j].Obs.Position.X);
                if (step >= -MaxBackwardStep * p[i].Height && length[j] + 1 > length[i])
                {
                    length[i] = length[j] + 1;
                    previous[i] = j;
                }
            }

            if (length[i] > length[best])
            {
                best = i;
            }
        }

        var kept = new List<TrackObservation>(length[best]);
        for (int k = best; k >= 0; k = previous[k])
        {
            kept.Add(p[k].Obs);
        }

        kept.Reverse();
        return kept;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] v = values.OrderBy(x => x).ToArray();
        return v.Length == 0 ? 0 : v.Length % 2 == 1 ? v[v.Length / 2] : (v[(v.Length / 2) - 1] + v[v.Length / 2]) / 2;
    }

    /// <summary>Verso nell'immagine: +1 verso destra, −1 verso sinistra, 0 fermo o non chiaro.</summary>
    private static int TravelDirectionSign(IReadOnlyList<TrackPoint> points)
    {
        if (points.Count < 2)
        {
            return 0;
        }

        int half = Math.Max(1, points.Count / 2);
        double dx = points.Skip(points.Count - half).Average(p => p.Box.CenterX) - points.Take(half).Average(p => p.Box.CenterX);
        return Math.Abs(dx) < 0.25 * points.Average(p => p.Box.Width) ? 0 : Math.Sign(dx);
    }

    /// <summary>
    /// Vertice in basso sul davanti del veicolo, dal verso di marcia (spostamento del centro dei riquadri
    /// tra la prima e l'ultima metà della traccia). Veicolo fermo: centro del lato inferiore.
    /// </summary>
    public static Func<BoundingBox, Point2> LeadingCorner(IReadOnlyList<TrackPoint> points)
    {
        if (points.Count < 2)
        {
            return b => b.BottomCenter;
        }

        int half = Math.Max(1, points.Count / 2);
        double first = points.Take(half).Average(p => p.Box.CenterX);
        double last = points.Skip(points.Count - half).Average(p => p.Box.CenterX);
        double dx = last - first;
        double minMove = 0.25 * points.Average(p => p.Box.Width);
        return Math.Abs(dx) < minMove ? b => b.BottomCenter
            : dx > 0 ? b => b.BottomRight
            : b => b.BottomLeft;
    }
}
