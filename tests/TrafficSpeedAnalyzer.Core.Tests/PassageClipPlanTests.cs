using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using Xunit;

namespace TrafficSpeedAnalyzer.Core.Tests;

/// <summary>Sequenza dei fotogrammi del video di un passaggio: arrivo, fermo all'ingresso, tragitto, fermo all'uscita.</summary>
public class PassageClipPlanTests
{
    private const double Fps = 30;

    private static readonly double[] Times = [.. Enumerable.Range(0, 600).Select(i => i / Fps)];

    /// <summary>Ingresso tra i fotogrammi 99 e 100 (istante 3,31 s), uscita tra 159 e 160 (5,32 s).</summary>
    private static VehiclePassage Passage()
    {
        var entry = new CrossingEstimate(3.31, CrossingMethod.Interpolated, 0.001, new TrackObservation(100, 100 / Fps, new Point2(0, 0)), 3, 1);
        var exit = new CrossingEstimate(5.32, CrossingMethod.Interpolated, 0.001, new TrackObservation(160, 160 / Fps, new Point2(0, 0)), 3, -1);
        return new VehiclePassage(1, TravelDirection.AToB, entry, exit, SpeedCalculator.Compute(20, 3.31, 5.32));
    }

    [Fact]
    public void Build_ArrivesPausesAtEntryRunsAndPausesAtExit()
    {
        List<ClipFrame> f = PassageClipPlan.Build(Passage(), Times, Fps);

        // Arrivo: almeno 3 s prima dell'istante d'ingresso (3,31 s → dal fotogramma 9, a 0,30 s), senza cronometro.
        Assert.All(f.TakeWhile(c => c.Phase == ClipPhase.Approach), c => Assert.Null(c.Chrono));
        Assert.Equal(Enumerable.Range(9, 91), f.Where(c => c.Phase == ClipPhase.Approach).Select(c => c.SourceFrame));
        Assert.True(3.31 - Times[f[0].SourceFrame] >= 3);

        // Fermo all'ingresso: 1,5 s sul fotogramma 100 con il cronometro a zero.
        List<ClipFrame> entry = [.. f.Where(c => c.Phase == ClipPhase.EntryPause)];
        Assert.Equal(45, entry.Count);
        Assert.All(entry, c => Assert.Equal((100, 0.0), (c.SourceFrame, c.Chrono!.Value)));

        // Tragitto: il cronometro segna il tempo vero di ogni fotogramma dall'istante d'ingresso, sempre crescente.
        List<ClipFrame> run = [.. f.Where(c => c.Phase == ClipPhase.Timing)];
        Assert.Equal(Enumerable.Range(101, 59), run.Select(c => c.SourceFrame));
        Assert.Equal((101 / Fps) - 3.31, run[0].Chrono!.Value, 9);
        Assert.True(run.Zip(run.Skip(1)).All(p => p.Second.Chrono > p.First.Chrono));

        // Fermo all'uscita: 2 s sul fotogramma 160 con il cronometro sulla durata misurata, poi 3 s con la velocità.
        List<ClipFrame> exit = [.. f.Where(c => c.Phase == ClipPhase.ExitPause)];
        Assert.Equal(60, exit.Count);
        Assert.All(exit, c => Assert.Equal((160, 5.32 - 3.31), (c.SourceFrame, c.Chrono!.Value)));
        List<ClipFrame> speed = [.. f.Where(c => c.Phase == ClipPhase.SpeedPause)];
        Assert.Equal(90, speed.Count);
        Assert.All(speed, c => Assert.Equal((160, 5.32 - 3.31), (c.SourceFrame, c.Chrono!.Value)));

        // Le fasi sono in ordine.
        Assert.True(f.Select(c => (int)c.Phase).Zip(f.Select(c => (int)c.Phase).Skip(1)).All(p => p.Second >= p.First));
    }

    [Fact]
    public void Build_StartsAtTheBeginningWhenTheVideoIsShorter()
    {
        List<ClipFrame> f = PassageClipPlan.Build(Passage(), Times, Fps, new PassageClipOptions { ApproachSeconds = 10 });

        Assert.Equal(0, f[0].SourceFrame);
    }
}
