using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Calibration;

public enum IssueSeverity
{
    /// <summary>La misura non è possibile finché il problema non viene corretto.</summary>
    Error,

    /// <summary>La misura è possibile ma con precisione ridotta.</summary>
    Warning,
}

public sealed record CalibrationIssue(IssueSeverity Severity, string Code, string Message);

/// <summary>Controlla che la calibrazione sia utilizzabile e segnala le configurazioni a rischio.</summary>
public static class CalibrationValidator
{
    public const double MinLineLengthPixels = 10;
    public const double MinSeparationPixels = 30;
    public const double RecommendedMinDistanceMeters = 15;
    public const double MaxReasonableDistanceMeters = 300;

    public static IReadOnlyList<CalibrationIssue> Validate(LineSegment? lineA, LineSegment? lineB, double? distanceMeters)
    {
        var issues = new List<CalibrationIssue>();

        if (lineA is null)
        {
            issues.Add(new(IssueSeverity.Error, "A_MISSING", Loc.T("Disegna la linea A.")));
        }
        else if (lineA.Value.Length < MinLineLengthPixels)
        {
            issues.Add(new(IssueSeverity.Error, "A_SHORT", Loc.T("La linea A è troppo corta.")));
        }

        if (lineB is null)
        {
            issues.Add(new(IssueSeverity.Error, "B_MISSING", Loc.T("Disegna la linea B.")));
        }
        else if (lineB.Value.Length < MinLineLengthPixels)
        {
            issues.Add(new(IssueSeverity.Error, "B_SHORT", Loc.T("La linea B è troppo corta.")));
        }

        if (distanceMeters is null || !double.IsFinite(distanceMeters.Value))
        {
            issues.Add(new(IssueSeverity.Error, "DIST_MISSING", Loc.T("Inserisci la distanza misurata tra le linee.")));
        }
        else if (distanceMeters.Value <= 0)
        {
            issues.Add(new(IssueSeverity.Error, "DIST_INVALID", Loc.T("La distanza deve essere maggiore di zero.")));
        }
        else if (distanceMeters.Value < RecommendedMinDistanceMeters)
        {
            issues.Add(new(IssueSeverity.Warning, "DIST_SHORT",
                Loc.S($"Distanza breve: l'errore relativo aumenta. Sono consigliati almeno {RecommendedMinDistanceMeters:0} m.")));
        }
        else if (distanceMeters.Value > MaxReasonableDistanceMeters)
        {
            issues.Add(new(IssueSeverity.Warning, "DIST_LONG",
                Loc.T("Distanza insolitamente lunga: verifica di aver inserito il valore in metri.")));
        }

        bool linesUsable = lineA is { } a && lineB is { } b &&
                           a.Length >= MinLineLengthPixels && b.Length >= MinLineLengthPixels;
        if (linesUsable)
        {
            LineSegment la = lineA!.Value;
            LineSegment lb = lineB!.Value;

            if (GeometryMath.SegmentsIntersect(la, lb))
            {
                issues.Add(new(IssueSeverity.Error, "LINES_CROSS",
                    Loc.T("Le linee A e B si incrociano: devono attraversare la strada separatamente.")));
            }
            else
            {
                double sep = Math.Min(Math.Abs(lb.SignedDistance(la.Midpoint)), Math.Abs(la.SignedDistance(lb.Midpoint)));
                if (sep < MinSeparationPixels)
                {
                    issues.Add(new(IssueSeverity.Warning, "LINES_CLOSE",
                        Loc.T("Le linee sono molto vicine nell'immagine: la misura sarà poco precisa.")));
                }
            }

            // Nessun controllo sul parallelismo: in prospettiva due linee perpendicolari alla strada
            // convergono verso il punto di fuga, quindi nell'immagine è normale che non siano parallele.
        }

        return issues;
    }

    public static bool HasErrors(IEnumerable<CalibrationIssue> issues) =>
        issues.Any(i => i.Severity == IssueSeverity.Error);
}
