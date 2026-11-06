using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Tracking;

namespace TrafficSpeedAnalyzer.Core.Tests;

public class VehicleScaleTests
{
    private const double W = 1920, H = 1080;

    // Linee convergenti come in una ripresa reale: a y = 700 incontrano la traiettoria in x = 670 e x = 1250.
    private static readonly LineSegment A = new(new Point2(700, 900), new Point2(640, 500));
    private static readonly LineSegment B = new(new Point2(1220, 900), new Point2(1280, 500));

    private static readonly ReferenceVehicle Car4m = new("Prova", "Auto", "", 2020, 0, 4000, 2600, "auto");

    /// <summary>Auto che attraversa l'immagine da sinistra a destra con il lato inferiore a y = 700.</summary>
    private static List<TrackPoint> Track(Func<double, double> widthAt, double fromX = 150, double toX = 1750, double step = 25)
    {
        var pts = new List<TrackPoint>();
        long frame = 0;
        for (double cx = fromX; cx <= toX; cx += step, frame++)
        {
            double w = widthAt(cx);
            pts.Add(new TrackPoint(frame, frame / 30.0, new BoundingBox(cx - (w / 2), 700 - 80, w, 80), VehicleClass.Car, 0.9f, false));
        }

        return pts;
    }

    [Fact]
    public void Estimate_RecoversDistance_FromKnownLength()
    {
        // 4 m = 200 px → 0,02 m/px; tra le linee 580 px → 11,6 m.
        VehicleScaleEstimate? e = VehicleScaleEstimator.Estimate(Track(_ => 200), A, B, W, H, Car4m, out string? failure);
        Assert.Null(failure);
        Assert.NotNull(e);
        Assert.Equal(11.6, e.DistanceMeters, 3);
        Assert.Equal(0.02, e.MetersPerPixel, 6);
        Assert.Equal(580, e.LinesDistancePixels, 3);
        Assert.InRange(e.UncertaintyMeters, 0.2, 0.35);            // ≈ 2,2% (riquadro + lunghezza del modello)
        Assert.InRange(e.BestBox.CenterX, 945, 975);               // fotogramma più vicino al centro
        Assert.Empty(e.Warnings);
    }

    [Fact]
    public void Estimate_WarnsWhenSizeChangesAcrossTheImage()
    {
        // Da 160 px a sinistra a 240 px a destra: camera non perpendicolare.
        VehicleScaleEstimate? e = VehicleScaleEstimator.Estimate(Track(cx => 160 + (80 * cx / W)), A, B, W, H, Car4m, out _);
        Assert.NotNull(e);
        Assert.Contains(e.Warnings, w => w.Contains("perpendicolare"));
    }

    [Fact]
    public void Estimate_IgnoresOutliersNearTheCenter()
    {
        // Un riquadro sbagliato (fuso con un altro veicolo) proprio al centro non sposta la stima.
        List<TrackPoint> track = Track(_ => 200);
        int i = track.FindIndex(p => Math.Abs(p.Box.CenterX - 950) < 13);
        TrackPoint p = track[i];
        track[i] = p with { Box = new BoundingBox(p.Box.CenterX - 160, p.Box.Y, 320, p.Box.Height) };
        VehicleScaleEstimate? e = VehicleScaleEstimator.Estimate(track, A, B, W, H, Car4m, out _);
        Assert.NotNull(e);
        Assert.Equal(11.6, e.DistanceMeters, 2);
    }

    [Fact]
    public void Estimate_FailsWithoutWholeVehicle()
    {
        List<TrackPoint> track = Track(_ => 200).Select(p => p with { Truncated = true }).ToList();
        Assert.Null(VehicleScaleEstimator.Estimate(track, A, B, W, H, Car4m, out string? failure));
        Assert.NotNull(failure);
    }

    [Fact]
    public void Combine_WeightsEstimates_AndDetectsDisagreement()
    {
        VehicleScaleEstimate e1 = VehicleScaleEstimator.Estimate(Track(_ => 200), A, B, W, H, Car4m, out _)!;
        VehicleScaleEstimate e2 = e1 with { DistanceMeters = 11.8 };
        CombinedScaleEstimate c = VehicleScaleEstimator.Combine([e1, e2])!;
        Assert.True(c.Consistent);
        Assert.InRange(c.DistanceMeters, 11.6, 11.8);
        Assert.Equal(2, c.Vehicles);

        CombinedScaleEstimate bad = VehicleScaleEstimator.Combine([e1, e1 with { DistanceMeters = 14 }])!;
        Assert.False(bad.Consistent);
        Assert.NotNull(bad.Warning);
        Assert.Null(VehicleScaleEstimator.Combine([]));
    }

    [Fact]
    public void Catalog_LoadsAndSearches()
    {
        Assert.True(ReferenceVehicleCatalog.All.Count > 150);
        Assert.All(ReferenceVehicleCatalog.All, v => Assert.InRange(v.LengthMm, 2500, 6500));
        Assert.Equal(2, ReferenceVehicleCatalog.Search("fiat 500l").Count());
        Assert.Equal(3653, Assert.Single(ReferenceVehicleCatalog.Search("panda 319"), v => v.Model == "Panda").LengthMm);
        Assert.Contains(ReferenceVehicleCatalog.Search("citroen c3"), v => v.Make == "Citroën");   // ricerca senza accenti
        Assert.Equal(ReferenceVehicleCatalog.All.Count, ReferenceVehicleCatalog.Search("  ").Count());
    }
}

public class VehicleEndLinesTests
{
    [Fact]
    public void LinesAtVehicleEnds_AreOneVehicleLengthApart_AlongItsPath()
    {
        const double w = 1920, h = 1080;
        var pts = new List<TrackPoint>();
        long frame = 0;
        for (double cx = 150; cx <= 1750; cx += 25, frame++)
        {
            pts.Add(new TrackPoint(frame, frame / 30.0, new BoundingBox(cx - 100, 620, 200, 80), VehicleClass.Car, 0.9f, false));
        }

        CenterFrameMeasurement m = VehicleScaleEstimator.MeasureAtCenter(pts, w, h, out _)!;
        (LineSegment a, LineSegment b) = VehicleScaleEstimator.LinesAtVehicleEnds(m, w, h);

        // Le linee passano per le estremità a terra del veicolo al centro (y = 700, 200 px di lunghezza).
        Assert.Equal(0, a.SignedDistance(new Point2(m.Best.Box.CenterX - 100, 700)), 6);
        Assert.Equal(0, b.SignedDistance(new Point2(m.Best.Box.CenterX + 100, 700)), 6);

        // Parallele (non si incrociano), interamente nell'immagine e più lunghe dell'altezza del veicolo.
        Assert.False(GeometryMath.SegmentsIntersect(a, b));
        foreach (LineSegment l in new[] { a, b })
        {
            Assert.All(new[] { l.Start, l.End }, p => Assert.True(p.X >= 0 && p.X <= w && p.Y >= 0 && p.Y <= h));
            Assert.True(l.Length > 2 * 80);
        }

        // Con queste linee la distanza stimata dallo stesso veicolo è la sua lunghezza.
        var car = new ReferenceVehicle("Prova", "Auto", "", 2020, 0, 4150, 2612, "auto");
        VehicleScaleEstimate e = VehicleScaleEstimator.Estimate(pts, a, b, w, h, car, out _)!;
        Assert.Equal(4.15, e.DistanceMeters, 2);
    }
}

public class CrossesCenterTests
{
    private static List<TrackPoint> Track(double fromX, double toX, double step = 20)
    {
        var pts = new List<TrackPoint>();
        long f = 0;
        for (double x = fromX; step > 0 ? x <= toX : x >= toX; x += step, f++)
        {
            pts.Add(new TrackPoint(f, f / 30.0, new BoundingBox(x - 100, 620, 200, 80), VehicleClass.Car, 0.9f, false));
        }

        return pts;
    }

    [Fact]
    public void MovingCarThroughTheCenter_IsAccepted()
    {
        Assert.True(VehicleScaleEstimator.CrossesImageCenter(Track(300, 1600), 1920, 1080));
        Assert.True(VehicleScaleEstimator.CrossesImageCenter(Track(1600, 300, -20), 1920, 1080));
    }

    [Fact]
    public void ParkedOrSideCars_AreExcluded()
    {
        // Ferma (parcheggiata): pochi pixel di movimento.
        Assert.False(VehicleScaleEstimator.CrossesImageCenter(Track(500, 506, 1), 1920, 1080));
        // In movimento ma solo sul lato sinistro dell'immagine.
        Assert.False(VehicleScaleEstimator.CrossesImageCenter(Track(100, 800), 1920, 1080));
    }
}
