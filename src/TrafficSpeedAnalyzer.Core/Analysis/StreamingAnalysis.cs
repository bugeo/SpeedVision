using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Tracking;

namespace TrafficSpeedAnalyzer.Core.Analysis;

public sealed record StreamingAnalysisOptions
{
    public static StreamingAnalysisOptions Default { get; } = new();

    /// <summary>Confidenza minima per iniziare a seguire un veicolo.</summary>
    public float StartScore { get; init; } = 0.5f;

    public TrackPassageOptions Passages { get; init; } = TrackPassageOptions.Default;

    /// <summary>
    /// Il passaggio viene misurato senza attendere la fine della traccia appena il veicolo ha
    /// superato entrambe le linee ed è stato seguito per questo tempo oltre l'uscita
    /// (abbastanza per la stima dell'istante). Utile quando il veicolo resta inquadrato o si ferma.
    /// </summary>
    public double EarlyMeasureAfterExitSeconds { get; init; } = 0.35;
}

/// <summary>
/// Analisi automatica alimentata un fotogramma alla volta con i rilevamenti già calcolati
/// (per esempio nel browser). Stessa logica del ciclo di AnalysisPipeline dell'app desktop:
/// riquadri tagliati dal bordo della zona esclusi dal punto di riferimento, tracker ByteTrack,
/// tracce concluse convertite in passaggi. Non dipende da video, modello o interfaccia.
/// </summary>
public sealed class StreamingAnalysis
{
    private readonly CalibrationSettings _calibration;
    private readonly StreamingAnalysisOptions _options;
    private readonly ByteTracker _tracker;
    private readonly List<TrackPassageCandidate> _new = [];
    private readonly Dictionary<string, int> _reasons = new();
    private double _lastTime = double.NegativeInfinity;

    // Storia di ogni traccia (stessi punti che il tracker conserva) per misurare in anticipo.
    private readonly Dictionary<int, List<TrackPoint>> _history = new();
    private readonly HashSet<int> _measured = new();
    private readonly Dictionary<int, double> _speeds = new();

    /// <param name="region">Zona analizzata, in coordinate del video (come le linee).</param>
    public StreamingAnalysis(CalibrationSettings calibration, BoundingBox region, StreamingAnalysisOptions? options = null)
    {
        _calibration = calibration;
        _options = options ?? StreamingAnalysisOptions.Default;
        Region = region;
        _tracker = new ByteTracker(ByteTrackerOptions.Default with
        {
            NewTrackScore = _options.StartScore,
            HighScore = Math.Min(_options.StartScore, ByteTrackerOptions.Default.HighScore),
        });
    }

    public BoundingBox Region { get; }

    public CalibrationSettings Calibration => _calibration;

    /// <summary>Tracce attive o temporaneamente perse: finché ce ne sono il modello va eseguito.</summary>
    public int ActiveTracks => _tracker.ActiveCount;

    public int FramesDetected { get; private set; }

    public int Passages { get; private set; }

    public int RejectedTracks { get; private set; }

    public IReadOnlyDictionary<string, int> RejectionReasons => _reasons;

    /// <summary>
    /// Aggiunge i rilevamenti di un fotogramma (coordinate del video). I fotogrammi devono
    /// arrivare in ordine di tempo; quelli saltati semplicemente non vengono passati.
    /// </summary>
    /// <param name="timeSeconds">Tempo reale del fotogramma rispetto al primo fotogramma del video.</param>
    public IReadOnlyList<TrackedVehicle> AddDetections(long frameIndex, double timeSeconds, IReadOnlyList<VehicleDetection> detections)
    {
        if (!double.IsFinite(timeSeconds) || timeSeconds <= _lastTime)
        {
            throw new ArgumentException("I fotogrammi devono avere tempi crescenti.", nameof(timeSeconds));
        }

        _lastTime = timeSeconds;
        var truncated = new bool[detections.Count];
        for (int i = 0; i < detections.Count; i++)
        {
            truncated[i] = AnalysisRegion.IsTruncated(detections[i].Box, Region);
        }

        IReadOnlyList<TrackedVehicle> vehicles = _tracker.Update(frameIndex, timeSeconds, detections, truncated);
        FramesDetected++;

        foreach (TrackedVehicle v in vehicles)
        {
            if (_measured.Contains(v.TrackId))
            {
                continue; // già misurata: non serve conservarne altre posizioni
            }

            int index = IndexOf(detections, v.Detection);
            bool trunc = index >= 0 && truncated[index];
            if (!_history.TryGetValue(v.TrackId, out List<TrackPoint>? points))
            {
                points = [];
                _history[v.TrackId] = points;
            }

            points.Add(new TrackPoint(frameIndex, timeSeconds, v.Detection.Box, v.Detection.Class, v.Detection.Score, trunc));
            if (v.IsConfirmed && !_measured.Contains(v.TrackId))
            {
                TryMeasureEarly(v.TrackId, points);
            }
        }

        Finish(_tracker.TakeFinished());

        // Tracce scartate dal tracker senza concludersi (falsi positivi): storia non più necessaria.
        if (FramesDetected % 50 == 0)
        {
            foreach (int id in _history.Where(h => h.Value[^1].TimeSeconds < timeSeconds - 5).Select(h => h.Key).ToList())
            {
                _history.Remove(id);
            }
        }

        return vehicles;
    }

    /// <summary>Velocità già misurata per la traccia (km/h), per mostrarla sull'immagine.</summary>
    public double? SpeedOf(int trackId) => _speeds.TryGetValue(trackId, out double kmh) ? kmh : null;

    private static int IndexOf(IReadOnlyList<VehicleDetection> detections, VehicleDetection d)
    {
        for (int i = 0; i < detections.Count; i++)
        {
            if (ReferenceEquals(detections[i], d))
            {
                return i;
            }
        }

        return -1;
    }

    private void TryMeasureEarly(int trackId, List<TrackPoint> points)
    {
        int usable = 0;
        foreach (TrackPoint p in points)
        {
            if (!p.Truncated)
            {
                usable++;
            }
        }

        if (usable < _options.Passages.MinObservations)
        {
            return;
        }

        TrackPassageCandidate c = TrackPassageExtractor.Extract(new FinishedTrack(trackId, points.ToList()), _calibration, _options.Passages);
        if (!c.IsPassage)
        {
            return;
        }

        double exit = c.Result!.Passage!.Exit.TimeSeconds;
        double last = c.Observations.Max(o => o.TimeSeconds);
        if (last - exit < _options.EarlyMeasureAfterExitSeconds)
        {
            return;
        }

        Accept(trackId, c);
    }

    private void Accept(int trackId, TrackPassageCandidate c)
    {
        _measured.Add(trackId);
        _speeds[trackId] = c.Result!.Passage!.Speed.SpeedKmh;
        Passages++;
        _new.Add(c);
    }

    /// <summary>Passaggi misurati dall'ultima chiamata.</summary>
    public IReadOnlyList<TrackPassageCandidate> TakeNewPassages()
    {
        if (_new.Count == 0)
        {
            return [];
        }

        var list = _new.ToList();
        _new.Clear();
        return list;
    }

    /// <summary>Fine del video (o interruzione): valuta anche i veicoli ancora visibili.</summary>
    public IReadOnlyList<TrackPassageCandidate> Complete()
    {
        Finish(_tracker.FlushAll());
        return TakeNewPassages();
    }

    private void Finish(IReadOnlyList<FinishedTrack> finished)
    {
        foreach (FinishedTrack t in finished)
        {
            _history.Remove(t.TrackId);
            if (_measured.Contains(t.TrackId))
            {
                continue; // già misurata mentre era ancora seguita
            }

            TrackPassageCandidate c = TrackPassageExtractor.Extract(t, _calibration, _options.Passages);
            if (c.IsPassage)
            {
                Accept(t.TrackId, c);
            }
            else if (t.Points.Count >= _options.Passages.MinObservations)
            {
                RejectedTracks++;
                string reason = c.RejectionReason ?? "motivo sconosciuto";
                _reasons[reason] = _reasons.GetValueOrDefault(reason) + 1;
            }
        }
    }
}

/// <summary>
/// Zona del fotogramma da analizzare attorno alle linee di rilevamento.
/// Stessa logica di TrafficSpeedAnalyzer.Detection.AnalysisRegion (app desktop), qui nel nucleo
/// perché serve anche al prototipo web; in seguito l'app desktop potrà usare questa.
/// </summary>
public static class AnalysisRegion
{
    /// <summary>
    /// Rettangolo che contiene le due linee, allargato in ogni direzione di <paramref name="marginFraction"/>
    /// della sua dimensione maggiore, così i veicoli vicini alle linee sono interi. Coordinate del video.
    /// </summary>
    public static BoundingBox FromCalibration(CalibrationSettings calibration, double videoWidth, double videoHeight, double marginFraction = 0.6)
    {
        Point2[] pts = [calibration.LineA.Start, calibration.LineA.End, calibration.LineB.Start, calibration.LineB.End];
        double minX = pts.Min(p => p.X);
        double maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y);
        double maxY = pts.Max(p => p.Y);
        double margin = Math.Max(maxX - minX, maxY - minY) * marginFraction;
        double x1 = Math.Clamp(minX - margin, 0, videoWidth);
        double y1 = Math.Clamp(minY - margin, 0, videoHeight);
        double x2 = Math.Clamp(maxX + margin, 0, videoWidth);
        double y2 = Math.Clamp(maxY + margin, 0, videoHeight);
        return new BoundingBox(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }

    /// <summary>
    /// Vero se il riquadro è tagliato in un modo che falsa il punto di riferimento (centro del lato
    /// inferiore): bordo inferiore, sinistro o destro della zona. Un veicolo tagliato solo in alto
    /// ha comunque il lato inferiore e il centro corretti, quindi resta utilizzabile.
    /// </summary>
    public static bool IsTruncated(BoundingBox box, BoundingBox region, double margin = 2)
    {
        return box.X <= region.X + margin
            || box.Right >= region.Right - margin
            || box.Bottom >= region.Bottom - margin;
    }

    public static BoundingBox Scale(BoundingBox b, double sx, double sy) => new(b.X * sx, b.Y * sy, b.Width * sx, b.Height * sy);
}
