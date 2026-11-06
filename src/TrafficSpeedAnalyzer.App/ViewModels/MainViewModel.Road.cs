using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Passo «Strada», dopo il Video: quante corsie, quadrilatero sui bordi della carreggiata, linee tra le corsie.
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
    [NotifyPropertyChangedFor(nameof(IsRoadAreaStepActive), nameof(IsRoadDividersStepActive), nameof(ShowRoadDividersCard), nameof(ShowRoadProceed))]
    private bool _roadEdgesConfirmed;

    /// <summary>L'utente ha confermato le linee tra le corsie.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoadDividersStepActive), nameof(ShowRoadProceed))]
    private bool _roadDividersConfirmed;

    public bool HasRoad => Road is not null;

    public int RoadLaneCount => Road?.Lanes ?? 0;

    public bool HasLaneDividers => Road is { Lanes: > 1 };

    // ───────────── Guida: un riquadro acceso per volta ─────────────

    /// <summary>Primo passo: quante corsie.</summary>
    public bool IsRoadLanesStepActive => HasVideo && Road is null;

    /// <summary>Secondo passo: quadrilatero sui bordi della strada.</summary>
    public bool IsRoadAreaStepActive => Road is not null && !RoadEdgesConfirmed;

    public bool ShowRoadAreaCard => Road is not null;

    /// <summary>Terzo passo (con più corsie): linee tra le corsie.</summary>
    public bool ShowRoadDividersCard => HasLaneDividers && RoadEdgesConfirmed;

    public bool IsRoadDividersStepActive => ShowRoadDividersCard && !RoadDividersConfirmed;

    public bool ShowRoadProceed => Road is not null && RoadEdgesConfirmed && (!HasLaneDividers || RoadDividersConfirmed);

    public string RoadSummary => Road is { } r
        ? (r.Lanes == 1
            ? Loc.T("1 corsia: l'analisi considera solo i veicoli sulla strada (o appena fuori).")
            : Loc.S($"{r.Lanes} corsie: l'analisi considera solo i veicoli sulla strada (o appena fuori)."))
        : Loc.T("Strada non indicata: l'analisi considera tutta l'immagine.");

    partial void OnRoadChanged(RoadLayout? value)
    {
        OnPropertyChanged(nameof(IsRoadLanesStepActive));
        OnPropertyChanged(nameof(IsRoadAreaStepActive));
        OnPropertyChanged(nameof(ShowRoadAreaCard));
        OnPropertyChanged(nameof(ShowRoadDividersCard));
        OnPropertyChanged(nameof(IsRoadDividersStepActive));
        OnPropertyChanged(nameof(ShowRoadProceed));
        UpdateAnalysisRegion();
        OnLanesChanged();
    }

    /// <summary>«Quante corsie?»: 1–4. La strada compare al centro (o resta dov'era, con le linee ridistribuite).</summary>
    [RelayCommand]
    private void SetLaneCount(string? lanes)
    {
        if (!int.TryParse(lanes, out int n) || n < 1 || n > RoadLayout.MaxLanes)
        {
            return;
        }

        Road = Road is { } r ? r.WithLanes(n) : RoadLayout.CreateCentered(n, VideoWidth, VideoHeight);
        RoadDividersConfirmed = false;
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

    [RelayCommand]
    private void ConfirmRoadEdges() => RoadEdgesConfirmed = true;

    [RelayCommand]
    private void ConfirmRoadDividers() => RoadDividersConfirmed = true;

    /// <summary>«Ricomincia»: di nuovo la domanda sulle corsie.</summary>
    [RelayCommand]
    private void ResetRoad()
    {
        Road = null;
        RoadEdgesConfirmed = false;
        RoadDividersConfirmed = false;
    }

    /// <summary>Corsia di un veicolo dai punti a terra (0 = la più vicina), −1 se fuori strada, 0 senza strada.</summary>
    private int LaneOf(IReadOnlyList<Point2> groundPoints) => Road is { } r ? r.LaneOfTrack(groundPoints) : 0;

    /// <summary>Nuovo video: la strada resta se si tengono i marcatori, altrimenti si ricomincia.</summary>
    private void ResetRoadForNewVideo(bool keep)
    {
        if (!keep || Road is not { } r)
        {
            ResetRoad();
            return;
        }

        // Stessa posizione del treppiede: si tiene, limitata alla nuova immagine.
        Road = r.WithCorners(r.Corners, VideoWidth, VideoHeight);
    }
}
