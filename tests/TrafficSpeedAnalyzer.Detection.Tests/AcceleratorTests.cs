using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Detection.Tests;

/// <summary>
/// L'accelerazione automatica (DirectML su Windows, CoreML sui Mac con chip Apple) deve trovare gli
/// stessi veicoli della CPU, negli stessi punti. Dove non c'è acceleratore (Linux, CI senza GPU)
/// entrambi usano la CPU e il confronto è banale, ma il test resta valido.
/// </summary>
public class AcceleratorTests(DetectionFixture fx) : IClassFixture<DetectionFixture>
{
    [Fact(Timeout = 300_000)]
    public async Task AutoAcceleration_FindsSameVehiclesAsCpu()
    {
        if (fx.SkipReason is not null)
        {
            Assert.Skip(fx.SkipReason);
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        string modelPath = new ModelManager(DetectionFixture.ModelsDirectory).FindLocal(ModelCatalog.Small)!;
        using var auto = new YoloxDetector(modelPath, "s", DetectorOptions.Default with { Acceleration = AccelerationMode.Auto });
        using var cpu = new YoloxDetector(modelPath, "s", DetectorOptions.Default with { Acceleration = AccelerationMode.CpuOnly });
        TestContext.Current.SendDiagnosticMessage($"Esecutore automatico: {auto.ExecutionProvider}");
        Assert.Equal("CPU", cpu.ExecutionProvider);

        int compared = 0;
        foreach (int frameIndex in new[] { 20, 30, 38, 45 })
        {
            VideoFrame frame = await fx.Session!.GetFullResolutionFrameAsync(frameIndex, ct);
            List<VehicleDetection> expected = [.. cpu.Detect(frame.Pixels, frame.Width, frame.Height).Where(d => d.Score >= 0.5)];
            List<VehicleDetection> actual = [.. auto.Detect(frame.Pixels, frame.Width, frame.Height).Where(d => d.Score >= 0.3)];

            foreach (VehicleDetection e in expected)
            {
                // Ogni veicolo sicuro per la CPU c'è anche con l'acceleratore, nello stesso posto.
                VehicleDetection? match = actual.MinBy(a => Math.Abs(a.Box.CenterX - e.Box.CenterX) + Math.Abs(a.Box.Bottom - e.Box.Bottom));
                Assert.NotNull(match);
                Assert.InRange(match.Box.CenterX, e.Box.CenterX - 3, e.Box.CenterX + 3);
                Assert.InRange(match.Box.Bottom, e.Box.Bottom - 3, e.Box.Bottom + 3);
                Assert.InRange(match.Score, e.Score - 0.1f, e.Score + 0.1f);
                compared++;
            }
        }

        Assert.True(compared > 0, "Nessun veicolo da confrontare nei fotogrammi di prova.");
    }
}
