using System.Diagnostics;
using System.Threading.Channels;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Tracking;
using TrafficSpeedAnalyzer.Video;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Detection;

public sealed record AnalysisOptions
{
    /// <summary>Primo fotogramma da analizzare.</summary>
    public int StartIndex { get; init; }

    /// <summary>Ultimo fotogramma (incluso); -1 = fine del video.</summary>
    public int EndIndex { get; init; } = -1;

    /// <summary>Analizza solo il rettangolo attorno alle linee (più veloce e migliore con veicoli piccoli).</summary>
    public bool RegionAroundLines { get; init; } = true;

    /// <summary>Zona da analizzare indicata esplicitamente (per esempio attorno alla strada), in pixel del video. Ha la precedenza.</summary>
    public BoundingBox? Region { get; init; }

    /// <summary>Non esegue il modello se nella zona non cambia nulla e non ci sono veicoli seguiti.</summary>
    public bool SkipStaticFrames { get; init; } = true;

    /// <summary>Anche saltando, il modello viene eseguito almeno ogni N fotogrammi.</summary>
    public int MaxSkippedFrames { get; init; } = 15;

    /// <summary>Risoluzione massima di decodifica per l'analisi.</summary>
    public int MaxAnalysisWidth { get; init; } = 1920;

    public int MaxAnalysisHeight { get; init; } = 1080;

    /// <summary>Confidenza minima per iniziare a seguire un veicolo.</summary>
    public float StartScore { get; init; } = 0.5f;

    public TrackPassageOptions Passages { get; init; } = TrackPassageOptions.Default;

    /// <summary>
    /// Cerca le ruote nel riquadro di ogni veicolo: il punto di contatto a terra della ruota anteriore diventa
    /// il riferimento della misura quando è trovato con coerenza (vedi <see cref="TrackPassageExtractor"/>).
    /// </summary>
    public bool LocateWheels { get; init; } = true;

    /// <summary>
    /// Consegna anche le tracce che non attraversano le linee (abbastanza lunghe e affidabili).
    /// Senza calibrazione è sempre così: le linee arrivano dopo l'analisi.
    /// </summary>
    public bool CollectAllTracks { get; init; }
}

public sealed record AnalysisProgress(
    int FramesDone,
    int TotalFrames,
    int FrameIndex,
    int FramesDetected,
    int FramesSkipped,
    int Passages,
    int RejectedTracks,
    double FramesPerSecond,
    TimeSpan? Remaining);

/// <summary>Fotogramma analizzato con i veicoli seguiti, per l'anteprima. Coordinate del video.</summary>
public sealed record AnalysisPreview(VideoFrame Frame, IReadOnlyList<TrackedVehicle> Vehicles, BoundingBox Region);

public sealed record AnalysisSummary(
    int FramesProcessed,
    int FramesDetected,
    int FramesSkipped,
    int Passages,
    int RejectedTracks,
    double MeanDetectMilliseconds,
    string ExecutionProvider,
    TimeSpan Elapsed,
    bool Cancelled,
    IReadOnlyDictionary<string, int> RejectionReasons);

public sealed record BenchmarkResult(
    string ModelName,
    string ExecutionProvider,
    int Frames,
    double MeanDetectMilliseconds,
    double P95DetectMilliseconds,
    double MeanDecodeMilliseconds,
    int AnalysisWidth,
    int AnalysisHeight,
    BoundingBox Region,
    int VideoFrames,
    double VideoFps)
{
    /// <summary>Stima del tempo per analizzare tutto il video rilevando ogni fotogramma (caso peggiore).</summary>
    public TimeSpan EstimatedFullAnalysis =>
        TimeSpan.FromMilliseconds(VideoFrames * Math.Max(MeanDetectMilliseconds, MeanDecodeMilliseconds));

    public double FramesPerSecond => 1000.0 / Math.Max(1e-3, Math.Max(MeanDetectMilliseconds, MeanDecodeMilliseconds));
}

/// <summary>
/// Analisi automatica: decodifica in sequenza (in parallelo al rilevamento), rilevamento dei
/// veicoli e tracking. Senza calibrazione consegna tutte le tracce (le velocità si calcolano dopo,
/// quando le linee sono note); con la calibrazione consegna i passaggi tra le linee A e B.
/// </summary>
public static class AnalysisPipeline
{
    public static async Task<AnalysisSummary> RunAsync(
        VideoSession session,
        IVehicleDetector detector,
        CalibrationSettings? calibration,
        AnalysisOptions options,
        Action<TrackPassageCandidate> onPassage,
        IProgress<AnalysisProgress>? progress = null,
        Action<AnalysisPreview>? preview = null,
        CancellationToken ct = default)
    {
        VideoInfo info = session.Info;
        FrameIndex index = session.Index;
        int start = Math.Clamp(options.StartIndex, 0, index.Count - 1);
        int end = options.EndIndex < 0 ? index.Count - 1 : Math.Clamp(options.EndIndex, start, index.Count - 1);
        int total = end - start + 1;

        (int aw, int ah) = FrameSequence.AnalysisSize(info, options.MaxAnalysisWidth, options.MaxAnalysisHeight);
        double toAnalysisX = aw / (double)info.DisplayWidth;
        double toAnalysisY = ah / (double)info.DisplayHeight;
        bool collectAll = options.CollectAllTracks || calibration is null;
        BoundingBox regionVideo = options.Region is { } explicitRegion
            ? explicitRegion
            : options.RegionAroundLines && calibration is not null
                ? AnalysisRegion.FromCalibration(calibration, info.DisplayWidth, info.DisplayHeight)
                : new BoundingBox(0, 0, info.DisplayWidth, info.DisplayHeight);
        BoundingBox regionAnalysis = AnalysisRegion.Scale(regionVideo, toAnalysisX, toAnalysisY);

        var tracker = new ByteTracker(ByteTrackerOptions.Default with
        {
            NewTrackScore = options.StartScore,
            HighScore = Math.Min(options.StartScore, ByteTrackerOptions.Default.HighScore),
        });
        var gate = new MotionGate();
        var clock = Stopwatch.StartNew();
        var previewClock = Stopwatch.StartNew();
        int done = 0, detected = 0, skipped = 0, passages = 0, rejected = 0, sinceDetect = 0;
        double detectMs = 0;
        bool cancelled = false;
        var reasons = new Dictionary<string, int>();

        void Finish(IReadOnlyList<FinishedTrack> finished)
        {
            foreach (FinishedTrack t in finished)
            {
                TrackPassageCandidate c = TrackPassageExtractor.Extract(t, calibration, options.Passages);
                if (c.IsPassage)
                {
                    passages++;
                    onPassage(c);
                }
                else if (collectAll && c.Observations.Count >= options.Passages.MinObservations
                    && c.MeanScore >= options.Passages.MinMeanScore)
                {
                    passages++;
                    onPassage(c);
                }
                else if (t.Points.Count >= options.Passages.MinObservations)
                {
                    rejected++;
                    string reason = c.RejectionReason ?? Loc.T("motivo sconosciuto");
                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                }
            }
        }

        var channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(6) { SingleReader = true, SingleWriter = true });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task producer = Task.Run(async () =>
        {
            try
            {
                await foreach (VideoFrame f in FrameSequence.ReadAsync(session.Tools, info, index, aw, ah, start, end, linked.Token).ConfigureAwait(false))
                {
                    await channel.Writer.WriteAsync(f, linked.Token).ConfigureAwait(false);
                }

                channel.Writer.TryComplete();
            }
            catch (Exception e)
            {
                channel.Writer.TryComplete(e);
            }
        }, linked.Token);

        try
        {
            await Task.Run(async () =>
            {
                await foreach (VideoFrame frame in channel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                {
                    double t = frame.TimeSeconds - index.FirstTime;
                    bool motion = gate.HasMotion(frame.Pixels, frame.Width, frame.Height, regionAnalysis);
                    bool run = !options.SkipStaticFrames || motion || tracker.ActiveCount > 0 || sinceDetect >= options.MaxSkippedFrames;

                    IReadOnlyList<TrackedVehicle> vehicles = [];
                    if (run)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        IReadOnlyList<VehicleDetection> raw = detector.Detect(frame.Pixels, frame.Width, frame.Height, regionAnalysis);
                        detectMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                        detected++;
                        sinceDetect = 0;

                        // In coordinate del video, come le linee.
                        var dets = new List<VehicleDetection>(raw.Count);
                        var truncated = new List<bool>(raw.Count);
                        foreach (VehicleDetection d in raw)
                        {
                            bool cut = AnalysisRegion.IsTruncated(d.Box, regionAnalysis);
                            truncated.Add(cut);

                            // Ruote: solo nei veicoli interi, cercate sul fotogramma analizzato e riportate al video.
                            // Sono candidate: la ruota anteriore si sceglie sull'intera traccia.
                            IReadOnlyList<WheelCandidate>? wheels = null;
                            if (options.LocateWheels && !cut)
                            {
                                wheels = WheelLocator.LocateCandidates(frame.Pixels, frame.Width, frame.Height, frame.Stride, d.Box)
                                    .Select(w => w.Scale(1 / toAnalysisX, 1 / toAnalysisY))
                                    .ToList();
                            }

                            dets.Add(d with { Box = AnalysisRegion.Scale(d.Box, 1 / toAnalysisX, 1 / toAnalysisY), Wheels = wheels });
                        }

                        vehicles = tracker.Update(frame.Index, t, dets, truncated);
                        Finish(tracker.TakeFinished());
                    }
                    else
                    {
                        skipped++;
                        sinceDetect++;
                    }

                    done++;
                    if (preview is not null && previewClock.ElapsedMilliseconds >= 100)
                    {
                        previewClock.Restart();
                        preview(new AnalysisPreview(frame, vehicles, regionVideo));
                    }

                    if (progress is not null && (done % 5 == 0 || done == total))
                    {
                        double fps = done / Math.Max(1e-3, clock.Elapsed.TotalSeconds);
                        TimeSpan? remaining = done > 10 ? TimeSpan.FromSeconds((total - done) / fps) : null;
                        progress.Report(new AnalysisProgress(done, total, frame.Index, detected, skipped, passages, rejected, fps, remaining));
                    }
                }
            }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            linked.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // I veicoli ancora visibili alla fine (o all'interruzione) vengono comunque valutati.
        Finish(tracker.FlushAll());

        progress?.Report(new AnalysisProgress(done, total, start + done - 1, detected, skipped, passages, rejected,
            done / Math.Max(1e-3, clock.Elapsed.TotalSeconds), TimeSpan.Zero));

        return new AnalysisSummary(done, detected, skipped, passages, rejected,
            detected > 0 ? detectMs / detected : 0, detector.ExecutionProvider, clock.Elapsed, cancelled, reasons);
    }

    /// <summary>
    /// Misura le prestazioni su <paramref name="frames"/> fotogrammi consecutivi a partire da metà video,
    /// rilevando ogni fotogramma (nessun salto).
    /// </summary>
    public static async Task<BenchmarkResult> BenchmarkAsync(
        VideoSession session,
        IVehicleDetector detector,
        CalibrationSettings? calibration,
        AnalysisOptions options,
        int frames = 90,
        CancellationToken ct = default)
    {
        VideoInfo info = session.Info;
        FrameIndex index = session.Index;
        (int aw, int ah) = FrameSequence.AnalysisSize(info, options.MaxAnalysisWidth, options.MaxAnalysisHeight);
        BoundingBox regionVideo = options.RegionAroundLines && calibration is not null
            ? AnalysisRegion.FromCalibration(calibration, info.DisplayWidth, info.DisplayHeight)
            : new BoundingBox(0, 0, info.DisplayWidth, info.DisplayHeight);
        BoundingBox regionAnalysis = AnalysisRegion.Scale(regionVideo, aw / (double)info.DisplayWidth, ah / (double)info.DisplayHeight);

        int count = Math.Min(frames, index.Count);
        int start = Math.Max(0, index.Count / 2 - count / 2);
        var detectTimes = new List<double>();
        var decodeTimes = new List<double>();

        await Task.Run(async () =>
        {
            long last = Stopwatch.GetTimestamp();
            await foreach (VideoFrame f in FrameSequence.ReadAsync(session.Tools, info, index, aw, ah, start, start + count - 1, ct).ConfigureAwait(false))
            {
                decodeTimes.Add(Stopwatch.GetElapsedTime(last).TotalMilliseconds);
                long t0 = Stopwatch.GetTimestamp();
                detector.Detect(f.Pixels, f.Width, f.Height, regionAnalysis);
                detectTimes.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                last = Stopwatch.GetTimestamp();
            }
        }, ct).ConfigureAwait(false);

        // Il primo fotogramma include l'avvio di ffmpeg: escluso dalle medie.
        List<double> det = detectTimes.Skip(1).DefaultIfEmpty(detectTimes.FirstOrDefault()).ToList();
        List<double> dec = decodeTimes.Skip(1).DefaultIfEmpty(decodeTimes.FirstOrDefault()).ToList();
        double p95 = det.Count == 0 ? 0 : det.Order().ElementAt((int)Math.Min(det.Count - 1, Math.Ceiling(det.Count * 0.95) - 1));

        return new BenchmarkResult(detector.ModelName, detector.ExecutionProvider, detectTimes.Count,
            det.Average(), p95, dec.Average(), aw, ah, regionVideo, index.Count, index.Stats.MeanFps);
    }
}
