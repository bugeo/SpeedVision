using System.Collections.ObjectModel;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>Colonna del grafico: passaggi in una fascia di velocità.</summary>
/// <param name="OverLimit">Fascia sopra il limite di velocità: colonna rossa.</param>
public sealed record HistogramBin(double From, double? To, int Count, double BarHeight, bool ShowLabel, bool OverLimit = false)
{
    /// <summary>Etichetta sotto la colonna: limite inferiore, una sì e una no per non affollare l'asse.</summary>
    public string Label => ShowLabel ? (To is null ? "100+" : From.ToString("0", Loc.Culture)) : string.Empty;

    public string CountText => Count > 0 ? Count.ToString(Loc.Culture) : string.Empty;

    public string ToolTip => To is { } to
        ? Loc.S($"{From:0}–{to:0} km/h: {Count} passaggi")
        : Loc.S($"oltre {From:0} km/h: {Count} passaggi");
}

/// <summary>
/// Grafico dei risultati: quanti passaggi (mostrati dal filtro) per fascia di velocità, da 0 a 100 km/h
/// a scaglioni di 5 km/h; se qualcuno supera i 100 km/h si aggiunge la colonna «100+».
/// </summary>
public sealed partial class MainViewModel
{
    public const double HistogramStepKmh = 5, HistogramMaxKmh = 100;

    /// <summary>Altezza in pixel della colonna più alta.</summary>
    private const double HistogramBarMaxPx = 110;

    public ObservableCollection<HistogramBin> SpeedHistogram { get; } = [];

    public bool ShowHistogram => Passages.Count > 0 && !IsDistanceProvisional;

    /// <summary>Valore massimo dell'asse delle quantità.</summary>
    public string HistogramMaxText => SpeedHistogram.Count == 0 ? string.Empty
        : Loc.S($"max {SpeedHistogram.Max(b => b.Count)} per fascia");

    private void RefreshHistogram()
    {
        SpeedHistogram.Clear();
        if (Passages.Count > 0 && !IsDistanceProvisional)
        {
            int bins = (int)(HistogramMaxKmh / HistogramStepKmh);
            var counts = new int[bins + 1];   // l'ultima: oltre 100 km/h
            foreach (PassageRow r in Passages)
            {
                double v = r.Passage.Speed.SpeedKmh;
                int i = v >= HistogramMaxKmh ? bins : Math.Max(0, (int)Math.Floor(v / HistogramStepKmh));
                counts[i]++;
            }

            int max = Math.Max(1, counts.Max());
            for (int i = 0; i < bins; i++)
            {
                double from = i * HistogramStepKmh;
                SpeedHistogram.Add(new HistogramBin(from, from + HistogramStepKmh, counts[i], HistogramBarMaxPx * counts[i] / max, i % 2 == 0,
                    SpeedLimitKmh is { } limit && from >= limit));
            }

            if (counts[bins] > 0)
            {
                SpeedHistogram.Add(new HistogramBin(HistogramMaxKmh, null, counts[bins], HistogramBarMaxPx * counts[bins] / max, true,
                    SpeedLimitKmh is { } limit && HistogramMaxKmh >= limit));
            }
        }

        OnPropertyChanged(nameof(ShowHistogram));
        OnPropertyChanged(nameof(HistogramMaxText));
    }
}
