using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TrafficSpeedAnalyzer.App.Localization;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Time;
using TrafficSpeedAnalyzer.Video;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using TrafficSpeedAnalyzer.Core.Localization;
using TrafficSpeedAnalyzer.App;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Stabilizzazione del video prima della calibrazione e dell'analisi. Il video stabilizzato sostituisce
/// l'originale nella sessione; il primo fotogramma è identico, quindi le linee già disegnate restano valide.
/// </summary>
public sealed partial class MainViewModel
{
    private const int KeepStabilizedVideos = 5;

    private static readonly string StabilizationCacheRoot = Path.Combine(AppSettings.DataDirectory, "stabilized");

    /// <summary>Video aperto dall'utente (l'originale, anche quando è in uso la versione stabilizzata).</summary>
    private VideoInfo? _sourceInfo;

    private FrameIndex? _sourceIndex;

    private StabilizationReport? _stabilizationReport;

    private CancellationTokenSource? _stabilizationCts;

    private Task? _stabilizationTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoTitle), nameof(ShowStabilizeButton))]
    [NotifyCanExecuteChangedFor(nameof(StabilizeCommand), nameof(UseOriginalVideoCommand), nameof(SaveStabilizedCopyCommand))]
    private bool _isStabilized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStabilizeButton))]
    [NotifyCanExecuteChangedFor(nameof(StabilizeCommand), nameof(UseOriginalVideoCommand), nameof(CancelStabilizationCommand),
        nameof(StartAnalysisCommand), nameof(RunBenchmarkCommand), nameof(OpenVideoCommand))]
    private bool _isStabilizing;

    [ObservableProperty]
    private double _stabilizationProgressValue;

    [ObservableProperty]
    private string _stabilizationStepText = string.Empty;

    [ObservableProperty]
    private string _stabilizationEtaText = string.Empty;

    [ObservableProperty]
    private string _stabilizationSummary = string.Empty;

    [ObservableProperty]
    private string _stabilizationWarning = string.Empty;

    [ObservableProperty]
    private bool _isStabilizationUnsupported;

    [ObservableProperty]
    private string _stabilizationCacheText = string.Empty;

    public bool ShowStabilizeButton => !IsStabilizing && !IsStabilized;

    /// <summary>Controlla se FFmpeg permette il sistema scelto e se ne esiste già un risultato salvato.</summary>
    private async Task RefreshStabilizationAvailabilityAsync()
    {
        if (Tools is { } tools)
        {
            IsStabilizationUnsupported = !await Task.Run(() => VideoStabilizer.IsSupportedAsync(tools, default));
        }

        if (!IsStabilized && _sourceInfo is { } source && !IsStabilizing)
        {
            StabilizationSummary = !IsStabilizationUnsupported && VideoStabilizer.TryLoadCached(StabilizationCacheRoot, source) is not null
                ? Loc.T("Versione stabilizzata già pronta: verrà riusata subito.")
                : string.Empty;
        }
    }

    /// <summary>Nome del file aperto dall'utente (non quello del video stabilizzato in cache).</summary>
    public string SourceFileName => _sourceInfo?.FileName ?? Info?.FileName ?? "video";

    public string SourceFilePath => _sourceInfo?.FilePath ?? Info?.FilePath ?? string.Empty;

    /// <summary>Chiamato quando l'utente apre un nuovo video (originale).</summary>
    private async Task OnSourceVideoOpenedAsync(VideoSession session)
    {
        _sourceInfo = session.Info;
        _sourceIndex = session.Index;
        ResetVideoGuide();
        _stabilizationReport = null;
        IsStabilized = false;
        StabilizationSummary = string.Empty;
        StabilizationWarning = string.Empty;
        StabilizationStepText = string.Empty;
        OnPropertyChanged(nameof(VideoTitle));
        RefreshStabilizationCacheText();

        // Sistema scelto disponibile? Video già stabilizzato in passato con questo sistema (si riusa all'istante)?
        await RefreshStabilizationAvailabilityAsync();
    }

    private bool CanStabilize() => HasVideo && !IsStabilized && !IsStabilizing && !IsAnalyzing && Tools is not null;

    [RelayCommand(CanExecute = nameof(CanStabilize))]
    private Task StabilizeAsync()
    {
        _stabilizationTask = StabilizeCoreAsync();
        return _stabilizationTask;
    }

    private async Task StabilizeCoreAsync()
    {
        if (Tools is not { } tools || _sourceInfo is not { } source || _sourceIndex is not { } sourceIndex || _session is null)
        {
            return;
        }

        if (!ConfirmDiscardForVersionSwitch())
        {
            return;
        }

        StopPlayback();
        _stabilizationCts = new CancellationTokenSource();
        CancellationToken ct = _stabilizationCts.Token;
        IsStabilizing = true;
        IsBusy = true;
        StabilizationSummary = string.Empty;
        StabilizationWarning = string.Empty;
        StabilizationProgressValue = 0;
        StabilizationStepText = "Preparazione…";
        StabilizationEtaText = string.Empty;
        BusyText = Loc.T("Stabilizzazione del video…");
        var clock = Stopwatch.StartNew();
        var progress = new Progress<StabilizationProgress>(p =>
        {
            StabilizationProgressValue = p.Fraction * 100;
            StabilizationStepText = Loc.S($"Passo {p.Step} di {p.StepCount} · {p.Description}");
            BusyText = string.Create(It, $"Stabilizzazione: {p.Description.ToLowerInvariant()} ({p.Fraction * 100:0}%)");
            StabilizationEtaText = p.Fraction > 0.03
                ? Loc.T("Tempo rimanente stimato: ") + TimeFormatting.FormatVideoTime(clock.Elapsed.TotalSeconds * (1 - p.Fraction) / p.Fraction)[..^4]
                : string.Empty;
        });

        try
        {
            StabilizationReport report = await Task.Run(
                () => VideoStabilizer.StabilizeAsync(tools, source, sourceIndex, StabilizationCacheRoot, progress, ct), ct);

            BusyText = Loc.T("Apertura del video stabilizzato…");
            VideoSession stabilized = await Task.Run(
                () => VideoSession.OpenAsync(tools, report.OutputPath, 1920, 1080, null, ct, recordingMetadataFrom: source), ct);
            await SwitchSessionAsync(stabilized);

            _stabilizationReport = report;
            IsStabilized = true;
            OnPropertyChanged(nameof(ShowStabilizeButton));
            StabilizeCommand.NotifyCanExecuteChanged();
            StabilizationAnswered = true;   // guida: si passa alla data di registrazione
            DescribeStabilization(report);
            AppLog.Info(string.Create(It,
                $"Video stabilizzato{(report.FromCache ? Loc.T(" (dalla cache)") : string.Empty)}: {report.OutputPath} · vibrazione max {report.ShakeBeforeMaxPx:0.0} px · " +
                $"residua max {report.ResidualMaxPx:0.0} px · {report.MisalignedFrames.Count} fotogrammi non allineati · {report.ElapsedSeconds:0} s"));

            VideoStabilizer.PruneCache(StabilizationCacheRoot, KeepStabilizedVideos, Path.GetDirectoryName(report.OutputPath)!);

            if (StabilizationWarning.Length > 0)
            {
                ShowStatus(Loc.T("Video stabilizzato, con riserva"), StabilizationWarning, InfoBarSeverity.Warning);
            }
            else
            {
                ShowStatus(Loc.T("Video stabilizzato"),
                    Loc.T("Controlla che le linee siano ancora sui riferimenti a terra."),
                    InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Stabilizzazione annullata"), Loc.T("Resta in uso il video originale."), InfoBarSeverity.Informational);
        }
        catch (Exception e) when (e is StabilizationException or VideoProbeException or IOException or UnauthorizedAccessException
            or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("Stabilizzazione non riuscita", e);
            ShowStatus(Loc.T("Stabilizzazione non riuscita"), e.Message + Loc.T(" Resta in uso il video originale."), InfoBarSeverity.Error);
            StabilizationAnswered = true;
        }
        finally
        {
            IsBusy = false;
            IsStabilizing = false;
            StabilizationStepText = string.Empty;
            StabilizationEtaText = string.Empty;
            _stabilizationCts.Dispose();
            _stabilizationCts = null;
            RefreshStabilizationCacheText();
        }
    }

    private void DescribeStabilization(StabilizationReport r)
    {
        var sb = new StringBuilder();
        sb.Append(Loc.S($"Vibrazione nell'originale: fino a {r.ShakeBeforeMaxPx:0.0} px (95% dei fotogrammi entro {r.ShakeBeforeP95Px:0.0} px)."));
        sb.Append('\n');
        sb.Append(r.MisalignedFrames.Count == 0
            ? Loc.T("Dopo la stabilizzazione tutti i fotogrammi risultano allineati al primo (scarto sotto 1,5 px).")
            : Loc.S($"Dopo la stabilizzazione: scarto massimo {r.ResidualMaxPx:0.0} px."));
        sb.Append('\n');
        sb.Append(r.FromCache
            ? Loc.T("Ripreso da una stabilizzazione precedente.")
            : Loc.S($"Tempo impiegato: {TimeFormatting.FormatVideoTime(r.ElapsedSeconds)[..^4]}."));
        if (r.ShakeBeforeMaxPx < VideoStabilizer.MisalignedThresholdPx)
        {
            sb.Append(Loc.T("\nIl video era già fermo: la stabilizzazione non serviva e puoi tornare all'originale."));
        }

        StabilizationSummary = sb.ToString();

        var warn = new List<string>();
        if (r.MisalignedFrames.Count > 0 && _session is { } s)
        {
            int first = r.MisalignedFrames[0];
            string at = first < s.Index.Count ? TimeFormatting.FormatVideoTime(VideoRelative(s.Index[first])) : "?";
            warn.Add(Loc.S($"{r.MisalignedFrames.Count} fotogrammi restano spostati di almeno 1,5 px (il primo è il n. {first + 1}, a {at}). " +
                $"Controlla i passaggi in quei tratti oppure usa il video originale."));
        }

        if (r.WeakFrames > r.FrameCount / 20)
        {
            warn.Add(Loc.S($"In {r.WeakFrames} fotogrammi c'erano pochi punti fissi di riferimento (scena uniforme o coperta dai veicoli): lì la correzione è meno sicura."));
        }

        StabilizationWarning = string.Join(" ", warn);
    }

    private bool CanCancelStabilization() => IsStabilizing;

    [RelayCommand(CanExecute = nameof(CanCancelStabilization))]
    private void CancelStabilization() => _stabilizationCts?.Cancel();

    private bool CanUseOriginal() => IsStabilized && !IsStabilizing && !IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanUseOriginal))]
    private async Task UseOriginalVideoAsync()
    {
        if (Tools is not { } tools || _sourceInfo is not { } source || !ConfirmDiscardForVersionSwitch())
        {
            return;
        }

        StopPlayback();
        IsBusy = true;
        BusyText = Loc.T("Apertura del video originale…");
        try
        {
            VideoSession original = await Task.Run(() => VideoSession.OpenAsync(tools, source.FilePath, 1920, 1080));
            await SwitchSessionAsync(original);
            IsStabilized = false;
            StabilizationSummary = Loc.T("In uso il video originale. La versione stabilizzata resta pronta.");
            StabilizationWarning = string.Empty;
            IsStatusOpen = false;
        }
        catch (Exception e) when (e is VideoProbeException or IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("Riapertura dell'originale non riuscita", e);
            ShowStatus(Loc.T("Impossibile aprire il video originale"), e.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveStabilizedCopy() => IsStabilized && _stabilizationReport is not null;

    [RelayCommand(CanExecute = nameof(CanSaveStabilizedCopy))]
    private async Task SaveStabilizedCopyAsync()
    {
        if (_stabilizationReport is not { } r)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Loc.T("Salva una copia del video stabilizzato"),
            Filter = "Video MP4|*.mp4",
            FileName = Path.GetFileNameWithoutExtension(SourceFileName) + "_stabilizzato.mp4",
            InitialDirectory = Path.GetDirectoryName(SourceFilePath) ?? string.Empty,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        BusyText = Loc.T("Copia del video stabilizzato…");
        try
        {
            string target = dialog.FileName;
            await Task.Run(() => File.Copy(r.OutputPath, target, overwrite: true));
            ShowStatus(Loc.T("Copia salvata"), target, InfoBarSeverity.Success);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus(Loc.T("Copia non riuscita"), e.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearStabilizationCache()
    {
        string? inUse = IsStabilized && _stabilizationReport is { } r ? Path.GetDirectoryName(r.OutputPath) : null;
        if (inUse is null)
        {
            VideoStabilizer.ClearCache(StabilizationCacheRoot);
        }
        else
        {
            VideoStabilizer.ClearCache(StabilizationCacheRoot, inUse);
        }
        if (!IsStabilized && StabilizationSummary.StartsWith(Loc.T("Versione stabilizzata già pronta: verrà riusata subito."), StringComparison.Ordinal))
        {
            StabilizationSummary = string.Empty;
        }

        RefreshStabilizationCacheText();
    }

    private void RefreshStabilizationCacheText()
    {
        long bytes = VideoStabilizer.CacheSizeBytes(StabilizationCacheRoot);
        StabilizationCacheText = bytes == 0
            ? string.Empty
            : Loc.S($"Spazio occupato dai video stabilizzati: {(bytes < 1L << 30 ? $"{bytes / 1048576.0:0} MB" : $"{bytes / 1073741824.0:0.0} GB")}");
    }

    /// <summary>Le posizioni misurate valgono solo per la versione del video su cui sono state prese.</summary>
    private bool ConfirmDiscardForVersionSwitch()
    {
        if (_currentTrack.Count == 0 && _savedTracks.Count == 0)
        {
            return true;
        }

        MessageBoxResult r = MessageBox.Show(
            Loc.T("I passaggi già misurati si riferiscono alla versione attuale del video e verranno rimossi " +
            "(le posizioni dei veicoli cambiano con la stabilizzazione). La calibrazione resta. Continuare?"),
            AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

    /// <summary>Sostituisce il video in uso con un'altra versione dello stesso video (stessi fotogrammi e tempi).</summary>
    private async Task SwitchSessionAsync(VideoSession newSession)
    {
        int keepIndex = Math.Clamp(CurrentIndex, 0, newSession.Index.Count - 1);
        VideoSession? old = _session;
        _session = newSession;
        if (old is not null)
        {
            await old.DisposeAsync();
        }

        Info = newSession.Info;
        FrameCount = newSession.Index.Count;
        NotifyVideoProperties();
        ResetMeasurementsForNewVideo();
        ResetCalibrationForNewVideo();
        HasCompletedAnalysis = false;   // passaggi azzerati: l'analisi va rifatta
        CurrentIndex = -1; // forza il ridisegno anche se l'indice non cambia
        await ShowFrameAsync(keepIndex);
    }

    private async Task CancelStabilizationAndWaitAsync()
    {
        _stabilizationCts?.Cancel();
        if (_stabilizationTask is { } t)
        {
            try
            {
                await t;
            }
            catch (Exception e) when (e is OperationCanceledException or StabilizationException)
            {
            }
        }
    }
}
