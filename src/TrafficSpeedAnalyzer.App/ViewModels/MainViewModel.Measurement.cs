using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Time;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using TrafficSpeedAnalyzer.Core.Localization;
using TrafficSpeedAnalyzer.App;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Misura manuale: l'utente indica la posizione dello stesso punto del veicolo in alcuni
/// fotogrammi prima e dopo ciascuna linea. Non è un riconoscimento automatico.
/// </summary>
public sealed partial class MainViewModel
{
    private ManualTrack _currentTrack = new();
    private PassageResult? _currentAnalysis;

    [ObservableProperty]
    private bool _autoAdvance;

    [ObservableProperty]
    private int _autoAdvanceFrames;

    [ObservableProperty]
    private IReadOnlyList<OverlayPoint> _overlayPoints = [];

    [ObservableProperty]
    private IReadOnlyList<Point2> _crossingMarkers = [];

    [ObservableProperty]
    private string _measurementHint = string.Empty;

    [ObservableProperty]
    private string _crossingAStatus = "–";

    [ObservableProperty]
    private string _crossingBStatus = "–";

    [ObservableProperty]
    private bool _crossingAOk;

    [ObservableProperty]
    private bool _crossingBOk;

    [ObservableProperty]
    private string _currentResultText = string.Empty;

    [ObservableProperty]
    private string _currentSpeedText = "–";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePassageCommand))]
    private bool _canSavePassage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoPointCommand), nameof(NewVehicleCommand))]
    private int _currentPointCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentVehicleId))]
    private int _nextId = 1;

    /// <summary>Id del passaggio in modifica (riaperto dai risultati), altrimenti null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentVehicleId), nameof(IsEditingPassage), nameof(EditingLabel))]
    private int? _editingId;

    private SavedTrack? _editBackup;

    public int CurrentVehicleId => EditingId ?? NextId;

    public bool IsEditingPassage => EditingId is not null;

    public string EditingLabel => EditingId is null ? string.Empty : "(modifica)";

    public int[] AutoAdvanceOptions { get; } = [1, 2, 3, 5];

    public bool IsPointOnCurrentFrame => _currentTrack.TryGet(CurrentIndex, out _);

    partial void OnAutoAdvanceFramesChanged(int value)
    {
        if (value < 1)
        {
            AutoAdvanceFrames = 1;
        }
        else if (value > 10)
        {
            AutoAdvanceFrames = 10;
        }
    }

    /// <summary>Clic sul video in modalità misura.</summary>
    [RelayCommand]
    private void PlacePoint(Point2? position)
    {
        if (position is not { } p || _session is null)
        {
            return;
        }

        if (Calibration is null)
        {
            SelectedStep = WorkflowStep.Calibration;
            ShowStatus(Loc.T("Calibrazione incompleta"), Loc.T("Disegna le linee A e B e inserisci la distanza prima di misurare."), InfoBarSeverity.Warning);
            return;
        }

        StopPlayback();
        var clamped = new Point2(Math.Clamp(p.X, 0, VideoWidth), Math.Clamp(p.Y, 0, VideoHeight));
        _currentTrack.Set(new TrackObservation(CurrentIndex, VideoRelative(CurrentTime), clamped));
        AnalyzeCurrentTrack();

        if (AutoAdvance)
        {
            RequestFrame(CurrentIndex + AutoAdvanceFrames);
        }
    }

    private bool HasCurrentPoints() => CurrentPointCount > 0;

    [RelayCommand(CanExecute = nameof(HasCurrentPoints))]
    private void UndoPoint()
    {
        // Prima rimuove il punto del fotogramma visualizzato, altrimenti l'ultimo inserito.
        if (!_currentTrack.Remove(CurrentIndex))
        {
            _currentTrack.RemoveLast();
        }

        AnalyzeCurrentTrack();
    }

    [RelayCommand(CanExecute = nameof(HasCurrentPoints))]
    private void NewVehicle()
    {
        if (EditingId is not null && _editBackup is not null)
        {
            // Annulla la modifica: il passaggio torna com'era.
            _savedTracks.Add(_editBackup);
            _savedTracks.Sort((x, y) => x.Id.CompareTo(y.Id));
            RebuildPassages();
        }

        EditingId = null;
        _editBackup = null;
        _currentTrack = new ManualTrack();
        AnalyzeCurrentTrack();
    }

    [RelayCommand(CanExecute = nameof(CanSavePassage))]
    private void SavePassage()
    {
        if (_currentAnalysis?.Passage is null)
        {
            return;
        }

        int id = CurrentVehicleId;
        _savedTracks.Add(new SavedTrack(id, _currentTrack.Clone()));
        _savedTracks.Sort((x, y) => x.Id.CompareTo(y.Id));
        if (EditingId is null)
        {
            NextId++;
        }

        EditingId = null;
        _editBackup = null;
        _currentTrack = new ManualTrack();
        RebuildPassages();
        AnalyzeCurrentTrack();
        ShowStatus(Loc.T("Passaggio salvato"),
            Loc.S($"Veicolo {id:0000} salvato nei risultati. Puoi misurare il prossimo veicolo."),
            InfoBarSeverity.Success);
    }

    private void AnalyzeCurrentTrack()
    {
        CurrentPointCount = _currentTrack.Count;
        IReadOnlyList<TrackObservation> obs = _currentTrack.Observations;

        _currentAnalysis = Calibration is { } cal && obs.Count >= 2
            ? PassageAnalyzer.Analyze(CurrentVehicleId, obs, cal)
            : null;

        CrossingAStatus = DescribeCrossing(_currentAnalysis?.CrossingA, out bool aOk);
        CrossingBStatus = DescribeCrossing(_currentAnalysis?.CrossingB, out bool bOk);
        CrossingAOk = aOk;
        CrossingBOk = bOk;

        if (_currentAnalysis?.Passage is { } p)
        {
            CurrentSpeedText = IsDistanceProvisional ? "—" : p.Speed.SpeedKmh.ToString("0.0", It);
            CurrentResultText = IsDistanceProvisional
                ? Loc.S($"{(p.Direction == TravelDirection.AToB ? "A → B" : "B → A")} · {p.Speed.DurationSeconds:0.000} s · velocità dopo la stima della distanza")
                : string.Create(It,
                    $"{(p.Direction == TravelDirection.AToB ? "A → B" : "B → A")} · {p.Speed.DurationSeconds:0.000} s · ± {p.Speed.UncertaintyKmh:0.0} km/h");
            CanSavePassage = true;
        }
        else
        {
            CurrentSpeedText = "–";
            // Mostra il motivo solo per le misure scartate, non mentre il veicolo deve ancora arrivare alla linea.
            bool rejected = _currentAnalysis is { } a &&
                            (a.CrossingA.Status == CrossingStatus.Rejected || a.CrossingB.Status == CrossingStatus.Rejected ||
                             (a.CrossingA.Status == CrossingStatus.Crossed && a.CrossingB.Status == CrossingStatus.Crossed));
            CurrentResultText = rejected && _currentAnalysis?.FailureReason is { } reason ? reason : string.Empty;
            CanSavePassage = false;
        }

        MeasurementHint = BuildHint(obs.Count);
        RefreshOverlay();
        OnPropertyChanged(nameof(IsPointOnCurrentFrame));
    }

    private string BuildHint(int count)
    {
        if (Calibration is null)
        {
            return Loc.T("Completa prima la calibrazione (passo 2).");
        }

        if (count == 0)
        {
            return Loc.T("Porta il video poco prima che il veicolo raggiunga la prima linea e clicca il punto di riferimento.");
        }

        if (CanSavePassage)
        {
            return Loc.T("Misura completa. Aggiungi qualche punto in più vicino alle linee per ridurre l'incertezza, poi salva.");
        }

        bool a = CrossingAOk;
        bool b = CrossingBOk;
        if (!a && !b)
        {
            return Loc.T("Avanza e clicca lo stesso punto del veicolo finché non supera la prima linea.");
        }

        return Loc.T("Bene. Prosegui fino a superare anche l'altra linea, cliccando sempre lo stesso punto del veicolo.");
    }

    private string DescribeCrossing(CrossingResult? r, out bool ok)
    {
        ok = false;
        if (r is null)
        {
            return "–";
        }

        switch (r.Status)
        {
            case CrossingStatus.Crossed when r.Estimate is { } e:
                ok = true;
                return string.Create(It,
                    $"{FormatTime(e.TimeSeconds)} · {PassageRow.Describe(e.Method)} · ± {e.UncertaintySeconds * 1000:0.#} ms");
            case CrossingStatus.Rejected:
                return Loc.T("Scartata: ") + r.Reason;
            default:
                return Loc.T("Non ancora attraversata");
        }
    }

    /// <summary>Tempo da mostrare: orario reale se disponibile, altrimenti tempo del video.</summary>
    public string FormatTime(double videoRelativeSeconds) =>
        EffectiveRecordingStart is { } start
            ? TimeFormatting.FormatClock(start, videoRelativeSeconds)
            : TimeFormatting.FormatVideoTime(videoRelativeSeconds);

    private void OnCalibrationChanged()
    {
        UpdateAnalysisRegion();
        AnalyzeCurrentTrack();
        RebuildPassages();
    }

    private void ResetMeasurementsForNewVideo()
    {
        _currentTrack = new ManualTrack();
        _savedTracks.Clear();
        ResetDistanceEstimate();
        NextId = 1;
        EditingId = null;
        _editBackup = null;
        RebuildPassages();
        AnalyzeCurrentTrack();
        _hasUnexportedChanges = false;
    }

    /// <summary>Ricostruisce i punti e i marcatori sopra il video.</summary>
    private void RefreshOverlay()
    {
        ManualTrack? track = SelectedStep switch
        {
            WorkflowStep.Measurement => _currentTrack,
            WorkflowStep.Results => SelectedPassage?.Track,
            _ => null,
        };

        if (track is null || track.Count == 0)
        {
            OverlayPoints = [];
            CrossingMarkers = [];
            OnPropertyChanged(nameof(IsPointOnCurrentFrame));
            return;
        }

        IReadOnlyList<TrackObservation> obs = track.Observations;
        OverlayPoints = obs
            .Select(o => new OverlayPoint(o.Position, o.FrameIndex == CurrentIndex, false, false))
            .ToList();

        var markers = new List<Point2>();
        if (Calibration is { } cal)
        {
            foreach (LineSegment line in new[] { cal.LineA, cal.LineB })
            {
                if (CrossingPoint(obs, line) is { } m)
                {
                    markers.Add(m);
                }
            }
        }

        CrossingMarkers = markers;
        OnPropertyChanged(nameof(IsPointOnCurrentFrame));
    }

    /// <summary>Punto stimato in cui la traiettoria indicata attraversa la linea.</summary>
    private static Point2? CrossingPoint(IReadOnlyList<TrackObservation> obs, LineSegment line)
    {
        for (int k = 1; k < obs.Count; k++)
        {
            double d0 = line.SignedDistance(obs[k - 1].Position);
            double d1 = line.SignedDistance(obs[k].Position);
            if (Math.Sign(d0) != Math.Sign(d1))
            {
                double f = Math.Abs(d0) / (Math.Abs(d0) + Math.Abs(d1));
                return Point2.Lerp(obs[k - 1].Position, obs[k].Position, f);
            }
        }

        return null;
    }

    public bool HasUnsavedMeasurements => _currentTrack.Count > 0 || (_savedTracks.Count > 0 && _hasUnexportedChanges);

    private static bool ConfirmDiscardMeasurements()
    {
        System.Windows.MessageBoxResult r = System.Windows.MessageBox.Show(
            Loc.T("Ci sono misure non esportate. Aprendo un altro video andranno perse. Continuare?"),
            AppInfo.Name,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        return r == System.Windows.MessageBoxResult.Yes;
    }
}
