using System.Globalization;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Tracking;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Detection.Tests;

/// <summary>
/// Stabilizza Assets/shaky_known_speed.mp4 (il video a velocità note con una vibrazione simulata di
/// ±7 px e ±0,5°, generato da make_shaky.py) una sola volta per tutti i test.
/// </summary>
public sealed class StabilizationFixture : IAsyncLifetime
{
    public string? SkipReason { get; private set; }

    public FfmpegTools? Tools { get; private set; }

    public VideoSession? Shaky { get; private set; }

    public StabilizationReport? Report { get; private set; }

    public VideoSession? Stabilized { get; private set; }

    public YoloxDetector? Detector { get; private set; }

    public List<StabilizationProgress> ProgressLog { get; } = [];

    public static string CacheRoot => Path.Combine(AppContext.BaseDirectory, "stabilization-cache");

    public async ValueTask InitializeAsync()
    {
        Tools = await FfmpegLocator.LocateAsync();
        if (Tools is null)
        {
            SkipReason = "FFmpeg non trovato.";
            return;
        }

        if (!await VideoStabilizer.IsSupportedAsync(Tools))
        {
            SkipReason = "FFmpeg senza vidstab o libx264.";
            return;
        }

        if (Directory.Exists(CacheRoot))
        {
            Directory.Delete(CacheRoot, recursive: true);
        }

        Shaky = await VideoSession.OpenAsync(Tools, Path.Combine(AppContext.BaseDirectory, "Assets", "shaky_known_speed.mp4"));
        var progress = new SyncProgress<StabilizationProgress>(ProgressLog.Add);
        Report = await VideoStabilizer.StabilizeAsync(Tools, Shaky.Info, Shaky.Index, CacheRoot, progress);
        Stabilized = await VideoSession.OpenAsync(Tools, Report.OutputPath, recordingMetadataFrom: Shaky.Info);

        try
        {
            string model = await new ModelManager(DetectionFixture.ModelsDirectory).EnsureAsync(ModelCatalog.Small);
            Detector = new YoloxDetector(model, ModelCatalog.Small.DisplayName);
        }
        catch (HttpRequestException)
        {
            // i test con il modello verranno saltati
        }
    }

    public async ValueTask DisposeAsync()
    {
        Detector?.Dispose();
        if (Stabilized is not null)
        {
            await Stabilized.DisposeAsync();
        }

        if (Shaky is not null)
        {
            await Shaky.DisposeAsync();
        }
    }

    /// <summary>IProgress che chiama subito il delegato (Progress&lt;T&gt; usa il pool di thread).</summary>
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (this)
            {
                action(value);
            }
        }
    }
}

public class StabilizationTests(StabilizationFixture fx) : IClassFixture<StabilizationFixture>
{
    private static readonly CalibrationSettings Calibration = new(
        new LineSegment(new Point2(0, 180), new Point2(768, 180)),
        new LineSegment(new Point2(0, 380), new Point2(768, 380)),
        20);

    private void SkipIfNeeded(bool needsModel = false)
    {
        if (fx.SkipReason is not null)
        {
            Assert.Skip(fx.SkipReason);
        }

        if (needsModel && fx.Detector is null)
        {
            Assert.Skip("Modello non disponibile.");
        }
    }

    [Fact(Timeout = 240_000)]
    public void Report_MeasuresTheShake_AndTheResultIsStill()
    {
        SkipIfNeeded();
        StabilizationReport r = fx.Report!;
        Assert.False(r.FromCache);
        Assert.Equal(240, r.FrameCount);
        Assert.InRange(r.ShakeBeforeMaxPx, 8, 20);   // vibrazione simulata: fino a ~11 px più la rotazione agli angoli
        Assert.Empty(r.MisalignedFrames);
        Assert.True(r.ResidualMaxPx < VideoStabilizer.MisalignedThresholdPx, $"residuo {r.ResidualMaxPx}");
        Assert.Equal(0, r.WeakFrames);

        // Avanzamento: tutti e tre i passi, frazione crescente fino a 1.
        Assert.Equal([1, 2, 3], fx.ProgressLog.Select(p => p.Step).Distinct().Order());
        Assert.Equal(1.0, fx.ProgressLog[^1].Fraction, 3);
    }

    [Fact(Timeout = 240_000)]
    public void StabilizedVideo_KeepsFramesTimesSizeAndRecordingTime()
    {
        SkipIfNeeded();
        VideoSession a = fx.Shaky!, b = fx.Stabilized!;
        Assert.Equal(a.Index.Count, b.Index.Count);
        for (int i = 0; i < a.Index.Count; i++)
        {
            Assert.Equal(a.Index[i] - a.Index.FirstTime, b.Index[i] - b.Index.FirstTime, 0.001);
        }

        Assert.Equal(a.Info.DisplayWidth, b.Info.DisplayWidth);
        Assert.Equal(a.Info.DisplayHeight, b.Info.DisplayHeight);
        Assert.Equal(a.Info.RecordingStart, b.Info.RecordingStart);
        Assert.NotNull(b.Info.RecordingStart);
    }

    [Fact(Timeout = 240_000)]
    public async Task FirstFrame_IsUnchanged_SoLinesStayValid()
    {
        SkipIfNeeded();
        VideoFrame a = await fx.Shaky!.GetFullResolutionFrameAsync(0, TestContext.Current.CancellationToken);
        VideoFrame b = await fx.Stabilized!.GetFullResolutionFrameAsync(0, TestContext.Current.CancellationToken);
        double diff = 0;
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            diff += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        }

        // Solo la differenza dovuta alla ricodifica.
        Assert.True(diff / a.Pixels.Length < 3, $"differenza media {diff / a.Pixels.Length:0.00}");
    }

    [Fact(Timeout = 240_000)]
    public async Task SecondRun_ReusesTheCache()
    {
        SkipIfNeeded();
        StabilizationReport again = await VideoStabilizer.StabilizeAsync(
            fx.Tools!, fx.Shaky!.Info, fx.Shaky.Index, StabilizationFixture.CacheRoot, ct: TestContext.Current.CancellationToken);
        Assert.True(again.FromCache);
        Assert.Equal(fx.Report!.OutputPath, again.OutputPath);
        Assert.True(VideoStabilizer.CacheSizeBytes(StabilizationFixture.CacheRoot) > 0);
    }

    [Fact(Timeout = 240_000)]
    public async Task Cancellation_LeavesNothingInTheCache()
    {
        SkipIfNeeded();
        string root = Path.Combine(AppContext.BaseDirectory, "stabilization-cancel");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        using var cts = new CancellationTokenSource();
        var progress = new CancelAtProgress(cts, 0.4);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VideoStabilizer.StabilizeAsync(fx.Tools!, fx.Shaky!.Info, fx.Shaky.Index, root, progress, cts.Token));
        Assert.Null(VideoStabilizer.TryLoadCached(root, fx.Shaky.Info));
        Assert.Equal(0, VideoStabilizer.CacheSizeBytes(root));
    }

    [Fact(Timeout = 240_000)]
    public async Task Pipeline_OnStabilizedVideo_MeasuresKnownSpeeds()
    {
        SkipIfNeeded(needsModel: true);
        List<VehiclePassage> stab = await AnalyzeAsync(fx.Stabilized!);
        List<VehiclePassage> shaky = await AnalyzeAsync(fx.Shaky!);

        string Line(string label, VehiclePassage p) => string.Create(CultureInfo.InvariantCulture,
            $"{label}: {p.Direction} ingresso {p.Entry.TimeSeconds:0.000} s uscita {p.Exit.TimeSeconds:0.000} s {p.Speed.SpeedKmh:0.00} km/h");
        await File.WriteAllLinesAsync(
            Path.Combine(AppContext.BaseDirectory, "stabilization-results.txt"),
            [
                string.Create(CultureInfo.InvariantCulture,
                    $"Vibrazione max {fx.Report!.ShakeBeforeMaxPx:0.0} px, p95 {fx.Report.ShakeBeforeP95Px:0.0} px, residuo {fx.Report.ResidualMaxPx:0.0} px, {fx.Report.ElapsedSeconds:0.0} s"),
                .. shaky.Select(p => Line("mosso      ", p)),
                .. stab.Select(p => Line("stabilizzato", p)),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, stab.Count);
        double[] truth = [50.0, 72.0];
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(TravelDirection.BToA, stab[i].Direction);
            Assert.InRange(stab[i].Speed.SpeedKmh, truth[i] * 0.96, truth[i] * 1.04);
        }
    }

    private async Task<List<VehiclePassage>> AnalyzeAsync(VideoSession session)
    {
        var found = new List<TrackPassageCandidate>();
        AnalysisSummary s = await AnalysisPipeline.RunAsync(
            session, fx.Detector!, Calibration, new AnalysisOptions(), c => found.Add(c), ct: TestContext.Current.CancellationToken);
        Assert.False(s.Cancelled);
        return found.Where(c => c.Result?.Passage is not null).Select(c => c.Result!.Passage!).OrderBy(p => p.Entry.TimeSeconds).ToList();
    }

    private sealed class CancelAtProgress(CancellationTokenSource cts, double fraction) : IProgress<StabilizationProgress>
    {
        public void Report(StabilizationProgress value)
        {
            if (value.Fraction >= fraction)
            {
                cts.Cancel();
            }
        }
    }
}
