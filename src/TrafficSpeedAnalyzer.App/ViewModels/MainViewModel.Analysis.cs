using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.App.Localization;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Time;
using TrafficSpeedAnalyzer.Core.Tracking;
using TrafficSpeedAnalyzer.Detection;
using TrafficSpeedAnalyzer.Video;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using TrafficSpeedAnalyzer.Core.Localization;
using TrafficSpeedAnalyzer.App;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Analisi automatica: rilevamento dei veicoli (YOLOX) e tracking su tutta l'immagine, prima della
/// calibrazione. Si conservano tutti i tracciati; le velocità si calcolano quando le linee sono note,
/// e si ricalcolano a ogni modifica della calibrazione senza rifare l'analisi.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly ModelManager _models = new(Path.Combine(AppSettings.DataDirectory, "models"));
    private YoloxDetector? _detector;
    private string? _detectorKey;
    private CancellationTokenSource? _analysisCts;
    private Task? _analysisTask;

    public IReadOnlyList<ModelInfo> Models => ModelCatalog.All;

    public IReadOnlyList<LocText> AccelerationOptions { get; } = [LocText.For("Automatica (scheda grafica se disponibile)"), LocText.For("Solo processore (CPU)")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelStatusText), nameof(IsModelAvailable), nameof(ModelDescription))]
    private ModelInfo _selectedModel = ModelCatalog.Default;

    [ObservableProperty]
    private int _accelerationIndex;

    [ObservableProperty]
    private bool _regionAroundLines = true;

    [ObservableProperty]
    private bool _skipStaticFrames = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartScoreText))]
    private double _startScore = 0.5;

    [ObservableProperty]
    private bool _analyzeFromCurrentFrame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotAnalyzing), nameof(ShowAnalysisProceed))]
    [NotifyCanExecuteChangedFor(nameof(StartAnalysisCommand), nameof(StopAnalysisCommand), nameof(RunBenchmarkCommand),
        nameof(DownloadModelCommand), nameof(StepFramesCommand), nameof(TogglePlayCommand), nameof(GoToStartCommand),
        nameof(GoToEndCommand), nameof(ExportResultsCommand), nameof(StabilizeCommand), nameof(UseOriginalVideoCommand))]
    private bool _isAnalyzing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAnalysisCommand), nameof(RunBenchmarkCommand), nameof(DownloadModelCommand))]
    private bool _isDownloadingModel;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private double _analysisProgressValue;

    [ObservableProperty]
    private string _analysisStatusText = string.Empty;

    [ObservableProperty]
    private string _analysisSpeedText = string.Empty;

    [ObservableProperty]
    private string _analysisCountsText = string.Empty;

    [ObservableProperty]
    private string _executionProviderText = string.Empty;

    [ObservableProperty]
    private string _benchmarkText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<OverlayBox> _overlayBoxes = [];

    [ObservableProperty]
    private BoundingBox? _analysisRegionBox;

    public bool IsNotAnalyzing => !IsAnalyzing;

    /// <summary>Ci sono veicoli tracciati dall'analisi (servono per la calibrazione con l'auto e per le velocità automatiche).</summary>
    public bool HasTrackedVehicles => _savedTracks.Any(t => t.Source == PassageSource.Automatic);

    /// <summary>«Procedi» verso la calibrazione: analisi fatta (anche interrotta, se ha tracciato qualcosa).</summary>
    public bool ShowAnalysisProceed => !IsAnalyzing && (HasCompletedAnalysis || HasTrackedVehicles);

    public bool IsModelAvailable => _models.IsAvailable(SelectedModel);

    public string ModelDescription => SelectedModel.Description;

    public string ModelStatusText => IsModelAvailable
        ? Loc.T("Modello presente sul PC.")
        : Loc.S($"Da scaricare una sola volta ({SelectedModel.SizeText}, da GitHub). I video non vengono inviati.");

    public string StartScoreText => StartScore.ToString("0.00", It);

    /// <summary>Rettangolo della zona analizzata, mostrato nel passo «Analisi».</summary>
    public bool ShowAnalysisRegion => SelectedStep == WorkflowStep.Analysis || IsAnalyzing;

    public string? LastModelDescription { get; private set; }

    private void InitializeAnalysisSettings()
    {
        _selectedModel = ModelCatalog.Find(_settings.ModelId) ?? ModelCatalog.Default;
        _accelerationIndex = _settings.CpuOnly ? 1 : 0;
        _regionAroundLines = _settings.RegionAroundLines;
        _skipStaticFrames = _settings.SkipStaticFrames;
        _startScore = Math.Clamp(_settings.StartScore, 0.25, 0.8);
    }

    private void SaveAnalysisSettings()
    {
        _settings.ModelId = SelectedModel.Id;
        _settings.CpuOnly = AccelerationIndex == 1;
        _settings.RegionAroundLines = RegionAroundLines;
        _settings.SkipStaticFrames = SkipStaticFrames;
        _settings.StartScore = StartScore;
    }

    partial void OnRegionAroundLinesChanged(bool value) => UpdateAnalysisRegion();

    /// <summary>Zona analizzata: attorno alla strada indicata dall'utente, altrimenti tutta l'immagine (nessuna zona da mostrare).</summary>
    private void UpdateAnalysisRegion() => AnalysisRegionBox = Road?.AnalysisRegion(VideoWidth, VideoHeight);

    // ───────────── Modello ─────────────

    private bool CanDownload() => !IsDownloadingModel && !IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadModelAsync() => await EnsureModelAsync();

    private async Task<string?> EnsureModelAsync()
    {
        if (_models.FindLocal(SelectedModel) is { } path)
        {
            return path;
        }

        IsDownloadingModel = true;
        DownloadProgress = 0;
        try
        {
            // Il riquadro «Modello» può essere chiuso: l'avanzamento si vede anche nel riquadro di stato.
            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p * 100;
                AnalysisProgressValue = p * 100;
                AnalysisStatusText = Loc.S($"Scaricamento del modello… {p * 100:0}%");
            });
            path = await _models.EnsureAsync(SelectedModel, progress);
            AppLog.Info("Modello scaricato: " + path);
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            AppLog.Error("Download del modello non riuscito", e);
            ShowStatus(Loc.T("Download del modello non riuscito"),
                e.Message + Loc.T(" Verifica la connessione, oppure copia il file ") + SelectedModel.FileName + " in " + _models.ModelsDirectory + ".",
                InfoBarSeverity.Error);
            return null;
        }
        finally
        {
            IsDownloadingModel = false;
            OnPropertyChanged(nameof(IsModelAvailable));
            OnPropertyChanged(nameof(ModelStatusText));
        }
    }

    private async Task<YoloxDetector?> GetDetectorAsync()
    {
        string? path = await EnsureModelAsync();
        if (path is null)
        {
            return null;
        }

        var accel = AccelerationIndex == 1 ? AccelerationMode.CpuOnly : AccelerationMode.Auto;
        string key = SelectedModel.Id + "|" + accel;
        if (_detector is not null && _detectorKey == key)
        {
            return _detector;
        }

        DisposeDetector();
        AnalysisStatusText = Loc.T("Caricamento del modello…");
        ModelInfo model = SelectedModel;
        _detector = await Task.Run(() => new YoloxDetector(path, model.DisplayName, DetectorOptions.Default with { Acceleration = accel }));
        _detectorKey = key;
        ExecutionProviderText = Loc.T("Esecuzione su ") + _detector.ExecutionProvider;
        AppLog.Info($"Modello {model.Id} caricato su {_detector.ExecutionProvider}");
        return _detector;
    }

    private void DisposeDetector()
    {
        _detector?.Dispose();
        _detector = null;
        _detectorKey = null;
    }

    partial void OnSelectedModelChanged(ModelInfo value) => BenchmarkText = string.Empty;

    partial void OnAccelerationIndexChanged(int value) => BenchmarkText = string.Empty;

    // ───────────── Prova delle prestazioni ─────────────

    private bool CanAnalyze() => HasVideo && !IsAnalyzing && !IsDownloadingModel && !IsStabilizing;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task RunBenchmarkAsync()
    {
        if (_session is null)
        {
            return;
        }

        StopPlayback();
        IsAnalyzing = true;
        try
        {
            YoloxDetector? detector = await GetDetectorAsync();
            if (detector is null)
            {
                return;
            }

            AnalysisStatusText = Loc.T("Prova delle prestazioni su 90 fotogrammi…");
            BenchmarkResult b = await AnalysisPipeline.BenchmarkAsync(_session, detector, Calibration, BuildOptions(), 90);
            BenchmarkText = Loc.S($"{b.ModelName} su {b.ExecutionProvider}: {b.MeanDetectMilliseconds:0} ms per fotogramma (95% entro {b.P95DetectMilliseconds:0} ms), " +
                $"decodifica {b.MeanDecodeMilliseconds:0} ms. Circa {b.FramesPerSecond:0.#} fotogrammi/s: il video intero richiede al massimo " +
                $"{TimeFormatting.FormatVideoTime(b.EstimatedFullAnalysis.TotalSeconds)} (meno se salti i tratti vuoti).");
            AnalysisStatusText = string.Empty;
            AppLog.Info("Prestazioni: " + BenchmarkText);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Error("Prova delle prestazioni non riuscita", e);
            ShowStatus(Loc.T("Prova non riuscita"), e.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    // ───────────── Analisi ─────────────

    private AnalysisOptions BuildOptions() => new()
    {
        RegionAroundLines = false,
        Region = Road?.AnalysisRegion(VideoWidth, VideoHeight),
        CollectAllTracks = true,
        SkipStaticFrames = SkipStaticFrames,
        StartScore = (float)StartScore,
        StartIndex = AnalyzeFromCurrentFrame ? CurrentIndex : 0,
    };

    private bool CanStartAnalysis() => CanAnalyze();

    [RelayCommand(CanExecute = nameof(CanStartAnalysis))]
    /// <param name="from">"current" = dal fotogramma visualizzato; altrimenti tutto il video.</param>
    private async Task StartAnalysisAsync(string? from)
    {
        AnalyzeFromCurrentFrame = from == "current";
        if (_session is null)
        {
            return;
        }

        if (_savedTracks.Any(t => t.Source == PassageSource.Automatic))
        {
            MessageBoxResult answer = MessageBox.Show(
                Loc.T("Ci sono già risultati automatici. Vuoi sostituirli con quelli della nuova analisi?\n\n" +
                "Sì: elimina i precedenti automatici · No: li conserva e aggiunge i nuovi."),
                AppInfo.Name, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                _savedTracks.RemoveAll(t => t.Source == PassageSource.Automatic);
                RebuildPassages();
            }
        }

        StopPlayback();
        _analysisCts = new CancellationTokenSource();
        IsAnalyzing = true;
        OnPropertyChanged(nameof(ShowAnalysisRegion));
        IsStatusOpen = false;
        AnalysisProgressValue = 0;
        AnalysisSpeedText = string.Empty;
        AnalysisCountsText = string.Empty;
        var tcs = new TaskCompletionSource();
        _analysisTask = tcs.Task;
        try
        {
            YoloxDetector? detector = await GetDetectorAsync();
            if (detector is null)
            {
                return;
            }

            LastModelDescription = $"{detector.ModelName}, {detector.ExecutionProvider}";
            VideoSession session = _session;
            AnalysisOptions options = BuildOptions();
            AnalysisStatusText = "Avvio…";
            Dispatcher ui = Application.Current.Dispatcher;
            int added = 0;

            var progress = new Progress<AnalysisProgress>(p =>
            {
                AnalysisProgressValue = 100.0 * p.FramesDone / Math.Max(1, p.TotalFrames);
                AnalysisStatusText = Loc.S($"Fotogramma {p.FrameIndex + 1:N0} di {FrameCount:N0}");
                AnalysisSpeedText = string.Create(It, $"{p.FramesPerSecond:0.#} fotogrammi/s") +
                    (p.Remaining is { } r ? " · rimangono " + TimeFormatting.FormatVideoTime(r.TotalSeconds) : string.Empty);
                AnalysisCountsText = Loc.S($"{VehiclesText(p.Passages)} · {p.RejectedTracks} tracce scartate · {p.FramesSkipped:N0} fotogrammi senza movimento saltati");
            });

            // Prima posizione di ogni veicolo: dal suo spostamento si sa quale vertice basso è sul davanti.
            var firstCenterX = new Dictionary<int, double>();

            void OnPreview(AnalysisPreview pv)
            {
                BitmapSource bmp = BitmapSource.Create(pv.Frame.Width, pv.Frame.Height, 96, 96, PixelFormats.Bgr24, null, pv.Frame.Pixels, pv.Frame.Stride);
                bmp.Freeze();
                int Heading(TrackedVehicle v)
                {
                    BoundingBox b = v.Detection.Box;
                    double dx = b.CenterX - (firstCenterX.TryGetValue(v.TrackId, out double x0) ? x0 : firstCenterX[v.TrackId] = b.CenterX);
                    return Math.Abs(dx) < 0.25 * b.Width ? 0 : Math.Sign(dx);
                }

                var boxes = pv.Vehicles
                    .Select(v => new OverlayBox(v.Detection.Box, $"#{v.TrackId} {PassageRow.ClassName(v.Detection.Class)} {v.Detection.Score:0.00}", v.IsConfirmed, Heading(v)))
                    .ToList();
                ui.InvokeAsync(() =>
                {
                    if (!IsAnalyzing)
                    {
                        return;
                    }

                    CurrentFrame = bmp;
                    CurrentIndex = pv.Frame.Index;
                    CurrentTime = pv.Frame.TimeSeconds;
                    SetSliderSilently(pv.Frame.Index);
                    OverlayBoxes = boxes;
                });
            }

            int offRoad = 0;
            void OnPassage(TrackPassageCandidate c)
            {
                // Solo i veicoli sulla strada (o appena fuori); ognuno con la sua corsia.
                int lane = LaneOf(c.Observations.Select(o => o.Position).ToList());
                if (lane < 0)
                {
                    Interlocked.Increment(ref offRoad);
                    return;
                }

                var track = new ManualTrack();
                foreach (TrackObservation o in c.Observations)
                {
                    track.Set(o);
                }

                ui.InvokeAsync(() =>
                {
                    // Non tutti i tracciati attraverseranno le linee: quelli che non lo fanno non sono errori.
                    _savedTracks.Add(new SavedTrack(NextId++, track, PassageSource.Automatic, c.Class, c.MeanScore, c.Track.Points, AwaitingCalibration: true, Lane: lane, Reference: c.Reference));
                    added++;
                    RebuildPassages();
                });
            }

            AnalysisSummary summary = await AnalysisPipeline.RunAsync(
                session, detector, null, options, OnPassage, progress, OnPreview, _analysisCts.Token);

            // Lascia arrivare gli ultimi aggiornamenti in coda all'interfaccia.
            await ui.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            string time = TimeFormatting.FormatVideoTime(summary.Elapsed.TotalSeconds);
            string stats = Loc.S($"{VehiclesText(summary.Passages - offRoad)} in {time} ({summary.FramesProcessed / Math.Max(1e-3, summary.Elapsed.TotalSeconds):0.#} fotogrammi/s, {summary.ExecutionProvider}). Tracce scartate: {summary.RejectedTracks}.");
            if (Road is not null)
            {
                stats += Loc.S($" Fuori dalla strada: {offRoad}.");
            }
            if (summary.RejectionReasons.Count > 0)
            {
                stats += Loc.T(" Motivi: ") + string.Join("; ", summary.RejectionReasons
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .Select(kv => string.Create(It, $"{kv.Key.TrimEnd('.')} ({kv.Value})"))) + ".";
            }

            AppLog.Info("Analisi: " + stats);
            ShowStatus(summary.Cancelled ? Loc.T("Analisi interrotta") : Loc.T("Analisi completata"),
                (summary.Cancelled ? Loc.T("I veicoli tracciati fino all'interruzione sono conservati. ") : string.Empty) + stats +
                (summary.Passages > 0 ? Loc.T(" Ora premi «Procedi»: con la calibrazione si calcolano le velocità.") : string.Empty),
                summary.Cancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            AnalysisStatusText = summary.Cancelled ? Loc.T("Interrotta") : Loc.T("Completata");
            HasCompletedAnalysis = !summary.Cancelled || summary.Passages > 0;   // guida: si spengono i pulsanti di avvio, resta «Procedi»
            OnPropertyChanged(nameof(HasTrackedVehicles));
            OnPropertyChanged(nameof(ShowAnalysisProceed));

            // Giudizio sulla ripresa e prospettiva per la calibrazione con l'auto (dipendono dai tracciati).
            RefreshShotAssessment();
            if (EstimateDistanceFromVehicle && _reference is null)
            {
                StartReferenceIdentification();
            }

            if (!summary.Cancelled)
            {
                AnalysisProgressValue = 100;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Error("Analisi automatica non riuscita", e);
            ShowStatus(Loc.T("Analisi non riuscita"), e.Message, InfoBarSeverity.Error);
            AnalysisStatusText = Loc.T("Errore");
        }
        finally
        {
            int last = CurrentIndex;
            IsAnalyzing = false;
            OverlayBoxes = [];
            OnPropertyChanged(nameof(ShowAnalysisRegion));
            _analysisCts?.Dispose();
            _analysisCts = null;
            StopAnalysisCommand.NotifyCanExecuteChanged();
            tcs.TrySetResult();

            // Ricarica il fotogramma corrente alla risoluzione dell'anteprima.
            _requestedFrame = -1;
            CurrentIndex = -1;
            RequestFrame(last);

            // Stima della distanza: mostra l'auto da identificare (fotogramma e riquadro).
            RefreshReferenceOverlay();
        }
    }

    private static string VehiclesText(int n) => n == 1 ? Loc.T("1 veicolo tracciato") : Loc.S($"{n} veicoli tracciati");

    private bool CanStop() => IsAnalyzing && _analysisCts is not null;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopAnalysis() => _analysisCts?.Cancel();

    private async Task StopAnalysisAndWaitAsync()
    {
        _analysisCts?.Cancel();
        if (_analysisTask is { } t)
        {
            await Task.WhenAny(t, Task.Delay(5000));
        }
    }

    private void SetSliderSilently(int index)
    {
        _suppressSliderSeek = true;
        SliderPosition = index;
        _suppressSliderSeek = false;
    }
}
