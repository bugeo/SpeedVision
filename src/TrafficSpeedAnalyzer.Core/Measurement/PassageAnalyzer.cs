using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Measurement;

public enum TravelDirection
{
    AToB,
    BToA,
}

/// <summary>Passaggio misurato di un veicolo tra le due linee.</summary>
public sealed record VehiclePassage(
    int Id,
    TravelDirection Direction,
    CrossingEstimate Entry,
    CrossingEstimate Exit,
    SpeedResult Speed)
{
    public DetectionLine EntryLine => Direction == TravelDirection.AToB ? DetectionLine.A : DetectionLine.B;

    public DetectionLine ExitLine => Direction == TravelDirection.AToB ? DetectionLine.B : DetectionLine.A;
}

public sealed record PassageResult(VehiclePassage? Passage, string? FailureReason, CrossingResult CrossingA, CrossingResult CrossingB)
{
    public bool Success => Passage is not null;
}

public sealed record PassageOptions
{
    public static PassageOptions Default { get; } = new();

    public CrossingOptions Crossing { get; init; } = CrossingOptions.Default;

    /// <summary>Durata minima plausibile del passaggio tra le linee.</summary>
    public double MinDurationSeconds { get; init; } = 0.05;

    /// <summary>Velocità oltre la quale la misura viene considerata un errore.</summary>
    public double MaxPlausibleKmh { get; init; } = 300;

    /// <summary>Misura di riserva: ricostruisce l'ingresso se il veicolo compare già oltre la prima linea.</summary>
    public bool AllowExtrapolatedEntry { get; init; } = true;
}

/// <summary>
/// Dalla sequenza di posizioni di un veicolo ricava ingresso, uscita, verso e velocità.
/// Funziona in entrambi i versi di marcia: la linea attraversata per prima è l'ingresso.
/// </summary>
public static class PassageAnalyzer
{
    public static PassageResult Analyze(
        int id,
        IReadOnlyList<TrackObservation> observations,
        CalibrationSettings calibration,
        PassageOptions? options = null)
    {
        options ??= PassageOptions.Default;
        CrossingOptions co = options.Crossing;

        CrossingResult a = CrossingEstimator.Estimate(observations, calibration.LineA, co);
        CrossingResult b = CrossingEstimator.Estimate(observations, calibration.LineB, co);

        if (options.AllowExtrapolatedEntry && StartsBetweenLines(observations, calibration))
        {
            // Se una sola linea risulta attraversata e la traccia inizia tra le due linee,
            // l'altra è stata superata poco prima che il veicolo comparisse.
            if (a.Status == CrossingStatus.NotCrossed && b.Status == CrossingStatus.Crossed)
            {
                a = CrossingEstimator.EstimateFromTrackStart(observations, calibration.LineA, calibration.SideTowardsOther(DetectionLine.A), co);
            }
            else if (b.Status == CrossingStatus.NotCrossed && a.Status == CrossingStatus.Crossed)
            {
                b = CrossingEstimator.EstimateFromTrackStart(observations, calibration.LineB, calibration.SideTowardsOther(DetectionLine.B), co);
            }
        }

        PassageResult Fail(string reason) => new(null, reason, a, b);

        if (a.Status != CrossingStatus.Crossed)
        {
            return Fail(Loc.S($"Linea A: {a.Reason ?? Loc.T("non attraversata.")}"));
        }

        if (b.Status != CrossingStatus.Crossed)
        {
            return Fail(Loc.S($"Linea B: {b.Reason ?? Loc.T("non attraversata.")}"));
        }

        CrossingEstimate ea = a.Estimate!;
        CrossingEstimate eb = b.Estimate!;
        bool aFirst = ea.TimeSeconds <= eb.TimeSeconds;
        CrossingEstimate entry = aFirst ? ea : eb;
        CrossingEstimate exit = aFirst ? eb : ea;
        DetectionLine entryLine = aFirst ? DetectionLine.A : DetectionLine.B;
        DetectionLine exitLine = aFirst ? DetectionLine.B : DetectionLine.A;

        // All'ingresso il veicolo deve entrare nella zona tra le linee, all'uscita lasciarla.
        if (entry.SideAfter != calibration.SideTowardsOther(entryLine))
        {
            return Fail(Loc.S($"Verso incoerente: alla linea {entryLine} il veicolo non entra nel tratto di misura."));
        }

        if (exit.SideAfter == calibration.SideTowardsOther(exitLine))
        {
            return Fail(Loc.S($"Verso incoerente: alla linea {exitLine} il veicolo non esce dal tratto di misura."));
        }

        double duration = exit.TimeSeconds - entry.TimeSeconds;
        if (duration < options.MinDurationSeconds)
        {
            return Fail(Loc.T("Tempo di percorrenza troppo breve: verifica le posizioni indicate."));
        }

        SpeedResult speed = SpeedCalculator.Compute(
            calibration.DistanceMeters,
            entry.TimeSeconds,
            exit.TimeSeconds,
            entry.UncertaintySeconds,
            exit.UncertaintySeconds);

        if (speed.SpeedKmh > options.MaxPlausibleKmh)
        {
            return Fail(Loc.S($"Velocità non plausibile ({speed.SpeedKmh:0} km/h): misura scartata."));
        }

        var passage = new VehiclePassage(
            id,
            aFirst ? TravelDirection.AToB : TravelDirection.BToA,
            entry,
            exit,
            speed);

        return new PassageResult(passage, null, a, b);
    }

    /// <summary>Vero se la prima posizione (in ordine di tempo) è nel tratto compreso tra le due linee.</summary>
    internal static bool StartsBetweenLines(IReadOnlyList<TrackObservation> observations, CalibrationSettings calibration)
    {
        if (observations.Count == 0)
        {
            return false;
        }

        TrackObservation first = observations.MinBy(o => o.TimeSeconds);
        return calibration.LineA.SideOf(first.Position) == calibration.SideTowardsOther(DetectionLine.A)
            && calibration.LineB.SideOf(first.Position) == calibration.SideTowardsOther(DetectionLine.B);
    }
}
