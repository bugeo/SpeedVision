using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Tests;

public class GeometryTests
{
    private static readonly LineSegment Horizontal = new(new Point2(0, 0), new Point2(10, 0));

    [Fact]
    public void SignedDistance_IsPerpendicularDistanceWithSide()
    {
        Assert.Equal(5, Horizontal.SignedDistance(new Point2(3, 5)), 9);
        Assert.Equal(-2, Horizontal.SignedDistance(new Point2(30, -2)), 9);
        Assert.Equal(0, Horizontal.SignedDistance(new Point2(7, 0)), 9);
    }

    [Fact]
    public void SignedDistance_DoesNotDependOnSegmentLength()
    {
        var shortLine = new LineSegment(new Point2(0, 0), new Point2(1, 1));
        var longLine = new LineSegment(new Point2(0, 0), new Point2(100, 100));
        var p = new Point2(0, 10);
        Assert.Equal(shortLine.SignedDistance(p), longLine.SignedDistance(p), 9);
        Assert.Equal(10 / Math.Sqrt(2), Math.Abs(longLine.SignedDistance(p)), 9);
    }

    [Fact]
    public void SignedDistance_OnDegenerateSegment_Throws()
    {
        var degenerate = new LineSegment(new Point2(1, 1), new Point2(1, 1));
        Assert.True(degenerate.IsDegenerate);
        Assert.Throws<InvalidOperationException>(() => degenerate.SignedDistance(new Point2(0, 0)));
    }

    [Fact]
    public void ProjectParameter_MapsEndpointsToZeroAndOne()
    {
        Assert.Equal(0, Horizontal.ProjectParameter(new Point2(0, 4)), 9);
        Assert.Equal(1, Horizontal.ProjectParameter(new Point2(10, -4)), 9);
        Assert.Equal(0.25, Horizontal.ProjectParameter(new Point2(2.5, 1)), 9);
    }

    [Theory]
    [InlineData(5, -5, 5, 5, true)]    // incrocio al centro
    [InlineData(10, -5, 10, 5, true)]  // tocca l'estremo
    [InlineData(11, -5, 11, 5, false)] // oltre l'estremo
    [InlineData(5, 1, 5, 5, false)]    // non raggiunge il segmento
    [InlineData(0, 3, 10, 3, false)]   // parallelo
    public void SegmentsIntersect_Cases(double x1, double y1, double x2, double y2, bool expected)
    {
        var other = new LineSegment(new Point2(x1, y1), new Point2(x2, y2));
        Assert.Equal(expected, GeometryMath.SegmentsIntersect(Horizontal, other));
    }

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(10, 10, 45)]
    [InlineData(10, 0, 90)]
    [InlineData(-10, 10, 45)]
    public void AcuteAngle_IsBetweenZeroAndNinety(double dx, double dy, double expectedDeg)
    {
        var vertical = new LineSegment(new Point2(0, 0), new Point2(0, 10));
        var other = new LineSegment(new Point2(0, 0), new Point2(dx, dy));
        Assert.Equal(expectedDeg, GeometryMath.AcuteAngleDegrees(vertical, other), 6);
    }
}
