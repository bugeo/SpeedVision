using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Passo «Strada», dopo il Video: quadrilatero sui bordi della carreggiata, quante corsie, linee tra le corsie.
/// La strada limita l'analisi ai veicoli sulla carreggiata e assegna a ognuno la sua corsia; con la
/// calibrazione tramite le auto ogni corsia ha la sua auto di riferimento.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Strada indicata dall'utente (null = non indicata: si usa tutta l'immagine, una sola corsia).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoad), nameof(RoadLaneCount), nameof(HasLaneDividers), nameof(RoadSummary))]
    private RoadLayout? _road;

    /// <summary>L'utente ha confermato che la strada verde è sui bordi della strada.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoadAreaStepActive), nameof(IsRoadLanesStepActive), nameof(ShowRoadLanesCard),
        nameof(IsRoadDividersStepActive), nameof(ShowRoadDividersCard), nameof(ShowRoadProceed), nameof(ShowConfirmRoadEdges),
        nameof(ShowConfirmRoadDividers), nameof(IsVideoStepActive))]
    private bool _roadEdgesConfirmed;

    /// <summary>L'utente ha scelto il numero di corsie (finché non lo fa la strada ha una sola corsia provvisoria).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoadLanesStepActive), nameof(RoadLaneCount), nameof(IsRoadDividersStepActive),
        nameof(ShowRoadDividersCard), nameof(ShowRoadProceed), nameof(ShowConfirmRoadDividers), nameof(IsVideoStepActive))]
    private bool _roadLanesChosen;

    /// <summary>L'utente ha confermato le linee tra le corsie.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoadDividersStepActive), nameof(ShowRoadProceed), nameof(ShowConfirmRoadDividers), nameof(IsVideoStepActive), nameof(LaneVanishingPoint))]
    private bool _roadDividersConfirmed;

    public bool HasRoad => Road is not null;

    /// <summary>Numero di corsie scelto (0 = non ancora scelto): il pulsante corrispondente resta verde.</summary>
    public int RoadLaneCount => RoadLanesChosen ? Road?.Lanes ?? 0 : 0;

    public bool HasLaneDividers => Road is { Lanes: > 1 };

    // ───────────── Guida: un riquadro acceso per volta ─────────────

    /// <summary>Primo passo: quadrilatero sui bordi della strada.</summary>
    public bool IsRoadAreaStepActive => Road is not null && !RoadEdgesConfirmed;

    public bool ShowRoadAreaCard => Road is not null;

    /// <summary>Secondo passo, dopo i bordi: quante corsie.</summary>
    public bool ShowRoadLanesCard => Road is not null && RoadEdgesConfirmed;

    public bool IsRoadLanesStepActive => ShowRoadLanesCard && !RoadLanesChosen;

    /// <summary>Terzo passo (con più corsie): linee tra le corsie.</summary>
    public bool ShowRoadDividersCard => HasLaneDividers && RoadEdgesConfirmed && RoadLanesChosen;

    public bool IsRoadDividersStepActive => ShowRoadDividersCard && !RoadDividersConfirmed;

    /// <summary>Vertici della zona verde spostati dalla posizione proposta (0–4).</summary>
    public int RoadCornersMoved =>
        Road is { } r && VideoWidth > 0 && VideoHeight > 0
            ? r.CornersAtStart(VideoWidth, VideoHeight).Count(atStart => !atStart)
            : 0;

    /// <summary>
    /// Tutti e quattro i vertici sono stati spostati dalla posizione proposta: solo allora compare «I bordi sono a posto»,
    /// così la zona verde non viene confermata senza essere stata portata sugli angoli della strada.
    /// </summary>
    public bool RoadEdgesMoved => RoadCornersMoved == 4;

    /// <summary>«I bordi sono a posto» è visibile: passo dei bordi in corso e tutti i vertici spostati.</summary>
    public bool ShowConfirmRoadEdges => IsRoadAreaStepActive && RoadEdgesMoved;

    /// <summary>Cosa manca nel passo dei bordi: quanti pallini restano da spostare, poi la conferma.</summary>
    public string RoadCornersHint => (4 - RoadCornersMoved) switch
    {
        0 => Loc.T("Controlla che la zona verde copra la carreggiata, poi conferma."),
        1 => Loc.T("Manca ancora 1 pallino da spostare."),
        int left => Loc.S($"Mancano ancora {left} pallini da spostare."),
    };

    /// <summary>
    /// Almeno una linea tra le corsie è stata spostata dalla posizione proposta (corsie uguali): solo allora compare
    /// «Le linee sono a posto», così le linee non vengono confermate senza essere state portate sulle separazioni.
    /// </summary>
    public bool RoadDividersMoved =>
        Road is { Lanes: > 1 } r && !r.DividersAtStart;

    /// <summary>«Le linee sono a posto» è visibile: passo delle linee in corso e almeno una linea spostata.</summary>
    public bool ShowConfirmRoadDividers => IsRoadDividersStepActive && RoadDividersMoved;

    public string RoadDividersHint => RoadDividersMoved
        ? Loc.T("Controlla che ogni linea stia sulla separazione tra le corsie, poi conferma.")
        : Loc.T("Sposta almeno una linea tratteggiata sulla separazione tra le corsie.");

    /// <summary>
    /// Il prossimo passo si fa sul video (bordi della strada o linee tra le corsie): il visore ha la cornice verde.
    /// </summary>
    public bool IsVideoStepActive => SelectedStep == WorkflowStep.Road && (IsRoadAreaStepActive || IsRoadDividersStepActive);

    public bool ShowRoadProceed => Road is not null && RoadEdgesConfirmed && RoadLanesChosen && (!HasLaneDividers || RoadDividersConfirmed);

    public string RoadSummary => Road is { } r
        ? (r.Lanes == 1
            ? Loc.T("1 corsia.")
            : Loc.S($"{r.Lanes} corsie."))
        : Loc.T("Strada non indicata: l'analisi considera tutta l'immagine.");

    /// <summary>
    /// Nella calibrazione, con almeno 2 corsie sistemate: dove potrebbe essere il punto di fuga dei segmenti A e B,
    /// calcolato dai bordi della strada e dalle linee tra le corsie (corsie larghe uguali). Solo un aiuto visivo
    /// (pallino rosso): l'utente decide se orientare i segmenti verso di esso.
    /// </summary>
    public Point2? LaneVanishingPoint =>
        SelectedStep == WorkflowStep.Calibration && Road is { Lanes: > 1 } r && RoadDividersConfirmed && VideoWidth > 0
            ? LaneVanishing.Estimate(r, VideoWidth, VideoHeight)
            : null;

    partial void OnRoadChanged(RoadLayout? value)
    {
        OnPropertyChanged(nameof(LaneVanishingPoint));
        OnPropertyChanged(nameof(IsRoadLanesStepActive));
        OnPropertyChanged(nameof(ShowRoadLanesCard));
        OnPropertyChanged(nameof(IsRoadAreaStepActive));
        OnPropertyChanged(nameof(ShowRoadAreaCard));
        OnPropertyChanged(nameof(ShowRoadDividersCard));
        OnPropertyChanged(nameof(IsRoadDividersStepActive));
        OnPropertyChanged(nameof(ShowRoadProceed));
        OnPropertyChanged(nameof(RoadEdgesMoved));
        OnPropertyChanged(nameof(RoadCornersMoved));
        OnPropertyChanged(nameof(ShowConfirmRoadEdges));
        OnPropertyChanged(nameof(RoadCornersHint));
        OnPropertyChanged(nameof(RoadDividersMoved));
        OnPropertyChanged(nameof(ShowConfirmRoadDividers));
        OnPropertyChanged(nameof(RoadDividersHint));
        OnPropertyChanged(nameof(IsVideoStepActive));
        ConfirmRoadDividersCommand.NotifyCanExecuteChanged();
        ConfirmRoadEdgesCommand.NotifyCanExecuteChanged();
        UpdateAnalysisRegion();
        OnLanesChanged();
    }

    /// <summary>«Quante corsie?»: 1–4. La strada resta dove l'ha messa l'utente, con le linee tra le corsie ridistribuite.</summary>
    [RelayCommand]
    private void SetLaneCount(string? lanes)
    {
        if (!int.TryParse(lanes, out int n) || n < 1 || n > RoadLayout.MaxLanes)
        {
            return;
        }

        Road = Road is { } r ? r.WithLanes(n) : RoadLayout.CreateCentered(n, VideoWidth, VideoHeight);
        RoadLanesChosen = true;
        RoadDividersConfirmed = false;
        if (n == 1 && RoadEdgesConfirmed)
        {
            SelectedStep = WorkflowStep.Analysis;   // una corsia: niente linee da sistemare, si passa all'analisi
        }
    }

    /// <summary>La strada è stata modificata nel visore.</summary>
    [RelayCommand]
    private void RoadEdited(RoadLayout? road)
    {
        if (road is not null)
        {
            Road = road;
        }
    }

    [RelayCommand(CanExecute = nameof(RoadEdgesMoved))]
    private void ConfirmRoadEdges() => RoadEdgesConfirmed = true;

    /// <summary>«Le linee sono a posto»: la strada è completa, si passa subito all'analisi.</summary>
    [RelayCommand(CanExecute = nameof(RoadDividersMoved))]
    private void ConfirmRoadDividers()
    {
        RoadDividersConfirmed = true;
        SelectedStep = WorkflowStep.Analysis;
    }

    /// <summary>«Ricomincia»: di nuovo i bordi della strada, poi le corsie.</summary>
    [RelayCommand]
    private void ResetRoad()
    {
        Road = null;
        RoadEdgesConfirmed = false;
        RoadLanesChosen = false;
        RoadDividersConfirmed = false;
        EnsureRoadForEditing();
    }

    /// <summary>
    /// Entrando nel passo «Strada» compare subito la zona verde da portare sui bordi (una corsia provvisoria):
    /// il numero di corsie si sceglie dopo. Fuori da questo passo, senza strada indicata, si analizza tutta l'immagine.
    /// </summary>
    private void EnsureRoadForEditing()
    {
        if (SelectedStep == WorkflowStep.Road && HasVideo && Road is null)
        {
            Road = RoadLayout.CreateCentered(1, VideoWidth, VideoHeight);
        }
    }

    /// <summary>Corsia di un veicolo dai punti a terra (0 = la più vicina), −1 se fuori strada, 0 senza strada.</summary>
    private int LaneOf(IReadOnlyList<Point2> groundPoints) => Road is { } r ? r.LaneOfTrack(groundPoints) : 0;

    /// <summary>Nuovo video: la strada resta se si tengono i marcatori, altrimenti si ricomincia.</summary>
    private void ResetRoadForNewVideo(bool keep)
    {
        if (!keep || Road is not { } r)
        {
            Road = null;
            RoadEdgesConfirmed = false;
            RoadLanesChosen = false;
            RoadDividersConfirmed = false;
            return;
        }

        // Stessa posizione del treppiede: si tiene, limitata alla nuova immagine.
        Road = r.WithCorners(r.Corners, VideoWidth, VideoHeight);
    }
}
