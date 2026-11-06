using System.Globalization;
using System.Text.Json;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Tracking;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Detection.Tests;

/// <summary>
/// Assets/drift_known_speed.mp4 (make_drift.py): il video a velocità note (50 e 72 km/h) con la camera che
/// vibra e, tra 1,5 e 2,5 s, si sposta di 160 × 90 px ruotando di 0,8° (come un treppiede urtato).
/// Con spostamenti così grandi vidstab può perdere il riferimento: la verifica deve segnalarlo.
/// </summary>
public sealed class DriftFixture : IAsyncLifetime
{
    public string? SkipReason { get; private set; }

    public FfmpegTools? Tools { get; private set; }

    public VideoSession? Drift { get; private set; }

    public StabilizationReport? Report { get; private set; }

    public VideoSession? Stabilized { get; private set; }

    /// <summary>Spostamento vero di ogni fotogramma: dx, dy (px) e rotazione (gradi, convenzione di cv2.getRotationMatrix2D usata da make_drift.py).</summary>
    public double[][] Truth { get; private set; } = [];

    public static string CacheRoot => Path.Combine(AppContext.BaseDirectory, "stabilization-drift-cache");

    public async ValueTask InitializeAsync()
    {
        Tools = await FfmpegLocator.LocateAsync();
        if (Tools is null || !await VideoStabilizer.IsSupportedAsync(Tools))
        {
            SkipReason = "FFmpeg con vidstab e libx264 non trovato.";
            return;
        }

        if (Directory.Exists(CacheRoot))
        {
            Directory.Delete(CacheRoot, recursive: true);
        }

        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(assets, "drift_known_speed.json")));
        Truth = doc.RootElement.GetProperty("motion").EnumerateArray()
            .Select(e => e.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray();

        Drift = await VideoSession.OpenAsync(Tools, Path.Combine(assets, "drift_known_speed.mp4"));
        Report = await VideoStabilizer.StabilizeAsync(Tools, Drift.Info, Drift.Index, CacheRoot);
        Stabilized = await VideoSession.OpenAsync(Tools, Report.OutputPath, recordingMetadataFrom: Drift.Info);
    }

    public async ValueTask DisposeAsync()
    {
        if (Stabilized is not null)
        {
            await Stabilized.DisposeAsync();
        }

        if (Drift is not null)
        {
            await Drift.DisposeAsync();
        }
    }
}

public class DriftStabilizationTests(DriftFixture fx) : IClassFixture<DriftFixture>
{
    // Come IntegrationTests: linee orizzontali a y = 180 (A) e y = 380 (B), 200 px = 20 m.
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

    /// <summary>
    /// Con uno spostamento così grande vidstab non riesce ad allineare tutto: il risultato non deve sembrare
    /// buono. La verifica deve segnalare i fotogrammi rimasti spostati (l'utente vede l'avviso «con riserva»).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public void LargeCameraMove_IsReported_NotHidden()
    {
        SkipIfNeeded();
        StabilizationReport r = fx.Report!;
        TestContext.Current.SendDiagnosticMessage(string.Create(CultureInfo.InvariantCulture,
            $"Deriva: prima {r.ShakeBeforeMaxPx:0.0} px, dopo {r.ResidualMaxPx:0.00} px, fotogrammi spostati {r.MisalignedFrames.Count}, deboli {r.WeakFrames}, {r.ElapsedSeconds:0.0} s"));
        Assert.Equal(fx.Truth.Length, r.FrameCount);
        Assert.True(r.ShakeBeforeMaxPx > 20, $"spostamento misurato {r.ShakeBeforeMaxPx:0.0} px");
        Assert.True(r.MisalignedFrames.Count > 0 || r.ResidualMaxPx < VideoStabilizer.MisalignedThresholdPx,
            "residuo alto senza fotogrammi segnalati");
        Assert.Equal(r.MisalignedFrames.Count > 0, r.ResidualMaxPx >= VideoStabilizer.MisalignedThresholdPx);
    }
}
