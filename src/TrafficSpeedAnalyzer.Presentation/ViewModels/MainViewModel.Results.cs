using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Export;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Privacy;
using TrafficSpeedAnalyzer.Core.Time;
using TrafficSpeedAnalyzer.Video;
using TrafficSpeedAnalyzer.Core.Localization;
using TrafficSpeedAnalyzer.App;

namespace TrafficSpeedAnalyzer.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly List<SavedTrack> _savedTracks = [];
    private bool _hasUnexportedChanges;
    private string? _lastExportFolder;

    /// <summary>Passaggi mostrati ed esportati (dopo il filtro).</summary>
    public ObservableCollection<PassageRow> Passages { get; } = [];

    /// <summary>Tutti i passaggi corretti con la calibrazione attuale, prima del filtro.</summary>
    private readonly List<PassageRow> _allRows = [];

    /// <summary>
    /// Passaggi da correggere (dopo il filtro): misurati, ma con la ruota anteriore non seguita bene vicino alle
    /// linee, oppure spostati qui dall'utente. Non entrano in statistiche, grafico, esportazione e PDF.
    /// </summary>
    public ObservableCollection<PassageRow> ReviewPassages { get; } = [];

    /// <summary>Tutti i passaggi da correggere, prima del filtro.</summary>
    private readonly List<PassageRow> _reviewRows = [];

    /// <summary>
    /// Veicoli scartati (dopo il filtro): seguiti dall'analisi ma senza un passaggio valido tra le linee, oppure spostati
    /// qui dall'utente con «Elimina». Si gestiscono come quelli da correggere; non entrano nell'esportazione.
    /// </summary>
    public ObservableCollection<PassageRow> DiscardedPassages { get; } = [];

    /// <summary>Tutti i veicoli scartati, prima del filtro.</summary>
    private readonly List<PassageRow> _discardedRows = [];

    /// <summary>Linguetta scelta nei risultati.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownPassages), nameof(IsValidTab), nameof(IsReviewTab), nameof(IsDiscardedTab), nameof(IsNotValidTab),
        nameof(PassageCountText), nameof(ShowNoPassageHint), nameof(ShowSortButtons), nameof(DeleteTooltip), nameof(TabCaption), nameof(ShowTabCaption))]
    private ResultsTab _resultsTab;

    public bool IsValidTab
    {
        get => ResultsTab == ResultsTab.Valid;
        set
        {
            if (value)
            {
                ResultsTab = ResultsTab.Valid;
            }
        }
    }

    public bool IsReviewTab
    {
        get => ResultsTab == ResultsTab.Review;
        set
        {
            if (value)
            {
                ResultsTab = ResultsTab.Review;
            }
        }
    }

    public bool IsDiscardedTab
    {
        get => ResultsTab == ResultsTab.Discarded;
        set
        {
            if (value)
            {
                ResultsTab = ResultsTab.Discarded;
            }
        }
    }

    /// <summary>Da correggere o scartati: c'è «Sposta in passaggi validi».</summary>
    public bool IsNotValidTab => ResultsTab != ResultsTab.Valid;

    /// <summary>L'elenco della linguetta scelta.</summary>
    public ObservableCollection<PassageRow> ShownPassages => ResultsTab switch
    {
        ResultsTab.Review => ReviewPassages,
        ResultsTab.Discarded => DiscardedPassages,
        _ => Passages,
    };

    private List<PassageRow> ShownRowsBeforeFilter => ResultsTab switch
    {
        ResultsTab.Review => _reviewRows,
        ResultsTab.Discarded => _discardedRows,
        _ => _allRows,
    };

    public bool ShowSortButtons => ShownPassages.Count > 0;

    public string ValidTabText => Passages.Count == 1 ? Loc.T("1 corretto") : Loc.S($"{Passages.Count} corretti");

    public string ReviewTabText => ReviewPassages.Count == 1 ? Loc.T("1 da correggere") : Loc.S($"{ReviewPassages.Count} da correggere");

    public string DiscardedTabText => DiscardedPassages.Count == 1 ? Loc.T("1 scartato") : Loc.S($"{DiscardedPassages.Count} scartati");

    /// <summary>Sotto le linguette: che cosa contiene quella scelta e cosa si può fare.</summary>
    public string TabCaption => ResultsTab switch
    {
        ResultsTab.Review => Loc.T("Passaggi in cui la ruota anteriore non è stata seguita bene vicino alle linee A e B, o tolti dai passaggi corretti. Non entrano nell'esportazione: correggili con «Modifica» o riportali con «Sposta in passaggi validi»."),
        ResultsTab.Discarded => Loc.T("Veicoli seguiti dall'analisi senza un passaggio valido tra le linee A e B, o eliminati da «Da correggere». Non entrano nell'esportazione: correggili con «Modifica» e riportali con «Sposta in passaggi validi»."),
        _ => string.Empty,
    };

    public bool ShowTabCaption => ResultsTab != ResultsTab.Valid;

    [RelayCommand]
    private void ShowValidTab() => ResultsTab = ResultsTab.Valid;

    [RelayCommand]
    private void ShowReviewTab() => ResultsTab = ResultsTab.Review;

    [RelayCommand]
    private void ShowDiscardedTab() => ResultsTab = ResultsTab.Discarded;

    /// <summary>Che cosa fa il pulsante di eliminazione nella linguetta scelta.</summary>
    public string DeleteTooltip => IsValidTab
        ? Loc.T("Sposta il passaggio tra quelli da correggere: non entra nell'esportazione")
        : Loc.T("Sposta il veicolo tra gli scartati");

    partial void OnResultsTabChanged(ResultsTab value)
    {
        if (IsEditingPassage)
        {
            EndEdit();
        }

        SelectedPassage = null;
        DeletePassageCommand.NotifyCanExecuteChanged();
        MoveToValidCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeletePassageCommand), nameof(ShowEntryFrameCommand), nameof(ShowExitFrameCommand), nameof(MoveToValidCommand))]
    private PassageRow? _selectedPassage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenExportFolderCommand))]
    private bool _hasExportFolder;

    /// <summary>
    /// Riquadro mostrato nella finestra aperta dai pulsanti in fondo ai risultati: filtro, limite di velocità o
    /// statistiche (None con la finestra chiusa).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterPanelOpen), nameof(IsLimitPanelOpen), nameof(IsStatsPanelOpen))]
    private ResultsPanel _openResultsPanel;

    /// <summary>Pulsanti in fondo al pannello: solo nei risultati, con almeno un veicolo.</summary>
    public bool ShowResultsButtons => SelectedStep == WorkflowStep.Results && HasAnyPassage;

    public bool IsFilterPanelOpen => OpenResultsPanel == ResultsPanel.Filter;

    public bool IsLimitPanelOpen => OpenResultsPanel == ResultsPanel.Limit;

    public bool IsStatsPanelOpen => OpenResultsPanel == ResultsPanel.Stats;

    [RelayCommand]
    private void ToggleResultsPanel(string? name)
    {
        ResultsPanel panel = Enum.TryParse(name, out ResultsPanel p) ? p : ResultsPanel.None;
        OpenResultsPanel = OpenResultsPanel == panel ? ResultsPanel.None : panel;
    }

    public bool HasPassages => Passages.Count > 0;

    /// <summary>Con il filtro attivo il pulsante dice quanti passaggi verranno esportati.</summary>
    public string ExportButtonText => IsFilterActive
        ? Loc.S($"Esporta i {Passages.Count} passaggi filtrati…")
        : Loc.T("Esporta CSV, PDF e immagini…");

    /// <summary>Sopra l'elenco: quanti passaggi della linguetta il filtro lascia vedere.</summary>
    public string PassageCountText
    {
        get
        {
            int shown = ShownPassages.Count, all = ShownRowsBeforeFilter.Count;
            if (shown == all)
            {
                return shown == 0 ? Loc.T("Nessun passaggio") : string.Empty;
            }

            return Loc.S($"{shown} su {all} (filtro attivo)");
        }
    }

    public string MeanSpeedText => Passages.Count == 0 || IsDistanceProvisional ? "–" : Passages.Average(p => p.Passage!.Speed.SpeedKmh).ToString("0.0", It);

    public string MinSpeedText => Passages.Count == 0 || IsDistanceProvisional ? "–" : Passages.Min(p => p.Passage!.Speed.SpeedKmh).ToString("0.0", It);

    public string MaxSpeedText => Passages.Count == 0 || IsDistanceProvisional ? "–" : Passages.Max(p => p.Passage!.Speed.SpeedKmh).ToString("0.0", It);

    private int? _lastShownPassageId;

    /// <summary>«Nessun veicolo attraversa le linee»: solo se non ci sono veicoli in nessuna delle tre linguette.</summary>
    public bool ShowNoPassageHint => _allRows.Count == 0 && _reviewRows.Count == 0 && _discardedRows.Count == 0;

    /// <summary>Misura automatica fatta sulla ruota anteriore, seguita con regolarità attorno a entrambe le linee.</summary>
    private bool WheelTrackedWell(SavedTrack saved, VehiclePassage passage)
    {
        if (saved.Reference != Core.Tracking.ReferencePoint.FrontWheel)
        {
            return false;
        }

        return Core.Tracking.WheelCrossingCheck.Check(saved.Track.Observations, passage, saved.Points, WheelFramesPerSide) is null;
    }

    partial void OnSelectedPassageChanged(PassageRow? value)
    {
        // Scegliere un altro passaggio annulla la modifica in corso.
        if (IsEditingPassage && value?.Id != EditingId)
        {
            EndEdit();
        }

        RefreshOverlay();

        // Cliccando un risultato il video va all'ingresso del veicolo. Solo quando cambia veicolo:
        // la riselezione automatica dopo un ricalcolo non deve spostare il video.
        if (value is not null && value.Id != _lastShownPassageId && SelectedStep == WorkflowStep.Results && !IsAnalyzing)
        {
            GoToFrame((int)value.FirstFrame);
        }

        _lastShownPassageId = value?.Id;
    }

    /// <summary>Ricalcola tutti i passaggi salvati (cambiano se cambia la calibrazione).</summary>
    private void RebuildPassages()
    {
        OnPropertyChanged(nameof(WheelMethodNote));
        int? selectedId = SelectedPassage?.Id;
        _allRows.Clear();
        _reviewRows.Clear();
        _discardedRows.Clear();
        if (Calibration is { } cal)
        {
            foreach (SavedTrack saved in _savedTracks)
            {

                PassageOptions options = saved.Source == PassageSource.Automatic
                    ? Core.Tracking.TrackPassageOptions.Default.Passage
                    : PassageOptions.Default;
                PassageResult r = PassageAnalyzer.Analyze(saved.Id, saved.Track.Observations, cal, options);
                int lane = TrackLane(saved);
                VehiclePassage? p = r.Passage;
                // Scartati: senza un passaggio valido tra le linee, oppure per scelta dell'utente. Da correggere: per scelta
                // dell'utente, oppure (misure automatiche) se la ruota non è stata seguita bene.
                List<PassageRow> list = saved.Discarded || p is null ? _discardedRows
                    : saved.ToReview ?? (saved.Source == PassageSource.Automatic && !WheelTrackedWell(saved, p)) ? _reviewRows
                    : _allRows;
                list.Add(new PassageRow(p, saved, FormatTime, p is null ? r.FailureReason ?? Loc.T("Passaggio non valido.") : null)
                {
                    HideSpeed = IsDistanceProvisional,
                    LaneText = Road is { Lanes: > 1 } && lane >= 0 ? string.Create(It, $"corsia {lane + 1}") : string.Empty,
                    Lane = lane,
                });
            }
        }
        _hasUnexportedChanges = true;
        ApplyPassageFilter(selectedId);
    }

    /// <summary>Aggiorna l'elenco mostrato secondo il filtro (velocità minima e tipo di veicolo).</summary>
    private void ApplyPassageFilter(int? selectedId = null)
    {
        selectedId ??= SelectedPassage?.Id;
        Passages.Clear();
        foreach (PassageRow row in Sorted(_allRows.Where(PassesFilter)))
        {
            Passages.Add(row);
        }

        ReviewPassages.Clear();
        foreach (PassageRow row in Sorted(_reviewRows.Where(PassesFilter)))
        {
            ReviewPassages.Add(row);
        }

        DiscardedPassages.Clear();
        foreach (PassageRow row in Sorted(_discardedRows.Where(PassesFilter)))
        {
            DiscardedPassages.Add(row);
        }

        SelectedPassage = ShownPassages.FirstOrDefault(p => p.Id == selectedId);
        OnPropertyChanged(nameof(HasPassages));
        OnPropertyChanged(nameof(HasAnyPassage));
        OnPropertyChanged(nameof(ShowResultsButtons));
        OnPropertyChanged(nameof(ShowNoPassageHint));
        OnPropertyChanged(nameof(ValidTabText));
        OnPropertyChanged(nameof(ReviewTabText));
        OnPropertyChanged(nameof(DiscardedTabText));
        OnPropertyChanged(nameof(ShowSortButtons));
        OnPropertyChanged(nameof(HasTrackedVehicles));
        OnPropertyChanged(nameof(ShowAnalysisProceed));
        OnPropertyChanged(nameof(ShowGoToAnalysis));
        OnPropertyChanged(nameof(IsReferenceStepActive));
        OnPropertyChanged(nameof(DistanceEstimateHint));
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(FilterHeaderText));
        OnPropertyChanged(nameof(ExportButtonText));
        OnPropertyChanged(nameof(PassageCountText));
        OnPropertyChanged(nameof(MeanSpeedText));
        OnPropertyChanged(nameof(MinSpeedText));
        OnPropertyChanged(nameof(MaxSpeedText));
        RefreshSpeedLimitMarks();
        RefreshHistogram();
        ExportResultsCommand.NotifyCanExecuteChanged();
        ExportVideoCommand.NotifyCanExecuteChanged();
        RequestVideoCommand.NotifyCanExecuteChanged();
    }

    private void RefreshPassageTimes()
    {
        RebuildPassages();
        AnalyzeCurrentTrack();
    }

    private bool HasSelection() => SelectedPassage is not null;

    private bool CanDelete() => SelectedPassage is { } row && !_discardedRows.Contains(row);

    /// <summary>
    /// Nessun veicolo si cancella: «Da correggere» (passaggi corretti) lo sposta tra quelli da correggere, «Elimina» (da
    /// correggere) tra gli scartati. Da entrambe le linguette si può riportarlo tra i corretti.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeletePassage()
    {
        if (SelectedPassage is not { } row)
        {
            return;
        }

        if (_allRows.Contains(row))
        {
            SetState(row.Id, toReview: true, discarded: false);
            ShowStatus(Loc.T("Passaggio spostato"),
                Loc.S($"Il veicolo {row.IdText} è ora tra quelli da correggere: non entra nell'esportazione."), StatusSeverity.Informational);
            return;
        }

        SetState(row.Id, toReview: true, discarded: true);
        ShowStatus(Loc.T("Veicolo spostato"),
            Loc.S($"Il veicolo {row.IdText} è ora tra gli scartati: non entra nell'esportazione."), StatusSeverity.Informational);
    }

    /// <summary>Da correggere o scartato, con un passaggio valido tra le linee: si può riportare tra i corretti.</summary>
    private bool CanMoveToValid() => SelectedPassage is { HasPassage: true } row && !_allRows.Contains(row);

    /// <summary>Riporta un passaggio da correggere o scartato tra quelli corretti (entra nell'esportazione).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveToValid))]
    private void MoveToValid()
    {
        if (SelectedPassage is not { } row)
        {
            return;
        }

        SetState(row.Id, toReview: false, discarded: false);
        ShowStatus(Loc.T("Passaggio spostato"),
            Loc.S($"Il veicolo {row.IdText} è ora tra i passaggi corretti."), StatusSeverity.Success);
    }

    private void SetState(int id, bool toReview, bool discarded)
    {
        int i = _savedTracks.FindIndex(t => t.Id == id);
        if (i >= 0)
        {
            _savedTracks[i] = _savedTracks[i] with { ToReview = toReview, Discarded = discarded };
        }

        RebuildPassages();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowEntryFrame()
    {
        if (SelectedPassage is { } row)
        {
            GoToFrame((int)row.FirstFrame);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowExitFrame()
    {
        if (SelectedPassage is { } row)
        {
            GoToFrame((int)row.LastFrame);
        }
    }

    /// <summary>
    /// «Modifica»: il passaggio si corregge restando nei risultati. L'elenco resta visibile; sul passaggio scelto
    /// compaiono «Annulla modifica» e «Salva modifica» al posto delle altre azioni, e un clic sul video mette (o
    /// sposta) il punto del veicolo nel fotogramma mostrato. Il passaggio resta nella sua linguetta: tra i passaggi
    /// corretti lo sposta solo l'utente, con «Sposta in passaggi validi».
    /// </summary>
    [RelayCommand]
    private void EditPassage(PassageRow? row)
    {
        row ??= SelectedPassage;
        if (row is null)
        {
            return;
        }

        if (IsEditingPassage)
        {
            CancelEdit();
        }

        if (_currentTrack.Count > 0)
        {
            ShowStatus(Loc.T("Misura in corso"), Loc.T("Salva o annulla il veicolo che stai misurando prima di modificarne un altro."), StatusSeverity.Warning);
            return;
        }

        _editBackup = row.Saved;
        _editState = (row.Saved.ToReview ?? _reviewRows.Any(r => r.Id == row.Id), row.Saved.Discarded || _discardedRows.Any(r => r.Id == row.Id));
        _currentTrack = row.Track.Clone();
        EditingId = row.Id;
        ViewportMode = ViewportMode.PlacePoint;
        AnalyzeCurrentTrack();
        GoToFrame((int)row.FirstFrame);
        NotifyEditCommands();
    }

    /// <summary>Linguetta in cui era il passaggio quando è iniziata la modifica (ci resta anche dopo).</summary>
    private (bool ToReview, bool Discarded) _editState;

    private bool CanSaveEdit() => IsEditingPassage && CanSavePassage && SelectedStep == WorkflowStep.Results;

    /// <summary>«Salva modifica»: i punti corretti sostituiscono quelli del passaggio, che resta nella sua linguetta.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private void SaveEdit()
    {
        if (EditingId is not { } id || _currentAnalysis?.Passage is null)
        {
            return;
        }

        int i = _savedTracks.FindIndex(t => t.Id == id);
        if (i >= 0)
        {
            _savedTracks[i] = _savedTracks[i] with { Track = _currentTrack.Clone(), ToReview = _editState.ToReview, Discarded = _editState.Discarded };
        }

        EndEdit();
        ShowStatus(Loc.T("Modifica salvata"), Loc.S($"Veicolo {id:0000} aggiornato."), StatusSeverity.Success);
    }

    /// <summary>«Annulla modifica»: il passaggio resta com'era.</summary>
    [RelayCommand]
    private void CancelEdit() => EndEdit();

    private void EndEdit()
    {
        EditingId = null;
        _editBackup = null;
        _currentTrack = new ManualTrack();
        if (SelectedStep == WorkflowStep.Results)
        {
            ViewportMode = ViewportMode.None;
        }

        RebuildPassages();
        AnalyzeCurrentTrack();
        NotifyEditCommands();
    }

    private void NotifyEditCommands()
    {
        SaveEditCommand.NotifyCanExecuteChanged();
        ExportResultsCommand.NotifyCanExecuteChanged();
        ExportVideoCommand.NotifyCanExecuteChanged();
        RequestVideoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(EditHintText));
    }

    /// <summary>Sotto «Annulla/Salva modifica»: cosa fare e com'è la misura con i punti attuali.</summary>
    public string EditHintText => !IsEditingPassage
        ? string.Empty
        : Loc.T("Clic sul video: mette o sposta il punto del veicolo nel fotogramma mostrato.") +
          (string.IsNullOrEmpty(CurrentResultText) ? string.Empty : "\n" + CurrentSpeedText + " km/h · " + CurrentResultText);

    private bool CanExport() => HasPassages && !IsBusy && !IsAnalyzing && !IsDistanceProvisional && !IsEditingPassage;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportResultsAsync()
    {
        if (_session is null || Calibration is not { } cal || Passages.Count == 0)
        {
            return;
        }

        string? parent = await _platform.PickFolderAsync(
            Loc.T("Scegli dove salvare CSV, PDF e immagini"),
            Directory.Exists(_settings.LastExportFolder) ? _settings.LastExportFolder : _settings.LastVideoFolder);
        if (parent is null)
        {
            return;
        }

        StopPlayback();
        string baseName = Path.GetFileNameWithoutExtension(SourceFileName);
        string folder = Path.Combine(parent, string.Create(CultureInfo.InvariantCulture, $"{baseName}_misure_{DateTime.Now:yyyyMMdd_HHmmss}"));
        List<PassageRow> rows = [.. Passages.OrderBy(p => p.Id)];
        DateTimeOffset? start = EffectiveRecordingStart;
        VideoSession session = _session;

        IsBusy = true;
        try
        {
            Directory.CreateDirectory(folder);
            string clearFolder = Path.Combine(folder, ClearImagesFolder);
            string anonFolder = Path.Combine(folder, AnonymizedImagesFolder);
            Directory.CreateDirectory(clearFolder);
            Directory.CreateDirectory(anonFolder);
            // I due rapporti PDF (immagini in chiaro e veicoli pixellati) si scrivono mentre si esportano le immagini:
            // una pagina per veicolo.
            byte[]? logo = ReportLogo();
            bool showLane = Road is { Lanes: > 1 };
            var summary = new PdfReportSummary(
                SourceFileName,
                start?.ToString("dd/MM/yyyy HH:mm:ss", It),
                cal.DistanceMeters,
                CalibrationMethodText,
                FilterDescription,
                rows,
                [.. SpeedHistogram]);
            await using (FileStream clearStream = File.Create(Path.Combine(folder, Loc.T("rapporto") + ".pdf")))
            await using (FileStream anonStream = File.Create(Path.Combine(folder, Loc.T("rapporto_anonimizzato") + ".pdf")))
            using (var clearReport = new PdfReport(clearStream, SourceFileName, rows.Count, showLane, start, anonymized: false, logo))
            using (var anonReport = new PdfReport(anonStream, SourceFileName, rows.Count, showLane, start, anonymized: true, logo))
            {
                clearReport.AddSummary(summary);
                anonReport.AddSummary(summary);
                for (int i = 0; i < rows.Count; i++)
                {
                    BusyText = Loc.S($"Esportazione immagini {i + 1} di {rows.Count}…");
                    PassageImages images = await ExportImagesAsync(session, cal, rows[i], start, clearFolder, anonFolder);
                    clearReport.AddPassage(rows[i], images.ClearEntry, images.ClearExit, i + 2);
                    anonReport.AddPassage(rows[i], images.AnonEntry, images.AnonExit, i + 2);
                }
            }

            BusyText = Loc.T("Scrittura del CSV…");
            IReadOnlyDictionary<int, int>? lanes = Road is { Lanes: > 1 }
                ? rows.Where(r => r.Lane >= 0).ToDictionary(r => r.Id, r => r.Lane + 1)
                : null;
            await CsvExporter.WriteAsync(Path.Combine(folder, Loc.T("passaggi") + ".csv"), rows.Select(r => r.Passage!), start, default, lanes);
            await CalibrationFile.From(VideoWidth, VideoHeight, cal.LineA, cal.LineB, cal.DistanceMeters, SourceFileName)
                .SaveAsync(Path.Combine(folder, "calibrazione.tsacal.json"));
            await File.WriteAllTextAsync(Path.Combine(folder, "info.txt"), BuildInfoText(session, cal, rows, start), new UTF8Encoding(true));

            _settings.LastExportFolder = parent;
            _settings.Save();
            _lastExportFolder = folder;
            HasExportFolder = true;
            _hasUnexportedChanges = false;
            RefreshStepProgress();
            AppLog.Info($"Esportati {rows.Count} passaggi in {folder}");
            ShowStatus(Loc.T("Esportazione completata"), Loc.S($"{rows.Count} passaggi salvati in {folder}"), StatusSeverity.Success);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or VideoProbeException or InvalidOperationException)
        {
            AppLog.Error("Esportazione non riuscita", e);
            ShowStatus(Loc.T("Esportazione non riuscita"), e.Message, StatusSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string ClearImagesFolder => Loc.T("immagini");

    private static string AnonymizedImagesFolder => Loc.T("immagini_anonimizzate");

    /// <summary>
    /// Zone da pixellare in un fotogramma: tutti i veicoli tracciati presenti (anche quelli nascosti dal filtro).
    /// Per i veicoli automatici si usa il riquadro; per quelli misurati a mano, un'area attorno al punto indicato.
    /// </summary>
    private List<BoundingBox> PrivacyRegionsAt(long frameIndex)
    {
        var regions = new List<BoundingBox>();
        foreach (SavedTrack t in _savedTracks)
        {
            if (t.Points is { Count: > 0 } points)
            {
                if (PrivacyMasking.BoxAt(points, frameIndex) is { } box)
                {
                    regions.AddRange(PrivacyMasking.RegionsFor(box, VideoWidth, VideoHeight));
                }
            }
            else if (t.Track.TryGet(frameIndex, out TrackObservation obs))
            {
                regions.Add(PrivacyMasking.RegionAroundPoint(obs.Position.X, obs.Position.Y, VideoWidth, VideoHeight));
            }
        }

        return regions;
    }

    /// <summary>Logo per la prima pagina del rapporto PDF (risorsa dell'applicazione); null se non disponibile.</summary>
    private static byte[]? ReportLogo()
    {
        try
        {
            using Stream? stream = typeof(MainViewModel).Assembly.GetManifestResourceStream("speedvision-logo-report.jpg");
            if (stream is null)
            {
                return null;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch (IOException e)
        {
            AppLog.Error("Logo del rapporto non disponibile", e);
            return null;
        }
    }

    /// <summary>Metodo di calibrazione scelto, con lo stesso testo dell'opzione nell'interfaccia.</summary>
    private string CalibrationMethodText =>
        (IsVanishingMode ? Loc.T("Con due riferimenti a terra e punto di fuga")
        : EstimateDistanceFromVehicle ? Loc.T("Con un'auto che riconosco")
        : Loc.T("Con due riferimenti misurati sul campo"))
        + (IsDistanceFromMap ? Loc.T(" (distanza rilevata sulla mappa)") : string.Empty);

    /// <summary>Immagini JPEG di un passaggio, in chiaro e anonimizzate, per i rapporti PDF.</summary>
    private sealed record PassageImages(byte[] ClearEntry, byte[] ClearExit, byte[] AnonEntry, byte[] AnonExit);

    /// <summary>Salva le immagini in chiaro e anonimizzate e le restituisce per i rapporti PDF.</summary>
    private async Task<PassageImages> ExportImagesAsync(VideoSession session, CalibrationSettings cal, PassageRow row, DateTimeOffset? start, string clearFolder, string anonFolder)
    {
        VehiclePassage p = row.Passage!;
        var clear = new byte[2][];
        var anonymized = new byte[2][];
        foreach (bool isEntry in new[] { true, false })
        {
            CrossingEstimate e = isEntry ? p.Entry : p.Exit;
            DetectionLine line = isEntry ? p.EntryLine : p.ExitLine;
            int frameIndex = (int)e.FirstBeyond.FrameIndex;

            VideoFrame frame = await Task.Run(() => session.GetFullResolutionFrameAsync(frameIndex));
            double frameRelative = VideoRelative(frame.TimeSeconds);

            string label = isEntry ? Loc.T("INGRESSO") : Loc.T("USCITA");
            var text = new PassageImageText(
                Header: Loc.S($"Veicolo {row.IdText} · {label} · linea {line} · {row.DirectionText}"),
                MainTime: FormatTimeFor(start, e.TimeSeconds),
                Detail: Loc.S($"Istante stimato di attraversamento ({PassageRow.Describe(e.Method)}). Fotogramma n. {frameIndex + 1} delle {FormatTimeFor(start, frameRelative)}"),
                Footer: isEntry ? null : Loc.S($"Tempo {p.Speed.DurationSeconds:0.000} s · {p.Speed.SpeedKmh:0.0} km/h"));

            // Il disegno con WPF avviene sul thread dell'interfaccia.
            string name = string.Create(CultureInfo.InvariantCulture, $"{p.Id:0000}_{(isEntry ? Loc.T("ingresso") : Loc.T("uscita"))}.jpg");
            byte[] jpeg = PassageImageRenderer.RenderJpeg(frame, VideoWidth, VideoHeight, cal, line, e.FirstBeyond.Position, text);
            await File.WriteAllBytesAsync(Path.Combine(clearFolder, name), jpeg);
            clear[isEntry ? 0 : 1] = jpeg;

            byte[] anon = PassageImageRenderer.RenderJpeg(frame, VideoWidth, VideoHeight, cal, line, e.FirstBeyond.Position, text, PrivacyRegionsAt(frameIndex));
            await File.WriteAllBytesAsync(Path.Combine(anonFolder, name), anon);
            anonymized[isEntry ? 0 : 1] = anon;
        }

        return new PassageImages(clear[0], clear[1], anonymized[0], anonymized[1]);
    }

    private static string FormatTimeFor(DateTimeOffset? start, double relativeSeconds) =>
        start is { } s ? TimeFormatting.FormatClock(s, relativeSeconds) : TimeFormatting.FormatVideoTime(relativeSeconds);

    private string BuildInfoText(VideoSession session, CalibrationSettings cal, IReadOnlyList<PassageRow> rows, DateTimeOffset? start)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.S($"{AppInfo.Name} — esportazione misure"));
        sb.AppendLine(Loc.S($"Data esportazione: {DateTime.Now:dd/MM/yyyy HH:mm:ss}"));
        sb.AppendLine(Loc.T("Video: ") + SourceFilePath);
        if (IsStabilized && _stabilizationReport is { } stab)
        {
            sb.AppendLine(Loc.T("Stabilizzazione: sì, analisi eseguita sul video stabilizzato (") + stab.Parameters + ").");
            sb.AppendLine(Loc.S($"  Vibrazione originale max {stab.ShakeBeforeMaxPx:0.0} px (95% entro {stab.ShakeBeforeP95Px:0.0} px); dopo la stabilizzazione {stab.MisalignedFrames.Count} fotogrammi con scarto ≥ 1,5 px."));
            sb.AppendLine(Loc.T("  Tempi dei fotogrammi identici all'originale (verificati). Le immagini mostrano il video stabilizzato: ai bordi la parte di inquadratura persa dalla correzione è riempita ripetendo i pixel del bordo."));
        }

        sb.AppendLine(Loc.T("Risoluzione: ") + ResolutionText + " · " + FpsText);
        sb.AppendLine(start is { } s
            ? Loc.T("Inizio registrazione: ") + s.ToString("dd/MM/yyyy HH:mm:ss zzz", It)
            : Loc.T("Inizio registrazione: non indicato (orari = tempo del video)"));
        sb.AppendLine(Loc.S($"Distanza tra le linee: {cal.DistanceMeters:0.###} m"));
        foreach (string line in DistanceEstimateInfoLines())
        {
            sb.AppendLine(line);
        }

        sb.AppendLine(Loc.S($"Passaggi: {rows.Count} · velocità media {rows.Average(r => r.Passage!.Speed.SpeedKmh):0.0} km/h"));
        if (FilterDescription is { } filter)
        {
            sb.AppendLine(Loc.S($"Filtro applicato: {filter} ({rows.Count} passaggi esportati su {_allRows.Count})."));
        }

        if (_reviewRows.Count > 0)
        {
            sb.AppendLine(Loc.S($"Passaggi da correggere, non esportati: {_reviewRows.Count}."));
        }

        if (_discardedRows.Count > 0)
        {
            sb.AppendLine(Loc.S($"Veicoli scartati, non esportati: {_discardedRows.Count}."));
        }
        sb.AppendLine();
        int auto = rows.Count(r => r.IsAutomatic);
        sb.AppendLine(Loc.S($"Metodo: {auto} passaggi da rilevamento automatico ({LastModelDescription ?? "YOLOX"}), {rows.Count - auto} da posizioni indicate a mano."));
        sb.AppendLine(Loc.T("Punto di riferimento automatico: il punto in cui la ruota anteriore tocca l'asfalto, quando è trovata con coerenza nei fotogrammi (sta sulla strada come le linee, quindi senza errori di parallasse); altrimenti il vertice in basso sul davanti del riquadro del veicolo (il destro per chi va da sinistra a destra, il sinistro per chi va da destra a sinistra). Lo stesso punto per l'ingresso e per l'uscita."));
        int wheelRefs = rows.Count(r => r.IsAutomatic && r.Saved.Reference == Core.Tracking.ReferencePoint.FrontWheel);
        sb.AppendLine(Loc.S($"  Passaggi automatici misurati con la ruota anteriore: {wheelRefs}; con lo spigolo del riquadro: {auto - wheelRefs}."));
        sb.AppendLine(Loc.T("Istante di attraversamento stimato con precisione inferiore al fotogramma"));
        sb.AppendLine(Loc.T("(interpolazione o regressione sulle posizioni attorno alla linea)."));
        sb.AppendLine();
        sb.AppendLine(Loc.T("Avvertenze: misure non omologate, non utilizzabili per sanzioni."));
        sb.AppendLine(Loc.S($"Cartella «{ClearImagesFolder}»: immagini in chiaro. Possono contenere targhe e persone (dati personali, GDPR): conservale e condividile con cautela."));
        sb.AppendLine(Loc.S($"Cartella «{AnonymizedImagesFolder}»: le stesse immagini con pixellato l'intero riquadro di ogni veicolo tracciato."));
        sb.AppendLine(Loc.S($"«{Loc.T("rapporto")}.pdf» e «{Loc.T("rapporto_anonimizzato")}.pdf»: una pagina per veicolo, con le immagini in chiaro e con i veicoli pixellati."));
        sb.AppendLine(Loc.T("  Per le misure manuali (senza riquadro) si copre un'area della taglia di un'auto attorno al punto indicato."));
        sb.AppendLine(Loc.T("  Veicoli non tracciati (es. parcheggiati), pedoni e targhe fuori da queste zone restano visibili: controlla le immagini prima di condividerle."));
        return sb.ToString();
    }

    [RelayCommand(CanExecute = nameof(HasExportFolder))]
    private void OpenExportFolder()
    {
        if (_lastExportFolder is not null && Directory.Exists(_lastExportFolder))
        {
            _platform.OpenFolder(_lastExportFolder);
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(AppLog.LogDirectory);
        _platform.OpenFolder(AppLog.LogDirectory);
    }
}
