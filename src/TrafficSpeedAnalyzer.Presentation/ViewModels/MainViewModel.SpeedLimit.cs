using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Limite di velocità dei risultati (50 km/h se non cambiato, ricordato tra una sessione e l'altra): i passaggi
/// più veloci sono bordati di rosso nell'elenco, le fasce del grafico sopra il limite sono rosse e il riquadro
/// «Multe» conta i veicoli oltre il limite tra quelli mostrati dal filtro.
/// </summary>
public sealed partial class MainViewModel
{
    public const double DefaultSpeedLimitKmh = 50;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedLimitKmh), nameof(SpeedLimitError))]
    private string _speedLimitText = DefaultSpeedLimitKmh.ToString(CultureInfo.InvariantCulture);

    /// <summary>Limite in km/h; null se la casella è vuota o non valida.</summary>
    public double? SpeedLimitKmh =>
        double.TryParse(SpeedLimitText.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : null;

    public string SpeedLimitError =>
        SpeedLimitKmh is not null ? string.Empty : Loc.T("Inserisci il limite in km/h, per esempio 50.");

    /// <summary>Veicoli oltre il limite tra quelli mostrati («–» senza velocità o senza limite).</summary>
    public string FinesText => Passages.Count == 0 || IsDistanceProvisional || SpeedLimitKmh is null
        ? "–"
        : Passages.Count(IsOverLimit).ToString(Loc.Culture);

    public bool HasFines => SpeedLimitKmh is not null && !IsDistanceProvisional && Passages.Any(IsOverLimit);

    private bool IsOverLimit(PassageRow r) => SpeedLimitKmh is { } limit && !r.HideSpeed && r.Passage.Speed.SpeedKmh > limit;

    private void InitializeSpeedLimit()
    {
        double saved = _settings.SpeedLimitKmh > 0 ? _settings.SpeedLimitKmh : DefaultSpeedLimitKmh;
        SpeedLimitText = saved.ToString("0.#", CultureInfo.InvariantCulture);
    }

    partial void OnSpeedLimitTextChanged(string value)
    {
        if (SpeedLimitKmh is { } limit && _settings is not null)
        {
            _settings.SpeedLimitKmh = limit;
            _settings.Save();
        }

        ApplyPassageFilter();
    }

    /// <summary>Aggiorna righe, grafico e riquadro «Multe» dopo un cambio di passaggi, filtro o limite.</summary>
    private void RefreshSpeedLimitMarks()
    {
        foreach (PassageRow r in _allRows.Concat(_reviewRows))
        {
            r.IsOverLimit = IsOverLimit(r);
        }

        OnPropertyChanged(nameof(FinesText));
        OnPropertyChanged(nameof(HasFines));
    }
}
