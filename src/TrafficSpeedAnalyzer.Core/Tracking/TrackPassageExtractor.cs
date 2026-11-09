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
/// stesso punto del veicolo. Si usa quando la ruota è trovata nella maggior parte dei fotogrammi e si muove con
/// il veicolo (<see cref="WheelTrajectory"/>); altrimenti si usa il vertice in basso del riquadro rilevato (non
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
    /// Contatto a terra della ruota anteriore in ogni fotogramma in cui è sulla traiettoria della ruota
    /// (<see cref="WheelTrajectory"/>: i candidati che si muovono con il veicolo, sul davanti). Null se il verso
    /// non è noto o se la ruota è trovata in troppo pochi fotogrammi: allora si usa il vertice del riquadro.
    /// </summary>
    public static List<TrackObservation>? FrontWheelObservations(IReadOnlyList<TrackPoint> usable, TrackPassageOptions? options = null)
    {
        options ??= TrackPassageOptions.Default;
        List<(TrackPoint Point, WheelCandidate Wheel)>? front = WheelTrajectory.FrontWheel(usable);
        int needed = Math.Max(options.MinObservations, (int)Math.Ceiling(options.MinWheelFraction * usable.Count));
        if (front is null || front.Count < needed)
        {
            return null;
        }

        return [.. front.Select(f => new TrackObservation(f.Point.FrameIndex, f.Point.TimeSeconds, f.Wheel.Contact))];
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
