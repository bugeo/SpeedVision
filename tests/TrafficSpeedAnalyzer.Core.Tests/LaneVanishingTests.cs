using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Geometry;
using Xunit;

namespace TrafficSpeedAnalyzer.Core.Tests;

/// <summary>Punto di fuga trasversale stimato da bordi e linee tra le corsie (corsie larghe uguali).</summary>
public class LaneVanishingTests
{
    private const double F = 1000, Cx = 960, Cy = 540;

    /// <summary>
    /// Camera a 15 m dalla strada e 6 m d'altezza, che guarda la strada di fronte inclinata di 20° verso il basso.
    /// X lungo la strada, Y attraverso (lontano dalla camera), Z in alto.
    /// </summary>
    private static Point2 Project(double x, double y, double tiltDeg = 20, double yawDeg = 0)
    {
        double t = tiltDeg * Math.PI / 180, w = yawDeg * Math.PI / 180;
        (double X, double Y, double Z) fwd = (Math.Sin(w) * Math.Cos(t), Math.Cos(w) * Math.Cos(t), -Math.Sin(t));
        (double X, double Y, double Z) right = (Math.Cos(w), -Math.Sin(w), 0);
        (double X, double Y, double Z) down = (
            (fwd.Y * right.Z) - (fwd.Z * right.Y),
            (fwd.Z * right.X) - (fwd.X * right.Z),
            (fwd.X * right.Y) - (fwd.Y * right.X));
        (double X, double Y, double Z) v = (x, y + 15, -6);
        static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
        double zc = Dot(v, fwd);
        return new Point2(Cx + (F * Dot(v, right) / zc), Cy + (F * Dot(v, down) / zc));
    }

    /// <summary>Punto di fuga vero della direzione trasversale (0, 1, 0).</summary>
    private static Point2 TrueVanishing(double tiltDeg = 20, double yawDeg = 0)
    {
        Point2 a = Project(0, 1000, tiltDeg, yawDeg), b = Project(0, 100000, tiltDeg, yawDeg);
        return b + ((b - a) * 0.001);
    }

    /// <summary>Strada di <paramref name="lanes"/> corsie da 3,5 m, lati del quadrilatero dati da due rette a terra.</summary>
    private static RoadLayout Road(int lanes, Func<double, double> leftX, Func<double, double> rightX, double tilt = 20, double yaw = 0)
    {
        double width = 3.5 * lanes;
        var road = new RoadLayout(
            [Project(leftX(width), width, tilt, yaw), Project(rightX(width), width, tilt, yaw), Project(rightX(0), 0, tilt, yaw), Project(leftX(0), 0, tilt, yaw)],
            RoadLayout.EvenDividers(lanes));

        // Linee tra le corsie nelle posizioni vere, dall'alto (lontano) in basso.
        var dividers = new List<LaneDivider>();
        for (int k = lanes - 1; k >= 1; k--)
        {
            double y = 3.5 * k;
            dividers.Add(new LaneDivider(road.ToUnit(Project(leftX(y), y, tilt, yaw)).V, road.ToUnit(Project(rightX(y), y, tilt, yaw)).V));
        }

        return road with { Dividers = dividers };
    }

    [Fact]
    public void TwoLanes_FollowsTheFormulaOnOneTransversal()
    {
        // Bordo vicino a 0, prima linea a 100 px, seconda a 150 px: r = 0,5 → punto di fuga a 100·1,5/0,5 = 300 px.
        Point2? v = LaneVanishing.AlongTransversal([new(0, 500), new(0, 400), new(0, 350)]);

        Assert.NotNull(v);
        Assert.Equal(0, v.Value.X, 9);
        Assert.Equal(200, v.Value.Y, 9);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TransverseSides_GiveTheTrueVanishingPoint(int lanes)
    {
        RoadLayout road = Road(lanes, _ => -10, _ => 12);

        Point2 v = LaneVanishing.Estimate(road, 1920, 1080)!.Value;

        Point2 truth = TrueVanishing();
        Assert.True(Point2.Distance(v, truth) < 1, $"{v} invece di {truth}");
    }

    [Fact]
    public void SlantedSides_UseTheHorizon()
    {
        // Lati non trasversali (il sinistro è obliquo sul terreno): si passa dall'orizzonte.
        RoadLayout road = Road(2, y => -12 + (y * 0.6), y => 10 - (y * 0.3));

        Point2 v = LaneVanishing.Estimate(road, 1920, 1080)!.Value;

        Assert.True(Point2.Distance(v, TrueVanishing()) < 1);
    }

    [Fact]
    public void RotatedCamera_IsAnApproximation()
    {
        // Camera ruotata di 10° rispetto alla strada, lati obliqui: l'orizzonte è giusto (stessa altezza), la posizione
        // lungo l'orizzonte è quella di una camera che guarda la strada di fronte (qui circa 190 px di scarto).
        RoadLayout road = Road(3, y => -12 + (y * 0.6), y => 10 - (y * 0.3), yaw: 10);

        Point2 v = LaneVanishing.Estimate(road, 1920, 1080)!.Value;
        Point2 truth = TrueVanishing(yawDeg: 10);

        Assert.Equal(truth.Y, v.Y, 0);
        Assert.InRange(v.X - truth.X, 150, 230);
    }

    [Fact]
    public void OneLaneOrNoPerspective_GivesNothing()
    {
        Assert.Null(LaneVanishing.Estimate(Road(1, _ => -10, _ => 12), 1920, 1080));
        Assert.Null(LaneVanishing.AlongTransversal([new(0, 500), new(0, 400), new(0, 300)]));
    }
}
