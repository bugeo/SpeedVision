using TrafficSpeedAnalyzer.Core.Analysis;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Tracking;

namespace TrafficSpeedAnalyzer.Core.Tests;

/// <summary>
/// Analisi alimentata un fotogramma alla volta (usata dal prototipo web). Non simula il modello:
/// i "rilevamenti" sono riquadri ideali costruiti a velocità nota, per verificare tracker,
/// esclusione dei riquadri tagliati e conversione in passaggi.
/// Scena come il video di prova a velocità note: 768×432, linea A a y = 180, B a y = 380, 20 m.
/// </summary>
public class StreamingAnalysisTests
{
    private const int VideoWidth = 768;
    private const int VideoHeight = 432;

    private static readonly CalibrationSettings Calibration = new(
        new LineSegment(new Point2(0, 180), new Point2(VideoWidth, 180)),
        new LineSegment(new Point2(0, 380), new Point2(VideoWidth, 380)),
        20);

    private static BoundingBox Region => AnalysisRegion.FromCalibration(Calibration, VideoWidth, VideoHeight);

    /// <summary>Auto (60×100 px) che sale a velocità costante: lato inferiore da y0 verso l'alto.</summary>
    private static List<(long Frame, double Time, VehicleDetection Det)> UpwardCar(double kmh, double startTime, double bottom0, double x = 300, double fps = 30, double duration = 4)
    {
        double pxPerS = kmh / 3.6 / 0.1; // 0,1 m/px
        var list = new List<(long, double, VehicleDetection)>();
        int frames = (int)(duration * fps);
        for (int i = 0; i < frames; i++)
        {
            double t = startTime + i / fps;
            double bottom = bottom0 - pxPerS * (t - startTime);
            if (bottom < 40)
            {
                break;
            }

            var box = new BoundingBox(x, bottom - 100, 60, 100);
            list.Add(((long)Math.Round(t * fps), t, new VehicleDetection(box, VehicleClass.Car, 0.9f)));
        }

        return list;
    }

    private static List<TrackPassageCandidate> Run(StreamingAnalysis analysis, IEnumerable<(long Frame, double Time, VehicleDetection Det)> dets, double fps = 30, double end = 8)
    {
        var byFrame = dets.GroupBy(d => d.Frame).ToDictionary(g => g.Key, g => g.Select(d => d.Det).ToList());
        var found = new List<TrackPassageCandidate>();
        for (long f = 0; f < end * fps; f++)
        {
            List<VehicleDetection> list = byFrame.GetValueOrDefault(f) ?? [];
            analysis.AddDetections(f, f / fps, list);
            found.AddRange(analysis.TakeNewPassages());
        }

        found.AddRange(analysis.Complete());
        return found;
    }

    [Fact]
    public void Region_AroundFullWidthLines_IsWholeFrame()
    {
        Assert.Equal(new BoundingBox(0, 0, VideoWidth, VideoHeight), Region);
    }

    [Fact]
    public void Truncation_TopEdgeAllowed_OtherEdgesExcluded()
    {
        var region = new BoundingBox(100, 100, 400, 300);
        Assert.False(AnalysisRegion.IsTruncated(new BoundingBox(200, 100, 50, 50), region)); // solo in alto
        Assert.True(AnalysisRegion.IsTruncated(new BoundingBox(100, 200, 50, 50), region));  // a sinistra
        Assert.True(AnalysisRegion.IsTruncated(new BoundingBox(460, 200, 40, 50), region));  // a destra
        Assert.True(AnalysisRegion.IsTruncated(new BoundingBox(200, 360, 50, 40), region));  // in basso
    }

    [Theory]
    [InlineData(50.0)]
    [InlineData(72.0)]
    [InlineData(130.0)]
    public void SingleCar_MeasuredAtKnownSpeed(double kmh)
    {
        var analysis = new StreamingAnalysis(Calibration, Region);
        List<TrackPassageCandidate> found = Run(analysis, UpwardCar(kmh, 0.5, 425));

        TrackPassageCandidate c = Assert.Single(found);
        VehiclePassage p = c.Result!.Passage!;
        Assert.Equal(TravelDirection.BToA, p.Direction);
        Assert.Equal(VehicleClass.Car, c.Class);
        Assert.InRange(p.Speed.SpeedKmh, kmh * 0.995, kmh * 1.005);

        // Il lato inferiore attraversa B (y = 380) dopo (425 − 380) / v secondi.
        double v = kmh / 3.6 / 0.1;
        Assert.Equal(0.5 + 45 / v, p.Entry.TimeSeconds, 3);
        Assert.Equal(0.5 + 245 / v, p.Exit.TimeSeconds, 3);
        Assert.Equal(1, analysis.Passages);
        Assert.Equal(0, analysis.RejectedTracks);
    }

    [Fact]
    public void TwoCarsInSequence_BothMeasured()
    {
        // Come nel video di prova: prima un'auto a 50 km/h, poi una a 72 km/h.
        var dets = UpwardCar(50, 0.5, 425, x: 220).Concat(UpwardCar(72, 3.2, 425, x: 450));
        var analysis = new StreamingAnalysis(Calibration, Region);
        List<VehiclePassage> passages = Run(analysis, dets).Select(c => c.Result!.Passage!).OrderBy(p => p.Entry.TimeSeconds).ToList();

        Assert.Equal(2, passages.Count);
        Assert.InRange(passages[0].Speed.SpeedKmh, 49.75, 50.25);
        Assert.InRange(passages[1].Speed.SpeedKmh, 71.6, 72.4);
    }

    [Fact]
    public void CarTouchingRegionSide_IsNotMeasured()
    {
        // Riquadro sempre attaccato al bordo sinistro: il centro del lato inferiore non è affidabile.
        var analysis = new StreamingAnalysis(Calibration, Region);
        List<TrackPassageCandidate> found = Run(analysis, UpwardCar(50, 0.5, 425, x: 0));

        Assert.Empty(found);
        Assert.Equal(1, analysis.RejectedTracks);
        Assert.Equal(1, analysis.RejectionReasons["Traccia troppo breve."]);
    }

    [Fact]
    public void CarAlreadyBeyondFirstLine_IsNotReconstructed()
    {
        // Compare già tra le linee: in automatico niente ricostruzione all'indietro.
        var analysis = new StreamingAnalysis(Calibration, Region);
        List<TrackPassageCandidate> found = Run(analysis, UpwardCar(50, 0.5, 340));

        Assert.Empty(found);
        Assert.Equal(1, analysis.RejectedTracks);
    }

    [Fact]
    public void LowConfidenceDetections_DoNotStartTracks()
    {
        var weak = UpwardCar(50, 0.5, 425).Select(d => (d.Frame, d.Time, d.Det with { Score = 0.3f }));
        var analysis = new StreamingAnalysis(Calibration, Region);

        Assert.Empty(Run(analysis, weak));
        Assert.Equal(0, analysis.RejectedTracks);
    }

    [Fact]
    public void VehicleStillVisibleAtEnd_IsEvaluatedOnComplete()
    {
        // Il video finisce subito dopo la linea A, con l'auto ancora in vista.
        var analysis = new StreamingAnalysis(Calibration, Region);
        var dets = UpwardCar(50, 0.5, 425);
        foreach ((long f, double t, VehicleDetection d) in dets.Where(d => d.Det.Box.Bottom > 150))
        {
            analysis.AddDetections(f, t, [d]);
            Assert.Empty(analysis.TakeNewPassages());
        }

        Assert.Single(analysis.Complete());
    }

    [Fact]
    public void CarThatStopsAfterLineA_IsMeasuredWithoutWaitingForTheEnd()
    {
        // Ripresa dall'alto in un parcheggio: l'auto supera A e si ferma, restando inquadrata.
        var analysis = new StreamingAnalysis(Calibration, Region);
        List<(long Frame, double Time, VehicleDetection Det)> moving = UpwardCar(50, 0.5, 425);
        var dets = moving.Where(d => d.Det.Box.Bottom > 120).ToList();
        VehicleDetection parked = dets[^1].Det;
        long lastFrame = dets[^1].Frame;

        TrackPassageCandidate? found = null;
        long foundAt = -1;
        for (long f = 15; f < 240; f++)
        {
            VehicleDetection d = f <= lastFrame ? dets.First(x => x.Frame == f).Det : parked with { };
            IReadOnlyList<TrackedVehicle> tracked = analysis.AddDetections(f, f / 30.0, [d]);
            foreach (TrackPassageCandidate c in analysis.TakeNewPassages())
            {
                Assert.Null(found);
                found = c;
                foundAt = f;
                Assert.Equal(c.Result!.Passage!.Speed.SpeedKmh, analysis.SpeedOf(tracked[0].TrackId)!.Value);
            }
        }

        Assert.NotNull(found);
        double exitFrame = found!.Result!.Passage!.Exit.TimeSeconds * 30;
        Assert.InRange(foundAt, exitFrame + 10, exitFrame + 13); // circa 0,35 s dopo la linea A
        Assert.InRange(found.Result.Passage.Speed.SpeedKmh, 49.75, 50.25);
        Assert.Empty(analysis.Complete()); // nessun doppione a fine video
        Assert.Equal(1, analysis.Passages);
    }

    [Fact]
    public void TimesMustIncrease()
    {
        var analysis = new StreamingAnalysis(Calibration, Region);
        analysis.AddDetections(0, 0.5, []);
        Assert.Throws<ArgumentException>(() => analysis.AddDetections(1, 0.5, []));
    }
}
