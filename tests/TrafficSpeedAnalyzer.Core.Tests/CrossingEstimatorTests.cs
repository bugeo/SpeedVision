using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using static TrafficSpeedAnalyzer.Core.Tests.SyntheticTrack;

namespace TrafficSpeedAnalyzer.Core.Tests;

public class CrossingEstimatorTests
{
    private static readonly CrossingOptions TwoPointOnly = CrossingOptions.Default with { RegressionPointsPerSide = 1 };

    /// <summary>Istante esatto in cui un moto rettilineo uniforme raggiunge x = lineX.</summary>
    private static double TrueCrossingTime(Point2 p0, Point2 v, double lineX) => (lineX - p0.X) / v.X;

    [Fact]
    public void Interpolation_IsExact_ForUniformMotion_WithRandomSubFrameOffsets()
    {
        var rng = new Random(12345);
        for (int trial = 0; trial < 300; trial++)
        {
            double kmh = 20 + rng.NextDouble() * 130;
            double angle = (rng.NextDouble() - 0.5) * Math.PI * 2 / 3; // ±60° rispetto all'orizzontale
            double speedPx = KmhToPxPerS(kmh);
            var v = new Point2(Math.Cos(angle) * speedPx, Math.Sin(angle) * speedPx);
            var p0 = new Point2(100, 500);
            double phase = rng.NextDouble() / 30.0;
            List<double> times = ConstantFrameTimes(30, 0, 3, phase);
            List<TrackObservation> obs = Linear(times, p0, v);

            double truth = TrueCrossingTime(p0, v, LineAX);

            CrossingResult twoPoint = CrossingEstimator.Estimate(obs, LineA, TwoPointOnly);
            Assert.Equal(CrossingStatus.Crossed, twoPoint.Status);
            Assert.Equal(CrossingMethod.Interpolated, twoPoint.Estimate!.Method);
            Assert.InRange(twoPoint.Estimate.TimeSeconds - truth, -1e-9, 1e-9);

            CrossingResult regression = CrossingEstimator.Estimate(obs, LineA);
            Assert.Equal(CrossingMethod.Regression, regression.Estimate!.Method);
            Assert.InRange(regression.Estimate.TimeSeconds - truth, -1e-9, 1e-9);
        }
    }

    [Fact]
    public void Interpolation_IsExact_WithVariableFrameRate()
    {
        var rng = new Random(777);
        for (int trial = 0; trial < 100; trial++)
        {
            List<double> times = VariableFrameTimes(rng, 30, rng.NextDouble() * 0.03, 3, jitter: 0.25);
            var v = new Point2(KmhToPxPerS(60), 15);
            var p0 = new Point2(50, 300);
            List<TrackObservation> obs = Linear(times, p0, v);
            CrossingResult result = CrossingEstimator.Estimate(obs, LineA);
            Assert.InRange(result.Estimate!.TimeSeconds - TrueCrossingTime(p0, v, LineAX), -1e-9, 1e-9);
        }
    }

    [Fact]
    public void FirstFrameMethod_IsLate_ByUpToOneFrame()
    {
        var rng = new Random(42);
        double maxLate = 0;
        for (int trial = 0; trial < 300; trial++)
        {
            var v = new Point2(KmhToPxPerS(50), 0);
            var p0 = new Point2(100, 500);
            List<TrackObservation> obs = Linear(ConstantFrameTimes(30, 0, 3, rng.NextDouble() / 30), p0, v);
            double late = CrossingEstimator.FirstFrameBeyondTime(obs, LineA)!.Value - TrueCrossingTime(p0, v, LineAX);
            Assert.InRange(late, 0, 1.0 / 30 + 1e-9);
            maxLate = Math.Max(maxLate, late);
        }

        Assert.True(maxLate > 0.8 / 30, "Con fasi casuali il ritardo deve avvicinarsi a un fotogramma.");
    }

    [Fact]
    public void WithPositionNoise_RegressionBeatsInterpolation_AndBothBeatFirstFrame()
    {
        var rng = new Random(2024);
        double errFirst = 0, errInterp = 0, errReg = 0;
        const int trials = 600;
        for (int trial = 0; trial < trials; trial++)
        {
            var v = new Point2(KmhToPxPerS(50), 0);
            var p0 = new Point2(100, 500);
            List<TrackObservation> obs = Linear(ConstantFrameTimes(30, 0, 3, rng.NextDouble() / 30), p0, v, rng, noiseSigmaPx: 1.5);
            double truth = TrueCrossingTime(p0, v, LineAX);

            errFirst += Math.Abs(CrossingEstimator.FirstFrameBeyondTime(obs, LineA)!.Value - truth);
            errInterp += Math.Abs(CrossingEstimator.Estimate(obs, LineA, TwoPointOnly).Estimate!.TimeSeconds - truth);
            errReg += Math.Abs(CrossingEstimator.Estimate(obs, LineA).Estimate!.TimeSeconds - truth);
        }

        errFirst /= trials;
        errInterp /= trials;
        errReg /= trials;
        Assert.True(errInterp < errFirst, $"interp {errInterp:0.00000} vs primo frame {errFirst:0.00000}");
        Assert.True(errReg < errInterp, $"regressione {errReg:0.00000} vs interp {errInterp:0.00000}");
    }

    [Fact]
    public void ReportedUncertainty_IsConsistentWithObservedError()
    {
        var rng = new Random(99);
        int within2Sigma = 0;
        const int trials = 400;
        for (int trial = 0; trial < trials; trial++)
        {
            var v = new Point2(KmhToPxPerS(70), 0);
            var p0 = new Point2(100, 500);
            List<TrackObservation> obs = Linear(ConstantFrameTimes(30, 0, 3, rng.NextDouble() / 30), p0, v, rng, noiseSigmaPx: 1.5);
            CrossingEstimate e = CrossingEstimator.Estimate(obs, LineA).Estimate!;
            if (Math.Abs(e.TimeSeconds - TrueCrossingTime(p0, v, LineAX)) <= 2 * e.UncertaintySeconds)
            {
                within2Sigma++;
            }
        }

        // Con una stima corretta circa il 95% degli errori cade entro 2 sigma; tolleranza ampia.
        Assert.InRange(within2Sigma / (double)trials, 0.75, 1.0);
    }

    [Fact]
    public void ReturnsFirstFrameBeyondLine_ForImageExport()
    {
        var times = new List<double> { 0.0, 0.1, 0.2, 0.3 };
        var obs = Linear(times, new Point2(150, 50), new Point2(400, 0)); // x = 150, 190, 230, 270
        CrossingEstimate e = CrossingEstimator.Estimate(obs, LineA, TwoPointOnly).Estimate!;
        Assert.Equal(2, e.FirstBeyond.FrameIndex);
        Assert.Equal(30, e.OvershootPixels, 9);
        Assert.Equal(0.125, e.TimeSeconds, 9);
        Assert.Equal(LineA.SideOf(new Point2(300, 0)), e.SideAfter);
    }

    [Fact]
    public void ObservationExactlyOnLine_GivesThatTime()
    {
        var obs = new List<TrackObservation>
        {
            new(0, 1.0, new Point2(180, 10)),
            new(1, 1.1, new Point2(200, 10)),
            new(2, 1.2, new Point2(220, 10)),
        };
        CrossingResult r = CrossingEstimator.Estimate(obs, LineA, TwoPointOnly);
        Assert.Equal(CrossingStatus.Crossed, r.Status);
        Assert.Equal(1.1, r.Estimate!.TimeSeconds, 9);
    }

    [Fact]
    public void UnsortedObservations_AreHandled()
    {
        var obs = Linear(new List<double> { 0, 0.1, 0.2, 0.3 }, new Point2(150, 50), new Point2(400, 0));
        obs.Reverse();
        Assert.Equal(0.125, CrossingEstimator.Estimate(obs, LineA, TwoPointOnly).Estimate!.TimeSeconds, 9);
    }

    [Fact]
    public void PassingBeyondLineEnd_IsNotACrossing()
    {
        var shortLine = new LineSegment(new Point2(200, 0), new Point2(200, 100));
        var obs = Linear(ConstantFrameTimes(30, 0, 2), new Point2(100, 300), new Point2(200, 0));
        CrossingResult r = CrossingEstimator.Estimate(obs, shortLine);
        Assert.Equal(CrossingStatus.NotCrossed, r.Status);
        Assert.Contains("estremità", r.Reason);
    }

    [Fact]
    public void NoCrossing_IsReported()
    {
        var obs = Linear(ConstantFrameTimes(30, 0, 1), new Point2(10, 300), new Point2(100, 0));
        Assert.Equal(CrossingStatus.NotCrossed, CrossingEstimator.Estimate(obs, LineA).Status);
    }

    [Fact]
    public void SingleObservation_IsNotEnough()
    {
        var obs = new List<TrackObservation> { new(0, 0, new Point2(250, 10)) };
        Assert.Equal(CrossingStatus.NotCrossed, CrossingEstimator.Estimate(obs, LineA).Status);
    }

    [Fact]
    public void LargeGapAroundLine_IsRejected()
    {
        var obs = new List<TrackObservation>
        {
            new(0, 0.0, new Point2(150, 10)),
            new(30, 1.0, new Point2(250, 10)),
        };
        CrossingResult r = CrossingEstimator.Estimate(obs, LineA);
        Assert.Equal(CrossingStatus.Rejected, r.Status);
    }

    [Fact]
    public void Extrapolation_RecoversCrossingShortlyBeforeTrackStart()
    {
        var v = new Point2(KmhToPxPerS(50), 0);   // ~278 px/s
        double truth = 1.0;                         // attraversa x = 200 a t = 1 s
        var p0 = new Point2(LineAX - v.X * truth, 400);
        // la traccia inizia 0,05 s dopo l'attraversamento
        List<double> times = ConstantFrameTimes(30, truth + 0.05, truth + 0.5);
        List<TrackObservation> obs = Linear(times, p0, v);

        int sideAfter = LineA.SideOf(new Point2(LineBX, 400));
        CrossingResult r = CrossingEstimator.EstimateFromTrackStart(obs, LineA, sideAfter);
        Assert.Equal(CrossingStatus.Crossed, r.Status);
        Assert.Equal(CrossingMethod.Extrapolated, r.Estimate!.Method);
        Assert.InRange(r.Estimate.TimeSeconds - truth, -1e-9, 1e-9);
    }

    [Fact]
    public void Extrapolation_IsRejected_WhenVehicleIsTooFarBeyondLine()
    {
        var v = new Point2(KmhToPxPerS(50), 0);
        var p0 = new Point2(LineAX - v.X * 1.0, 400);
        List<TrackObservation> obs = Linear(ConstantFrameTimes(30, 1.3, 2.0), p0, v);
        int sideAfter = LineA.SideOf(new Point2(LineBX, 400));
        CrossingResult r = CrossingEstimator.EstimateFromTrackStart(obs, LineA, sideAfter);
        Assert.Equal(CrossingStatus.Rejected, r.Status);
    }

    [Fact]
    public void Extrapolation_IsRejected_WhenMovingTowardsLine()
    {
        // Il punto è sul lato "dopo" ma si avvicina alla linea: non può averla appena superata.
        var obs = Linear(ConstantFrameTimes(30, 0, 0.3), new Point2(260, 400), new Point2(-100, 0));
        int sideAfter = LineA.SideOf(new Point2(LineBX, 400));
        Assert.Equal(CrossingStatus.Rejected, CrossingEstimator.EstimateFromTrackStart(obs, LineA, sideAfter).Status);
    }
}
