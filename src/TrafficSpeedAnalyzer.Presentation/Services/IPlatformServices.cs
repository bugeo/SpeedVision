using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.App.Services;

/// <summary>Gravità dell'avviso in alto (informazione, riuscita, attenzione, errore).</summary>
public enum StatusSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>Risposta a una domanda con «Sì», «No» ed eventualmente «Annulla».</summary>
public enum DialogAnswer
{
    Yes,
    No,
    Cancel,
}

/// <summary>Tono della domanda: una scelta normale o un avvertimento (per esempio dati che andranno persi).</summary>
public enum DialogKind
{
    Question,
    Warning,
}

/// <summary>Tipo di file proposto nelle finestre «Apri» e «Salva»: nome mostrato e estensioni, per esempio «*.mp4».</summary>
public sealed record FileFilter(string Name, IReadOnlyList<string> Patterns);

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

/// <summary>Punti indicati sulla mappa e loro distanza (calcolata con <see cref="GeoMath"/>).</summary>
public sealed record MapResult(GeoPosition A, GeoPosition B, double Meters, double PrecisionMeters, string Layer, GeoPosition ViewCenter, double ViewZoom);

/// <summary>Esito della finestra della mappa: punti confermati (o null), ultima vista, eventuale errore.</summary>
public sealed record MapOutcome(MapResult? Result, (GeoPosition Center, double Zoom, string Layer)? LastView, string? Error);

/// <summary>Esecuzione sul thread dell'interfaccia.</summary>
public interface IUiDispatcher
{
    /// <summary>Esegue l'azione sul thread dell'interfaccia.</summary>
    Task InvokeAsync(Action action);

    /// <summary>Attende che l'interfaccia abbia smaltito gli aggiornamenti già in coda.</summary>
    Task YieldAsync();
}

/// <summary>
/// Ciò che i view model chiedono al sistema e all'interfaccia: finestre di dialogo, appunti, browser, cartelle,
/// immagini dei fotogrammi e mappa. Ogni interfaccia (WPF oggi, Avalonia per Mac e Linux) ne dà la sua versione;
/// i view model restano gli stessi.
/// </summary>
public interface IPlatformServices
{
    IUiDispatcher Ui { get; }

    /// <summary>Scelta di un file da aprire; null se l'utente annulla.</summary>
    Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters, string? initialDirectory = null);

    /// <summary>Scelta del file da salvare; null se l'utente annulla.</summary>
    Task<string?> PickSaveFileAsync(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string defaultExtension, string? initialDirectory = null);

    /// <summary>Scelta di una cartella; null se l'utente annulla.</summary>
    Task<string?> PickFolderAsync(string title, string? initialDirectory = null);

    /// <summary>Domanda con «Sì» e «No» (e «Annulla» se <paramref name="allowCancel"/>).</summary>
    Task<DialogAnswer> AskAsync(string message, DialogKind kind, bool allowCancel = false);

    /// <summary>Copia il testo negli appunti; false se gli appunti sono occupati.</summary>
    bool TryCopyText(string text);

    /// <summary>Apre l'indirizzo nel browser predefinito; false se non riesce.</summary>
    bool TryOpenUrl(string url);

    /// <summary>Apre la cartella nel gestore dei file del sistema (Esplora file, Finder…).</summary>
    void OpenFolder(string path);

    /// <summary>
    /// Immagine del fotogramma da mostrare nel riquadro del video. Chiamato anche fuori dal thread
    /// dell'interfaccia: l'oggetto restituito dev'essere utilizzabile da qualunque thread.
    /// </summary>
    object CreateFrameImage(VideoFrame frame);

    /// <summary>Finestra della mappa per la calibrazione; ritorna quando l'utente la chiude.</summary>
    Task<MapOutcome> ShowMapAsync(MapRequest request);
}
