using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Tests;

/// <summary>Punti a terra delle due ruote per la calibrazione con il passo.</summary>
public class VehicleWheelsTests
{
    private static readonly BoundingBox Car = new(100, 200, 400, 150);

    private static WheelCandidate Wheel(double x, double y) => new(new Point2(x, y - 30), 30, 90, new Point2(x, y));

    [Fact]
    public void TwoPlausibleWheels_GiveTheGroundContacts_LeftToRight()
    {
        WheelContacts w = VehicleWheels.FromCandidates([Wheel(420, 348), Wheel(170, 346)], Car)!.Value;
        Assert.Equal(new Point2(170, 346), w.Left);
        Assert.Equal(new Point2(420, 348), w.Right);
        Assert.Equal(250, w.AlongRoad(0), 6);
    }

    [Fact]
    public void MissingOrImplausibleWheels_GiveNothing()
    {
        Assert.Null(VehicleWheels.FromCandidates([Wheel(170, 346)], Car));                    // una sola ruota
        Assert.Null(VehicleWheels.FromCandidates([Wheel(170, 346), Wheel(230, 346)], Car));   // troppo vicine
        Assert.Null(VehicleWheels.FromCandidates([Wheel(110, 346), Wheel(495, 346)], Car));   // più del 90 % del riquadro
        Assert.Null(VehicleWheels.FromCandidates([Wheel(170, 230), Wheel(420, 346)], Car));   // una ruota sul tetto
    }

    [Fact]
    public void Catalog_HasTheWheelbaseOfEveryVehicle()
    {
        Assert.All(ReferenceVehicleCatalog.All, v => Assert.InRange(v.WheelbaseMm, 1800, 4500));
        Assert.All(ReferenceVehicleCatalog.All, v => Assert.True(v.WheelbaseMm < v.LengthMm));
    }
}
