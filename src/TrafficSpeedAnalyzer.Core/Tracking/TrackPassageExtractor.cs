using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Tracking;

/// <summary>Esito della conversione di una traccia automatica in passaggio.</summary>
public sealed record TrackPassageCandidate(
    FinishedTrack Track,
    IReadOnlyList<TrackObservation> Observations,
    VehicleClass Class,
    float MeanScore,
    PassageResult? Result,
    string? RejectionReason)
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

    public PassageOptions Passage { get; init; } = PassageOptions.Default with
    {
        // Con il rilevamento automatico niente ricostruzione all'indietro: se il veicolo compare
        // già oltre la linea, il passaggio non è misurabile con certezza.
        AllowExtrapolatedEntry = false,
    };
}

/// <summary>
/// Trasforma una traccia del tracker in un passaggio misurato. Il punto di riferimento è il vertice in basso
/// del riquadro rilevato (non filtrato) sul davanti del veicolo, cioè quello che taglia per primo le linee:
/// il vertice destro per i veicoli che vanno da sinistra a destra, il sinistro per gli altri. È lo stesso
/// punto per l'ingresso e per l'uscita, e corrisponde ai perni delle linee della calibrazione con l'auto
/// (spigoli bassi del riquadro). Esclude i riquadri tagliati dal bordo, nei quali i vertici non
/// corrispondono al veicolo.
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

        if (calibration is null)
        {
            return new TrackPassageCandidate(track, observations, cls, meanScore, null, null);
        }

        PassageResult result = PassageAnalyzer.Analyze(0, observations, calibration, options.Passage);
        return result.Success
            ? new TrackPassageCandidate(track, observations, cls, meanScore, result, null)
            : Reject(result.FailureReason ?? Loc.T("Passaggio non valido."), result);
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
