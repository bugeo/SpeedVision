using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Lingua dell'interfaccia: all'avvio quella scelta in precedenza, altrimenti quella di Windows.
/// Cambiandola, i testi si aggiornano subito; anche CSV, info.txt e immagini esportate la seguono.
/// </summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<AppLanguage> Languages => Loc.Languages;

    public AppLanguage SelectedLanguage
    {
        get => Loc.Current;
        set
        {
            if (value is null || value.Code == Loc.Language)
            {
                return;
            }

            Loc.SetLanguage(value.Code);
            _settings.Language = value.Code;
            _settings.Save();
            RefreshLanguage();
        }
    }

    /// <summary>Ricalcola i testi prodotti dal codice nella nuova lingua.</summary>
    private void RefreshLanguage()
    {
        // Caselle del filtro per corsia: si ricreano con il nuovo nome, conservando la scelta.
        bool[] shown = LaneFilters.Select(l => l.IsShown).ToArray();
        foreach (LaneFilterOption l in LaneFilters)
        {
            l.PropertyChanged -= OnLaneFilterChanged;
        }

        LaneFilters.Clear();
        SyncLaneFilters();
        for (int i = 0; i < Math.Min(shown.Length, LaneFilters.Count); i++)
        {
            LaneFilters[i].IsShown = shown[i];
        }

        if (_savedTracks.Count > 0)
        {
            RebuildPassages();
        }

        // Tutte le proprietà calcolate (titoli, suggerimenti, riepiloghi) si rileggono.
        OnPropertyChanged(string.Empty);
    }
}
