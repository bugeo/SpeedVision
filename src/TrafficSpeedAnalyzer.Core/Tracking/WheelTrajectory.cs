using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Tracking;

/// <summary>
/// Sceglie la ruota anteriore guardando l'intera traccia invece del singolo fotogramma. In ogni fotogramma la
/// ricerca propone più cerchi (le ruote, ma anche riflessi, ombre, fari, strisce sull'asfalto); la ruota vera è
/// quella che si muove con il veicolo. Il veicolo va a velocità quasi costante e, visto in prospettiva, il
/// contatto a terra di una ruota segue nell'immagine x(t) = (a·t + b) / (c·t + 1). Si cercano le traiettorie
/// di questo tipo che passano per più candidati (RANSAC: si provano terne di candidati di fotogrammi diversi
/// e si tiene la traiettoria che ne raccoglie di più, pesati con la loro sicurezza), poi tra queste si prende
/// quella sul davanti. In un fotogramma la ruota può mancare o essere confusa: il fotogramma resta fuori,
/// gli altri bastano.
/// </summary>
public static class WheelTrajectory
{
    /// <summary>Scarto massimo dalla traiettoria, in raggi della ruota.</summary>
    public const double Tolerance = 0.3;

    /// <summary>Differenza massima di raggio rispetto al riquadro (logaritmo del rapporto) tra i punti di una traiettoria.</summary>
    private const double RadiusTolerance = 0.4;

    private const int Trajectories = 3;
    private const int Attempts = 500;
    private const int MinPoints = 5;

    /// <param name="Size">Raggio rispetto all'altezza del riquadro: resta lo stesso anche se il veicolo si avvicina.</param>
    private readonly record struct Candidate(int Point, double T, double X, double Radius, double Size, double Score);

    /// <summary>
    /// Il contatto a terra della ruota anteriore nei fotogrammi in cui è sulla traiettoria trovata, in ordine di
    /// tempo; null se non c'è una traiettoria convincente sul davanti del veicolo.
    /// </summary>
    /// <param name="points">Punti della traccia con i riquadri interi (non tagliati dal bordo).</param>
    public static List<(TrackPoint Point, WheelCandidate Wheel)>? FrontWheel(IReadOnlyList<TrackPoint> points)
    {
        int dir = TravelDirection(points);
        if (dir == 0)
        {
            return null;
        }

        var all = new List<Candidate>();
        var wheels = new List<WheelCandidate>();
        double t0 = points.Count == 0 ? 0 : points.Average(p => p.TimeSeconds);
        for (int k = 0; k < points.Count; k++)
        {
            foreach (WheelCandidate w in points[k].Wheels ?? [])
            {
                if (w.Radius > 0 && w.Score > 0)
                {
                    all.Add(new Candidate(k, points[k].TimeSeconds - t0, w.Contact.X, w.Radius, w.Radius / Math.Max(1, points[k].Box.Height), w.Score));
                    wheels.Add(w);
                }
            }
        }

        if (all.Count < MinPoints)
        {
            return null;
        }

        var found = new List<(int[] Members, double[] Model)>();
        bool[] alive = Enumerable.Repeat(true, all.Count).ToArray();
        var rng = new Random(20261109);    // sempre lo stesso risultato per la stessa traccia
        for (int m = 0; m < Trajectories; m++)
        {
            (int[] Members, double[] Model)? best = BestTrajectory(all, alive, rng);
            if (best is null)
            {
                break;
            }

            found.Add(best.Value);
            // Via i candidati sulla traiettoria trovata o accanto (la stessa ruota proposta più volte).
            for (int i = 0; i < all.Count; i++)
            {
                if (Math.Abs(all[i].X - Predict(best.Value.Model, all[i].T)) < 0.9 * all[i].Radius)
                {
                    alive[i] = false;
                }
            }
        }

        // Sul davanti: in mediana la ruota è oltre il centro del riquadro nel verso di marcia.
        double Ahead(int[] members) => Median(members.Select(i =>
            (all[i].X - points[all[i].Point].Box.CenterX) * dir / points[all[i].Point].Box.Height));
        var front = found.Where(f => Ahead(f.Members) > 0).ToList();
        if (front.Count == 0)
        {
            return null;
        }

        // La più lunga; se un'altra è lunga quasi uguale, quella più avanti a metà traccia.
        int longest = front.Max(f => f.Members.Length);
        (int[] members, double[] _) = front
            .Where(f => f.Members.Length >= 0.6 * longest)
            .MaxBy(f => Predict(f.Model, 0) * dir);
        return [.. members.Select(i => (points[all[i].Point], wheels[i]))];
    }

    private static (int[] Members, double[] Model)? BestTrajectory(List<Candidate> all, bool[] alive, Random rng)
    {
        int[] pool = [.. Enumerable.Range(0, all.Count).Where(i => alive[i])];
        if (pool.Length < MinPoints)
        {
            return null;
        }

        // Estrazione pesata con la sicurezza: le terne di candidati netti si provano più spesso.
        double[] cumulative = new double[pool.Length];
        double total = 0;
        for (int i = 0; i < pool.Length; i++)
        {
            total += all[pool[i]].Score;
            cumulative[i] = total;
        }

        int Draw()
        {
            int i = Array.BinarySearch(cumulative, rng.NextDouble() * total);
            return pool[Math.Clamp(i < 0 ? ~i : i, 0, pool.Length - 1)];
        }

        double bestValue = 0;
        double[]? bestModel = null;
        double bestRadius = 0;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            int a = Draw(), b = Draw(), c = Draw();
            if (all[a].Point == all[b].Point || all[a].Point == all[c].Point || all[b].Point == all[c].Point)
            {
                continue;
            }

            double r0 = Median([all[a].Size, all[b].Size, all[c].Size]);
            if (new[] { a, b, c }.Any(i => Math.Abs(Math.Log(all[i].Size / r0)) > RadiusTolerance))
            {
                continue;
            }

            double[]? model = Fit([a, b, c], all);
            if (model is null || !Plausible(model, all))
            {
                continue;
            }

            int[] members = Members(model, r0, all, alive);
            double value = members.Sum(i => all[i].Score);
            if (value > bestValue)
            {
                (bestValue, bestModel, bestRadius) = (value, model, r0);
            }
        }

        if (bestModel is null)
        {
            return null;
        }

        // Rifinitura sui punti raccolti.
        int[] best = Members(bestModel, bestRadius, all, alive);
        for (int pass = 0; pass < 3 && best.Length >= MinPoints; pass++)
        {
            double[]? refit = Fit(best, all);
            if (refit is null || !Plausible(refit, all))
            {
                break;
            }

            bestModel = refit;
            bestRadius = Median(best.Select(i => all[i].Size));
            best = Members(bestModel, bestRadius, all, alive);
        }

        return best.Length >= MinPoints ? (best, bestModel) : null;
    }

    /// <summary>Per ogni fotogramma il candidato più vicino alla traiettoria, se entro la tolleranza.</summary>
    private static int[] Members(double[] model, double size, List<Candidate> all, bool[] alive)
    {
        var best = new Dictionary<int, (int Index, double Error)>();
        for (int i = 0; i < all.Count; i++)
        {
            if (!alive[i] || Math.Abs(Math.Log(all[i].Size / size)) > RadiusTolerance)
            {
                continue;
            }

            double e = Math.Abs(all[i].X - Predict(model, all[i].T)) / all[i].Radius;
            if (e < Tolerance && (!best.TryGetValue(all[i].Point, out var b) || e < b.Error))
            {
                best[all[i].Point] = (i, e);
            }
        }

        return [.. best.Values.Select(v => v.Index).OrderBy(i => all[i].Point)];
    }

    private static double Predict(double[] m, double t) => ((m[0] * t) + m[1]) / ((m[2] * t) + 1);

    /// <summary>Il denominatore non si annulla nell'intervallo della traccia (niente traiettorie «all'infinito»).</summary>
    private static bool Plausible(double[] m, List<Candidate> all)
    {
        double lo = all.Min(c => c.T), hi = all.Max(c => c.T);
        return (m[2] * lo) + 1 > 0.05 && (m[2] * hi) + 1 > 0.05 && m.All(double.IsFinite);
    }

    /// <summary>Minimi quadrati di x + c·t·x = a·t + b (lineare in a, b, c).</summary>
    private static double[]? Fit(IReadOnlyList<int> idx, List<Candidate> all)
    {
        var ata = new double[3, 3];
        var atb = new double[3];
        foreach (int i in idx)
        {
            double t = all[i].T, x = all[i].X;
            double[] row = [t, 1, -t * x];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    ata[r, c] += row[r] * row[c];
                }

                atb[r] += row[r] * x;
            }
        }

        return Solve3(ata, atb);
    }

    private static double[]? Solve3(double[,] a, double[] b)
    {
        double Det(double[,] m) =>
            (m[0, 0] * ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1])))
            - (m[0, 1] * ((m[1, 0] * m[2, 2]) - (m[1, 2] * m[2, 0])))
            + (m[0, 2] * ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])));
        double d = Det(a);
        double scale = Math.Abs(a[0, 0] * a[1, 1] * a[2, 2]);
        if (Math.Abs(d) <= 1e-12 * Math.Max(1, scale))
        {
            return null;
        }

        var result = new double[3];
        for (int k = 0; k < 3; k++)
        {
            var m = (double[,])a.Clone();
            for (int r = 0; r < 3; r++)
            {
                m[r, k] = b[r];
            }

            result[k] = Det(m) / d;
        }

        return result;
    }

    /// <summary>Verso nell'immagine: +1 verso destra, −1 verso sinistra, 0 fermo o non chiaro.</summary>
    internal static int TravelDirection(IReadOnlyList<TrackPoint> points)
    {
        if (points.Count < 2)
        {
            return 0;
        }

        int half = Math.Max(1, points.Count / 2);
        double dx = points.Skip(points.Count - half).Average(p => p.Box.CenterX) - points.Take(half).Average(p => p.Box.CenterX);
        return Math.Abs(dx) < 0.25 * points.Average(p => p.Box.Width) ? 0 : Math.Sign(dx);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] v = [.. values.OrderBy(x => x)];
        return v.Length == 0 ? 0 : v.Length % 2 == 1 ? v[v.Length / 2] : (v[(v.Length / 2) - 1] + v[v.Length / 2]) / 2;
    }
}
