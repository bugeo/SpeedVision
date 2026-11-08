using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Time;
using TrafficSpeedAnalyzer.Video;
using TrafficSpeedAnalyzer.Core.Localization;
using TrafficSpeedAnalyzer.App;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Stato dell'applicazione. Diviso in più file: video e riproduzione (qui), calibrazione,
/// misura manuale, risultati ed esportazione.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Cultura della lingua scelta (numeri e date).</summary>
    public static CultureInfo It => Loc.Culture;

    public static readonly string[] VideoExtensions =
        [".mp4", ".mov", ".m4v", ".avi", ".mkv", ".mts", ".m2ts", ".3gp", ".webm", ".wmv"];

    private readonly AppSettings _settings;
    private VideoSession? _session;
    private CancellationTokenSource? _playCts;
    private bool _frameLoopRunning;
    private int _requestedFrame = -1;
    private bool _suppressSliderSeek;

    private readonly IPlatformServices _platform;

    public MainViewModel(IPlatformServices platform)
    {
        _platform = platform;
        _settings = AppSettings.Load();
        HookStepProgress();
        LogReferenceCatalog();
        _autoAdvance = _settings.AutoAdvance;
        _autoAdvanceFrames = Math.Clamp(_settings.AutoAdvanceFrames, 1, 10);
        InitializeAnalysisSettings();
        InitializeSpeedLimit();
    }

    // ───────────── Stato generale ─────────────

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = string.Empty;

    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private StatusSeverity _statusSeverity = StatusSeverity.Informational;

    [ObservableProperty]
    private WorkflowStep _selectedStep = WorkflowStep.Video;

    [ObservableProperty]
    private ViewportMode _viewportMode = ViewportMode.None;

    // ───────────── FFmpeg ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFfmpegMissing), nameof(IsReady))]
    private FfmpegTools? _tools;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFfmpegMissing), nameof(IsReady))]
    private bool _ffmpegChecked;

    public bool IsFfmpegMissing => FfmpegChecked && Tools is null;

    public bool IsReady => Tools is not null;

    public string FfmpegInstallCommand => "winget install --id Gyan.FFmpeg -e";

    // ───────────── Video ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVideo), nameof(ShowEmptyState))]
    [NotifyCanExecuteChangedFor(nameof(StepFramesCommand), nameof(TogglePlayCommand), nameof(GoToStartCommand), nameof(GoToEndCommand),
        nameof(StartAnalysisCommand), nameof(RunBenchmarkCommand), nameof(StabilizeCommand))]
    private VideoInfo? _info;

    public bool HasVideo => Info is not null;

    public bool ShowEmptyState => Info is null && !IsBusy && Tools is not null;

    /// <summary>Immagine del fotogramma mostrato, preparata dall'interfaccia (<see cref="IPlatformServices.CreateFrameImage"/>).</summary>
    [ObservableProperty]
    private object? _currentFrame;

    /// <summary>Pixel del fotogramma mostrato (per l'anteprima con le linee da portare sulla mappa).</summary>
    private VideoFrame? _currentVideoFrame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameCounterText), nameof(IsBeyondFirstFrame))]
    private int _currentIndex;

    /// <summary>Il video mostra un fotogramma diverso dal primo: «Dal fotogramma corrente» si accende.</summary>
    public bool IsBeyondFirstFrame => CurrentIndex > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTimeText), nameof(CurrentClockText))]
    private double _currentTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameCounterText), nameof(SliderMaximum))]
    private int _frameCount;

    [ObservableProperty]
    private double _sliderPosition;

    [ObservableProperty]
    private bool _isPlaying;

    public double SliderMaximum => Math.Max(1, FrameCount - 1);

    public int VideoWidth => Info?.DisplayWidth ?? 1920;

    public int VideoHeight => Info?.DisplayHeight ?? 1080;

    public string CurrentTimeText => TimeFormatting.FormatVideoTime(Math.Max(0, CurrentTime - (_session?.Index.FirstTime ?? 0)));

    public string CurrentClockText => EffectiveRecordingStart is { } s
        ? TimeFormatting.FormatClock(s, VideoRelative(CurrentTime))
        : string.Empty;

    public string FrameCounterText => FrameCount > 0
        ? Loc.S($"Fotogramma {CurrentIndex + 1:N0} di {FrameCount:N0}")
        : string.Empty;

    public string VideoTitle => Info is null ? Loc.T("Nessun video") : IsStabilized ? Loc.S($"{SourceFileName} · stabilizzato") : SourceFileName;

    public string ResolutionText => Info is null ? "–" : string.Create(It, $"{Info.DisplayWidth} × {Info.DisplayHeight}");

    public string FpsText
    {
        get
        {
            if (Info is null || _session is null)
            {
                return "–";
            }

            FrameRateStats s = _session.Index.Stats;
            return s.IsVariable
                ? Loc.S($"{s.MeanFps:0.##} fps (variabile {s.MinFps:0.#}–{s.MaxFps:0.#})")
                : string.Create(It, $"{s.MeanFps:0.##} fps");
        }
    }

    public bool IsVariableFrameRate => _session?.Index.Stats.IsVariable ?? false;

    public string DurationText => _session is null
        ? "–"
        : TimeFormatting.FormatVideoTime(_session.Index.LastTime - _session.Index.FirstTime + _session.Index.IntervalAround(_session.Index.Count - 1));

    public string CodecText => Info is null ? "–" : (Info.CodecName.ToUpperInvariant() + (Info.PixelFormat is { } pf ? $" · {pf}" : string.Empty));

    public string FileSizeText => Info is null
        ? "–"
        : Info.FileSizeBytes < 1024 * 1024
            ? string.Create(It, $"{Info.FileSizeBytes / 1024.0:0.#} KB")
            : string.Create(It, $"{Info.FileSizeBytes / 1024.0 / 1024.0:0.0} MB");

    public string RotationText => Info is null || Info.Rotation == 0 ? Loc.T("nessuna") : $"{Info.Rotation}°";

    public double VideoRelative(double absoluteTime) => absoluteTime - (_session?.Index.FirstTime ?? 0);

    // ───────────── Avvio ─────────────

    [RelayCommand]
    private async Task InitializeAsync()
    {
        await LocateFfmpegAsync();
    }

    private async Task LocateFfmpegAsync()
    {
        try
        {
            IsBusy = true;
            BusyText = Loc.T("Ricerca di FFmpeg…");
            Tools = await FfmpegLocator.LocateAsync(_settings.FfmpegDirectory);
            if (Tools is not null)
            {
                AppLog.Info("FFmpeg: " + Tools.FfmpegPath + " — " + Tools.Version);
            }
        }
        finally
        {
            FfmpegChecked = true;
            IsBusy = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    [RelayCommand]
    private async Task RetryFfmpegAsync() => await LocateFfmpegAsync();

    [RelayCommand]
    private async Task ChooseFfmpegAsync()
    {
        string ffmpeg = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string? file = await _platform.PickOpenFileAsync(
            Loc.S($"Seleziona {ffmpeg}"),
            OperatingSystem.IsWindows() ? [new FileFilter("ffmpeg.exe", ["ffmpeg.exe"]), new FileFilter(Loc.T("Eseguibili"), ["*.exe"])] : []);
        if (file is null)
        {
            return;
        }

        _settings.FfmpegDirectory = Path.GetDirectoryName(file);
        _settings.Save();
        await LocateFfmpegAsync();
        if (Tools is null)
        {
            ShowStatus(Loc.T("FFmpeg non utilizzabile"), Loc.T("Nella stessa cartella devono esserci ffmpeg.exe e ffprobe.exe funzionanti."), StatusSeverity.Error);
        }
    }

    [RelayCommand]
    private void CopyInstallCommand()
    {
        // Se gli appunti sono occupati da un'altra applicazione non succede nulla: si può ripremere.
        if (_platform.TryCopyText(FfmpegInstallCommand))
        {
            ShowStatus(Loc.T("Comando copiato"), Loc.T("Incollalo in un terminale (PowerShell), poi premi «Riprova»."), StatusSeverity.Success);
        }
    }

    // ───────────── Apertura video ─────────────

    [RelayCommand]
    private async Task OpenVideoAsync()
    {
        string? file = await _platform.PickOpenFileAsync(
            Loc.T("Apri un video di traffico"),
            [new FileFilter("Video", [.. VideoExtensions.Select(e => "*" + e)]), new FileFilter(Loc.T("Tutti i file"), ["*.*"])],
            Directory.Exists(_settings.LastVideoFolder) ? _settings.LastVideoFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        if (file is not null)
        {
            await OpenVideoFileAsync(file);
        }
    }

    public async Task OpenVideoFileAsync(string path)
    {
        if (Tools is null)
        {
            ShowStatus(Loc.T("FFmpeg mancante"), Loc.T("Installa FFmpeg prima di aprire un video."), StatusSeverity.Warning);
            return;
        }

        if (IsAnalyzing)
        {
            ShowStatus(Loc.T("Analisi in corso"), Loc.T("Interrompi l'analisi velocità prima di aprire un altro video."), StatusSeverity.Warning);
            return;
        }

        if (IsStabilizing)
        {
            ShowStatus(Loc.T("Stabilizzazione in corso"), Loc.T("Annulla la stabilizzazione prima di aprire un altro video."), StatusSeverity.Warning);
            return;
        }

        if (HasUnsavedMeasurements && !await ConfirmDiscardMeasurementsAsync())
        {
            return;
        }

        // Guida: con le linee già posizionate si chiede se tenerle (stesso punto di ripresa) o ripartire da capo.
        bool keepCalibration = false;
        if (LineA is not null || LineB is not null)
        {
            DialogAnswer keep = await _platform.AskAsync(
                Loc.T("Vuoi mantenere la posizione delle linee A e B e la distanza anche per il nuovo video?\n\n" +
                "Sì: stesse linee (il telefono è rimasto nello stesso punto).\n" +
                "No: si riparte da capo, con una nuova calibrazione."),
                DialogKind.Question);
            keepCalibration = keep == DialogAnswer.Yes;
        }

        StopPlayback();
        IsBusy = true;
        OnPropertyChanged(nameof(ShowEmptyState));
        var progress = new Progress<string>(s => BusyText = s);
        VideoSession? newSession = null;
        try
        {
            newSession = await Task.Run(() => VideoSession.OpenAsync(Tools, path, 1920, 1080, progress));
            VideoSession? old = _session;
            _session = newSession;
            if (old is not null)
            {
                await old.DisposeAsync();
            }

            _settings.LastVideoFolder = Path.GetDirectoryName(path);
            _settings.Save();

            ResetForNewVideo(newSession, keepCalibration);
            await OnSourceVideoOpenedAsync(newSession);
            AppLog.Info($"Video aperto: {path} ({newSession.Index.Count} fotogrammi)");
            IsStatusOpen = false;
            await ShowFrameAsync(0);
            if (newSession.Index.Stats.IsVariable)
            {
                ShowStatus(Loc.T("Frame rate variabile"),
                    Loc.T("Nessun problema: ogni misura usa il tempo reale di ciascun fotogramma."),
                    StatusSeverity.Informational);
            }
        }
        catch (Exception e) when (e is VideoProbeException or IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("Apertura video non riuscita: " + path, e);
            if (newSession is not null && newSession != _session)
            {
                await newSession.DisposeAsync();
            }

            ShowStatus(Loc.T("Impossibile aprire il video"), e.Message, StatusSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private void ResetForNewVideo(VideoSession session, bool keepCalibration)
    {
        if (keepCalibration)
        {
            KeepCalibrationForNewVideo();
        }
        else
        {
            ResetEverythingForNewVideo();
        }

        Info = session.Info;
        FrameCount = session.Index.Count;
        _suppressSliderSeek = true;
        SliderPosition = 0;
        _suppressSliderSeek = false;
        CurrentIndex = 0;
        CurrentTime = session.Index.FirstTime;
        _requestedFrame = -1;

        RecordingStartText = session.Info.RecordingStart is { } rs ? rs.ToString(RecordingStartFormat, CultureInfo.InvariantCulture) : string.Empty;
        NotifyVideoProperties();
        ResetRoadForNewVideo(keepCalibration);

        ResetCalibrationForNewVideo();
        ResetMeasurementsForNewVideo();
        SelectedStep = WorkflowStep.Video;
    }

    private void NotifyVideoProperties()
    {
        OnPropertyChanged(nameof(VideoWidth));
        OnPropertyChanged(nameof(VideoHeight));
        OnPropertyChanged(nameof(VideoTitle));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(FpsText));
        OnPropertyChanged(nameof(IsVariableFrameRate));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(CodecText));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(RotationText));
        OnPropertyChanged(nameof(RecordingStartSourceText));
        OnPropertyChanged(nameof(SourceFileName));
        OnPropertyChanged(nameof(IsRoadLanesStepActive));
    }

    // ───────────── Navigazione tra i fotogrammi ─────────────

    partial void OnSliderPositionChanged(double value)
    {
        if (_suppressSliderSeek || _session is null)
        {
            return;
        }

        StopPlayback();
        RequestFrame((int)Math.Round(value));
    }

    private bool CanNavigate() => HasVideo && !IsAnalyzing;

    /// <param name="delta">Numero di fotogrammi (testo, es. "-10"), per poterlo indicare dallo XAML.</param>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void StepFrames(string? delta)
    {
        if (!int.TryParse(delta, NumberStyles.Integer, CultureInfo.InvariantCulture, out int step))
        {
            return;
        }

        StopPlayback();
        int baseIndex = _requestedFrame >= 0 ? _requestedFrame : CurrentIndex;
        RequestFrame(baseIndex + step);
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void GoToStart()
    {
        StopPlayback();
        RequestFrame(0);
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void GoToEnd()
    {
        StopPlayback();
        RequestFrame(FrameCount - 1);
    }

    public void GoToFrame(int index)
    {
        StopPlayback();
        RequestFrame(index);
    }

    /// <summary>
    /// Richiede un fotogramma. Le richieste ravvicinate (trascinamento del cursore) vengono
    /// accorpate: si mostra sempre l'ultimo fotogramma richiesto.
    /// </summary>
    private void RequestFrame(int index)
    {
        if (_session is null)
        {
            return;
        }

        _requestedFrame = Math.Clamp(index, 0, _session.Index.Count - 1);
        if (!_frameLoopRunning)
        {
            _ = FrameLoopAsync();
        }
    }

    private async Task FrameLoopAsync()
    {
        _frameLoopRunning = true;
        try
        {
            while (_session is not null && _requestedFrame >= 0 && _requestedFrame != CurrentIndex)
            {
                int target = _requestedFrame;
                await ShowFrameAsync(target);
                if (_requestedFrame == target)
                {
                    break;
                }
            }
        }
        finally
        {
            _frameLoopRunning = false;
            _requestedFrame = -1;
        }
    }

    private async Task ShowFrameAsync(int index, CancellationToken ct = default)
    {
        VideoSession? session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            (VideoFrame frame, object image) = await Task.Run(async () =>
            {
                VideoFrame f = await session.Preview.GetFrameAsync(index, ct);
                return (f, _platform.CreateFrameImage(f));
            }, ct);

            if (session != _session)
            {
                return; // nel frattempo è stato aperto un altro video
            }

            _currentVideoFrame = frame;
            CurrentFrame = image;
            CurrentIndex = frame.Index;
            CurrentTime = frame.TimeSeconds;
            _suppressSliderSeek = true;
            SliderPosition = frame.Index;
            _suppressSliderSeek = false;
            OnFrameChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is VideoProbeException or IOException or InvalidOperationException)
        {
            AppLog.Error($"Lettura del fotogramma {index} non riuscita", e);
            ShowStatus(Loc.T("Errore di lettura"), e.Message, StatusSeverity.Error);
        }
    }

    // ───────────── Riproduzione ─────────────

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void TogglePlay()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }

        if (_session is null)
        {
            return;
        }

        int first = CurrentIndex >= FrameCount - 1 ? 0 : CurrentIndex + 1;
        _playCts = new CancellationTokenSource();
        IsPlaying = true;
        _ = PlayLoopAsync(first, _playCts.Token);
    }

    public void StopPlayback()
    {
        if (_playCts is not null)
        {
            _playCts.Cancel();
            _playCts.Dispose();
            _playCts = null;
        }

        IsPlaying = false;
    }

    private async Task PlayLoopAsync(int firstIndex, CancellationToken ct)
    {
        VideoSession? session = _session;
        if (session is null)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        double startTime = session.Index[firstIndex];
        try
        {
            for (int i = firstIndex; i < session.Index.Count; i++)
            {
                (VideoFrame frame, object image) = await Task.Run(async () =>
                {
                    VideoFrame f = await session.Preview.GetFrameAsync(i, ct);
                    return (f, _platform.CreateFrameImage(f));
                }, ct);

                double wait = (frame.TimeSeconds - startTime) - clock.Elapsed.TotalSeconds;
                if (wait > 0.001)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                }

                if (ct.IsCancellationRequested || session != _session)
                {
                    return;
                }

                _currentVideoFrame = frame;
                CurrentFrame = image;
                CurrentIndex = frame.Index;
                CurrentTime = frame.TimeSeconds;
                _suppressSliderSeek = true;
                SliderPosition = frame.Index;
                _suppressSliderSeek = false;
                OnFrameChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is VideoProbeException or IOException or InvalidOperationException)
        {
            AppLog.Error("Riproduzione interrotta", e);
            ShowStatus(Loc.T("Riproduzione interrotta"), e.Message, StatusSeverity.Error);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsPlaying = false;
            }
        }
    }

    /// <summary>Aggiorna ciò che dipende dal fotogramma corrente (punti sovrapposti ecc.).</summary>
    private void OnFrameChanged()
    {
        RefreshOverlay();
    }

    // ───────────── Messaggi ─────────────

    /// <summary>Passo in cui è stato mostrato l'avviso in alto: cambiando passo l'avviso si chiude.</summary>
    private WorkflowStep _statusStep;

    public void ShowStatus(string title, string message, StatusSeverity severity)
    {
        _statusStep = SelectedStep;
        StatusTitle = title;
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        ExportResultsCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedStepChanged(WorkflowStep value)
    {
        RefreshVanishingState();   // punti di riferimento visibili solo nella calibrazione
        EnsureRoadForEditing();    // passo «Strada»: la zona verde compare subito, da portare sui bordi

        // Gli avvisi riguardano il passo in cui sono comparsi (es. «Analisi completata… premi Procedi»):
        // negli altri passi sarebbero solo d'intralcio e indicherebbero un'azione non più valida.
        if (IsStatusOpen && _statusStep != value)
        {
            IsStatusOpen = false;
        }

        // Esce dalle modalità di disegno quando si cambia passo.
        ViewportMode = value switch
        {
            WorkflowStep.Measurement => ViewportMode.PlacePoint,
            WorkflowStep.Calibration => NextCalibrationMode(),
            _ => ViewportMode.None,
        };
        OnPropertyChanged(nameof(ShowAnalysisRegion));
        StopPlayback();
        if (value == WorkflowStep.Calibration && _shot is null && HasTrackedVehicles)
        {
            RefreshShotAssessment();   // tracciati presenti ma giudizio non ancora fatto
        }

        RefreshOverlay();
        RefreshReferenceOverlay();
    }

    public async ValueTask DisposeAsync()
    {
        StopPlayback();
        await CancelStabilizationAndWaitAsync();
        await StopAnalysisAndWaitAsync();
        _settings.AutoAdvance = AutoAdvance;
        _settings.AutoAdvanceFrames = AutoAdvanceFrames;
        SaveAnalysisSettings();
        _settings.Save();
        DisposeDetector();
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }
}
