using TrafficSpeedAnalyzer.Core.Detection;

namespace TrafficSpeedAnalyzer.Core.Tracking;

/// <summary>Rilevamento associato a una traccia in un fotogramma.</summary>
/// <param name="Truncated">Il riquadro tocca il bordo dell'immagine o della zona analizzata: il veicolo è parzialmente visibile.</param>
/// <param name="Wheels">Ruote trovate nel riquadro, null se non cercate.</param>
public sealed record TrackPoint(long FrameIndex, double TimeSeconds, BoundingBox Box, VehicleClass Class, float Score, bool Truncated,
    IReadOnlyList<WheelCandidate>? Wheels = null);

/// <summary>Traccia conclusa (veicolo uscito o perso), con tutti i rilevamenti associati.</summary>
public sealed record FinishedTrack(int TrackId, IReadOnlyList<TrackPoint> Points);

public interface IVehicleTracker
{
    /// <summary>Numero di tracce attive o temporaneamente perse.</summary>
    int ActiveCount { get; }

    /// <param name="truncated">Per ogni rilevamento, se il riquadro è tagliato dal bordo (facoltativo).</param>
    IReadOnlyList<TrackedVehicle> Update(long frameIndex, double timeSeconds, IReadOnlyList<VehicleDetection> detections, IReadOnlyList<bool>? truncated = null);

    /// <summary>Restituisce (e dimentica) le tracce concluse dall'ultima chiamata.</summary>
    IReadOnlyList<FinishedTrack> TakeFinished();

    /// <summary>Conclude tutte le tracce confermate ancora aperte (fine del video).</summary>
    IReadOnlyList<FinishedTrack> FlushAll();

    void Reset();
}

public sealed record ByteTrackerOptions
{
    public static ByteTrackerOptions Default { get; } = new();

    /// <summary>Rilevamenti con punteggio superiore: associati per primi e possono creare nuove tracce.</summary>
    public float HighScore { get; init; } = 0.45f;

    /// <summary>Rilevamenti tra questa soglia e <see cref="HighScore"/> servono solo a proseguire tracce esistenti.</summary>
    public float LowScore { get; init; } = 0.1f;

    /// <summary>Punteggio minimo per iniziare una nuova traccia.</summary>
    public float NewTrackScore { get; init; } = 0.5f;

    /// <summary>Sovrapposizione minima (IoU) per associare un rilevamento ad alta confidenza.</summary>
    public double MinIoUHigh { get; init; } = 0.2;

    /// <summary>Sovrapposizione minima per i rilevamenti a bassa confidenza.</summary>
    public double MinIoULow { get; init; } = 0.4;

    /// <summary>Sovrapposizione minima per proseguire una traccia non ancora confermata.</summary>
    public double MinIoUTentative { get; init; } = 0.3;

    /// <summary>Rilevamenti necessari per confermare una traccia.</summary>
    public int MinHits { get; init; } = 3;

    /// <summary>Tempo dopo cui una traccia persa viene conclusa.</summary>
    public double MaxLostSeconds { get; init; } = 1.0;
}

/// <summary>
/// Tracker multi-oggetto in stile ByteTrack: predizione con filtro di Kalman, associazione
/// per sovrapposizione con l'algoritmo ungherese in due passaggi (prima i rilevamenti sicuri,
/// poi quelli deboli per non perdere veicoli parzialmente coperti).
/// Le posizioni registrate nelle tracce sono quelle misurate (non filtrate): il filtro serve
/// solo ad associare, così la stima dell'istante di attraversamento non subisce ritardi.
/// </summary>
public sealed class ByteTracker : IVehicleTracker
{
    private readonly ByteTrackerOptions _o;
    private readonly List<Track> _tracks = [];
    private readonly List<FinishedTrack> _finished = [];
    private int _nextId = 1;

    public ByteTracker(ByteTrackerOptions? options = null)
    {
        _o = options ?? ByteTrackerOptions.Default;
    }

    public int ActiveCount => _tracks.Count;

    public IReadOnlyList<TrackedVehicle> Update(long frameIndex, double timeSeconds, IReadOnlyList<VehicleDetection> detections, IReadOnlyList<bool>? truncated = null)
    {
        foreach (Track t in _tracks)
        {
            t.Predict(timeSeconds);
        }

        var high = new List<int>();
        var low = new List<int>();
        for (int i = 0; i < detections.Count; i++)
        {
            float s = detections[i].Score;
            if (s >= _o.HighScore)
            {
                high.Add(i);
            }
            else if (s >= _o.LowScore)
            {
                low.Add(i);
            }
        }

        var output = new List<TrackedVehicle>();
        var matchedTracks = new HashSet<Track>();

        void Apply(Track t, int detIndex)
        {
            VehicleDetection d = detections[detIndex];
            bool trunc = truncated is not null && detIndex < truncated.Count && truncated[detIndex];
            t.Update(frameIndex, timeSeconds, d, trunc, _o.MinHits);
            matchedTracks.Add(t);
            output.Add(new TrackedVehicle(t.Id, d, t.State == TrackState.Confirmed));
        }

        // 1) rilevamenti sicuri ↔ tracce confermate (anche se temporaneamente perse)
        List<Track> confirmed = _tracks.Where(t => t.State is TrackState.Confirmed or TrackState.Lost).ToList();
        List<int> remainingHigh = Associate(confirmed, high, detections, _o.MinIoUHigh, Apply);

        // 2) rilevamenti deboli ↔ tracce confermate rimaste (non perse)
        List<Track> leftover = confirmed.Where(t => !matchedTracks.Contains(t) && t.State == TrackState.Confirmed).ToList();
        Associate(leftover, low, detections, _o.MinIoULow, Apply);

        // 3) rilevamenti sicuri rimasti ↔ tracce non ancora confermate
        List<Track> tentative = _tracks.Where(t => t.State == TrackState.Tentative).ToList();
        remainingHigh = Associate(tentative, remainingHigh, detections, _o.MinIoUTentative, Apply);

        // Tracce non associate
        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            Track t = _tracks[i];
            if (matchedTracks.Contains(t))
            {
                continue;
            }

            if (t.State == TrackState.Tentative)
            {
                _tracks.RemoveAt(i); // probabile falso positivo
                continue;
            }

            t.State = TrackState.Lost;
            if (timeSeconds - t.LastUpdateTime > _o.MaxLostSeconds)
            {
                _finished.Add(t.ToFinished());
                _tracks.RemoveAt(i);
            }
        }

        // Nuove tracce
        foreach (int di in remainingHigh)
        {
            VehicleDetection d = detections[di];
            if (d.Score < _o.NewTrackScore)
            {
                continue;
            }

            bool trunc = truncated is not null && di < truncated.Count && truncated[di];
            var t = new Track(_nextId++, frameIndex, timeSeconds, d, trunc, _o.MinHits);
            _tracks.Add(t);
            output.Add(new TrackedVehicle(t.Id, d, t.State == TrackState.Confirmed));
        }

        return output;
    }

    private static List<int> Associate(
        List<Track> tracks,
        List<int> detIndices,
        IReadOnlyList<VehicleDetection> detections,
        double minIoU,
        Action<Track, int> apply)
    {
        if (tracks.Count == 0 || detIndices.Count == 0)
        {
            return detIndices;
        }

        var cost = new double[tracks.Count, detIndices.Count];
        for (int i = 0; i < tracks.Count; i++)
        {
            for (int j = 0; j < detIndices.Count; j++)
            {
                cost[i, j] = 1 - BoundingBox.IoU(tracks[i].PredictedBox, detections[detIndices[j]].Box);
            }
        }

        int[] assignment = Hungarian.Solve(cost, 1 - minIoU);
        var used = new bool[detIndices.Count];
        for (int i = 0; i < tracks.Count; i++)
        {
            int j = assignment[i];
            if (j >= 0)
            {
                used[j] = true;
                apply(tracks[i], detIndices[j]);
            }
        }

        var remaining = new List<int>();
        for (int j = 0; j < detIndices.Count; j++)
        {
            if (!used[j])
            {
                remaining.Add(detIndices[j]);
            }
        }

        return remaining;
    }

    public IReadOnlyList<FinishedTrack> TakeFinished()
    {
        if (_finished.Count == 0)
        {
            return [];
        }

        var list = _finished.ToList();
        _finished.Clear();
        return list;
    }

    public IReadOnlyList<FinishedTrack> FlushAll()
    {
        foreach (Track t in _tracks)
        {
            if (t.State != TrackState.Tentative)
            {
                _finished.Add(t.ToFinished());
            }
        }

        _tracks.Clear();
        return TakeFinished();
    }

    public void Reset()
    {
        _tracks.Clear();
        _finished.Clear();
        _nextId = 1;
    }

    private enum TrackState
    {
        Tentative,
        Confirmed,
        Lost,
    }

    private sealed class Track
    {
        private readonly List<TrackPoint> _points = [];
        private KalmanAxis _cx;
        private KalmanAxis _cy;
        private KalmanAxis _w;
        private KalmanAxis _h;
        private double _stateTime;
        private int _hits;

        public Track(int id, long frame, double time, VehicleDetection d, bool truncated, int minHits)
        {
            Id = id;
            BoundingBox b = d.Box;
            double size = Math.Max(4, b.Height);
            _cx = new KalmanAxis(b.CenterX, size / 10, size * 2);
            _cy = new KalmanAxis(b.CenterY, size / 10, size * 2);
            _w = new KalmanAxis(b.Width, size / 10, size / 2);
            _h = new KalmanAxis(b.Height, size / 10, size / 2);
            _stateTime = time;
            LastUpdateTime = time;
            _hits = 1;
            State = minHits <= 1 ? TrackState.Confirmed : TrackState.Tentative;
            _points.Add(new TrackPoint(frame, time, b, d.Class, d.Score, truncated, d.Wheels));
            PredictedBox = b;
        }

        public int Id { get; }

        public TrackState State { get; set; }

        public double LastUpdateTime { get; private set; }

        public BoundingBox PredictedBox { get; private set; }

        public void Predict(double time)
        {
            double dt = time - _stateTime;
            if (dt <= 0)
            {
                return;
            }

            double size = Math.Max(4, _h.Position);
            _cx.Predict(dt, size * 3);
            _cy.Predict(dt, size * 3);
            _w.Predict(dt, size);
            _h.Predict(dt, size);
            _stateTime = time;
            PredictedBox = BoundingBox.FromCenter(_cx.Position, _cy.Position, Math.Max(2, _w.Position), Math.Max(2, _h.Position));
        }

        public void Update(long frame, double time, VehicleDetection d, bool truncated, int minHits)
        {
            BoundingBox b = d.Box;
            double std = Math.Max(1, b.Height / 20);
            _cx.Update(b.CenterX, std);
            _cy.Update(b.CenterY, std);
            _w.Update(b.Width, std);
            _h.Update(b.Height, std);
            PredictedBox = BoundingBox.FromCenter(_cx.Position, _cy.Position, Math.Max(2, _w.Position), Math.Max(2, _h.Position));
            LastUpdateTime = time;
            _hits++;
            State = _hits >= minHits || State is TrackState.Confirmed or TrackState.Lost ? TrackState.Confirmed : TrackState.Tentative;
            _points.Add(new TrackPoint(frame, time, b, d.Class, d.Score, truncated, d.Wheels));
        }

        public FinishedTrack ToFinished() => new(Id, _points.ToList());
    }
}
