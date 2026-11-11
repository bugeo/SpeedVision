using CommunityToolkit.Mvvm.ComponentModel;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>Ordine dell'elenco dei risultati.</summary>
public enum PassageSortOrder
{
    /// <summary>Dal primo all'ultimo passaggio (orario di ingresso crescente). È l'ordine iniziale.</summary>
    FirstToLast,

    /// <summary>Dall'ultimo al primo passaggio.</summary>
    LastToFirst,

    /// <summary>Velocità crescente.</summary>
    SpeedAscending,

    /// <summary>Velocità decrescente.</summary>
    SpeedDescending,
}

/// <summary>
/// Ordinamento dell'elenco dei risultati con quattro pulsanti a freccia sopra l'elenco. Cambia solo l'elenco
/// mostrato: statistiche, grafico ed esportazione (sempre in ordine di numero) non dipendono dall'ordine.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortFirstToLast), nameof(IsSortLastToFirst), nameof(IsSortSpeedAscending), nameof(IsSortSpeedDescending))]
    private PassageSortOrder _sortOrder = PassageSortOrder.FirstToLast;

    public bool IsSortFirstToLast
    {
        get => SortOrder == PassageSortOrder.FirstToLast;
        set => SetSortIf(value, PassageSortOrder.FirstToLast);
    }

    public bool IsSortLastToFirst
    {
        get => SortOrder == PassageSortOrder.LastToFirst;
        set => SetSortIf(value, PassageSortOrder.LastToFirst);
    }

    public bool IsSortSpeedAscending
    {
        get => SortOrder == PassageSortOrder.SpeedAscending;
        set => SetSortIf(value, PassageSortOrder.SpeedAscending);
    }

    public bool IsSortSpeedDescending
    {
        get => SortOrder == PassageSortOrder.SpeedDescending;
        set => SetSortIf(value, PassageSortOrder.SpeedDescending);
    }

    private void SetSortIf(bool value, PassageSortOrder order)
    {
        if (value)
        {
            SortOrder = order;
        }
    }

    partial void OnSortOrderChanged(PassageSortOrder value) => ApplyPassageFilter();

    /// <summary>Righe nell'ordine scelto; a parità di velocità vale l'orario di ingresso.</summary>
    private IEnumerable<PassageRow> Sorted(IEnumerable<PassageRow> rows) => SortOrder switch
    {
        PassageSortOrder.LastToFirst => rows.OrderByDescending(r => r.FirstTime).ThenByDescending(r => r.Id),
        PassageSortOrder.SpeedAscending => rows.OrderBy(r => r.SpeedKmh ?? double.MaxValue).ThenBy(r => r.FirstTime),
        PassageSortOrder.SpeedDescending => rows.OrderByDescending(r => r.SpeedKmh ?? double.MinValue).ThenBy(r => r.FirstTime),
        _ => rows.OrderBy(r => r.FirstTime).ThenBy(r => r.Id),
    };
}
