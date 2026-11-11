using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Tracking;
using static TrafficSpeedAnalyzer.Core.Tests.SyntheticTrack;

namespace TrafficSpeedAnalyzer.Core.Tests;

public class HungarianTests
{
    [Fact]
    public void FindsMinimumCostAssignment()
    {
        double[,] cost =
        {
            { 4, 1, 3 },
            { 2, 0, 5 },
            { 3, 2, 2 },
        };
        int[] a = Hungarian.Solve(cost);
        Assert.Equal(5, a.Select((c, r) => cost[r, c]).Sum()); // 1 + 2 + 2
        Assert.Equal(3, a.Distinct().Count());
    }

    [Fact]
    public void HandlesRectangularMatrices()
    {
        double[,] moreRows = { { 1 }, { 0.2 }, { 3 } };
        Assert.Equal(new[] { -1, 0, -1 }, Hungarian.Solve(moreRows));

        double[,] moreCols = { { 5, 0.5, 2 } };
        Assert.Equal(new[] { 1 }, Hungarian.Solve(moreCols));
    }

    [Fact]
    public void RejectsPairsAboveMaxCost()
    {
        double[,] cost = { { 0.9, 0.95 }, { 0.95, 0.1 } };
        Assert.Equal(new[] { -1, 1 }, Hungarian.Solve(cost, maxCost: 0.8));
    }

    [Fact]
    public void RandomMatricesMatchBruteForce()
    {
        var rng = new Random(3);
        for (int trial = 0; trial < 200; trial++)
        {
            int n = rng.Next(1, 6);
            var cost = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    cost[i, j] = rng.NextDouble();
                }
            }

            int[] a = Hungarian.Solve(cost);
            double got = a.Select((c, r) => cost[r, c]).Sum();
            Assert.Equal(BruteForce(cost, n), got, 9);
        }
    }

    private static double BruteForce(double[,] cost, int n)
    {
        double best = double.MaxValue;
        foreach (int[] perm in Permutations(Enumerable.Range(0, n).ToArray()))
        {
            best = Math.Min(best, perm.Select((c, r) => cost[r, c]).Sum());
        }

        return best;
    }

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (int i = 0; i < items.Length; i++)
        {
            int[] rest = items.Where((_, k) => k != i).ToArray();
            foreach (int[] p in Permutations(rest))
            {
                yield return [items[i], .. p];
            }
        }
    }
}

public class ByteTrackerTests
{
    private static VehicleDetection Car(double cx, double bottom, double w = 80, double h = 50, float score = 0.9f) =>
        new(new BoundingBox(cx - w / 2, bottom - h, w, h), VehicleClass.Car, score);

    /// <summary>Riquadri di un veicolo che si muove in orizzontale a velocità costante.</summary>
    private static IEnumerable<(long Frame, double Time, VehicleDetection Det)> Moving(double kmh, double x0, double bottom, double fps, double duration, double phase = 0)
    {
        double v = KmhToPxPerS(kmh);
        long frame = 0;
        for (double t = phase; t <= duration; t += 1 / fps, frame++)
        {
            yield return (frame, t, Car(x0 + v * t, bottom));
        }
    }

    [Fact]
    public void SingleVehicle_KeepsOneId()
    {
        var tracker = new ByteTracker();
        var ids = new HashSet<int>();
        foreach ((long f, double t, VehicleDetection d) in Moving(60, 0, 400, 30, 2.5))
        {
            foreach (TrackedVehicle tv in tracker.Update(f, t, [d]))
            {
                ids.Add(tv.TrackId);
            }
        }

        Assert.Single(ids);
        FinishedTrack track = Assert.Single(tracker.FlushAll());
        Assert.True(track.Points.Count > 70);
    }

    [Fact]
    public void TwoVehiclesCrossingPaths_KeepTheirIds()
    {
        // Due veicoli in versi opposti su corsie vicine: i riquadri si sovrappongono quando si incrociano.
        var tracker = new ByteTracker();
        double v = KmhToPxPerS(50);
        var idOfLane = new Dictionary<int, HashSet<int>> { [0] = [], [1] = [] };
        long frame = 0;
        for (double t = 0; t <= 3; t += 1 / 30.0, frame++)
        {
            var dets = new List<VehicleDetection> { Car(100 + v * t, 400), Car(800 - v * t, 430) };
            foreach (TrackedVehicle tv in tracker.Update(frame, t, dets))
            {
                int lane = tv.Detection.Box.Bottom < 415 ? 0 : 1;
                idOfLane[lane].Add(tv.TrackId);
            }
        }

        Assert.Single(idOfLane[0]);
        Assert.Single(idOfLane[1]);
        Assert.NotEqual(idOfLane[0].Single(), idOfLane[1].Single());
    }

    [Fact]
    public void MissedDetections_DoNotBreakTrack()
    {
        var tracker = new ByteTracker();
        var ids = new HashSet<int>();
        int n = 0;
        foreach ((long f, double t, VehicleDetection d) in Moving(70, 0, 400, 30, 2.5))
        {
            // perde 4 fotogrammi consecutivi ogni 15
            IReadOnlyList<VehicleDetection> dets = n++ % 15 < 4 && n > 10 ? [] : [d];
            foreach (TrackedVehicle tv in tracker.Update(f, t, dets))
            {
                ids.Add(tv.TrackId);
            }
        }

        Assert.Single(ids);
    }

    [Fact]
    public void LowScoreDetections_ExtendExistingTrack_ButDoNotStartNewOnes()
    {
        var tracker = new ByteTracker();
        var ids = new HashSet<int>();
        int n = 0;
        foreach ((long f, double t, VehicleDetection d) in Moving(50, 0, 400, 30, 2))
        {
            VehicleDetection det = n++ < 5 ? d : d with { Score = 0.2f };
            foreach (TrackedVehicle tv in tracker.Update(f, t, [det]))
            {
                ids.Add(tv.TrackId);
            }
        }

        Assert.Single(ids);
        Assert.True(tracker.FlushAll().Single().Points.Count > 50);

        var fresh = new ByteTracker();
        Assert.Empty(fresh.Update(0, 0, [Car(100, 400, score: 0.2f)]));
    }

    [Fact]
    public void ShortFalsePositive_IsNeverConfirmed()
    {
        var tracker = new ByteTracker();
        tracker.Update(0, 0.0, [Car(300, 300)]);
        tracker.Update(1, 1 / 30.0, []);
        tracker.Update(2, 2 / 30.0, []);
        Assert.Empty(tracker.FlushAll());
    }

    [Fact]
    public void LostTrack_FinishesAfterTimeout()
    {
        var tracker = new ByteTracker(ByteTrackerOptions.Default with { MaxLostSeconds = 0.5 });
        long f = 0;
        for (double t = 0; t < 1; t += 1 / 30.0)
        {
            tracker.Update(f++, t, [Car(100 + 300 * t, 400)]);
        }

        Assert.Empty(tracker.TakeFinished());
        for (double t = 1; t < 1.7; t += 1 / 30.0)
        {
            tracker.Update(f++, t, []);
        }

        Assert.Single(tracker.TakeFinished());
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public void WorksWithVariableFrameIntervals()
    {
        var tracker = new ByteTracker();
        var rng = new Random(9);
        var ids = new HashSet<int>();
        double v = KmhToPxPerS(80);
        double t = 0;
        for (long f = 0; f < 80; f++)
        {
            foreach (TrackedVehicle tv in tracker.Update(f, t, [Car(50 + v * t, 400)]))
            {
                ids.Add(tv.TrackId);
            }

            t += (1 / 30.0) * (0.6 + 0.8 * rng.NextDouble());
        }

        Assert.Single(ids);
    }
}

public class TrackPassageExtractorTests
{
    [Fact]
    public void FrontWheel_DropsPointsThatJumpBackwards()
    {
        // Veicolo verso destra, 20 px per fotogramma; negli ultimi 4 fotogrammi (quando esce dall'inquadratura) la
        // «ruota anteriore» trovata torna indietro di 250 px: è la posteriore, e quei punti vanno scartati.
        var points = new List<TrackPoint>();
        for (int i = 0; i < 20; i++)
        {
            double x = 100 + (20 * i);
            // Quando esce, il riquadro copre solo la parte visibile: la ruota posteriore sta alla solita distanza dal suo bordo.
            double wheelX = i < 16 ? x + 330 : x + 80;
            var box = i < 16 ? new BoundingBox(x, 300, 400, 160) : new BoundingBox(x, 300, 150, 160);
            points.Add(new TrackPoint(i, i / 30.0, box, VehicleClass.Car, 0.9f, false,
                [new WheelCandidate(new Point2(wheelX, 428), 32, 90, new Point2(wheelX, 460))]));
        }

        List<TrackObservation> obs = TrackPassageExtractor.FrontWheelObservations(points)!;

        Assert.NotNull(obs);
        Assert.Equal(16, obs.Count);
        Assert.All(obs, o => Assert.True(o.FrameIndex < 16));
    }

    private static FinishedTrack TrackAt(double kmh, double fps, double phase, Func<int, bool>? truncated = null, float score = 0.9f, double noisePx = 0, int seed = 1)
    {
        var rng = new Random(seed);
        double v = KmhToPxPerS(kmh);
        var points = new List<TrackPoint>();
        int i = 0;
        for (double t = phase; t <= 700 / v; t += 1 / fps, i++)
        {
            double cx = 100 + v * t - 40 + Gaussian(rng) * noisePx;
            double bottom = 450 + Gaussian(rng) * noisePx;
            var box = new BoundingBox(cx - 40, bottom - 50, 80, 50);
            points.Add(new TrackPoint(i, t, box, VehicleClass.Car, score, truncated?.Invoke(i) ?? false));
        }

        return new FinishedTrack(1, points);
    }

    [Theory]
    [InlineData(40.0)]
    [InlineData(90.0)]
    public void ExactBoxes_GiveExactSpeed(double kmh)
    {
        TrackPassageCandidate c = TrackPassageExtractor.Extract(TrackAt(kmh, 30, 0.013), TestCalibration);
        Assert.True(c.IsPassage, c.RejectionReason);
        Assert.Equal(kmh, c.Result!.Passage!.Speed.SpeedKmh, 6);
        Assert.Equal(VehicleClass.Car, c.Class);
    }

    [Fact]
    public void ReferencePoint_IsTheFrontBottomCorner()
    {
        // Da sinistra a destra: vertice destro (il riquadro è largo 80, il centro è a 40 dal vertice).
        FinishedTrack right = TrackAt(50, 30, 0.013);
        TrackPassageCandidate c = TrackPassageExtractor.Extract(right, null);
        Assert.Equal(right.Points[0].Box.Right, c.Observations[0].Position.X, 9);
        Assert.Equal(right.Points[0].Box.Bottom, c.Observations[0].Position.Y, 9);

        // Da destra a sinistra: vertice sinistro.
        var reversed = new FinishedTrack(2, right.Points
            .Select((p, i) => p with { Box = p.Box with { X = 1000 - p.Box.Right } })
            .ToList());
        TrackPassageCandidate l = TrackPassageExtractor.Extract(reversed, null);
        Assert.Equal(reversed.Points[0].Box.X, l.Observations[0].Position.X, 9);

        // Fermo: centro del lato inferiore.
        var still = new FinishedTrack(3, right.Points.Select(p => p with { Box = right.Points[0].Box }).ToList());
        Assert.Equal(right.Points[0].Box.CenterX, TrackPassageExtractor.Extract(still, null).Observations[0].Position.X, 9);
    }

    /// <summary>
    /// Traccia con le ruote: la ruota anteriore avanza in modo esatto, mentre il muso del riquadro «balla»
    /// (riflessi, ombre, il veicolo visto prima di fronte e poi di lato), come nei video veri.
    /// </summary>
    private static FinishedTrack TrackWithWheels(double kmh, double noseNoisePx, Func<int, bool>? hasWheel = null, int seed = 1)
    {
        var rng = new Random(seed);
        double v = KmhToPxPerS(kmh);
        var points = new List<TrackPoint>();
        int i = 0;
        for (double t = 0.013; t <= 700 / v; t += 1 / 30.0, i++)
        {
            double wheelX = 100 + (v * t);                      // contatto a terra della ruota anteriore
            double right = wheelX + 12 + (Gaussian(rng) * noseNoisePx);
            var box = new BoundingBox(right - 80, 400, 80, 50);
            IReadOnlyList<WheelCandidate>? wheels = hasWheel?.Invoke(i) == false ? [] :
            [
                new WheelCandidate(new Point2(wheelX, 440), 10, 120),           // anteriore
                new WheelCandidate(new Point2(wheelX - 50, 440), 10, 110),      // posteriore
            ];
            points.Add(new TrackPoint(i, t, box, VehicleClass.Car, 0.9f, false, wheels));
        }

        return new FinishedTrack(1, points);
    }

    [Fact]
    public void FrontWheel_IsTheReference_AndIgnoresTheJitteringNose()
    {
        double cornerError = 0;
        for (int seed = 0; seed < 20; seed++)
        {
            FinishedTrack track = TrackWithWheels(60, noseNoisePx: 6, seed: seed);
            TrackPassageCandidate wheel = TrackPassageExtractor.Extract(track, TestCalibration);
            Assert.True(wheel.IsPassage, wheel.RejectionReason);
            Assert.Equal(ReferencePoint.FrontWheel, wheel.Reference);
            Assert.Equal(60, wheel.Result!.Passage!.Speed.SpeedKmh, 6);   // la ruota è esatta
            Assert.Equal(450, wheel.Observations[0].Position.Y, 9);       // punto a terra: fondo della ruota

            TrackPassageCandidate corner = TrackPassageExtractor.Extract(track, TestCalibration, TrackPassageOptions.Default with { UseWheels = false });
            Assert.Equal(ReferencePoint.BoxCorner, corner.Reference);
            cornerError += Math.Abs(corner.Result!.Passage!.Speed.SpeedKmh - 60) / 20;
        }

        // Il confronto ha senso solo se il muso che «balla» sposta davvero la misura con il riquadro.
        Assert.True(cornerError > 0.1, $"errore medio con il riquadro {cornerError:0.000} km/h");
    }

    /// <summary>Passaggio misurato sulla ruota anteriore di una traccia con le ruote esatte, più le sue osservazioni.</summary>
    private static (List<TrackObservation> Wheel, VehiclePassage Passage, IReadOnlyList<TrackPoint> Points) WheelPassage()
    {
        FinishedTrack track = TrackWithWheels(60, noseNoisePx: 0);
        TrackPassageCandidate c = TrackPassageExtractor.Extract(track, TestCalibration);
        Assert.Equal(ReferencePoint.FrontWheel, c.Reference);
        return ([.. c.Observations], c.Result!.Passage!, track.Points);
    }

    [Fact]
    public void WheelCheck_AcceptsAWheelThatMovesSmoothlyAcrossBothLines()
    {
        (List<TrackObservation> wheel, VehiclePassage passage, IReadOnlyList<TrackPoint> points) = WheelPassage();
        Assert.Null(WheelCrossingCheck.Check(wheel, passage, points));
    }

    [Fact]
    public void WheelCheck_RejectsAJumpNearALine()
    {
        // Nel fotogramma subito dopo la linea d'uscita la «ruota» è un'altra cosa, qualche pixel più avanti.
        (List<TrackObservation> wheel, VehiclePassage passage, IReadOnlyList<TrackPoint> points) = WheelPassage();
        int i = wheel.FindIndex(o => o.TimeSeconds >= passage.Exit.TimeSeconds);
        wheel[i] = wheel[i] with { Position = new Point2(wheel[i].Position.X + 8, wheel[i].Position.Y) };
        string? problem = WheelCrossingCheck.Check(wheel, passage, points);
        Assert.NotNull(problem);
        Assert.Contains("linea B", problem);
    }

    [Fact]
    public void WheelCheck_RejectsAWheelLostNearALine()
    {
        // Ruota persa per cinque fotogrammi appena dopo la linea d'ingresso.
        (List<TrackObservation> wheel, VehiclePassage passage, IReadOnlyList<TrackPoint> points) = WheelPassage();
        int i = wheel.FindIndex(o => o.TimeSeconds >= passage.Entry.TimeSeconds);
        wheel.RemoveRange(i + 1, 5);
        Assert.Contains("linea A", WheelCrossingCheck.Check(wheel, passage, points));
    }

    [Fact]
    public void WheelCheck_FarFromTheLines_DoesNotMatter()
    {
        // Un salto lontano dalle linee non tocca gli istanti di attraversamento: il veicolo resta.
        (List<TrackObservation> wheel, VehiclePassage passage, IReadOnlyList<TrackPoint> points) = WheelPassage();
        double Far(TrackObservation o) => Math.Min(Math.Abs(o.TimeSeconds - passage.Entry.TimeSeconds), Math.Abs(o.TimeSeconds - passage.Exit.TimeSeconds));
        int i = wheel.IndexOf(wheel.MaxBy(Far)!);
        Assert.True(Far(wheel[i]) > WheelCrossingCheck.WindowSeconds);
        wheel[i] = wheel[i] with { Position = new Point2(wheel[i].Position.X + 30, wheel[i].Position.Y) };
        Assert.Null(WheelCrossingCheck.Check(wheel, passage, points));
    }

    [Fact]
    public void FewWheels_FallBackToTheBoxCorner()
    {
        // Ruota trovata solo in un fotogramma su quattro: non abbastanza.
        TrackPassageCandidate c = TrackPassageExtractor.Extract(TrackWithWheels(60, 0, hasWheel: i => i % 4 == 0), TestCalibration);
        Assert.True(c.IsPassage, c.RejectionReason);
        Assert.Equal(ReferencePoint.BoxCorner, c.Reference);
    }

    [Fact]
    public void WheelOutliers_AreDropped()
    {
        // In qualche fotogramma la ruota «anteriore» trovata è in realtà un'ombra in mezzo al veicolo.
        FinishedTrack track = TrackWithWheels(60, 0);
        var points = track.Points.Select(p => p.FrameIndex % 5 == 2
            ? p with { Wheels = [new WheelCandidate(new Point2(p.Box.CenterX + 3, 440), 10, 130)] }
            : p).ToList();
        TrackPassageCandidate c = TrackPassageExtractor.Extract(new FinishedTrack(1, points), TestCalibration);
        Assert.Equal(ReferencePoint.FrontWheel, c.Reference);
        Assert.DoesNotContain(c.Observations, o => o.FrameIndex % 5 == 2);
        Assert.Equal(60, c.Result!.Passage!.Speed.SpeedKmh, 6);
    }

    [Fact]
    public void NoisyBoxes_GiveSpeedWithinUncertainty()
    {
        int within = 0;
        for (int seed = 0; seed < 100; seed++)
        {
            TrackPassageCandidate c = TrackPassageExtractor.Extract(TrackAt(60, 30, seed / 3000.0, noisePx: 2, seed: seed), TestCalibration);
            Assert.True(c.IsPassage, c.RejectionReason);
            SpeedResult s = c.Result!.Passage!.Speed;
            Assert.InRange(s.SpeedKmh, 57, 63);
            if (Math.Abs(s.SpeedKmh - 60) <= 2 * s.UncertaintyKmh)
            {
                within++;
            }
        }

        Assert.True(within >= 80, $"solo {within}/100 entro 2 sigma");
    }

    [Fact]
    public void TruncatedBoxes_AreExcluded()
    {
        // Tutti i riquadri vicini alla linea A sono tagliati: l'ingresso non è misurabile.
        FinishedTrack track = TrackAt(50, 30, 0, truncated: i => i < 40);
        TrackPassageCandidate c = TrackPassageExtractor.Extract(track, TestCalibration);
        Assert.False(c.IsPassage);
        Assert.True(c.Observations.Count < track.Points.Count);
    }

    [Fact]
    public void LowConfidenceTrack_IsRejected()
    {
        TrackPassageCandidate c = TrackPassageExtractor.Extract(TrackAt(50, 30, 0, score: 0.15f), TestCalibration);
        Assert.False(c.IsPassage);
        Assert.Contains("affidabile", c.RejectionReason);
    }

    [Fact]
    public void ShortTrack_IsRejected()
    {
        FinishedTrack full = TrackAt(50, 30, 0);
        var shortTrack = new FinishedTrack(2, full.Points.Take(3).ToList());
        Assert.False(TrackPassageExtractor.Extract(shortTrack, TestCalibration).IsPassage);
    }

    [Fact]
    public void WithoutCalibration_TrackIsKept_AndSpeedComesLater()
    {
        // Analisi prima della calibrazione: la traccia si conserva senza passaggio...
        FinishedTrack track = TrackAt(70, 30, 0.011);
        TrackPassageCandidate c = TrackPassageExtractor.Extract(track, null);
        Assert.False(c.IsPassage);
        Assert.Null(c.RejectionReason);
        Assert.Equal(track.Points.Count, c.Observations.Count);

        // ...e la velocità, calcolata dopo con le linee, è la stessa dell'analisi con le linee già note.
        PassageResult later = PassageAnalyzer.Analyze(0, c.Observations, TestCalibration, TrackPassageOptions.Default.Passage);
        TrackPassageCandidate direct = TrackPassageExtractor.Extract(track, TestCalibration);
        Assert.Equal(direct.Result!.Passage!.Speed.SpeedKmh, later.Passage!.Speed.SpeedKmh, 9);
        Assert.Equal(70, later.Passage.Speed.SpeedKmh, 6);

        // Le tracce inutilizzabili restano scartate anche senza calibrazione.
        Assert.NotNull(TrackPassageExtractor.Extract(TrackAt(50, 30, 0, score: 0.15f), null).RejectionReason);
    }

    [Fact]
    public void TrackerAndExtractor_EndToEnd_OnSyntheticDetections()
    {
        var tracker = new ByteTracker();
        double v = KmhToPxPerS(65);
        long f = 0;
        for (double t = 0.007; t <= 3.5; t += 1 / 30.0, f++)
        {
            double cx = 60 + v * t;
            var det = new VehicleDetection(new BoundingBox(cx - 40, 400, 80, 50), VehicleClass.Car, 0.85f);
            tracker.Update(f, t, cx > 1000 ? [] : [det]);
        }

        FinishedTrack track = Assert.Single(tracker.FlushAll());
        TrackPassageCandidate c = TrackPassageExtractor.Extract(track, TestCalibration);
        Assert.True(c.IsPassage, c.RejectionReason);
        Assert.Equal(65, c.Result!.Passage!.Speed.SpeedKmh, 6);
    }
}
