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
    /// <summary>Sotto questa distanza tra le linee si avvisa che sono troppo vicine (l'errore relativo cresce molto).</summary>
    public const double MinDistanceMeters = 2;
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
        else if (distanceMeters.Value < MinDistanceMeters)
        {
            issues.Add(new(IssueSeverity.Warning, "DIST_SHORT",
                Loc.S($"Le linee sono troppo vicine: meno di {MinDistanceMeters:0} m. La misura sarà poco precisa; se puoi, allontanale.")));
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

            // L'avviso «linee troppo vicine» dipende solo dalla distanza in metri (DIST_SHORT, sotto 2 m),
            // non dalla distanza in pixel nell'immagine.
            // Nessun controllo sul parallelismo: in prospettiva due linee perpendicolari alla strada
            // convergono verso il punto di fuga, quindi nell'immagine è normale che non siano parallele.
        }

        return issues;
    }

    public static bool HasErrors(IEnumerable<CalibrationIssue> issues) =>
        issues.Any(i => i.Severity == IssueSeverity.Error);
}
