using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Distanza tra le linee rilevata sulla mappa: nel riquadro della distanza «Rileva su mappa» apre una mappa
/// (OpenStreetMap o satellite) dove l'utente clicca i punti in cui le linee A e B incontrano lo stesso bordo della
/// strada. La distanza tra i due punti (ellissoide WGS84) viene scritta nella casella della distanza, dove si può
/// ancora correggere. Vale per i metodi con due riferimenti (sul campo e con il punto di fuga).
/// </summary>
public sealed partial class MainViewModel
{
    private MapResult? _mapMeasurement;

    /// <summary>Le linee sono state spostate dopo la misura sulla mappa: la misura potrebbe non valere più.</summary>
    private bool _mapLinesChanged;

    public bool HasMapMeasurement => _mapMeasurement is not null;

    /// <summary>La distanza in uso è quella rilevata sulla mappa (linee non spostate, distanza non corretta a mano).</summary>
    private bool IsDistanceFromMap =>
        (IsDistanceKnownMode || IsVanishingMode) && _mapMeasurement is { } m && !_mapLinesChanged &&
        DistanceMeters is { } d && Math.Abs(d - Math.Round(m.Meters, 2)) <= 0.005;

    public string MapResultText => _mapMeasurement is { } m
        ? Loc.S($"Sulla mappa: {m.Meters:0.00} m tra A e B (precisione dei clic circa ± {m.PrecisionMeters:0.00} m).")
        : string.Empty;

    public string? MapWarning =>
        _mapLinesChanged
            ? Loc.T("Hai spostato le linee dopo la misura sulla mappa: rimisura.")
        : _mapMeasurement is { } m && DistanceMeters is { } d && Math.Abs(d - Math.Round(m.Meters, 2)) > 0.005
            ? Loc.T("La distanza è stata modificata a mano dopo la misura sulla mappa.")
        : null;

    /// <summary>Nota sotto la casella della distanza: solo per una distanza presa dalla mappa.</summary>
    public string? DistanceCardHint => IsDistanceFromMap
        ? Loc.T("Misurata sulla mappa: puoi correggerla.")
        : null;

    /// <summary>Le linee sono cambiate: con una misura già fatta sulla mappa, va ricontrollata.</summary>
    private void OnLinesChangedForMap()
    {
        if (_mapMeasurement is not null)
        {
            _mapLinesChanged = true;
        }

        RefreshMapState();
    }

    [RelayCommand]
    private async Task OpenMapAsync()
    {
        if (LineA is null || LineB is null)
        {
            return;
        }

        StopPlayback();
        GeoPosition? center = _mapMeasurement is { } last ? last.ViewCenter
            : Info?.Location ?? (_settings.MapLatitude is { } lat && _settings.MapLongitude is { } lon ? new GeoPosition(lat, lon) : null);
        double? zoom = _mapMeasurement?.ViewZoom ?? (Info?.Location is not null ? 19 : _settings.MapZoom);
        var request = new MapRequest(
            center,
            zoom,
            CenterFromVideo: _mapMeasurement is null && Info?.Location is not null,
            VideoLocation: Info?.Location,
            A: _mapMeasurement?.A,
            B: _mapMeasurement?.B,
            SnapshotDataUrl: BuildCalibrationSnapshot(),
            Layer: _settings.MapLayer ?? "map");

        MapOutcome window = await _platform.ShowMapAsync(request);

        if (window.LastView is { } view)
        {
            _settings.MapLatitude = view.Center.Latitude;
            _settings.MapLongitude = view.Center.Longitude;
            _settings.MapZoom = view.Zoom;
            _settings.MapLayer = view.Layer;
            _settings.Save();
        }

        if (window.Error is { } error)
        {
            ShowStatus(Loc.T("Mappa non disponibile"), error, StatusSeverity.Error);
            return;
        }

        if (window.Result is not { } result)
        {
            return;
        }

        _mapMeasurement = result;
        _mapLinesChanged = false;
        DistanceText = result.Meters.ToString("0.00", It);
        AppLog.Info($"Distanza dalla mappa: A {result.A}, B {result.B}, {result.Meters:0.000} m ({result.Layer})");
        RefreshMapState();
    }

    /// <summary>Fotogramma corrente con le linee A e B, in piccolo, da tenere sotto gli occhi mentre si cerca sulla mappa.</summary>
    private string? BuildCalibrationSnapshot()
    {
        if (_currentVideoFrame is not { } frame || VideoWidth <= 0)
        {
            return null;
        }

        try
        {
            byte[] jpeg = PassageImageRenderer.RenderSnapshotJpeg(frame, VideoWidth, LineA, LineB, maxWidth: 720, quality: 85);
            return "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            AppLog.Warn("Anteprima per la mappa non riuscita: " + e.Message);
            return null;
        }
    }

    /// <summary>Righe di info.txt sulla misura fatta sulla mappa.</summary>
    private IEnumerable<string> MapInfoLines()
    {
        if (!(IsDistanceKnownMode || IsVanishingMode) || _mapMeasurement is not { } m)
        {
            yield break;
        }

        string layer = m.Layer == "satellite" ? Loc.T("immagini satellitari Esri") : "OpenStreetMap";
        yield return Loc.S($"Distanza tra le linee misurata sulla mappa ({layer}): {m.Meters:0.000} m, precisione dei clic circa ± {m.PrecisionMeters:0.00} m.");
        yield return Loc.S($"  Punto A: {m.A} · punto B: {m.B} (WGS84, distanza sull'ellissoide).");
        if (MapWarning is { } w)
        {
            yield return "  " + w;
        }
    }

    private void ResetMapForNewVideo()
    {
        _mapMeasurement = null;
        _mapLinesChanged = false;
        RefreshMapState();
    }

    private void RefreshMapState()
    {
        OnPropertyChanged(nameof(HasMapMeasurement));
        OnPropertyChanged(nameof(MapResultText));
        OnPropertyChanged(nameof(MapWarning));
        OnPropertyChanged(nameof(DistanceCardHint));
        OnPropertyChanged(nameof(ShowDistanceCard));
    }
}
