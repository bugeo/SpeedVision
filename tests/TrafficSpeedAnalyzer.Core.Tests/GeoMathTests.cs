using TrafficSpeedAnalyzer.Core.Calibration;

namespace TrafficSpeedAnalyzer.Core.Tests;

/// <summary>Distanze tra punti della mappa (calibrazione «sulla mappa») e posizione GPS dei video.</summary>
public class GeoMathTests
{
    // Valori di riferimento calcolati con GeographicLib (Karney), ellissoide WGS84.
    [Theory]
    [InlineData(45.4642, 9.1900, 45.4645, 9.1903, 40.769859)]       // Milano, distanza tipica tra le linee
    [InlineData(45.0, 9.0, 45.0, 9.001, 78.846835)]                 // solo longitudine
    [InlineData(45.0, 9.0, 45.0003, 9.0, 33.339534)]                // solo latitudine
    [InlineData(59.9, 10.7, 59.9002, 10.7006, 40.300978)]           // nord Europa
    [InlineData(-33.86, 151.21, -33.8601, 151.2102, 21.576578)]     // emisfero sud
    [InlineData(0, 0, 0, 1, 111319.490793)]                         // equatore
    [InlineData(45, 9, 46, 10, 135869.091247)]
    public void Distance_MatchesGeographicLib(double lat1, double lon1, double lat2, double lon2, double expected)
    {
        var a = new GeoPosition(lat1, lon1);
        var b = new GeoPosition(lat2, lon2);
        Assert.Equal(expected, GeoMath.DistanceMeters(a, b), 3);   // entro un millimetro
        Assert.Equal(expected, GeoMath.DistanceMeters(b, a), 3);
    }

    [Fact]
    public void Distance_SamePointIsZero_AndAntipodesDoNotHang()
    {
        Assert.Equal(0, GeoMath.DistanceMeters(new GeoPosition(45, 9), new GeoPosition(45, 9)));
        double d = GeoMath.DistanceMeters(new GeoPosition(0, 0), new GeoPosition(0.5, 179.7));
        Assert.InRange(d, 19_900_000, 20_100_000);
    }

    [Theory]
    [InlineData("+45.4642+009.1900+120.000/", 45.4642, 9.19)]   // iPhone (com.apple.quicktime.location.ISO6709)
    [InlineData("+45.4642+009.1900/", 45.4642, 9.19)]           // Android (location)
    [InlineData("-33.8568+151.2153/", -33.8568, 151.2153)]
    [InlineData("+48-002/", 48, -2)]
    public void Iso6709_IsParsed(string text, double lat, double lon)
    {
        GeoPosition p = GeoPosition.ParseIso6709(text)!.Value;
        Assert.Equal(lat, p.Latitude, 9);
        Assert.Equal(lon, p.Longitude, 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Milano")]
    [InlineData("+95.0+009.0/")]       // latitudine impossibile
    [InlineData("+0.0000+000.0000/")]  // posizione nulla scritta da alcune app
    public void Iso6709_Invalid_IsNull(string? text) => Assert.Null(GeoPosition.ParseIso6709(text));
}
