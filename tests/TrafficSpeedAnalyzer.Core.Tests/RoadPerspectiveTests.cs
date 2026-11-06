using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Tracking;

namespace TrafficSpeedAnalyzer.Core.Tests;

/// <summary>
/// Scena sintetica con una vera camera prospettica (stenoscopica): strada parallela al piano
/// dell'immagine, camera alta 4 m e inclinata verso il basso, due corsie a 11 m e 14,5 m.
/// I riquadri sono le proiezioni di auto a forma di parallelepipedo (estremità dei paraurti a 0,5 m).
/// </summary>
internal sealed class PinholeScene
{
    public const double W = 1920, H = 1080;
    public const double NearLane = 11.0, FarLane = 14.5;

    private readonly double _f = 1400, _pitch, _camHeight, _yaw;

    public PinholeScene(double pitchDegrees = 12, double yawDegrees = 0, double cameraHeight = 4.0)
    {
        _pitch = pitchDegrees * Math.PI / 180;
        _yaw = yawDegrees * Math.PI / 180;
        _camHeight = cameraHeight;
    }

    /// <summary>Riga dell'orizzonte vero.</summary>
    public double HorizonY => (H / 2) - (_f * Math.Tan(_pitch));

    /// <summary>Proiezione di un punto: X lungo la strada, Y verso il basso (terra a Y = altezza camera), Z profondità.</summary>
    public Point2 Project(double x, double y, double z)
    {
        // Imbardata: la strada ruota attorno alla verticale (camera non perpendicolare).
        double xr = (Math.Cos(_yaw) * x) - (Math.Sin(_yaw) * z);
        double zr = (Math.Sin(_yaw) * x) + (Math.Cos(_yaw) * z);
        double yc = (Math.Cos(_pitch) * y) - (Math.Sin(_pitch) * zr);
        double zc = (Math.Sin(_pitch) * y) + (Math.Cos(_pitch) * zr);
        return new Point2((W / 2) + (_f * xr / zc), (H / 2) + (_f * yc / zc));
    }

    /// <summary>Riquadro dell'auto con il centro a <paramref name="x"/> sulla corsia <paramref name="lane"/>.</summary>
    public BoundingBox Box(double x, double lane, double length, double width = 1.8, double height = 1.5)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (double z in new[] { lane, lane + width })
        {
            foreach (double s in new[] { -1.0, 1.0 })
            {
                xs.Add(Project(x + (s * length / 2), _camHeight - 0.5, z).X);   // paraurti
                ys.Add(Project(x + (s * length * 0.45), _camHeight, z).Y);      // terra
                ys.Add(Project(x + (s * length * 0.45), _camHeight - height, z).Y);   // tetto
            }
        }

        return new BoundingBox(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min());
    }

    /// <summary>Punto a terra sul lato vicino dell'auto, sotto il suo centro.</summary>
    public Point2 Ground(double x, double lane) => Project(x, _camHeight, lane);

    /// <summary>Auto che attraversa l'immagine a velocità costante (riquadri tagliati dal bordo esclusi).</summary>
    public List<TrackPoint> Track(double lane, double length, double kmh, double fps = 30, double phase = 0.004, bool leftToRight = true)
    {
        double v = kmh / 3.6;
        var pts = new List<TrackPoint>();
        long frame = 0;
        for (double t = phase; t < 30 / v; t += 1 / fps, frame++)
        {
            double x = leftToRight ? -15 + (v * t) : 15 - (v * t);
            BoundingBox b = Box(x, lane, length);
            if (b.X > 3 && b.Right < W - 3 && b.Y > 3 && b.Bottom < H - 3)
            {
                pts.Add(new TrackPoint(frame, t, b, VehicleClass.Car, 0.9f, false));
            }
        }

        return pts;
    }
}

public class RoadPerspectiveTests
{
    private static List<TrackObservation> Observations(IEnumerable<TrackPoint> pts) =>
        pts.Select(p => new TrackObservation(p.FrameIndex, p.TimeSeconds, p.Box.BottomCenter)).ToList();

    private static CenterFrameMeasurement Measure(List<TrackPoint> track) =>
        VehicleScaleEstimator.MeasureAtCenter(track, PinholeScene.W, PinholeScene.H, out _)!;

    private static double SpeedWith(LineSegment a, LineSegment b, double meters, List<TrackPoint> track)
    {
        PassageResult r = PassageAnalyzer.Analyze(1, Observations(track), new CalibrationSettings(a, b, meters), TrackPassageOptions.Default.Passage);
        Assert.True(r.Success, r.FailureReason);
        return r.Passage!.Speed.SpeedKmh;
    }

    [Fact]
    public void Assess_PerpendicularCamera_IsSuitable_AngledCamera_IsNot()
    {
        var straight = new PinholeScene();
        List<CenterFrameMeasurement> ok = RoadPerspective.UsableVehicles(
            [straight.Track(PinholeScene.NearLane, 4.3, 50), straight.Track(PinholeScene.FarLane, 4.1, 60, leftToRight: false)],
            PinholeScene.W, PinholeScene.H);
        ShotAssessment good = RoadPerspective.Assess(ok);
        Assert.True(good.Suitable, good.Message);
        Assert.Equal(2, good.UsableVehicles);

        var angled = new PinholeScene(yawDegrees: 25);
        List<CenterFrameMeasurement> bad = RoadPerspective.UsableVehicles(
            [angled.Track(PinholeScene.NearLane + 4, 4.3, 50)], PinholeScene.W, PinholeScene.H);
        ShotAssessment notOk = RoadPerspective.Assess(bad);
        Assert.False(notOk.Suitable);
        Assert.Contains("perpendicolare", notOk.Message);

        Assert.False(RoadPerspective.Assess([]).Suitable);
    }

    [Fact]
    public void Assess_LowCloseCamera_PartialTrack_IsStillPerpendicular()
    {
        // Telefono a 1,5 m, auto a 12 m: ai lati si vede anche il muso o il retro e il riquadro si allarga.
        // Un'auto seguita solo da poco prima del centro in poi non deve far credere a una camera storta.
        double pitch = Math.Atan(3.5 / 12) * 180 / Math.PI;
        var scene = new PinholeScene(pitch, 0, cameraHeight: 1.5);
        List<TrackPoint> partial = scene.Track(12, 4.2, 40).Where(p => p.Box.CenterX > 0.4 * PinholeScene.W).ToList();
        CenterFrameMeasurement m = Measure(partial);
        Assert.InRange(m.SizeChangeFraction, -0.05, 0.05);
        Assert.True(RoadPerspective.Assess([m]).Suitable);

        // Camera ruotata di 8° rispetto alla strada: la variazione si vede.
        var yawed = new PinholeScene(pitch, 8, cameraHeight: 1.5);
        Assert.True(Math.Abs(Measure(yawed.Track(12, 4.2, 40)).SizeChangeFraction) > 0.06);
    }

    [Fact]
    public void TwoReferences_RecoverTheHorizon()
    {
        var scene = new PinholeScene();
        CenterFrameMeasurement near = Measure(scene.Track(PinholeScene.NearLane, 4.36, 50));
        CenterFrameMeasurement far = Measure(scene.Track(PinholeScene.FarLane, 3.84, 50));
        HorizonEstimate h = RoadPerspective.FromTwoReferences(near, 4.36, far, 3.84, 0, PinholeScene.W, PinholeScene.H)!;
        Assert.Equal(HorizonSource.TwoReferences, h.Source);
        Assert.InRange(h.HorizonV, scene.HorizonY - 25, scene.HorizonY + 25);

        // Stessa corsia: l'orizzonte non è ricavabile.
        CenterFrameMeasurement near2 = Measure(scene.Track(PinholeScene.NearLane, 4.0, 60));
        Assert.Null(RoadPerspective.FromTwoReferences(near, 4.36, near2, 4.0, 0, PinholeScene.W, PinholeScene.H));
    }

    [Fact]
    public void LinesTowardVanishingPoint_GiveTheSameSpeedOnBothLanes()
    {
        var scene = new PinholeScene();
        List<TrackPoint> refTrack = scene.Track(PinholeScene.NearLane, 4.36, 40);
        List<TrackPoint> farCar = scene.Track(PinholeScene.FarLane, 4.1, 50, phase: 0.017);
        List<TrackPoint> nearCar = scene.Track(PinholeScene.NearLane, 4.6, 70, phase: 0.009, leftToRight: false);
        CenterFrameMeasurement reference = Measure(refTrack);
        CenterFrameMeasurement second = Measure(scene.Track(PinholeScene.FarLane, 3.84, 45));
        HorizonEstimate horizon = RoadPerspective.FromTwoReferences(reference, 4.36, second, 3.84, 0, PinholeScene.W, PinholeScene.H)!;

        (LineSegment a, LineSegment b) = RoadPerspective.LinesAtVehicleEnds(reference, horizon, PinholeScene.W, PinholeScene.H);

        // Le linee convergono verso l'alto (punto di fuga sopra l'immagine) e non si incrociano nell'immagine.
        Assert.True(Point2.Distance(a.Start, b.Start) > Point2.Distance(a.End, b.End));
        Assert.False(GeometryMath.SegmentsIntersect(a, b));

        Assert.InRange(SpeedWith(a, b, 4.36, nearCar), 70 * 0.98, 70 * 1.02);
        Assert.InRange(SpeedWith(a, b, 4.36, farCar), 50 * 0.98, 50 * 1.02);

        // Con linee verticali (senza prospettiva) la corsia lontana sbaglia di molto: è l'errore che si corregge.
        (LineSegment va, LineSegment vb) = VehicleScaleEstimator.LinesAtVehicleEnds(reference, PinholeScene.W, PinholeScene.H);
        double wrong = SpeedWith(va, vb, 4.36, farCar);
        Assert.True(Math.Abs(wrong / 50 - 1) > 0.10, $"con linee verticali {wrong:0.0} km/h");
    }

    [Fact]
    public void StatisticalHorizon_FromManyCars_OnTwoLanes()
    {
        var scene = new PinholeScene();
        var rng = new Random(7);
        var tracks = new List<List<TrackPoint>>();
        for (int i = 0; i < 40; i++)
        {
            double length = 4.3 + ((rng.NextDouble() - 0.5) * 0.8);   // auto tra 3,9 e 4,7 m
            tracks.Add(scene.Track(i % 2 == 0 ? PinholeScene.NearLane : PinholeScene.FarLane, length, 40 + (i % 5 * 5), phase: rng.NextDouble() / 30));
        }

        List<CenterFrameMeasurement> cars = RoadPerspective.UsableVehicles(tracks, PinholeScene.W, PinholeScene.H);
        Assert.Equal(40, cars.Count);
        HorizonEstimate h = RoadPerspective.EstimateHorizon(cars, RoadPerspective.RoadAngle(tracks), PinholeScene.W, PinholeScene.H)!;
        Assert.Equal(HorizonSource.Statistical, h.Source);
        Assert.InRange(h.HorizonV, scene.HorizonY - 80, scene.HorizonY + 80);
        Assert.InRange(h.LaneErrorFraction, 0, RoadPerspective.MaxStatisticalLaneError);

        // Con l'orizzonte stimato la corsia lontana resta entro il 5%.
        List<TrackPoint> refTrack = scene.Track(PinholeScene.NearLane, 4.36, 40);
        (LineSegment a, LineSegment b) = RoadPerspective.LinesAtVehicleEnds(Measure(refTrack), h, PinholeScene.W, PinholeScene.H);
        Assert.InRange(SpeedWith(a, b, 4.36, scene.Track(PinholeScene.FarLane, 4.1, 50, phase: 0.017)), 50 * 0.95, 50 * 1.05);
    }

    [Fact]
    public void SingleLane_NoPerspective_AndTooFewCars_IsUnreliable()
    {
        var scene = new PinholeScene();
        List<List<TrackPoint>> oneLane = Enumerable.Range(0, 8).Select(i => scene.Track(PinholeScene.NearLane, 4.0 + (i * 0.1), 50)).ToList();
        HorizonEstimate single = RoadPerspective.EstimateHorizon(
            RoadPerspective.UsableVehicles(oneLane, PinholeScene.W, PinholeScene.H), 0, PinholeScene.W, PinholeScene.H)!;
        Assert.Equal(HorizonSource.None, single.Source);

        // Due corsie ma solo tre auto: nessuna stima (servono più auto o una seconda auto riconosciuta).
        List<List<TrackPoint>> few = [scene.Track(PinholeScene.NearLane, 4.3, 50), scene.Track(PinholeScene.FarLane, 4.0, 50), scene.Track(PinholeScene.NearLane, 4.5, 50)];
        Assert.Null(RoadPerspective.EstimateHorizon(RoadPerspective.UsableVehicles(few, PinholeScene.W, PinholeScene.H), 0, PinholeScene.W, PinholeScene.H));
    }

    [Fact]
    public void RoadAngle_FollowsTiltedTracks()
    {
        var pts = new List<TrackPoint>();
        for (int i = 0; i < 40; i++)
        {
            double x = 100 + (i * 40), y = 600 + (i * 40 * Math.Tan(3 * Math.PI / 180));
            pts.Add(new TrackPoint(i, i / 30.0, new BoundingBox(x - 100, y - 80, 200, 80), VehicleClass.Car, 0.9f, false));
        }

        Assert.Equal(3, RoadPerspective.RoadAngle([pts]) * 180 / Math.PI, 3);
        (double u, double v) = RoadPerspective.ToRoad(new Point2(300, 200), 0.3);
        Point2 back = RoadPerspective.FromRoad(u, v, 0.3);
        Assert.Equal(300, back.X, 9);
        Assert.Equal(200, back.Y, 9);
    }
}
