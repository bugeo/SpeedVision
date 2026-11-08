using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Filtro dei risultati: velocità minima, tipo di veicolo, verso, corsia e punto di misura (ruota anteriore o
/// spigolo del riquadro; vale solo per le misure automatiche). I tipi sono quelli che il modello
/// riconosce (classi COCO auto, moto, autobus, camion); le misure manuali non hanno un tipo.
/// L'esportazione comprende solo i passaggi mostrati.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinSpeedFilterError))]
    private string _minSpeedFilterText = string.Empty;

    [ObservableProperty]
    private bool _showCars = true;

    [ObservableProperty]
    private bool _showMotorcycles = true;

    [ObservableProperty]
    private bool _showBuses = true;

    [ObservableProperty]
    private bool _showTrucks = true;

    [ObservableProperty]
    private bool _showManual = true;

    [ObservableProperty]
    private bool _showAToB = true;

    /// <summary>Misure automatiche fatte sulla ruota anteriore.</summary>
    [ObservableProperty]
    private bool _showWheelReference = true;

    /// <summary>Misure automatiche fatte sullo spigolo del riquadro (ruota non trovata con sicurezza).</summary>
    [ObservableProperty]
    private bool _showBoxReference = true;

    [ObservableProperty]
    private bool _showBToA = true;

    /// <summary>Una casella per corsia (solo con più corsie).</summary>
    public ObservableCollection<LaneFilterOption> LaneFilters { get; } = [];

    public bool ShowLaneFilter => LaneFilters.Count > 1;

    /// <summary>Vero se esiste almeno un passaggio valido, anche se nascosto dal filtro.</summary>
    public bool HasAnyPassage => _allRows.Count > 0;

    /// <summary>Titolo del riquadro (chiuso all'inizio): dice se il filtro sta nascondendo qualcosa.</summary>
    public string FilterHeaderText => IsFilterActive ? Loc.T("Filtro (attivo)") : Loc.T("Filtro");

    public bool IsFilterActive =>
        MinSpeedFilter is not null || !ShowCars || !ShowMotorcycles || !ShowBuses || !ShowTrucks || !ShowManual || !ShowAToB || !ShowBToA
        || !ShowWheelReference || !ShowBoxReference
        || LaneFilters.Any(l => !l.IsShown);

    /// <summary>Velocità minima in km/h (vuoto = nessun limite).</summary>
    public double? MinSpeedFilter =>
        double.TryParse(MinSpeedFilterText.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : null;

    public string MinSpeedFilterError =>
        string.IsNullOrWhiteSpace(MinSpeedFilterText) || MinSpeedFilter is not null ? string.Empty : Loc.T("Inserisci un numero, per esempio 50.");

    /// <summary>Descrizione del filtro per info.txt (null se non è attivo).</summary>
    public string? FilterDescription
    {
        get
        {
            if (!IsFilterActive)
            {
                return null;
            }

            var parts = new List<string>();
            if (MinSpeedFilter is { } min)
            {
                parts.Add(Loc.S($"velocità superiore a {min:0.#} km/h"));
            }

            var types = new List<string>();
            if (ShowCars)
            {
                types.Add(Loc.T("auto"));
            }

            if (ShowMotorcycles)
            {
                types.Add(Loc.T("moto"));
            }

            if (ShowBuses)
            {
                types.Add(Loc.T("autobus"));
            }

            if (ShowTrucks)
            {
                types.Add(Loc.T("camion"));
            }

            if (ShowManual)
            {
                types.Add(Loc.T("misure manuali"));
            }

            if (types.Count < 5)
            {
                parts.Add(types.Count == 0 ? Loc.T("nessun tipo") : Loc.S($"solo {string.Join(", ", types)}"));
            }

            if (ShowAToB != ShowBToA)
            {
                parts.Add(ShowAToB ? Loc.T("solo verso A → B") : ShowBToA ? Loc.T("solo verso B → A") : string.Empty);
            }
            else if (!ShowAToB)
            {
                parts.Add(Loc.T("nessun verso"));
            }

            if (ShowWheelReference != ShowBoxReference)
            {
                parts.Add(ShowWheelReference ? Loc.T("misure automatiche solo sulla ruota anteriore") : Loc.T("misure automatiche solo sullo spigolo del riquadro"));
            }
            else if (!ShowWheelReference)
            {
                parts.Add(Loc.T("nessuna misura automatica"));
            }

            if (LaneFilters.Any(l => !l.IsShown))
            {
                string[] shown = LaneFilters.Where(l => l.IsShown).Select(l => (l.Lane + 1).ToString(It)).ToArray();
                parts.Add(shown.Length == 0 ? Loc.T("nessuna corsia") : shown.Length == 1 ? Loc.S($"solo corsia {shown[0]}") : Loc.S($"solo corsie {string.Join(", ", shown)}"));
            }

            return string.Join("; ", parts);
        }
    }

    private bool PassesFilter(PassageRow row)
    {
        if (MinSpeedFilter is { } min && row.Passage.Speed.SpeedKmh <= min)
        {
            return false;
        }

        bool directionShown = row.Passage.Direction == TravelDirection.AToB ? ShowAToB : ShowBToA;
        if (!directionShown)
        {
            return false;
        }

        if (row.Lane >= 0 && row.Lane < LaneFilters.Count && !LaneFilters[row.Lane].IsShown)
        {
            return false;
        }

        if (!row.IsAutomatic)
        {
            return ShowManual;
        }

        if (!(row.IsWheelReference ? ShowWheelReference : ShowBoxReference))
        {
            return false;
        }

        return row.Saved.Class switch
        {
            VehicleClass.Car => ShowCars,
            VehicleClass.Motorcycle => ShowMotorcycles,
            VehicleClass.Bus => ShowBuses,
            VehicleClass.Truck => ShowTrucks,
            _ => true,
        };
    }

    partial void OnMinSpeedFilterTextChanged(string value) => ApplyPassageFilter();

    partial void OnShowCarsChanged(bool value) => ApplyPassageFilter();

    partial void OnShowMotorcyclesChanged(bool value) => ApplyPassageFilter();

    partial void OnShowBusesChanged(bool value) => ApplyPassageFilter();

    partial void OnShowTrucksChanged(bool value) => ApplyPassageFilter();

    partial void OnShowManualChanged(bool value) => ApplyPassageFilter();

    partial void OnShowAToBChanged(bool value) => ApplyPassageFilter();

    partial void OnShowBToAChanged(bool value) => ApplyPassageFilter();

    partial void OnShowWheelReferenceChanged(bool value) => ApplyPassageFilter();

    partial void OnShowBoxReferenceChanged(bool value) => ApplyPassageFilter();

    [RelayCommand]
    private void ResetFilter()
    {
        MinSpeedFilterText = string.Empty;
        ShowCars = ShowMotorcycles = ShowBuses = ShowTrucks = ShowManual = true;
        ShowAToB = ShowBToA = true;
        ShowWheelReference = ShowBoxReference = true;
        foreach (LaneFilterOption l in LaneFilters)
        {
            l.IsShown = true;
        }
    }

    /// <summary>Caselle delle corsie allineate alla strada: si ricreano (tutte attive) solo se cambia il numero di corsie.</summary>
    private void SyncLaneFilters()
    {
        int n = Road is { Lanes: > 1 } r ? r.Lanes : 0;
        if (LaneFilters.Count == n)
        {
            return;
        }

        foreach (LaneFilterOption l in LaneFilters)
        {
            l.PropertyChanged -= OnLaneFilterChanged;
        }

        LaneFilters.Clear();
        for (int lane = 0; lane < n; lane++)
        {
            var option = new LaneFilterOption(lane, Loc.S($"Corsia {lane + 1}"));
            option.PropertyChanged += OnLaneFilterChanged;
            LaneFilters.Add(option);
        }

        OnPropertyChanged(nameof(ShowLaneFilter));
    }

    private void OnLaneFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LaneFilterOption.IsShown))
        {
            ApplyPassageFilter();
        }
    }
}

/// <summary>Casella del filtro per una corsia (0 = la più vicina).</summary>
public sealed partial class LaneFilterOption(int lane, string label) : ObservableObject
{
    public int Lane { get; } = lane;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isShown = true;
}
