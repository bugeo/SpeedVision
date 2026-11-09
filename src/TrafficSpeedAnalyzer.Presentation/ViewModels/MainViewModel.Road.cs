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
        nameof(IsRoadDividersStepActive), nameof(ShowRoadDividersCard), nameof(ShowRoadProceed))]
    private bool _roadEdgesConfirmed;

    /// <summary>L'utente ha scelto il numero di corsie (finché non lo fa la strada ha una sola corsia provvisoria).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoadLanesStepActive), nameof(RoadLaneCount), nameof(IsRoadDividersStepActive),
        nameof(ShowRoadDividersCard), nameof(ShowRoadProceed))]
    private bool _roadLanesChosen;

    /// <summary>L'utente ha confermato le linee tra le corsie.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoadDividersStepActive), nameof(ShowRoadProceed))]
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

    /// <summary>
    /// Almeno un vertice è stato spostato dalla posizione proposta: solo allora «I bordi sono a posto» si accende,
    /// così la zona verde non viene confermata senza essere stata portata sulla strada.
    /// </summary>
    public bool RoadEdgesMoved =>
        Road is { } r && VideoWidth > 0 && VideoHeight > 0 &&
        r.Corners.Zip(RoadLayout.CreateCentered(1, VideoWidth, VideoHeight).Corners).Any(c => Point2.Distance(c.First, c.Second) > 0.5);

    public bool ShowRoadProceed => Road is not null && RoadEdgesConfirmed && RoadLanesChosen && (!HasLaneDividers || RoadDividersConfirmed);

    public string RoadSummary => Road is { } r
        ? (r.Lanes == 1
            ? Loc.T("1 corsia.")
            : Loc.S($"{r.Lanes} corsie."))
        : Loc.T("Strada non indicata: l'analisi considera tutta l'immagine.");

    partial void OnRoadChanged(RoadLayout? value)
    {
        OnPropertyChanged(nameof(IsRoadLanesStepActive));
        OnPropertyChanged(nameof(ShowRoadLanesCard));
        OnPropertyChanged(nameof(IsRoadAreaStepActive));
        OnPropertyChanged(nameof(ShowRoadAreaCard));
        OnPropertyChanged(nameof(ShowRoadDividersCard));
        OnPropertyChanged(nameof(IsRoadDividersStepActive));
        OnPropertyChanged(nameof(ShowRoadProceed));
        OnPropertyChanged(nameof(RoadEdgesMoved));
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
    [RelayCommand]
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
