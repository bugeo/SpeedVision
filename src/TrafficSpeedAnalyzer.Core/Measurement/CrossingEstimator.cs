using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Measurement;

/// <summary>
/// Stima con precisione inferiore al fotogramma l'istante in cui il punto di riferimento
/// di un veicolo attraversa una linea.
/// </summary>
/// <remarks>
/// Il primo fotogramma oltre la linea può mostrare il veicolo già avanti di qualche decina di
/// centimetri. Usando la distanza (in pixel, perpendicolare alla linea) del punto di riferimento
/// nel fotogramma prima e in quello dopo, si ricava la frazione di intervallo in cui è avvenuto
/// l'attraversamento. Le distanze in pixel non dipendono dalla scala metrica, quindi la prospettiva
/// non introduce errori in questo passaggio.
/// </remarks>
public static class CrossingEstimator
{
    /// <summary>
    /// Limite inferiore del rumore di posizione: anche con punti perfettamente allineati
    /// la precisione reale di un clic o di un riquadro non è migliore di circa un pixel.
    /// </summary>
    private const double MinPositionSigmaPixels = 1.0;

    /// <summary>
    /// Cerca il primo attraversamento della linea nella sequenza di osservazioni
    /// e ne stima l'istante.
    /// </summary>
    public static CrossingResult Estimate(
        IReadOnlyList<TrackObservation> observations,
        LineSegment line,
        CrossingOptions? options = null)
    {
        options ??= CrossingOptions.Default;
        if (line.IsDegenerate)
        {
            return CrossingResult.Rejected(Loc.T("La linea non è valida."));
        }

        List<TrackObservation> obs = Normalize(observations);
        if (obs.Count < 2)
        {
            return CrossingResult.NotCrossed(Loc.T("Servono almeno due posizioni."));
        }

        double[] d = obs.Select(o => line.SignedDistance(o.Position)).ToArray();
        string? lastReason = null;

        for (int k = 1; k < obs.Count; k++)
        {
            double d0 = d[k - 1];
            double d1 = d[k];
            if (Math.Sign(d0) == Math.Sign(d1))
            {
                continue;
            }

            double f = Math.Abs(d0) / (Math.Abs(d0) + Math.Abs(d1));
            Point2 crossingPoint = Point2.Lerp(obs[k - 1].Position, obs[k].Position, f);
            double u = line.ProjectParameter(crossingPoint);
            if (u < -options.LineEndTolerance || u > 1 + options.LineEndTolerance)
            {
                lastReason = Loc.T("Il veicolo passa fuori dalle estremità della linea.");
                continue;
            }

            double t0 = obs[k - 1].TimeSeconds;
            double t1 = obs[k].TimeSeconds;
            double gap = t1 - t0;
            if (gap > options.MaxGapSeconds)
            {
                return CrossingResult.Rejected(
                    Loc.S($"Mancano posizioni attorno alla linea (intervallo di {gap:0.000} s)."));
            }

            int sideAfter = d1 != 0 ? Math.Sign(d1) : -Math.Sign(d0);
            double interpolatedTime = t0 + f * gap;
            double vPerp = (d1 - d0) / gap;
            double interpSigma = options.PositionNoisePixels * Math.Sqrt(f * f + (1 - f) * (1 - f)) / Math.Abs(vPerp);

            CrossingEstimate estimate = new(
                interpolatedTime,
                CrossingMethod.Interpolated,
                interpSigma,
                obs[k],
                Math.Abs(d1),
                sideAfter);

            if (TryRegression(obs, d, k, interpolatedTime, sideAfter, options, out double regTime, out double regSigma))
            {
                estimate = estimate with
                {
                    TimeSeconds = Math.Clamp(regTime, t0, t1),
                    Method = CrossingMethod.Regression,
                    UncertaintySeconds = regSigma,
                };
            }

            return CrossingResult.Crossed(estimate);
        }

        return CrossingResult.NotCrossed(lastReason ?? Loc.T("Il veicolo non attraversa la linea."));
    }

    /// <summary>
    /// Caso in cui il veicolo è già oltre la linea nel primo fotogramma disponibile
    /// (per esempio perché non era stato rilevato prima). L'istante viene ricostruito
    /// all'indietro dalla velocità perpendicolare alla linea, solo se il tempo da ricostruire
    /// non supera <see cref="CrossingOptions.MaxExtrapolationSeconds"/>.
    /// </summary>
    /// <param name="expectedSideAfter">Lato della linea su cui deve trovarsi il veicolo dopo l'attraversamento.</param>
    public static CrossingResult EstimateFromTrackStart(
        IReadOnlyList<TrackObservation> observations,
        LineSegment line,
        int expectedSideAfter,
        CrossingOptions? options = null)
    {
        options ??= CrossingOptions.Default;
        if (line.IsDegenerate || expectedSideAfter == 0)
        {
            return CrossingResult.Rejected(Loc.T("Parametri non validi."));
        }

        List<TrackObservation> obs = Normalize(observations);
        if (obs.Count < 2)
        {
            return CrossingResult.NotCrossed(Loc.T("Servono almeno due posizioni per ricostruire l'attraversamento."));
        }

        TrackObservation first = obs[0];
        double dFirst = line.SignedDistance(first.Position);
        if (Math.Sign(dFirst) != Math.Sign(expectedSideAfter))
        {
            return CrossingResult.NotCrossed(Loc.T("Il veicolo non è ancora oltre la linea."));
        }

        double u = line.ProjectParameter(first.Position);
        if (u < -options.LineEndTolerance || u > 1 + options.LineEndTolerance)
        {
            return CrossingResult.NotCrossed(Loc.T("Il veicolo è fuori dalle estremità della linea."));
        }

        var ts = new List<double>();
        var ds = new List<double>();
        foreach (TrackObservation o in obs)
        {
            if (o.TimeSeconds - first.TimeSeconds > options.RegressionWindowSeconds || ts.Count > options.RegressionPointsPerSide)
            {
                break;
            }

            ts.Add(o.TimeSeconds - first.TimeSeconds);
            ds.Add(line.SignedDistance(o.Position));
        }

        if (ts.Count < 2)
        {
            return CrossingResult.NotCrossed(Loc.T("Posizioni insufficienti vicino alla linea."));
        }

        if (!FitLine(ts, ds, out double a, out double b, out double sigma, out double sxx, out double tMean))
        {
            return CrossingResult.Rejected(Loc.T("Impossibile stimare la velocità del veicolo."));
        }

        if (Math.Sign(b) != Math.Sign(expectedSideAfter))
        {
            return CrossingResult.Rejected(Loc.T("Il veicolo non si allontana dalla linea nel verso atteso."));
        }

        // d(t) = a + b·t, con t relativo al primo fotogramma: l'attraversamento è dove d = 0.
        double tRel = -a / b;
        if (tRel > 0)
        {
            tRel = 0;
        }

        if (-tRel > options.MaxExtrapolationSeconds)
        {
            return CrossingResult.Rejected(Loc.S($"Il veicolo era già troppo oltre la linea ({-tRel:0.000} s): misura scartata."));
        }

        double s = ts.Count > 2 ? Math.Max(sigma, MinPositionSigmaPixels) : options.PositionNoisePixels;
        int n = ts.Count;
        double varT = s * s / (b * b) * (1.0 / n + (tRel - tMean) * (tRel - tMean) / sxx);

        return CrossingResult.Crossed(new CrossingEstimate(
            first.TimeSeconds + tRel,
            CrossingMethod.Extrapolated,
            Math.Sqrt(varT),
            first,
            Math.Abs(dFirst),
            Math.Sign(expectedSideAfter)));
    }

    /// <summary>
    /// Metodo semplice di confronto: istante del primo fotogramma oltre la linea, senza correzioni.
    /// Usato solo nei test per misurare il guadagno dell'interpolazione.
    /// </summary>
    public static double? FirstFrameBeyondTime(IReadOnlyList<TrackObservation> observations, LineSegment line)
    {
        List<TrackObservation> obs = Normalize(observations);
        for (int k = 1; k < obs.Count; k++)
        {
            if (Math.Sign(line.SignedDistance(obs[k - 1].Position)) != Math.Sign(line.SignedDistance(obs[k].Position)))
            {
                return obs[k].TimeSeconds;
            }
        }

        return null;
    }

    private static bool TryRegression(
        List<TrackObservation> obs,
        double[] d,
        int k,
        double approxTime,
        int sideAfter,
        CrossingOptions options,
        out double time,
        out double sigma)
    {
        time = sigma = double.NaN;
        int perSide = Math.Max(1, options.RegressionPointsPerSide);
        var ts = new List<double>();
        var ds = new List<double>();

        for (int i = k - 1; i >= 0 && i >= k - perSide; i--)
        {
            if (approxTime - obs[i].TimeSeconds > options.RegressionWindowSeconds)
            {
                break;
            }

            // Prima della linea il punto deve stare dal lato opposto (o sulla linea).
            if (Math.Sign(d[i]) == sideAfter)
            {
                break;
            }

            ts.Add(obs[i].TimeSeconds - approxTime);
            ds.Add(d[i]);
        }

        for (int i = k; i < obs.Count && i < k + perSide; i++)
        {
            if (obs[i].TimeSeconds - approxTime > options.RegressionWindowSeconds)
            {
                break;
            }

            if (Math.Sign(d[i]) == -sideAfter)
            {
                break;
            }

            ts.Add(obs[i].TimeSeconds - approxTime);
            ds.Add(d[i]);
        }

        if (ts.Count < 3)
        {
            return false;
        }

        if (!FitLine(ts, ds, out double a, out double b, out double residualSigma, out double sxx, out double tMean))
        {
            return false;
        }

        if (Math.Sign(b) != sideAfter)
        {
            return false;
        }

        double tRel = -a / b;
        int n = ts.Count;
        double s = Math.Max(residualSigma, MinPositionSigmaPixels);
        double varT = s * s / (b * b) * (1.0 / n + (tRel - tMean) * (tRel - tMean) / sxx);
        time = approxTime + tRel;
        sigma = Math.Sqrt(varT);
        return double.IsFinite(time) && double.IsFinite(sigma);
    }

    /// <summary>Retta dei minimi quadrati y = a + b·x.</summary>
    internal static bool FitLine(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        out double a,
        out double b,
        out double residualSigma,
        out double sxx,
        out double xMean)
    {
        int n = x.Count;
        a = b = residualSigma = sxx = xMean = double.NaN;
        if (n < 2)
        {
            return false;
        }

        double mx = x.Average();
        double my = y.Average();
        double sxy = 0;
        sxx = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - mx;
            sxx += dx * dx;
            sxy += dx * (y[i] - my);
        }

        if (sxx <= 0)
        {
            return false;
        }

        b = sxy / sxx;
        a = my - b * mx;
        xMean = mx;
        if (Math.Abs(b) < 1e-12)
        {
            return false;
        }

        double ssr = 0;
        for (int i = 0; i < n; i++)
        {
            double r = y[i] - (a + b * x[i]);
            ssr += r * r;
        }

        residualSigma = n > 2 ? Math.Sqrt(ssr / (n - 2)) : 0;
        return true;
    }

    /// <summary>Ordina per tempo, elimina duplicati e valori non validi.</summary>
    private static List<TrackObservation> Normalize(IReadOnlyList<TrackObservation> observations)
    {
        var list = observations
            .Where(o => double.IsFinite(o.TimeSeconds) && o.Position.IsFinite)
            .OrderBy(o => o.TimeSeconds)
            .ToList();

        var result = new List<TrackObservation>(list.Count);
        foreach (TrackObservation o in list)
        {
            if (result.Count > 0 && o.TimeSeconds - result[^1].TimeSeconds < 1e-9)
            {
                result[^1] = o; // stessa posizione temporale: tiene l'ultima indicata
                continue;
            }

            result.Add(o);
        }

        return result;
    }
}
