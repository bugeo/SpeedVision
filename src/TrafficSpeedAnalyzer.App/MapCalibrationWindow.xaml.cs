using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App;

/// <summary>Dati per aprire la mappa: dove centrarla, punti già indicati, anteprima del fotogramma con le linee.</summary>
public sealed record MapRequest(
    GeoPosition? Center,
    double? Zoom,
    bool CenterFromVideo,
    GeoPosition? VideoLocation,
    GeoPosition? A,
    GeoPosition? B,
    string? SnapshotDataUrl,
    string Layer);

/// <summary>Punti indicati sulla mappa e loro distanza (calcolata qui, con <see cref="GeoMath"/>).</summary>
public sealed record MapResult(GeoPosition A, GeoPosition B, double Meters, double PrecisionMeters, string Layer, GeoPosition ViewCenter, double ViewZoom);

/// <summary>
/// Finestra con la mappa (WebView2 + Leaflet, pagina locale in <c>Map\map.html</c>). La pagina guida l'utente
/// e manda al programma i due punti scelti; la distanza viene ricalcolata qui sull'ellissoide WGS84.
/// Solo i tasselli della mappa e la ricerca dei luoghi passano da internet: il video resta sul PC.
/// </summary>
public partial class MapCalibrationWindow
{
    private const string HostName = "tsa-map.local";
    private const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private readonly MapRequest _request;

    public MapCalibrationWindow(MapRequest request)
    {
        _request = request;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Punti confermati con «Usa questa distanza»; null se la finestra è stata chiusa.</summary>
    public MapResult? Result { get; private set; }

    /// <summary>Ultima vista della mappa, da riproporre la volta successiva.</summary>
    public (GeoPosition Center, double Zoom, string Layer)? LastView { get; private set; }

    /// <summary>La mappa non si è potuta aprire (per esempio manca WebView2).</summary>
    public string? Error { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Map");
        if (!File.Exists(Path.Combine(folder, "map.html")))
        {
            Fail(Loc.S($"Mancano i file della mappa in {folder}. Reinstalla il programma."));
            return;
        }

        try
        {
            // Dati di WebView2 (cache dei tasselli) nella cartella dell'app, non accanto all'eseguibile.
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.DataDirectory, "WebView2"));
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Fail(Loc.S($"Per la mappa serve Microsoft Edge WebView2 Runtime (già presente in Windows 11). Scaricalo da {RuntimeDownloadUrl} e riprova."));
            return;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            AppLog.Error("WebView2 non avviato", ex);
            Fail(Loc.T("Impossibile avviare la mappa: ") + ex.Message);
            return;
        }

        CoreWebView2 core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping(HostName, folder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenInBrowser(args.Uri);
        };
        core.NavigationStarting += (_, args) =>
        {
            // I link (crediti della mappa) si aprono nel browser, non al posto della mappa.
            if (!args.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                OpenInBrowser(args.Uri);
            }
        };
        core.NavigationCompleted += (_, _) => LoadingPanel.Visibility = Visibility.Collapsed;
        Web.Source = new Uri($"https://{HostName}/map.html");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement m = doc.RootElement;
            switch (m.GetProperty("type").GetString())
            {
                case "ready":
                    Web.CoreWebView2.PostWebMessageAsJson(BuildInitMessage());
                    break;
                case "result":
                    ReadView(m);
                    GeoPosition a = ReadPoint(m.GetProperty("a")), b = ReadPoint(m.GetProperty("b"));
                    double meters = GeoMath.DistanceMeters(a, b);
                    double precision = m.TryGetProperty("precision", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
                    if (a.IsValid && b.IsValid && meters > 0)
                    {
                        Result = new MapResult(a, b, meters, precision, LastView?.Layer ?? "map",
                            LastView?.Center ?? a, LastView?.Zoom ?? 19);
                    }

                    Close();
                    break;
                case "cancel":
                    ReadView(m);
                    Close();
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            AppLog.Warn("Messaggio della mappa non valido: " + ex.Message);
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
    private string BuildInitMessage()
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

        object? center = _request.Center is { } c ? new { lat = c.Latitude, lon = c.Longitude, zoom = _request.Zoom } : null;
        return JsonSerializer.Serialize(new
        {
            type = "init",
            strings,
            language = Loc.Culture.Name,
            center,
            fromVideo = _request.CenterFromVideo,
            videoLocation = Point(_request.VideoLocation),
            a = Point(_request.A),
            b = Point(_request.B),
            snapshot = _request.SnapshotDataUrl,
            layer = _request.Layer,
        });
    }

    private void Fail(string message)
    {
        Error = message;
        Close();
    }

    private static void OpenInBrowser(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
        {
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
    }
}
