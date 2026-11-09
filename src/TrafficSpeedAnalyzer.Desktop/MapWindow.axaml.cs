using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Desktop;

/// <summary>
/// Finestra con la mappa (WebView del sistema + Leaflet), come quella dell'app WPF. La pagina guida l'utente e manda
/// al programma i due punti scelti; la distanza viene ricalcolata qui (<see cref="MapBridge"/>). La pagina è caricata
/// come testo con tutto dentro: solo i tasselli della mappa e la ricerca dei luoghi passano da internet.
/// </summary>
public partial class MapWindow : Window
{
    /// <summary>Indirizzo fittizio della pagina: serve solo a distinguerla dai link esterni.</summary>
    private static readonly Uri PageUri = new("https://tsa-map.local/map.html");

    private readonly MapBridge _bridge;
    private bool _initSent;

    public MapWindow()
        : this(new MapRequest(null, null, false, null, null, null, null, "map"))
    {
    }

    public MapWindow(MapRequest request)
    {
        _bridge = new MapBridge(request);
        InitializeComponent();
        Web.EnvironmentRequested += OnEnvironmentRequested;
        Web.WebMessageReceived += OnWebMessage;
        Web.NavigationStarted += OnNavigationStarted;
        Web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenInBrowser(e.Request);
        };
        Web.NavigationCompleted += async (_, _) =>
        {
            LoadingPanel.IsVisible = false;
            await SendInitAsync(); // se il messaggio «ready» della pagina è arrivato prima del collegamento
        };
        Opened += OnOpened;
    }

    public MapResult? Result => _bridge.Result;

    public (GeoPosition Center, double Zoom, string Layer)? LastView => _bridge.LastView;

    /// <summary>La mappa non si è potuta aprire.</summary>
    public string? Error { get; private set; }

    private void OnOpened(object? sender, EventArgs e)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Map");
        string html;
        try
        {
            html = MapBridge.BuildSelfContainedPage(folder);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            AppLog.Error("Pagina della mappa", ex);
            Fail(Loc.S($"Mancano i file della mappa in {folder}. Reinstalla il programma."));
            return;
        }

        try
        {
            Web.NavigateToString(html, PageUri);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException or DllNotFoundException)
        {
            AppLog.Error("WebView non avviata", ex);
            Fail(Loc.T("Impossibile avviare la mappa: ") + ex.Message);
        }
    }

    /// <summary>Dati della WebView (cache dei tasselli) nella cartella dell'app, come nell'app WPF.</summary>
    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;
        if (e is WindowsWebView2EnvironmentRequestedEventArgs w2)
        {
            w2.UserDataFolder = Path.Combine(AppSettings.DataDirectory, "WebView2");
        }
    }

    private async void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        switch (_bridge.Handle(e.Body ?? string.Empty))
        {
            case MapMessageKind.Ready:
                _initSent = false;
                await SendInitAsync();
                break;
            case MapMessageKind.Result or MapMessageKind.Cancel:
                Close();
                break;
        }
    }

    private async Task SendInitAsync()
    {
        if (_initSent)
        {
            return;
        }

        _initSent = true;
        try
        {
            await Web.InvokeScript("window.tsaMap && window.tsaMap.init(" + _bridge.BuildInitMessage() + ")");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
            _initSent = false; // pagina non ancora pronta: riproverà con «ready» o a fine caricamento
            AppLog.Warn("Mappa: dati iniziali non inviati: " + ex.Message);
        }
    }

    /// <summary>I link (crediti della mappa) si aprono nel browser, non al posto della mappa.</summary>
    private static void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        Uri? u = e.Request;
        if (u is null || u.Scheme is "about" or "data" || string.Equals(u.Host, PageUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        OpenInBrowser(u);
    }

    private void Fail(string message)
    {
        Error = message;
        Close();
    }

    private static void OpenInBrowser(Uri? u)
    {
        if (u is not null && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
        {
            ProcessStartInfo psi = OperatingSystem.IsMacOS() ? new("open") { ArgumentList = { u.AbsoluteUri } }
                : OperatingSystem.IsLinux() ? new("xdg-open") { ArgumentList = { u.AbsoluteUri } }
                : new(u.AbsoluteUri) { UseShellExecute = true };
            try
            {
                Process.Start(psi);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AppLog.Error($"Impossibile aprire {u}", ex);
            }
        }
    }
}
