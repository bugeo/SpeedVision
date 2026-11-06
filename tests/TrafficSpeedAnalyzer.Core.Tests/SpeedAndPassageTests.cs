using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using static TrafficSpeedAnalyzer.Core.Tests.SyntheticTrack;

namespace TrafficSpeedAnalyzer.Core.Tests;

public class SpeedCalculatorTests
{
    [Fact]
    public void TwentyMetersIn1_44Seconds_Is50Kmh()
    {
        SpeedResult r = SpeedCalculator.Compute(20, 10.0, 11.44);
        Assert.Equal(1.44, r.DurationSeconds, 9);
        Assert.Equal(50.0, r.SpeedKmh, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDistance_Throws(double distance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedCalculator.Compute(distance, 0, 1));
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.0)]
    public void ExitNotAfterEntry_Throws(double entry, double exit)
    {
        Assert.Throws<ArgumentException>(() => SpeedCalculator.Compute(20, entry, exit));
    }

    [Fact]
    public void Uncertainty_CombinesBothTimes()
    {
        // 20 m in 1 s = 72 km/h; sigma 10 ms su ciascun istante → relativo √2·1%.
        SpeedResult r = SpeedCalculator.Compute(20, 0, 1, 0.01, 0.01);
        Assert.Equal(72 * Math.Sqrt(2) * 0.01, r.UncertaintyKmh, 9);
    }

    [Fact]
    public void Uncertainty_IncludesDistanceError()
    {
        SpeedResult r = SpeedCalculator.Compute(20, 0, 1, 0, 0, distanceSigmaMeters: 0.2);
        Assert.Equal(72 * 0.01, r.UncertaintyKmh, 9);
    }
}

public class PassageAnalyzerTests
{
    private static List<TrackObservation> Track(double kmh, double fps, double phase, double direction = 1, double vy = 0)
    {
        double vx = KmhToPxPerS(kmh) * direction;
        var p0 = direction > 0 ? new Point2(100, 400) : new Point2(700, 400);
        double travel = 600 / Math.Abs(vx);
        return Linear(ConstantFrameTimes(fps, 0, travel, phase), p0, new Point2(vx, vy));
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(50.0)]
    [InlineData(90.0)]
    [InlineData(130.0)]
    public void AToB_SpeedIsExact(double kmh)
    {
        PassageResult r = PassageAnalyzer.Analyze(1, Track(kmh, 30, 0.0123), TestCalibration);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(TravelDirection.AToB, r.Passage!.Direction);
        Assert.Equal(DetectionLine.A, r.Passage.EntryLine);
        Assert.Equal(kmh, r.Passage.Speed.SpeedKmh, 6);
    }

    [Fact]
    public void BToA_IsDetected_AndSpeedIsExact()
    {
        PassageResult r = PassageAnalyzer.Analyze(2, Track(70, 30, 0.004, direction: -1), TestCalibration);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(TravelDirection.BToA, r.Passage!.Direction);
        Assert.Equal(DetectionLine.B, r.Passage.EntryLine);
        Assert.Equal(70, r.Passage.Speed.SpeedKmh, 6);
    }

    [Fact]
    public void DiagonalMotion_IsMeasuredAlongCalibratedDistance()
    {
        // Il veicolo si muove anche in verticale: la distanza tra le linee resta 20 m.
        PassageResult r = PassageAnalyzer.Analyze(3, Track(60, 30, 0.01, vy: 40), TestCalibration);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(60, r.Passage!.Speed.SpeedKmh, 6);
    }

    [Fact]
    public void EverySecondFrame_StillExact()
    {
        List<TrackObservation> all = Track(80, 30, 0.02);
        var halved = all.Where((_, i) => i % 2 == 0).ToList();
        PassageResult r = PassageAnalyzer.Analyze(4, halved, TestCalibration);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(80, r.Passage!.Speed.SpeedKmh, 6);
    }

    [Fact]
    public void SubFrameEstimate_IsMuchMoreAccurateThanFirstFrame()
    {
        var rng = new Random(5);
        double worstFirstFrame = 0, worstEstimated = 0;
        for (int i = 0; i < 200; i++)
        {
            List<TrackObservation> obs = Track(50, 30, rng.NextDouble() / 30);
            double tA = CrossingEstimator.FirstFrameBeyondTime(obs, LineA)!.Value;
            double tB = CrossingEstimator.FirstFrameBeyondTime(obs, LineB)!.Value;
            worstFirstFrame = Math.Max(worstFirstFrame, Math.Abs(SpeedCalculator.Compute(DistanceMeters, tA, tB).SpeedKmh - 50));

            PassageResult r = PassageAnalyzer.Analyze(i, obs, TestCalibration);
            worstEstimated = Math.Max(worstEstimated, Math.Abs(r.Passage!.Speed.SpeedKmh - 50));
        }

        Assert.True(worstEstimated < 1e-6, $"errore stimato {worstEstimated}");
        Assert.True(worstFirstFrame > 0.1, $"errore primo frame {worstFirstFrame}");
    }

    [Fact]
    public void TrackNotReachingB_Fails()
    {
        List<TrackObservation> obs = Linear(ConstantFrameTimes(30, 0, 1), new Point2(100, 400), new Point2(300, 0));
        PassageResult r = PassageAnalyzer.Analyze(5, obs, TestCalibration);
        Assert.False(r.Success);
        Assert.StartsWith("Linea B", r.FailureReason);
        Assert.Equal(CrossingStatus.Crossed, r.CrossingA.Status);
    }

    [Fact]
    public void TrackStartingJustAfterA_UsesExtrapolatedEntry()
    {
        double vx = KmhToPxPerS(50);
        var p0 = new Point2(LineAX - vx * 1.0, 400);          // attraversa A a t = 1
        List<double> times = ConstantFrameTimes(30, 1.04, 1.0 + 500 / vx);
        PassageResult r = PassageAnalyzer.Analyze(6, Linear(times, p0, new Point2(vx, 0)), TestCalibration);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(CrossingMethod.Extrapolated, r.Passage!.Entry.Method);
        Assert.Equal(50, r.Passage.Speed.SpeedKmh, 6);
    }

    [Fact]
    public void ImplausibleSpeed_IsRejected()
    {
        var obs = new List<TrackObservation>
        {
            new(0, 0.00, new Point2(150, 400)),
            new(1, 0.01, new Point2(650, 400)), // 20 m in ~0,008 s
        };
        PassageResult r = PassageAnalyzer.Analyze(7, obs, TestCalibration, PassageOptions.Default with { MinDurationSeconds = 0 });
        Assert.False(r.Success);
    }

    [Fact]
    public void VehicleTurningBack_IsRejected()
    {
        // Attraversa A, poi torna indietro e riattraversa A: B non viene mai raggiunta.
        var obs = new List<TrackObservation>
        {
            new(0, 0.0, new Point2(180, 400)),
            new(1, 0.1, new Point2(220, 400)),
            new(2, 0.2, new Point2(180, 400)),
        };
        Assert.False(PassageAnalyzer.Analyze(8, obs, TestCalibration).Success);
    }
}
