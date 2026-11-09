using System.Text;
using System.Text.Json;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.Services;

/// <summary>Cosa fare dopo un messaggio della pagina della mappa.</summary>
public enum MapMessageKind
{
    /// <summary>Messaggio sconosciuto o non valido: si ignora.</summary>
    None,

    /// <summary>La pagina è pronta: le si mandano i dati iniziali.</summary>
    Ready,

    /// <summary>Punti confermati con «Usa questa distanza» (o non validi): la finestra si chiude.</summary>
    Result,

    /// <summary>«Annulla»: la finestra si chiude senza risultato.</summary>
    Cancel,
}

/// <summary>
/// Dialogo con la pagina della mappa (Map/map.html con Leaflet), comune all'app WPF (WebView2) e all'interfaccia
/// multipiattaforma (WebView di Avalonia): dati iniziali nella lingua scelta, lettura dei messaggi della pagina,
/// distanza ricalcolata qui sull'ellissoide WGS84. Solo i tasselli della mappa e la ricerca passano da internet.
/// </summary>
public sealed class MapBridge(MapRequest request)
{
    /// <summary>Punti confermati con «Usa questa distanza»; null finché non ci sono.</summary>
    public MapResult? Result { get; private set; }

    /// <summary>Ultima vista della mappa, da riproporre la volta successiva.</summary>
    public (GeoPosition Center, double Zoom, string Layer)? LastView { get; private set; }

    /// <summary>Legge un messaggio della pagina (JSON con il campo «type»).</summary>
    public MapMessageKind Handle(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement m = doc.RootElement;
            switch (m.GetProperty("type").GetString())
            {
                case "ready":
                    return MapMessageKind.Ready;
                case "result":
                    ReadView(m);
                    GeoPosition a = ReadPoint(m.GetProperty("a")), b = ReadPoint(m.GetProperty("b"));
                    double meters = GeoMath.DistanceMeters(a, b);
                    double precision = m.TryGetProperty("precision", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
                    if (a.IsValid && b.IsValid && meters > 0)
                    {
                        Result = new MapResult(a, b, meters, precision, LastView?.Layer ?? "map", LastView?.Center ?? a, LastView?.Zoom ?? 19);
                    }

                    return MapMessageKind.Result;
                case "cancel":
                    ReadView(m);
                    return MapMessageKind.Cancel;
                default:
                    return MapMessageKind.None;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            AppLog.Warn("Messaggio della mappa non valido: " + ex.Message);
            return MapMessageKind.None;
        }
    }

    private static GeoPosition ReadPoint(JsonElement p) => new(p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble());

    private void ReadView(JsonElement m)
    {
        if (m.TryGetProperty("view", out JsonElement v))
        {
            string layer = m.TryGetProperty("layer", out JsonElement l) && l.GetString() == "satellite" ? "satellite" : "map";
            LastView = (ReadPoint(v), v.GetProperty("zoom").GetDouble(), layer);
        }
    }

    /// <summary>Dati iniziali per la pagina: testi nella lingua scelta, centro, punti già indicati, anteprima.</summary>
    public string BuildInitMessage()
    {
        object? Point(GeoPosition? p) => p is { } q ? new { lat = q.Latitude, lon = q.Longitude } : null;
        var strings = new Dictionary<string, string>
        {
            ["title"] = Loc.T("Distanza dalla mappa"),
            ["findTitle"] = Loc.T("Trova il luogo della ripresa"),
            ["findHint"] = Loc.T("Cerca la via o il luogo, poi ingrandisci la mappa fino a vedere i dettagli della strada."),
            ["findFromVideo"] = Loc.T("La mappa è centrata sulla posizione registrata nel video (precisione di qualche metro): controlla che sia il punto giusto."),
            ["searchPlaceholder"] = Loc.T("Via, piazza, città…"),
            ["search"] = Loc.T("Cerca"),
            ["notFound"] = Loc.T("Nessun luogo trovato."),
            ["searchError"] = Loc.T("Ricerca non riuscita: controlla la connessione a internet."),
            ["pointA"] = Loc.T("Punto A"),
            ["pointB"] = Loc.T("Punto B"),
            ["hintA"] = Loc.T("Clicca sulla mappa dove la linea A incontra il bordo della strada."),
            ["hintB"] = Loc.T("Clicca dove la linea B incontra lo stesso bordo della strada."),
            ["placed"] = Loc.T("Posizionato: trascinalo per correggerlo."),
            ["distance"] = Loc.T("Distanza tra A e B"),
            ["resultHint"] = Loc.T("Compare dopo aver messo i due punti."),
            ["precision"] = Loc.T("Precisione dei clic a questo ingrandimento: circa ± {0} m. Ingrandisci di più per una misura più precisa."),
            ["use"] = Loc.T("Usa questa distanza"),
            ["reset"] = Loc.T("Rimetti i punti"),
            ["cancel"] = Loc.T("Annulla"),
            ["snapshot"] = Loc.T("Fotogramma con le linee A e B"),
            ["privacy"] = Loc.T("La mappa e la ricerca arrivano da internet (OpenStreetMap, Esri); il video resta sul tuo PC. Con «Satellite» si vedono strisce, tombini e cordoli."),
            ["zoomIn"] = Loc.T("Mappa ingrandita: ora clicca il punto preciso."),
            ["dragToMove"] = Loc.T("I due punti ci sono già: trascinali per spostarli, o premi «Rimetti i punti»."),
            ["videoPosition"] = Loc.T("Posizione registrata nel video"),
            ["layerMap"] = Loc.T("Mappa"),
            ["layerSatellite"] = Loc.T("Satellite"),
        };

        object? center = request.Center is { } c ? new { lat = c.Latitude, lon = c.Longitude, zoom = request.Zoom } : null;
        return JsonSerializer.Serialize(new
        {
            type = "init",
            strings,
            language = Loc.Culture.Name,
            center,
            fromVideo = request.CenterFromVideo,
            videoLocation = Point(request.VideoLocation),
            a = Point(request.A),
            b = Point(request.B),
            snapshot = request.SnapshotDataUrl,
            layer = request.Layer,
        });
    }

    /// <summary>
    /// La pagina in un solo file HTML, con Leaflet, stili, script e immagini dentro: la WebView di Avalonia la
    /// carica da una stringa, senza servire file locali (che WebKit sul Mac limita). <paramref name="mapFolder"/>
    /// è la cartella con map.html, map.css, map.js, geo.js e leaflet/.
    /// </summary>
    public static string BuildSelfContainedPage(string mapFolder)
    {
        string Read(string relative) => File.ReadAllText(Path.Combine(mapFolder, relative), Encoding.UTF8);
        string DataUri(string relative) => "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(Path.Combine(mapFolder, relative)));

        string leafletCss = Read(Path.Combine("leaflet", "leaflet.css"))
            .Replace("url(images/layers-2x.png)", "url(" + DataUri(Path.Combine("leaflet", "images", "layers-2x.png")) + ")", StringComparison.Ordinal)
            .Replace("url(images/layers.png)", "url(" + DataUri(Path.Combine("leaflet", "images", "layers.png")) + ")", StringComparison.Ordinal);
        string html = Read("map.html");
        html = Replace(html, "<link rel=\"stylesheet\" href=\"leaflet/leaflet.css\" />", "<style>\n" + leafletCss + "\n</style>");
        html = Replace(html, "<link rel=\"stylesheet\" href=\"map.css\" />", "<style>\n" + Read("map.css") + "\n</style>");
        html = Replace(html, "<script src=\"leaflet/leaflet.js\"></script>", "<script>\n" + Read(Path.Combine("leaflet", "leaflet.js")) + "\n</script>");
        html = Replace(html, "<script src=\"geo.js\"></script>", "<script>\n" + Read("geo.js") + "\n</script>");
        html = Replace(html, "<script src=\"map.js\"></script>", "<script>\n" + Read("map.js") + "\n</script>");
        return html;
    }

    private static string Replace(string html, string tag, string inline)
    {
        int i = html.IndexOf(tag, StringComparison.Ordinal);
        if (i < 0)
        {
            throw new InvalidDataException("map.html: manca " + tag);
        }

        return string.Concat(html.AsSpan(0, i), inline, html.AsSpan(i + tag.Length));
    }
}
