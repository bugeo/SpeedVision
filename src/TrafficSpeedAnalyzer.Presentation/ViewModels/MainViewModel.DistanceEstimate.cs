using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>Auto riconosciuta dall'utente (modello da scheda tecnica e misura al centro dell'immagine).</summary>
public sealed record RecognizedVehicle(int TrackId, ReferenceVehicle Vehicle, CenterFrameMeasurement Measurement);

/// <summary>
/// Calibrazione con un'auto riconosciuta: la prima (nella corsia più vicina) dà le linee A e B, con perno
/// sugli spigoli bassi del suo riquadro (distanza = sua lunghezza); la seconda (nella corsia più lontana)
/// non aggiunge linee ma dà l'orizzonte: le due linee ruotano attorno al perno verso il punto di fuga, così
/// la distanza tra A e B è la stessa su tutte le corsie.
/// </summary>
public sealed record ReferenceChoice(
    RecognizedVehicle First,
    RecognizedVehicle? Second,
    HorizonEstimate? Horizon)
{
    public int TrackId => First.TrackId;

    public ReferenceVehicle Vehicle => First.Vehicle;

    public CenterFrameMeasurement Measurement => First.Measurement;
}

/// <summary>
/// Calibrazione con un'auto di lunghezza nota. L'analisi (passo precedente) ha già tracciato tutti i
/// veicoli; entrando nella calibrazione l'app giudica se la ripresa lo permette (camera perpendicolare
/// alla strada: le auto non cambiano grandezza attraversando l'immagine). Poi propone le auto una alla
/// volta e chiede il modello, oppure la lunghezza inserita a mano. Prima un'auto della corsia più vicina:
/// le linee A e B partono dagli spigoli bassi del suo riquadro. Con più corsie poi un'auto della corsia più
/// lontana, che orienta le stesse due linee verso il punto di fuga; se non se ne riconosce nessuna,
/// l'orizzonte si stima dalla grandezza di tutte le auto. Le linee attraversano tutta la strada.
/// L'utente può correggere le linee; le velocità si ricalcolano senza rifare l'analisi.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Auto utilizzabili per la calibrazione (passano per il centro, misurabili per intero), in ordine di passaggio.</summary>
    private readonly List<(int TrackId, CenterFrameMeasurement M)> _usableVehicles = [];

    private readonly List<int> _referenceCandidates = [];

    private int _candidateIndex;

    private ReferenceChoice? _reference;

    /// <summary>Si sta chiedendo un'auto della corsia più lontana (per orientare le linee).</summary>
    private bool _askingSecond;

    /// <summary>«Modifica» sulla prima auto: si sceglie di nuovo, le linee restano finché non si conferma.</summary>
    private bool _editingFirst;

    private ShotAssessment? _shot;

    private double _roadAngle;

    /// <summary>Orizzonte stimato dalle auto: null = più corsie ma stima non affidabile.</summary>
    private HorizonEstimate? _statisticalHorizon;

    /// <summary>Ultimo fotogramma/passo mostrato dalla guida: evita di riportare il video indietro a ogni ricalcolo.</summary>
    private (long Frame, WorkflowStep Step)? _lastGuideFrame;

    /// <summary>Metodo scelto: con un'auto riconosciuta. Nessun metodo preselezionato all'inizio.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalibrationMethod), nameof(IsMethodStepActive), nameof(ShowDistanceCard))]
    private bool _estimateDistanceFromVehicle;

    /// <summary>Metodo scelto: con due riferimenti misurati sul campo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalibrationMethod), nameof(IsMethodStepActive), nameof(ShowDistanceCard))]
    private bool _isDistanceKnownMode;

    [ObservableProperty]
    private string _vehicleSearchText = string.Empty;

    /// <summary>Lunghezza dell'auto inserita a mano (metri), in alternativa al modello.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualLengthError), nameof(HasValidManualLength))]
    [NotifyCanExecuteChangedFor(nameof(UseManualLengthCommand))]
    private string _manualLengthText = string.Empty;

    private double? ManualLengthMeters =>
        double.TryParse(ManualLengthText.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
        && v >= ReferenceVehicle.MinManualMeters && v <= ReferenceVehicle.MaxManualMeters ? v : null;

    /// <summary>Lunghezza valida scritta: il pulsante acceso diventa «Usa questa lunghezza».</summary>
    public bool HasValidManualLength => ManualLengthMeters is not null;

    public string ManualLengthError =>
        string.IsNullOrWhiteSpace(ManualLengthText) || ManualLengthMeters is not null
            ? string.Empty
            : Loc.S($"Inserisci una lunghezza tra {ReferenceVehicle.MinManualMeters:0} e {ReferenceVehicle.MaxManualMeters:0} metri, per esempio 4,25.");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseReferenceVehicleCommand))]
    private ReferenceVehicle? _selectedReferenceVehicle;

    public ObservableCollection<ReferenceVehicle> FilteredReferenceVehicles { get; } = new(ReferenceVehicleCatalog.All);

    public bool HasCalibrationMethod => IsDistanceKnownMode || EstimateDistanceFromVehicle || IsVanishingMode;

    /// <summary>Guida: il primo passo è scegliere come calibrare.</summary>
    public bool IsMethodStepActive => !HasCalibrationMethod;

    /// <summary>Il riquadro della distanza compare solo dopo aver disegnato le due linee.</summary>
    public bool ShowDistanceCard => LineA is not null && LineB is not null && (IsDistanceKnownMode || IsVanishingMode);

    /// <summary>La calibrazione con l'auto è possibile: ripresa adatta (camera perpendicolare, auto utilizzabili).</summary>
    public bool IsVehicleCalibrationAvailable => _shot?.Suitable == true;

    /// <summary>Spiega sotto la scelta del metodo se la calibrazione con l'auto è possibile e perché.</summary>
    public string VehicleMethodNote => !HasTrackedVehicles
        ? Loc.T("Serve l'analisi dei veicoli.")
        : _shot is { Suitable: false, UsableVehicles: > 0 }
            ? string.Empty   // auto che cambiano grandezza: l'avviso resta nel registro e in info.txt, non sotto la scelta
            : _shot?.Message ?? string.Empty;

    /// <summary>Modalità «auto» attiva ma auto non ancora scelta: velocità non note.</summary>
    public bool IsDistanceProvisional => EstimateDistanceFromVehicle && _reference is null;

    public bool HasReference => EstimateDistanceFromVehicle && _reference is not null;

    /// <summary>Si sta chiedendo all'utente il modello della prima auto.</summary>
    public bool IsIdentifyingReference => EstimateDistanceFromVehicle && (_reference is null || _editingFirst) && CurrentCandidate is not null;

    /// <summary>Si sta chiedendo un'auto della corsia più lontana (orienta le linee).</summary>
    public bool IsIdentifyingSecond => HasReference && _askingSecond && CurrentCandidate is not null;

    /// <summary>Elenco dei modelli visibile: si sta riconoscendo un'auto (prima o seconda).</summary>
    public bool IsPickingVehicle => IsIdentifyingReference || IsIdentifyingSecond;

    /// <summary>«Cambia auto»: riferimento scelto e nessuna domanda in corso.</summary>
    public bool ShowChangeReference => HasReference && !IsIdentifyingSecond && !_editingFirst;

    // ───────────── Riquadri delle auto confermate ─────────────

    /// <summary>Riquadro «Auto corsia 1»: prima auto confermata (nascosto mentre la si modifica).</summary>
    public bool ShowFirstCarBox => HasReference && !_editingFirst;

    /// <summary>Riquadro «Auto corsia N»: auto della corsia lontana confermata (nascosto mentre la si modifica).</summary>
    public bool ShowSecondCarBox => HasReference && _reference!.Second is not null && !_askingSecond;

    public string FirstCarTitle => HasLanes ? Loc.T("Auto corsia 1") : Loc.T("Auto di riferimento");

    public string SecondCarTitle => HasLanes ? Loc.S($"Auto corsia {FarthestLane + 1}") : Loc.T("Auto sull'altra corsia");

    public string FirstCarDetail => _reference is { } r ? CarDetail(r.First.Vehicle) : string.Empty;

    public string SecondCarDetail => _reference?.Second is { } s ? CarDetail(s.Vehicle) : string.Empty;

    private static string CarDetail(ReferenceVehicle v) => v.IsManual
        ? Loc.S($"Lunghezza {v.LengthMeters:0.00} m (inserita a mano)")
        : string.Create(It, $"{v.DisplayName} · lunghezza {v.LengthMeters:0.00} m");

    /// <summary>«Salta: va bene così» alla prima richiesta dell'auto della corsia lontana (non durante una modifica).</summary>
    public bool ShowSkipSecond => IsIdentifyingSecond && _reference!.Second is null;

    /// <summary>Si sta modificando un'auto già confermata: «Annulla» la lascia com'era.</summary>
    public bool ShowCancelCarEdit => (_editingFirst && IsIdentifyingReference) || (IsIdentifyingSecond && _reference!.Second is not null);

    /// <summary>Riquadro «Che auto è?» nella calibrazione.</summary>
    public bool ShowDistanceEstimatePanel => EstimateDistanceFromVehicle;

    /// <summary>Calibrazione con l'auto scelta ma nessun veicolo tracciato: prima va fatta l'analisi.</summary>
    public bool ShowGoToAnalysis => EstimateDistanceFromVehicle && !HasTrackedVehicles;

    /// <summary>Riquadro «Che auto è?» da evidenziare: c'è un'auto da riconoscere, o manca l'analisi.</summary>
    public bool IsReferenceStepActive => IsPickingVehicle || ShowGoToAnalysis;

    /// <summary>Linee visibili e modificabili nella calibrazione: misurate a mano oppure ricavate dall'auto.</summary>
    public bool ShowLinesCard => IsDistanceKnownMode || (EstimateDistanceFromVehicle && _reference is not null);

    /// <summary>
    /// Si può passare ai risultati: calibrazione completa. Mentre si propone la seconda auto si risponde
    /// prima a quella domanda («È questa», «Non lo so» o «Salta»): un solo passo acceso per volta.
    /// </summary>
    public bool CanLeaveCalibration => IsCalibrationValid && !IsIdentifyingSecond && !_editingFirst;

    private SavedTrack? CurrentCandidate =>
        _candidateIndex >= 0 && _candidateIndex < _referenceCandidates.Count
            ? _savedTracks.FirstOrDefault(t => t.Id == _referenceCandidates[_candidateIndex])
            : null;

    private CenterFrameMeasurement? CurrentCandidateMeasurement() =>
        CurrentCandidate is { } t ? _usableVehicles.FirstOrDefault(u => u.TrackId == t.Id).M : null;

    public string DistanceEstimateTitle =>
        IsIdentifyingSecond ? (HasLanes ? Loc.T("Che auto è? — corsia più lontana") : Loc.T("Che auto è? — altra corsia"))
        : _reference is { } r && !_editingFirst ? Loc.T("Auto di riferimento")
        : HasLanes ? Loc.T("Che auto è? — corsia più vicina")
        : Loc.T("Che auto è?");

    public string DistanceEstimateHint
    {
        get
        {
            if (IsIdentifyingSecond)
            {
                return Loc.S($"Auto {_candidateIndex + 1} di {_referenceCandidates.Count}, corsia lontana: serve a orientare le linee A e B. Scegli il modello o scrivi la lunghezza.");
            }

            if (_reference is { } r && !_editingFirst)
            {
                return Loc.S($"Linee A e B sulla {r.Vehicle.DisplayName} ({r.Vehicle.LengthMeters:0.00} m). ") +
                    RoadPerspective.Describe(r.Horizon) + Loc.T(" Se non coincidono, trascinale.");
            }

            if (!HasTrackedVehicles)
            {
                return Loc.T("Serve prima l'analisi dei veicoli: torna al passo «Analisi veicoli».");
            }

            return _referenceCandidates.Count == 0
                ? Loc.T("Nessuna auto ripresa per intero al centro dell'immagine. Calibra con due riferimenti misurati sul campo.")
                : Loc.S($"Auto {_candidateIndex + 1} di {_referenceCandidates.Count}{(HasLanes ? Loc.T(", passata nella corsia più vicina") : string.Empty)}. Se la riconosci scegli il modello o scrivi la lunghezza, altrimenti «Non lo so».");
        }
    }

    /// <summary>
    /// Avviso sull'elenco delle auto (file esterno <see cref="ReferenceVehicleCatalog.FileName"/>): mancante o con righe scartate.
    /// I dettagli riga per riga sono nel registro.
    /// </summary>
    private static string? CatalogNote =>
        ReferenceVehicleCatalog.All.Count == 0
            ? Loc.S($"Elenco delle auto non disponibile: {ReferenceVehicleCatalog.Problems.FirstOrDefault()} Puoi comunque scrivere la lunghezza dell'auto.")
            : ReferenceVehicleCatalog.Problems.Count > 0
                ? Loc.S($"Elenco delle auto: {ReferenceVehicleCatalog.Problems.Count} righe del file {ReferenceVehicleCatalog.Source} non sono valide e sono state ignorate (dettagli nel registro).")
                : null;

    /// <summary>Scrive nel registro da dove viene l'elenco delle auto e le righe scartate.</summary>
    private static void LogReferenceCatalog()
    {
        AppLog.Info($"Elenco auto: {ReferenceVehicleCatalog.All.Count} veicoli da {ReferenceVehicleCatalog.Source}");
        foreach (string problem in ReferenceVehicleCatalog.Problems)
        {
            AppLog.Warn("Elenco auto: " + problem);
        }
    }

    public string? DistanceEstimateWarning
    {
        get
        {
            var w = new List<string>();
            if (IsPickingVehicle && CatalogNote is { } note)
            {
                w.Add(note);
            }

            if (IsPickingVehicle && CurrentCandidateMeasurement() is { } m)
            {
                w.AddRange(m.Warnings);
            }
            else if (_reference is { } r)
            {
                w.AddRange(r.Measurement.Warnings);
                if (r.Horizon is null)
                {
                    w.Add(Loc.T("Prospettiva non stimabile: le velocità valgono solo nella corsia dell'auto di riferimento. Per tutte le corsie, calibra sul campo."));
                }
            }

            return w.Count > 0 ? string.Join(" ", w) : null;
        }
    }

    public string CalibrationEstimateText => EstimateDistanceFromVehicle && _reference is { } r
        ? Loc.S($"Riferimento: {r.Vehicle.DisplayName}, {r.Vehicle.LengthMeters:0.00} m.")
        : string.Empty;

    /// <summary>Distanza usata per i calcoli: misurata a mano oppure lunghezza del veicolo di riferimento.</summary>
    private double? EffectiveDistanceMeters =>
        EstimateDistanceFromVehicle ? _reference?.Vehicle.LengthMeters : IsDistanceKnownMode || IsVanishingMode ? DistanceMeters : null;

    partial void OnEstimateDistanceFromVehicleChanged(bool value)
    {
        if (value)
        {
            IsDistanceKnownMode = false;
            IsVanishingMode = false;
            if (_reference is null)
            {
                StartReferenceIdentification();   // mostra subito la prima auto da riconoscere
            }
        }
        else
        {
            _reference = null;
            _askingSecond = false;
            _editingFirst = false;
        }

        OnCalibrationMethodChanged();
    }

    partial void OnIsDistanceKnownModeChanged(bool value)
    {
        if (value)
        {
            EstimateDistanceFromVehicle = false;
            IsVanishingMode = false;
        }

        OnCalibrationMethodChanged();
    }

    private void OnCalibrationMethodChanged()
    {
        // Con i due riferimenti il video è subito pronto per disegnare la linea mancante.
        if (SelectedStep == WorkflowStep.Calibration)
        {
            ViewportMode = NextCalibrationMode();
        }

        UpdateCalibration();
        RefreshEstimateState();
        RefreshMapState();
    }

    partial void OnVehicleSearchTextChanged(string value)
    {
        FilteredReferenceVehicles.Clear();
        foreach (ReferenceVehicle v in ReferenceVehicleCatalog.Search(value))
        {
            FilteredReferenceVehicles.Add(v);
        }
    }

    /// <summary>
    /// Ricalcola, dai tracciati dell'analisi, le auto utilizzabili, il giudizio sulla ripresa e la
    /// prospettiva stimata. Va chiamato quando cambiano i tracciati (fine analisi, nuovo video).
    /// </summary>
    private void RefreshShotAssessment()
    {
        _usableVehicles.Clear();
        List<SavedTrack> auto = _savedTracks
            .Where(t => t.Source == PassageSource.Automatic && t.Points is { Count: > 0 }
                && t.Class is VehicleClass.Car or VehicleClass.Truck or VehicleClass.Bus)
            .OrderBy(t => t.Points!.Min(p => p.FrameIndex))
            .ToList();
        foreach (SavedTrack t in auto)
        {
            if (VehicleScaleEstimator.CrossesImageCenter(t.Points!, VideoWidth, VideoHeight)
                && VehicleScaleEstimator.MeasureAtCenter(t.Points!, VideoWidth, VideoHeight, out _) is { } m)
            {
                _usableVehicles.Add((t.Id, m));
            }
        }

        if (auto.Count == 0)
        {
            _shot = null;
            _statisticalHorizon = null;
        }
        else
        {
            List<CenterFrameMeasurement> ms = _usableVehicles.Select(u => u.M).ToList();
            _shot = RoadPerspective.Assess(ms);
            _roadAngle = RoadPerspective.RoadAngle(_usableVehicles.Select(u => _savedTracks.First(t => t.Id == u.TrackId).Points!));
            _statisticalHorizon = RoadPerspective.EstimateHorizon(ms, _roadAngle, VideoWidth, VideoHeight);
            AppLog.Info(_shot.Message + " " + RoadPerspective.Describe(_statisticalHorizon));
        }

        OnPropertyChanged(nameof(IsVehicleCalibrationAvailable));
        OnPropertyChanged(nameof(VehicleMethodNote));
    }

    /// <summary>Prepara l'elenco delle auto da proporre, nell'ordine di passaggio, e mostra la prima.</summary>
    private void StartReferenceIdentification()
    {
        if (_usableVehicles.Count == 0 && HasTrackedVehicles)
        {
            RefreshShotAssessment();
        }

        // Con più corsie si parte dalla più vicina; se lì non c'è nessuna auto utilizzabile, tutte le auto.
        _editingFirst = false;
        _referenceCandidates.Clear();
        List<int> near = HasLanes ? _usableVehicles.Where(u => TrackLane(u.TrackId) == 0).Select(u => u.TrackId).ToList() : [];
        _referenceCandidates.AddRange(near.Count > 0 ? near : _usableVehicles.Select(u => u.TrackId));
        ManualLengthText = string.Empty;
        _askingSecond = false;
        _candidateIndex = 0;
        SelectedReferenceVehicle = null;
        VehicleSearchText = string.Empty;
        RefreshEstimateState();
    }

    /// <summary>
    /// Dopo la prima auto propone le auto della corsia più lontana, che orientano le linee verso il punto di fuga.
    /// Senza strada indicata (o se nella corsia più lontana non ce n'è nessuna) le auto su un'altra corsia:
    /// riquadro più in alto o più in basso di almeno un terzo della sua altezza.
    /// </summary>
    private void StartSecondIdentification(RecognizedVehicle first)
    {
        double v0 = RoadPerspective.ToRoad(first.Measurement.Best.Box.BottomCenter, _roadAngle).V;
        double minGap = 0.3 * first.Measurement.Best.Box.Height;
        List<(int TrackId, CenterFrameMeasurement M)> others = _usableVehicles
            .Where(u => u.TrackId != first.TrackId && u.M.Horizontal
                && Math.Abs(RoadPerspective.ToRoad(u.M.Best.Box.BottomCenter, _roadAngle).V - v0) >= minGap)
            .ToList();
        List<int> far = HasLanes ? others.Where(u => TrackLane(u.TrackId) == FarthestLane).Select(u => u.TrackId).ToList() : [];
        _referenceCandidates.Clear();
        _referenceCandidates.AddRange(far.Count > 0 ? far : others.Select(u => u.TrackId));
        _candidateIndex = 0;
        _askingSecond = _referenceCandidates.Count > 0;
        ManualLengthText = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanUseReferenceVehicle))]
    private void UseReferenceVehicle()
    {
        if (SelectedReferenceVehicle is { } vehicle)
        {
            UseVehicle(vehicle);
        }
    }

    /// <summary>«Usa questa lunghezza»: l'utente conosce la lunghezza dell'auto ma non il modello.</summary>
    [RelayCommand(CanExecute = nameof(CanUseManualLength))]
    private void UseManualLength()
    {
        if (ManualLengthMeters is { } meters)
        {
            UseVehicle(ReferenceVehicle.Manual(meters));
        }
    }

    private bool CanUseManualLength() => ManualLengthMeters is not null && IsPickingVehicle;

    private void UseVehicle(ReferenceVehicle vehicle)
    {
        if (CurrentCandidate is not { } track || CurrentCandidateMeasurement() is not { } m)
        {
            return;
        }

        var recognized = new RecognizedVehicle(track.Id, vehicle, m);
        SelectedReferenceVehicle = null;
        VehicleSearchText = string.Empty;
        ManualLengthText = string.Empty;

        if (_reference is { } first && _askingSecond)
        {
            // Seconda auto: orizzonte esatto dalle due auto riconosciute.
            HorizonEstimate? h = RoadPerspective.FromTwoReferences(
                first.Measurement, first.Vehicle.LengthMeters, m, vehicle.LengthMeters, _roadAngle, VideoWidth, VideoHeight);
            _askingSecond = false;
            if (h is null)
            {
                ShowStatus(Loc.T("Auto non utilizzabile"),
                    Loc.T("Con queste due auto la prospettiva non si ricava. Restano le linee di prima."),
                    StatusSeverity.Warning);
                RefreshEstimateState();
                return;
            }

            ApplyReference(first with { Second = recognized, Horizon = h });
            ShowStatus(Loc.T("Linee orientate"),
                Loc.S($"Linee orientate con la {vehicle.DisplayName}: la distanza vale per tutte le corsie. Controllale e premi «Procedi»."),
                StatusSeverity.Success);
            return;
        }

        if (_editingFirst && _reference is { Second: { } second })
        {
            // Prima auto cambiata, seconda già confermata: si ricalcola l'orizzonte con le due auto.
            _editingFirst = false;
            HorizonEstimate? h2 = RoadPerspective.FromTwoReferences(
                m, vehicle.LengthMeters, second.Measurement, second.Vehicle.LengthMeters, _roadAngle, VideoWidth, VideoHeight);
            ApplyReference(h2 is null
                ? new ReferenceChoice(recognized, null, _statisticalHorizon)
                : new ReferenceChoice(recognized, second, h2));
            ShowStatus(h2 is null ? Loc.T("Auto della corsia lontana da rifare") : Loc.T("Prima auto aggiornata"),
                h2 is null
                    ? Loc.T("Con la nuova prima auto quella della corsia lontana non permette di ricavare la prospettiva: riconoscine un'altra con «Modifica».")
                    : Loc.T("Linee A e B ricalcolate con la nuova prima auto e orientate con quella della corsia lontana."),
                h2 is null ? StatusSeverity.Warning : StatusSeverity.Success);
            return;
        }

        // Prima auto: linee dagli spigoli bassi verso il punto di fuga stimato (se c'è).
        _editingFirst = false;
        ApplyReference(new ReferenceChoice(recognized, null, _statisticalHorizon));
        StartSecondIdentification(recognized);
        RefreshEstimateState();

        string perspective = RoadPerspective.Describe(_statisticalHorizon);
        ShowStatus(_askingSecond ? Loc.T("Velocità calcolate — ancora una domanda") : Loc.T("Velocità calcolate"),
            Loc.S($"Linee A e B sulla {vehicle.DisplayName} ({vehicle.LengthMeters:0.00} m). {perspective}") +
            (_askingSecond ? Loc.T(" Ora un'auto della corsia più lontana, per orientare le linee.") : Loc.T(" Controlla le linee e premi «Procedi».")) +
            (m.Warnings.Count > 0 ? " " + string.Join(" ", m.Warnings) : string.Empty),
            m.Warnings.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
    }

    /// <summary>Mette le linee A e B sul veicolo di riferimento e ricalcola tutte le velocità.</summary>
    private void ApplyReference(ReferenceChoice choice)
    {
        _reference = choice;
        (LineSegment a, LineSegment b) = AcrossRoad(RoadPerspective.LinesAtVehicleEnds(choice.Measurement, choice.Horizon, VideoWidth, VideoHeight));
        LineA = a;
        LineB = b;   // ricalcola calibrazione e velocità di tutti i veicoli
        AppLog.Info(string.Create(It,
            $"Riferimento: veicolo {choice.TrackId} = {choice.Vehicle.DisplayName} ({choice.Vehicle.LengthMm} mm), {choice.Measurement.LengthPixels:0} px al fotogramma {choice.Measurement.Best.FrameIndex + 1}. ") +
            (choice.Second is { } s ? string.Create(It, $"Seconda auto: veicolo {s.TrackId} = {s.Vehicle.DisplayName}. ") : string.Empty) +
            RoadPerspective.Describe(choice.Horizon));
        UpdateCalibration();
        RefreshEstimateState();
    }

    private bool CanUseReferenceVehicle() => SelectedReferenceVehicle is not null && IsPickingVehicle;

    /// <summary>«Non lo so»: passa all'auto successiva.</summary>
    [RelayCommand]
    private void SkipReferenceVehicle()
    {
        if (_referenceCandidates.Count == 0)
        {
            return;
        }

        SelectedReferenceVehicle = null;
        VehicleSearchText = string.Empty;
        ManualLengthText = string.Empty;
        if (IsIdentifyingSecond && _candidateIndex == _referenceCandidates.Count - 1)
        {
            // Viste tutte le auto dell'altra corsia: la domanda facoltativa finisce qui.
            SkipSecondReference();
            return;
        }

        _candidateIndex = (_candidateIndex + 1) % _referenceCandidates.Count;
        RefreshEstimateState();
        if (_candidateIndex == 0)
        {
            ShowStatus(Loc.T("Ricomincio dalla prima auto"),
                Loc.T("Hai visto tutte le auto. Se non ne riconosci nessuna, scegli «Con due riferimenti misurati sul campo»."),
                StatusSeverity.Informational);
        }
    }

    /// <summary>«Salta»: nessuna seconda auto; restano le linee della prima.</summary>
    [RelayCommand]
    private void SkipSecondReference()
    {
        _askingSecond = false;
        SelectedReferenceVehicle = null;
        VehicleSearchText = string.Empty;
        RefreshEstimateState();
        ShowStatus(Loc.T("Calibrazione pronta"),
            RoadPerspective.Describe(_reference?.Horizon) + Loc.T(" Controlla le linee e premi «Procedi»."),
            _reference?.Horizon is null ? StatusSeverity.Warning : StatusSeverity.Success);
    }

    /// <summary>«Modifica» sul riquadro della prima auto: la si sceglie di nuovo, partendo da quella confermata.</summary>
    [RelayCommand]
    private void EditFirstCar()
    {
        if (_reference is not { } r)
        {
            return;
        }

        StartReferenceIdentification();
        _editingFirst = true;
        _askingSecond = false;
        _candidateIndex = Math.Max(0, _referenceCandidates.IndexOf(r.TrackId));
        RefreshEstimateState();
    }

    /// <summary>«Modifica» sul riquadro dell'auto della corsia lontana.</summary>
    [RelayCommand]
    private void EditSecondCar()
    {
        if (_reference is not { } r)
        {
            return;
        }

        _editingFirst = false;
        StartSecondIdentification(r.First);
        if (r.Second is { } s)
        {
            _candidateIndex = Math.Max(0, _referenceCandidates.IndexOf(s.TrackId));
        }

        SelectedReferenceVehicle = null;
        VehicleSearchText = string.Empty;
        RefreshEstimateState();
        if (!_askingSecond)
        {
            ShowStatus(Loc.T("Nessuna auto da proporre"), Loc.T("Non ci sono auto utilizzabili su un'altra corsia."), StatusSeverity.Warning);
        }
    }

    /// <summary>«Annulla»: la modifica finisce, le auto confermate restano com'erano.</summary>
    [RelayCommand]
    private void CancelCarEdit()
    {
        _editingFirst = false;
        _askingSecond = false;
        SelectedReferenceVehicle = null;
        VehicleSearchText = string.Empty;
        ManualLengthText = string.Empty;
        RefreshEstimateState();
    }

    /// <summary>Scegliere un'altra auto di riferimento (le linee vengono ricalcolate).</summary>
    [RelayCommand]
    private void ChangeReferenceVehicle()
    {
        _reference = null;
        LineA = null;
        LineB = null;
        StartReferenceIdentification();
        UpdateCalibration();
        RefreshEstimateState();
    }

    private void RefreshEstimateState()
    {
        OnPropertyChanged(nameof(CalibrationSummary));
        OnPropertyChanged(nameof(IsDistanceProvisional));
        OnPropertyChanged(nameof(HasReference));
        OnPropertyChanged(nameof(IsIdentifyingReference));
        OnPropertyChanged(nameof(IsIdentifyingSecond));
        OnPropertyChanged(nameof(IsPickingVehicle));
        OnPropertyChanged(nameof(ShowChangeReference));
        OnPropertyChanged(nameof(ShowFirstCarBox));
        OnPropertyChanged(nameof(ShowSecondCarBox));
        OnPropertyChanged(nameof(FirstCarTitle));
        OnPropertyChanged(nameof(SecondCarTitle));
        OnPropertyChanged(nameof(FirstCarDetail));
        OnPropertyChanged(nameof(SecondCarDetail));
        OnPropertyChanged(nameof(ShowCancelCarEdit));
        OnPropertyChanged(nameof(ShowSkipSecond));
        OnPropertyChanged(nameof(IsReferenceStepActive));
        OnPropertyChanged(nameof(ShowGoToAnalysis));
        OnPropertyChanged(nameof(ShowDistanceEstimatePanel));
        OnPropertyChanged(nameof(ShowLinesCard));
        OnPropertyChanged(nameof(ShowDistanceCard));
        OnPropertyChanged(nameof(CanLeaveCalibration));
        OnPropertyChanged(nameof(DistanceEstimateTitle));
        OnPropertyChanged(nameof(DistanceEstimateHint));
        OnPropertyChanged(nameof(DistanceEstimateWarning));
        OnPropertyChanged(nameof(CalibrationEstimateText));
        OnPropertyChanged(nameof(IsVehicleCalibrationAvailable));
        OnPropertyChanged(nameof(VehicleMethodNote));
        UseReferenceVehicleCommand.NotifyCanExecuteChanged();
        UseManualLengthCommand.NotifyCanExecuteChanged();
        ExportResultsCommand.NotifyCanExecuteChanged();
        RefreshReferenceOverlay();
    }

    /// <summary>
    /// Nella calibrazione mostra l'auto da riconoscere (o quella di riferimento) nel fotogramma in cui è
    /// più vicina al centro, con il suo riquadro.
    /// </summary>
    private void RefreshReferenceOverlay()
    {
        if (IsAnalyzing)
        {
            return;
        }

        CenterFrameMeasurement? m = null;
        string label = string.Empty;
        if (IsPickingVehicle && SelectedStep == WorkflowStep.Calibration)
        {
            m = CurrentCandidateMeasurement();
            label = Loc.T("Che auto è?");
        }
        else if (HasReference && _reference is { } r && SelectedStep == WorkflowStep.Calibration)
        {
            m = r.Measurement;
            label = string.Create(It, $"{(r.Vehicle.IsManual ? Loc.T("Riferimento") : r.Vehicle.Model)} · {r.Vehicle.LengthMeters:0.00} m");
        }

        if (m is null)
        {
            _lastGuideFrame = null;
            if (OverlayBoxes.Count > 0)
            {
                OverlayBoxes = [];
            }

            return;
        }

        OverlayBoxes = [new OverlayBox(m.Best.Box, label, true, Heading: 2)];   // perni delle linee A e B
        var target = (m.Best.FrameIndex, SelectedStep);
        if (_lastGuideFrame != target)
        {
            _lastGuideFrame = target;
            GoToFrame((int)m.Best.FrameIndex);
        }
    }

    private void ResetDistanceEstimate()
    {
        _referenceCandidates.Clear();
        _usableVehicles.Clear();
        _candidateIndex = 0;
        _reference = null;
        _askingSecond = false;
        _editingFirst = false;
        _shot = null;
        _statisticalHorizon = null;
        RefreshEstimateState();
    }

    /// <summary>Righe per info.txt sulla calibrazione da veicolo (vuoto se la distanza è misurata).</summary>
    private IEnumerable<string> DistanceEstimateInfoLines()
    {
        foreach (string line in MapInfoLines())
        {
            yield return line;
        }

        if (!EstimateDistanceFromVehicle || _reference is not { } r)
        {
            yield break;
        }

        CenterFrameMeasurement m = r.Measurement;
        yield return Loc.S($"Calibrazione da veicolo di riferimento (distanza non misurata sul campo): {r.Vehicle.DisplayName}, {r.Vehicle.Years}, lunghezza {r.Vehicle.LengthMm} mm {(r.Vehicle.IsManual ? Loc.T("inserita a mano") : Loc.T("da scheda tecnica"))}.");
        yield return Loc.S($"  Linee A e B dagli spigoli bassi del veicolo {r.TrackId:0000} al fotogramma n. {m.Best.FrameIndex + 1} (lunghezza misurata {m.LengthPixels:0} px, media di {m.FramesUsed} fotogrammi), dirette verso il punto di fuga; eventuali correzioni manuali.");
        if (r.Second is { } s)
        {
            yield return Loc.S($"  Auto nella corsia lontana (orienta le linee verso il punto di fuga): {s.Vehicle.DisplayName}, {s.Vehicle.LengthMm} mm (veicolo {s.TrackId:0000}, {s.Measurement.LengthPixels:0} px).");
        }

        yield return Loc.T("  Prospettiva: ") + RoadPerspective.Describe(r.Horizon);
        if (_shot is { } shot)
        {
            yield return Loc.T("  Ripresa: ") + shot.Message;
        }

        yield return Loc.S($"  Incertezza della scala circa ±{VehicleScaleEstimator.RelativeUncertainty(m) * 100:0.0}% (riquadro del veicolo e allestimento). Camera perpendicolare alla strada.");
        foreach (string w in m.Warnings)
        {
            yield return Loc.T("  Avviso: ") + w;
        }
    }
}
