using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App;

/// <summary>
/// Finestra con la mappa (WebView2 + Leaflet, pagina locale in <c>Map\map.html</c>). La pagina guida l'utente
/// e manda al programma i due punti scelti; la distanza viene ricalcolata qui sull'ellissoide WGS84.
/// Solo i tasselli della mappa e la ricerca dei luoghi passano da internet: il video resta sul PC.
/// </summary>
public partial class MapCalibrationWindow
{
    private const string HostName = "tsa-map.local";
    private const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private readonly MapBridge _bridge;

    public MapCalibrationWindow(MapRequest request)
    {
        _bridge = new MapBridge(request);
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Punti confermati con «Usa questa distanza»; null se la finestra è stata chiusa.</summary>
    public MapResult? Result => _bridge.Result;

    /// <summary>Ultima vista della mappa, da riproporre la volta successiva.</summary>
    public (GeoPosition Center, double Zoom, string Layer)? LastView => _bridge.LastView;

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
        switch (_bridge.Handle(e.WebMessageAsJson))
        {
            case MapMessageKind.Ready:
                Web.CoreWebView2.PostWebMessageAsJson(_bridge.BuildInitMessage());
                break;
            case MapMessageKind.Result or MapMessageKind.Cancel:
                Close();
                break;
        }
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
