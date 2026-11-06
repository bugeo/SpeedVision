using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Tests;

public class RoadLayoutTests
{
    // Strada «rettangolare»: y 500–800, mezzeria a 650.
    private static readonly RoadLayout TwoLanes = new(
        [new(100, 500), new(1800, 500), new(1800, 800), new(100, 800)], [new LaneDivider(0.5, 0.5)]);

    // Strada in prospettiva: il bordo lontano è più corto e più in alto a destra.
    private static readonly RoadLayout Perspective = new(
        [new(400, 420), new(1500, 380), new(1850, 760), new(80, 900)], [new LaneDivider(0.5, 0.5)]);

    [Fact]
    public void Centered_HasEvenLanes()
    {
        RoadLayout r = RoadLayout.CreateCentered(3, 1920, 1080);
        Assert.Equal(3, r.Lanes);
        Assert.Equal([new LaneDivider(1 / 3.0, 1 / 3.0), new LaneDivider(2 / 3.0, 2 / 3.0)], r.Dividers);
        Assert.Equal(960, r.Bounds.CenterX, 6);
        Assert.Equal(540, r.Bounds.CenterY, 6);
        Assert.Equal(1, r.WithLanes(1).Lanes);
        Assert.Empty(r.WithLanes(1).Dividers);
        Assert.Equal(RoadLayout.MaxLanes, r.WithLanes(9).Lanes);
    }

    [Fact]
    public void Homography_MapsCornersAndBack()
    {
        Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        for (int i = 0; i < 4; i++)
        {
            Point2 img = Perspective.FromUnit(unit[i].X, unit[i].Y);
            Assert.Equal(Perspective.Corners[i].X, img.X, 6);
            Assert.Equal(Perspective.Corners[i].Y, img.Y, 6);
        }

        (double u, double v) = Perspective.ToUnit(Perspective.FromUnit(0.3, 0.7));
        Assert.Equal(0.3, u, 9);
        Assert.Equal(0.7, v, 9);
    }

    [Fact]
    public void Lanes_AreCountedFromTheCamera()
    {
        Assert.Equal(0, TwoLanes.LaneOf(new Point2(900, 760)));   // corsia vicina (in basso)
        Assert.Equal(1, TwoLanes.LaneOf(new Point2(900, 560)));   // corsia lontana
        Assert.Equal(0, Perspective.LaneOf(Perspective.FromUnit(0.8, 0.9)));
        Assert.Equal(1, Perspective.LaneOf(Perspective.FromUnit(0.2, 0.1)));
    }

    [Fact]
    public void JustOutside_IsAccepted_FarOutside_IsNot()
    {
        // Tolleranza: un quarto di corsia = 37,5 px sulla strada rettangolare.
        Assert.Equal(0, TwoLanes.LaneOf(new Point2(900, 830)));    // appena sotto il bordo vicino
        Assert.Equal(1, TwoLanes.LaneOf(new Point2(900, 470)));    // appena sopra il bordo lontano
        Assert.Equal(-1, TwoLanes.LaneOf(new Point2(900, 900)));   // marciapiede
        Assert.Equal(-1, TwoLanes.LaneOf(new Point2(900, 400)));   // auto parcheggiata oltre la strada
        Assert.Equal(-1, Perspective.LaneOf(Perspective.FromUnit(0.5, 1.3)));
        Assert.Equal(-1, Perspective.LaneOf(new Point2(960, 100)));   // cielo, palazzi
    }

    [Fact]
    public void SlantedDivider_SplitsTheLanesAlongItsLength()
    {
        // Mezzeria più alta a sinistra (0,3) che a destra (0,7).
        RoadLayout slanted = TwoLanes with { Dividers = [new LaneDivider(0.3, 0.7)] };
        Assert.Equal(0, slanted.LaneOf(new Point2(150, 650)));    // a sinistra la riga 650 è sotto la mezzeria
        Assert.Equal(1, slanted.LaneOf(new Point2(1750, 650)));   // a destra è sopra
        (Point2 l, Point2 r) = slanted.DividerEnds(0);
        Assert.Equal(590, l.Y, 6);
        Assert.Equal(710, r.Y, 6);

        (double top, double bottom) = slanted.LaneSpanAt(0, 100, withTolerance: false);
        Assert.Equal(590, top, 6);
        Assert.Equal(800, bottom, 6);
    }

    [Fact]
    public void Track_LaneComesFromMostOfItsPoints()
    {
        var near = Enumerable.Range(0, 20).Select(i => new Point2(150 + (i * 80), 740 + (i % 3))).ToList();
        Assert.Equal(0, TwoLanes.LaneOfTrack(near));

        // Auto ferma sul marciapiede: fuori.
        var parked = Enumerable.Range(0, 20).Select(_ => new Point2(500, 950)).ToList();
        Assert.Equal(-1, TwoLanes.LaneOfTrack(parked));

        // Più punti nella corsia lontana che nella vicina: corsia lontana.
        var change = Enumerable.Range(0, 21).Select(i => new Point2(150 + (i * 80), i < 12 ? 560 : 740)).ToList();
        Assert.Equal(1, TwoLanes.LaneOfTrack(change));
        Assert.Equal(-1, TwoLanes.LaneOfTrack([]));
    }

    [Fact]
    public void Corners_MoveFreely_ButTheShapeStaysValid()
    {
        RoadLayout r = TwoLanes.MoveCorner(1, new Point2(1500, 350), 1920, 1080);
        Assert.Equal(new Point2(1500, 350), r.TopRight);
        Assert.Equal(TwoLanes.TopLeft, r.TopLeft);

        // Vertice portato oltre il lato opposto: il quadrilatero non sarebbe più convesso, resta com'era.
        RoadLayout crossed = TwoLanes.MoveCorner(0, new Point2(1850, 850), 1920, 1080);
        Assert.Same(TwoLanes, crossed);

        // Nell'immagine.
        RoadLayout clamped = TwoLanes.MoveCorner(3, new Point2(-50, 1200), 1920, 1080);
        Assert.Equal(new Point2(0, 1080), clamped.BottomLeft);

        // Un lato si sposta con i suoi due vertici; la strada intera resta nell'immagine.
        RoadLayout lower = TwoLanes.MoveEdge(2, new Point2(0, 100), 1920, 1080);
        Assert.Equal(900, lower.BottomLeft.Y, 9);
        Assert.Equal(900, lower.BottomRight.Y, 9);
        Assert.Equal(500, lower.TopLeft.Y, 9);
        RoadLayout moved = TwoLanes.Move(new Point2(500, 0), 1920, 1080);
        Assert.Equal(1920, moved.TopRight.X, 9);
    }

    [Fact]
    public void Dividers_MoveByEnds_AndStayBetweenNeighbours()
    {
        RoadLayout three = TwoLanes.WithLanes(3);   // linee a 600 e 700
        RoadLayout leftDown = three.MoveDivider(0, -1, new Point2(100, 630), new Point2(100, 600));
        Assert.Equal((1 / 3.0) + 0.1, leftDown.Dividers[0].Left, 9);   // 30 px su 300 = 0,1
        Assert.Equal(1 / 3.0, leftDown.Dividers[0].Right, 9);

        // Troppo in basso: si ferma prima della seconda linea.
        RoadLayout tooFar = three.MoveDivider(0, 0, new Point2(900, 2000), new Point2(900, 600));
        Assert.Equal((2 / 3.0) - RoadLayout.MinLaneFraction, tooFar.Dividers[0].Left, 9);
        Assert.Equal((2 / 3.0) - RoadLayout.MinLaneFraction, tooFar.Dividers[0].Right, 9);

        // Le linee seguono la strada quando la si deforma.
        RoadLayout shifted = TwoLanes.MoveEdge(0, new Point2(0, -200), 1920, 1080);
        (Point2 l, Point2 _) = shifted.DividerEnds(0);
        Assert.Equal(550, l.Y, 6);
    }

    [Fact]
    public void SpanAcross_GoesFromNearToFarEdge_PlusTheMargin()
    {
        // Retta verticale per x = 900: bordo vicino a 800, lontano a 500, margine 0,125 × 300 = 37,5 px.
        (Point2 near, Point2 far) = TwoLanes.SpanAcross(new Point2(900, 700), new Point2(0, -1))!.Value;
        Assert.Equal(900, near.X, 6);
        Assert.Equal(837.5, near.Y, 6);
        Assert.Equal(462.5, far.Y, 6);

        // Retta obliqua (verso un punto di fuga in alto a destra): resta sulla stessa retta.
        (Point2 n2, Point2 f2) = TwoLanes.SpanAcross(new Point2(900, 800), new Point2(100, -1000))!.Value;
        Assert.Equal(896.25, n2.X, 6);   // 900 − 37,5 / 10
        Assert.Equal(933.75, f2.X, 6);   // 900 + 337,5 / 10
        Assert.Equal(462.5, f2.Y, 6);

        // Parallela ai bordi: nessun attraversamento.
        Assert.Null(TwoLanes.SpanAcross(new Point2(900, 700), new Point2(1, 0)));
    }

    [Fact]
    public void ManualVehicle_KeepsTheLength()
    {
        ReferenceVehicle v = ReferenceVehicle.Manual(4.256);
        Assert.True(v.IsManual);
        Assert.Equal(4256, v.LengthMm);
        Assert.Equal("anno non indicato", v.Years);
    }

    [Fact]
    public void AnalysisRegion_LeavesRoomForVehiclesAboveTheFarLane()
    {
        var r = TwoLanes.AnalysisRegion(1920, 1080);
        Assert.Equal(0, r.X);
        Assert.Equal(1920, r.Width);
        Assert.Equal(50, r.Y, 6);              // 500 − 1,5 × 300
        Assert.True(r.Bottom >= 800 + 37.5);
        Assert.True(r.Bottom <= 1080);
    }

    [Fact]
    public void Csv_HasTheLaneColumn_OnlyWithLanes()
    {
        var cal = new CalibrationSettings(new LineSegment(new Point2(100, 0), new Point2(100, 100)), new LineSegment(new Point2(300, 0), new Point2(300, 100)), 10);
        var obs = Enumerable.Range(0, 20).Select(i => new Core.Measurement.TrackObservation(i, i / 10.0, new Point2(i * 20, 50))).ToList();
        Core.Measurement.VehiclePassage p = Core.Measurement.PassageAnalyzer.Analyze(7, obs, cal, Core.Measurement.PassageOptions.Default).Passage!;

        string plain = Core.Export.CsvExporter.BuildCsv([p], null);
        Assert.StartsWith("Id;Verso;Orario ingresso", plain);

        string withLanes = Core.Export.CsvExporter.BuildCsv([p], null, new Dictionary<int, int> { [7] = 2 });
        string[] lines = withLanes.Split("\r\n");
        Assert.StartsWith("Id;Verso;Corsia;Orario ingresso", lines[0]);
        Assert.StartsWith("7;A → B;2;", lines[1]);
    }
}
