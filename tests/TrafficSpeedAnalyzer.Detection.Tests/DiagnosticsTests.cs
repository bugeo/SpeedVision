using System.Diagnostics;
using System.Globalization;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Detection.Tests;

/// <summary>
/// Scrive detection-diagnostics.txt (cartella di output dei test): esecutore usato, tempi e punteggio
/// migliore per fotogramma con CPU e con l'accelerazione automatica, da confrontare con il riferimento Python.
/// Il file viene aggiornato man mano, così è leggibile anche se il test viene interrotto.
/// </summary>
public class DiagnosticsTests(DetectionFixture fx) : IClassFixture<DetectionFixture>
{
    [Fact(Timeout = 240_000)]
    public async Task WriteDetectionDiagnostics()
    {
        if (fx.SkipReason is not null)
        {
            Assert.Skip(fx.SkipReason);
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        string modelPath = new ModelManager(DetectionFixture.ModelsDirectory).FindLocal(ModelCatalog.Small)!;
        string path = Path.Combine(AppContext.BaseDirectory, "detection-diagnostics.txt");
        var inv = CultureInfo.InvariantCulture;
        await File.WriteAllTextAsync(path, $"Inizio {DateTime.Now:HH:mm:ss}\n", ct);

        foreach (AccelerationMode mode in new[] { AccelerationMode.Auto, AccelerationMode.CpuOnly })
        {
            var sw = Stopwatch.StartNew();
            using var det = new YoloxDetector(modelPath, "s", DetectorOptions.Default with { Acceleration = mode, MinScore = 0.01f });
            await File.AppendAllTextAsync(path, string.Create(inv, $"{mode} -> {det.ExecutionProvider}, creazione {sw.ElapsedMilliseconds} ms\n"), ct);

            var scores = new List<string>();
            sw.Restart();
            int n = 0;
            await foreach (VideoFrame f in FrameSequence.ReadAsync(fx.Session!.Tools, fx.Session.Info, fx.Session.Index,
                fx.Session.Info.DisplayWidth, fx.Session.Info.DisplayHeight, 20, 49, ct))
            {
                IReadOnlyList<VehicleDetection> d = det.Detect(f.Pixels, f.Width, f.Height);
                VehicleDetection? best = d.MaxBy(x => x.Score);
                scores.Add(best is null ? "-" : string.Create(inv, $"{best.Score:0.00}@{best.Box.CenterX:0},{best.Box.Bottom:0}"));
                n++;
            }

            await File.AppendAllTextAsync(path, string.Create(inv,
                $"  {n} fotogrammi (20–49) in {sw.ElapsedMilliseconds} ms, {sw.ElapsedMilliseconds / Math.Max(1, n)} ms/fotogramma\n  {string.Join(' ', scores)}\n"), ct);
        }
    }
}
