namespace TrafficSpeedAnalyzer.Core.Measurement;

public enum CrossingMethod
{
    /// <summary>Interpolazione lineare tra il fotogramma prima e quello dopo la linea.</summary>
    Interpolated,

    /// <summary>Retta di regressione su più fotogrammi attorno alla linea (riduce il rumore di posizione).</summary>
    Regression,

    /// <summary>
    /// Il veicolo era già oltre la linea nel primo fotogramma disponibile: l'istante viene
    /// ricostruito all'indietro dalla sua velocità, solo se il sorpasso è piccolo.
    /// </summary>
    Extrapolated,
}

public enum CrossingStatus
{
    Crossed,
    NotCrossed,
    Rejected,
}

/// <summary>Istante stimato in cui il punto di riferimento ha attraversato una linea.</summary>
public sealed record CrossingEstimate(
    double TimeSeconds,
    CrossingMethod Method,
    double UncertaintySeconds,
    TrackObservation FirstBeyond,
    double OvershootPixels,
    int SideAfter);

public sealed record CrossingResult(CrossingStatus Status, CrossingEstimate? Estimate, string? Reason)
{
    public static CrossingResult Crossed(CrossingEstimate estimate) => new(CrossingStatus.Crossed, estimate, null);

    public static CrossingResult NotCrossed(string reason) => new(CrossingStatus.NotCrossed, null, reason);

    public static CrossingResult Rejected(string reason) => new(CrossingStatus.Rejected, null, reason);
}

public sealed record CrossingOptions
{
    public static CrossingOptions Default { get; } = new();

    /// <summary>Tolleranza oltre le estremità della linea, in frazione della sua lunghezza.</summary>
    public double LineEndTolerance { get; init; } = 0.05;

    /// <summary>Numero massimo di osservazioni usate per lato nella regressione.</summary>
    public int RegressionPointsPerSide { get; init; } = 3;

    /// <summary>Intervallo massimo attorno all'attraversamento usato per la regressione, in secondi.</summary>
    public double RegressionWindowSeconds { get; init; } = 0.35;

    /// <summary>Intervallo massimo tra due osservazioni a cavallo della linea, in secondi.</summary>
    public double MaxGapSeconds { get; init; } = 0.5;

    /// <summary>Rumore stimato sulla posizione del punto di riferimento, in pixel (1 sigma).</summary>
    public double PositionNoisePixels { get; init; } = 1.5;

    /// <summary>Tempo massimo ricostruibile all'indietro se manca il fotogramma prima della linea.</summary>
    public double MaxExtrapolationSeconds { get; init; } = 0.12;
}
