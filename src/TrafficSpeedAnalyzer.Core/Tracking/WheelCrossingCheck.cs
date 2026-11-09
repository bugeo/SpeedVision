using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Localization;
using TrafficSpeedAnalyzer.Core.Measurement;

namespace TrafficSpeedAnalyzer.Core.Tracking;

/// <summary>
/// Controlla che la ruota anteriore sia seguita con regolarità proprio dove conta, cioè attorno alle linee A e B:
/// la velocità dipende solo dagli istanti in cui il punto a terra le attraversa. Vicino a ciascuna linea servono
/// abbastanza fotogrammi con la ruota (quanti, si sceglie nelle impostazioni), senza buchi, e posizioni che avanzano in modo uniforme (lungo una curva
/// dolce, non a salti). Un salto vuol dire che in quei fotogrammi è stata presa un'altra cosa (la ruota
/// posteriore, un'ombra, una striscia): la misura non è affidabile e il veicolo si scarta.
/// </summary>
public static class WheelCrossingCheck
{
    /// <summary>Punti della ruota considerati per lato della linea (i più vicini all'attraversamento).</summary>
    public const int PointsPerSide = 4;

    /// <summary>Fotogrammi minimi con la ruota per lato della linea, se non indicato diversamente.</summary>
    public const int DefaultMinPointsPerSide = 2;

    /// <summary>Distanza massima dall'attraversamento dei punti considerati, in secondi.</summary>
    public const double WindowSeconds = 0.35;

    /// <summary>Buco massimo tra due punti vicini alla linea, in fotogrammi.</summary>
    public const double MaxGapFrames = 3.5;

    /// <summary>Scarto massimo di un punto dalla curva dei vicini, in raggi della ruota (minimo 2 pixel).</summary>
    public const double MaxDeviationRadii = 0.15;

    /// <summary>
    /// Null se attorno a entrambe le linee la ruota è seguita bene; altrimenti il motivo (per il registro).
    /// </summary>
    /// <param name="wheel">Punti a terra della ruota anteriore usati per la misura.</param>
    /// <param name="points">Punti della traccia con le ruote trovate (per il raggio); null: raggio stimato dal riquadro.</param>
    /// <param name="minPointsPerSide">Fotogrammi minimi con la ruota prima e dopo ciascuna linea (da 1 a <see cref="PointsPerSide"/>).</param>
    public static string? Check(IReadOnlyList<TrackObservation> wheel, VehiclePassage passage, IReadOnlyList<TrackPoint>? points,
        int minPointsPerSide = DefaultMinPointsPerSide)
    {
        minPointsPerSide = Math.Clamp(minPointsPerSide, 1, PointsPerSide);
        List<TrackObservation> obs = [.. wheel.OrderBy(o => o.TimeSeconds)];
        if (obs.Count < 2 * minPointsPerSide)
        {
            return Loc.T("ruota trovata in troppo pochi fotogrammi");
        }

        double frame = FrameInterval(points, obs);
        foreach ((CrossingEstimate crossing, DetectionLine line) in new[] { (passage.Entry, passage.EntryLine), (passage.Exit, passage.ExitLine) })
        {
            string? problem = CheckAround(obs, crossing.TimeSeconds, frame, points, minPointsPerSide);
            if (problem is not null)
            {
                return Loc.S($"vicino alla linea {line}: {problem}");
            }
        }

        return null;
    }

    private static string? CheckAround(List<TrackObservation> obs, double tc, double frame, IReadOnlyList<TrackPoint>? points, int minPointsPerSide)
    {
        List<TrackObservation> before = [.. obs.Where(o => o.TimeSeconds < tc && tc - o.TimeSeconds <= WindowSeconds).TakeLast(PointsPerSide)];
        List<TrackObservation> after = [.. obs.Where(o => o.TimeSeconds >= tc && o.TimeSeconds - tc <= WindowSeconds).Take(PointsPerSide)];
        if (before.Count < minPointsPerSide || after.Count < minPointsPerSide)
        {
            return Loc.T("ruota trovata in troppo pochi fotogrammi");
        }

        List<TrackObservation> near = [.. before, .. after];
        for (int i = 1; i < near.Count; i++)
        {
            if (near[i].TimeSeconds - near[i - 1].TimeSeconds > MaxGapFrames * frame)
            {
                return Loc.T("ruota persa per alcuni fotogrammi");
            }
        }

        // Curva dolce (parabola nel tempo) per x e per y; nessun punto deve scostarsene.
        double t0 = tc;
        double[] t = [.. near.Select(o => o.TimeSeconds - t0)];
        double[] rx = Residuals(t, [.. near.Select(o => o.Position.X)]);
        double[] ry = Residuals(t, [.. near.Select(o => o.Position.Y)]);
        double worst = 0;
        for (int i = 0; i < near.Count; i++)
        {
            worst = Math.Max(worst, Math.Sqrt((rx[i] * rx[i]) + (ry[i] * ry[i])));
        }

        double radius = Radius(near, points);
        double tolerance = Math.Max(2, MaxDeviationRadii * radius);
        return worst > tolerance ? Loc.T("la ruota non avanza in modo regolare") : null;
    }

    /// <summary>Raggio della ruota nei punti considerati; senza ruote note, un quinto dell'altezza del riquadro.</summary>
    private static double Radius(List<TrackObservation> near, IReadOnlyList<TrackPoint>? points)
    {
        if (points is null)
        {
            return 10;
        }

        var radii = new List<double>();
        foreach (TrackObservation o in near)
        {
            TrackPoint? p = points.FirstOrDefault(q => q.FrameIndex == o.FrameIndex);
            if (p is null)
            {
                continue;
            }

            WheelCandidate? w = p.Wheels?.MinBy(c => Point2Distance(c, o));
            radii.Add(w is not null && Point2Distance(w, o) < 1 ? w.Radius : 0.2 * p.Box.Height);
        }

        if (radii.Count == 0)
        {
            return 10;
        }

        radii.Sort();
        return radii[radii.Count / 2];
    }

    private static double Point2Distance(WheelCandidate c, TrackObservation o) =>
        Math.Sqrt(Math.Pow(c.Contact.X - o.Position.X, 2) + Math.Pow(c.Contact.Y - o.Position.Y, 2));

    private static double FrameInterval(IReadOnlyList<TrackPoint>? points, List<TrackObservation> obs)
    {
        IEnumerable<double> times = points is { Count: > 1 } ? points.Select(p => p.TimeSeconds) : obs.Select(o => o.TimeSeconds);
        double[] t = [.. times.Order()];
        double[] d = [.. t.Zip(t.Skip(1), (a, b) => b - a).Where(x => x > 0).Order()];
        return d.Length == 0 ? 1 / 30.0 : d[d.Length / 2];
    }

    /// <summary>Scarti dalla parabola dei minimi quadrati (dalla retta se i punti sono pochi).</summary>
    private static double[] Residuals(double[] t, double[] v)
    {
        int n = t.Length;
        int terms = n >= 5 ? 3 : 2;
        var a = new double[terms, terms];
        var b = new double[terms];
        for (int i = 0; i < n; i++)
        {
            double[] row = terms == 3 ? [1, t[i], t[i] * t[i]] : [1, t[i]];
            for (int r = 0; r < terms; r++)
            {
                for (int c = 0; c < terms; c++)
                {
                    a[r, c] += row[r] * row[c];
                }

                b[r] += row[r] * v[i];
            }
        }

        double[] coef = Solve(a, b);
        var res = new double[n];
        for (int i = 0; i < n; i++)
        {
            double fit = coef[0] + (coef[1] * t[i]) + (terms == 3 ? coef[2] * t[i] * t[i] : 0);
            res[i] = v[i] - fit;
        }

        return res;
    }

    /// <summary>Eliminazione di Gauss con pivot parziale (sistemi 2×2 o 3×3).</summary>
    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var y = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(m[pivot, col]) < 1e-12)
            {
                return new double[n];
            }

            for (int c = 0; c < n; c++)
            {
                (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            }

            (y[col], y[pivot]) = (y[pivot], y[col]);
            for (int r = col + 1; r < n; r++)
            {
                double f = m[r, col] / m[col, col];
                for (int c = col; c < n; c++)
                {
                    m[r, c] -= f * m[col, c];
                }

                y[r] -= f * y[col];
            }
        }

        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = y[r];
            for (int c = r + 1; c < n; c++)
            {
                s -= m[r, c] * x[c];
            }

            x[r] = s / m[r, r];
        }

        return x;
    }
}
