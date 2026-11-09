using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;

namespace TrafficSpeedAnalyzer.Presentation.Tests;

/// <summary>Dialogo con la pagina della mappa, comune all'app WPF e all'interfaccia Avalonia.</summary>
public class MapBridgeTests
{
    private static string MapFolder
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SpeedVision.sln")))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir!.FullName, "src", "TrafficSpeedAnalyzer.App", "Map");
        }
    }

    [Fact]
    public void SelfContainedPage_HasEverythingInside()
    {
        string html = MapBridge.BuildSelfContainedPage(MapFolder);

        // Nessun file locale da caricare: stili, script e immagini sono nella pagina.
        Assert.DoesNotContain("src=\"", html);
        Assert.DoesNotContain("href=\"leaflet", html);
        Assert.DoesNotContain("url(images/layers", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("window.tsaMap", html);
        Assert.Contains("invokeCSharpAction", html);
    }

    [Fact]
    public void Result_IsMeasuredHere_OnTheEllipsoid()
    {
        var bridge = new MapBridge(new MapRequest(null, null, false, null, null, null, null, "map"));
        Assert.Equal(MapMessageKind.Ready, bridge.Handle("""{"type":"ready"}"""));

        // Due punti a 0,0002° di latitudine: circa 22,2 m.
        MapMessageKind k = bridge.Handle("""{"type":"result","a":{"lat":45.0,"lon":9.0},"b":{"lat":45.0002,"lon":9.0},"precision":0.1,"layer":"satellite","view":{"lat":45.0001,"lon":9.0,"zoom":20}}""");
        Assert.Equal(MapMessageKind.Result, k);
        Assert.NotNull(bridge.Result);
        Assert.Equal(22.2, bridge.Result.Meters, 0.2);
        Assert.Equal("satellite", bridge.LastView!.Value.Layer);
    }

    [Fact]
    public void InvalidMessage_IsIgnored()
    {
        var bridge = new MapBridge(new MapRequest(null, null, false, null, null, null, null, "map"));
        Assert.Equal(MapMessageKind.None, bridge.Handle("non è json"));
        Assert.Equal(MapMessageKind.None, bridge.Handle("""{"type":"altro"}"""));
        Assert.Null(bridge.Result);
    }

    [Fact]
    public void InitMessage_CarriesTextsAndPoints()
    {
        var bridge = new MapBridge(new MapRequest(new GeoPosition(45, 9), 18, true, null, new GeoPosition(45, 9), null, null, "map"));
        string init = bridge.BuildInitMessage();
        Assert.Contains("\"type\":\"init\"", init);
        Assert.Contains("\"use\":", init);
        Assert.Contains("\"fromVideo\":true", init);
    }
}
