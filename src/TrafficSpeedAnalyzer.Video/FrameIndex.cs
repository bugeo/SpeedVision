namespace TrafficSpeedAnalyzer.Video;

/// <summary>Statistiche degli intervalli tra fotogrammi.</summary>
public sealed record FrameRateStats(double MeanFps, double MinFps, double MaxFps, bool IsVariable);

/// <summary>
/// Elenco ordinato dei timestamp reali (in secondi) di tutti i fotogrammi del video,
/// letti dal contenitore. È la fonte di verità per i tempi di ogni misura.
/// </summary>
public sealed class FrameIndex
{
    private readonly double[] _times;

    public FrameIndex(IEnumerable<double> timestamps)
    {
        _times = timestamps.Where(double.IsFinite).Order().Distinct().ToArray();
        if (_times.Length == 0)
        {
            throw new ArgumentException("Il video non contiene fotogrammi leggibili.", nameof(timestamps));
        }

        Stats = ComputeStats(_times);
    }

    public int Count => _times.Length;

    public double this[int index] => _times[index];

    public double FirstTime => _times[0];

    public double LastTime => _times[^1];

    public FrameRateStats Stats { get; }

    public IReadOnlyList<double> Times => _times;

    /// <summary>Indice del fotogramma con timestamp più vicino a <paramref name="time"/>.</summary>
    public int NearestIndex(double time)
    {
        int i = Array.BinarySearch(_times, time);
        if (i >= 0)
        {
            return i;
        }

        int next = ~i;
        if (next <= 0)
        {
            return 0;
        }

        if (next >= _times.Length)
        {
            return _times.Length - 1;
        }

        return time - _times[next - 1] <= _times[next] - time ? next - 1 : next;
    }

    /// <summary>Indice dell'ultimo fotogramma mostrato all'istante <paramref name="time"/>.</summary>
    public int IndexAtOrBefore(double time)
    {
        int i = Array.BinarySearch(_times, time);
        if (i >= 0)
        {
            return i;
        }

        return Math.Max(0, ~i - 1);
    }

    /// <summary>Durata tipica di un fotogramma attorno all'indice indicato.</summary>
    public double IntervalAround(int index)
    {
        if (_times.Length < 2)
        {
            return 1.0 / 30;
        }

        if (index <= 0)
        {
            return _times[1] - _times[0];
        }

        if (index >= _times.Length - 1)
        {
            return _times[^1] - _times[^2];
        }

        return (_times[index + 1] - _times[index - 1]) / 2;
    }

    private static FrameRateStats ComputeStats(double[] t)
    {
        if (t.Length < 2)
        {
            return new FrameRateStats(0, 0, 0, false);
        }

        var intervals = new double[t.Length - 1];
        for (int i = 1; i < t.Length; i++)
        {
            intervals[i - 1] = t[i] - t[i - 1];
        }

        double mean = (t[^1] - t[0]) / intervals.Length;

        // Percentili per ignorare singoli fotogrammi anomali (es. inizio/fine registrazione).
        double[] sorted = intervals.Order().ToArray();
        double p02 = sorted[(int)Math.Floor(0.02 * (sorted.Length - 1))];
        double p98 = sorted[(int)Math.Ceiling(0.98 * (sorted.Length - 1))];
        bool variable = mean > 0 && (p98 - p02) / mean > 0.05;
        return new FrameRateStats(1 / mean, p98 > 0 ? 1 / p98 : 0, p02 > 0 ? 1 / p02 : 0, variable);
    }
}
