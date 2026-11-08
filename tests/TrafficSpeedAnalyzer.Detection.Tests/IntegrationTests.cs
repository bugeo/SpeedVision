using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Tracking;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Detection.Tests;

/// <summary>
/// Prepara FFmpeg, il modello YOLOX-S (scaricato una volta e verificato con SHA-256)
/// e il video di prova a velocità note (Assets/known_speed.mp4, generato da make_known_speed.py:
/// due auto reali, riprese dall'alto, che si muovono a 50 e 72 km/h esatti).
/// </summary>
public sealed class DetectionFixture : IAsyncLifetime
{
    public string? SkipReason { get; private set; }

    public VideoSession? Session { get; private set; }

    public YoloxDetector? Detector { get; private set; }

    public string VideoPath => Path.Combine(AppContext.BaseDirectory, "Assets", "known_speed.mp4");

    public static string ModelsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrafficSpeedAnalyzer", "models");

    public async ValueTask InitializeAsync()
    {
        FfmpegTools? tools = await FfmpegLocator.LocateAsync();
        if (tools is null)
        {
            SkipReason = "FFmpeg non trovato.";
            return;
        }

        string modelPath;
        try
        {
            modelPath = await new ModelManager(ModelsDirectory).EnsureAsync(ModelCatalog.Small);
        }
        catch (HttpRequestException e)
        {
            SkipReason = "Modello non scaricabile (rete non disponibile): " + e.Message;
            return;
        }

        Detector = new YoloxDetector(modelPath, ModelCatalog.Small.DisplayName);
        Session = await VideoSession.OpenAsync(tools, VideoPath);
    }

    public async ValueTask DisposeAsync()
    {
        Detector?.Dispose();
        if (Session is not null)
        {
            await Session.DisposeAsync();
        }
    }
}

public class IntegrationTests(DetectionFixture fx) : IClassFixture<DetectionFixture>
{
    // Linee orizzontali a y = 180 (A) e y = 380 (B), su tutta la larghezza: 200 px = 20 m.
    private static readonly CalibrationSettings Calibration = new(
        new LineSegment(new Point2(0, 180), new Point2(768, 180)),
        new LineSegment(new Point2(0, 380), new Point2(768, 380)),
        20);

    private void SkipIfNeeded()
    {
        if (fx.SkipReason is not null)
        {
            Assert.Skip(fx.SkipReason);
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task Detector_FindsTheCar_WhereExpected()
    {
        SkipIfNeeded();
        VideoFrame frame = await fx.Session!.GetFullResolutionFrameAsync(38, TestContext.Current.CancellationToken);
        IReadOnlyList<VehicleDetection> dets = fx.Detector!.Detect(frame.Pixels, frame.Width, frame.Height);

        // Fotogramma 38 (t = 1,267 s): il bordo inferiore dell'immagine della prima auto (centro x = 250)
        // è a y ≈ 363; il riferimento Python con lo stesso modello dà 0,84 a (253, 362).
        VehicleDetection car = Assert.Single(dets, d => d.Score > 0.5 && Math.Abs(d.Box.CenterX - 250) < 40);
        Assert.Equal(VehicleClass.Car, car.Class);
        Assert.InRange(car.Box.Bottom, 345, 375);
        Assert.Matches("PU|CoreML", fx.Detector.ExecutionProvider); // "GPU (DirectML)", "Chip Apple (CoreML)" oppure "CPU"
    }

    [Fact(Timeout = 240_000)]
    public async Task Pipeline_MeasuresKnownSpeeds()
    {
        SkipIfNeeded();
        var found = new List<TrackPassageCandidate>();
        AnalysisSummary summary = await AnalysisPipeline.RunAsync(
            fx.Session!, fx.Detector!, Calibration, new AnalysisOptions(),
            c => found.Add(c), ct: TestContext.Current.CancellationToken);

        Assert.False(summary.Cancelled);
        Assert.Equal(fx.Session!.Index.Count, summary.FramesProcessed);
        Assert.Equal(2, found.Count);

        List<VehiclePassage> passages = found.Select(c => c.Result!.Passage!).OrderBy(p => p.Entry.TimeSeconds).ToList();
        await File.WriteAllLinesAsync(
            Path.Combine(AppContext.BaseDirectory, "known-speed-results.txt"),
            passages.Select(p => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{p.Direction} ingresso {p.Entry.TimeSeconds:0.000} s ({p.Entry.Method}) uscita {p.Exit.TimeSeconds:0.000} s ({p.Exit.Method}) {p.Speed.SpeedKmh:0.00} km/h ± {p.Speed.UncertaintyKmh:0.00}"))
            .Append($"{summary.ExecutionProvider}, {summary.FramesProcessed} fotogrammi, {summary.FramesSkipped} saltati, {summary.Elapsed.TotalSeconds:0.0} s"),
            TestContext.Current.CancellationToken);
        (double kmh, double tB, double tA)[] truth = [(50.0, 1.148, 2.588), (72.0, 3.65, 4.65)];
        for (int i = 0; i < 2; i++)
        {
            VehiclePassage p = passages[i];
            Assert.Equal(TravelDirection.BToA, p.Direction);
            Assert.InRange(p.Speed.SpeedKmh, truth[i].kmh * 0.96, truth[i].kmh * 1.04);

            // Il riquadro del modello termina qualche pixel sopra il bordo dell'immagine dell'auto:
            // gli istanti hanno uno scarto costante (≈ 50–70 ms) che non incide sulla velocità.
            Assert.InRange(p.Entry.TimeSeconds - truth[i].tB, -0.15, 0.05);
            Assert.InRange(p.Exit.TimeSeconds - truth[i].tA, -0.15, 0.05);
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task Pipeline_WithoutCalibration_KeepsTracks_AndSpeedsComeFromLinesDrawnLater()
    {
        // Nuovo ordine dei passi: prima l'analisi (senza linee), poi la calibrazione.
        SkipIfNeeded();
        var tracks = new List<TrackPassageCandidate>();
        AnalysisSummary summary = await AnalysisPipeline.RunAsync(
            fx.Session!, fx.Detector!, null, new AnalysisOptions(),
            c => tracks.Add(c), ct: TestContext.Current.CancellationToken);

        Assert.False(summary.Cancelled);
        Assert.True(tracks.Count >= 2, $"tracce {tracks.Count}");
        Assert.All(tracks, t => Assert.False(t.IsPassage));

        // Le linee arrivano dopo: le velocità si calcolano dalle tracce conservate, senza rifare l'analisi.
        List<VehiclePassage> passages = tracks
            .Select(t => PassageAnalyzer.Analyze(0, t.Observations, Calibration, TrackPassageOptions.Default.Passage).Passage)
            .OfType<VehiclePassage>()
            .OrderBy(p => p.Entry.TimeSeconds)
            .ToList();
        Assert.Equal(2, passages.Count);
        Assert.InRange(passages[0].Speed.SpeedKmh, 50 * 0.96, 50 * 1.04);
        Assert.InRange(passages[1].Speed.SpeedKmh, 72 * 0.96, 72 * 1.04);
    }

    [Fact(Timeout = 240_000)]
    public async Task Pipeline_SkipsEmptyFrames_WithoutLosingVehicles()
    {
        SkipIfNeeded();
        var found = new List<TrackPassageCandidate>();
        AnalysisSummary summary = await AnalysisPipeline.RunAsync(
            fx.Session!, fx.Detector!, Calibration, new AnalysisOptions { SkipStaticFrames = true },
            c => found.Add(c), ct: TestContext.Current.CancellationToken);

        Assert.True(summary.FramesSkipped > 20, $"saltati {summary.FramesSkipped}");
        Assert.Equal(2, found.Count);
    }

    [Fact(Timeout = 240_000)]
    public async Task Pipeline_CanBeCancelled()
    {
        SkipIfNeeded();
        using var cts = new CancellationTokenSource();
        int seen = 0;
        var progress = new SyncProgress<AnalysisProgress>(p =>
        {
            if (++seen == 3)
            {
                cts.Cancel();
            }
        });
        AnalysisSummary summary = await AnalysisPipeline.RunAsync(
            fx.Session!, fx.Detector!, Calibration, new AnalysisOptions { SkipStaticFrames = false },
            _ => { }, progress, ct: cts.Token);

        Assert.True(summary.Cancelled);
        Assert.True(summary.FramesProcessed < fx.Session!.Index.Count);
    }

    [Fact(Timeout = 240_000)]
    public async Task Benchmark_ReportsTimings()
    {
        SkipIfNeeded();
        BenchmarkResult b = await AnalysisPipeline.BenchmarkAsync(fx.Session!, fx.Detector!, Calibration, new AnalysisOptions(), 30, TestContext.Current.CancellationToken);
        Assert.Equal(30, b.Frames);
        Assert.True(b.MeanDetectMilliseconds > 0);
        Assert.True(b.FramesPerSecond > 0);
        TestContext.Current.SendDiagnosticMessage(
            $"{b.ModelName} su {b.ExecutionProvider}: {b.MeanDetectMilliseconds:0.0} ms/fotogramma (p95 {b.P95DetectMilliseconds:0.0}), decodifica {b.MeanDecodeMilliseconds:0.0} ms");
    }

    /// <summary>IProgress che richiama subito (senza contesto di sincronizzazione).</summary>
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
