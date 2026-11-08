using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Testi che cambiano con il sistema operativo: nomi dei file di FFmpeg, comandi di installazione, tasti.
/// Li usa l'interfaccia multipiattaforma; l'app WPF per Windows ha gli stessi testi scritti nello XAML.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Su Mac le scorciatoie usano ⌘ al posto di Ctrl.</summary>
    public static string CommandKey => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";

    public string FfmpegMissingText =>
        OperatingSystem.IsMacOS()
            ? Loc.T("FFmpeg non è stato trovato. Scarica di nuovo SpeedVision completo, oppure installa FFmpeg con questo comando nel Terminale (serve Homebrew) e premi «Riprova».")
            : OperatingSystem.IsLinux()
                ? Loc.T("FFmpeg non è stato trovato. Installalo con questo comando nel terminale e premi «Riprova».")
                : Loc.T("Manca la cartella «ffmpeg» accanto a SpeedVision.exe. Estrai di nuovo lo zip completo, oppure installa FFmpeg con questo comando in PowerShell e premi «Riprova».");

    public string ChooseFfmpegText =>
        OperatingSystem.IsWindows() ? Loc.T("Indica dove si trova ffmpeg.exe…") : Loc.T("Indica dove si trova ffmpeg…");

    public string StabilizationUnsupportedText =>
        Loc.S($"Questo FFmpeg non può stabilizzare. Installa la versione completa: {FfmpegInstallCommand}");

    public string UndoShortcutText => CommandKey + "Z";

    public string MeasurementKeysText =>
        Loc.S($"Tasti: ← → fotogramma · Maiusc ±10 · Spazio riproduci · {CommandKey}Z annulla · Invio salva · rotellina zoom");
}
