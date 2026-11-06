using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Localization;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Calibrazione «con due riferimenti trovati sulla mappa»: l'utente disegna le linee A e B su due punti
/// riconoscibili, poi su una mappa (OpenStreetMap o satellite) clicca dove le due linee incontrano lo stesso
/// bordo della strada. La distanza tra i due punti (ellissoide WGS84) diventa la distanza tra le linee.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Metodo scelto: due riferimenti trovati sulla mappa.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalibrationMethod), nameof(IsMethodStepActive), nameof(ShowDistanceCard))]
    private bool _isMapMode;

    private MapResult? _mapMeasurement;

    /// <summary>Le linee sono state spostate dopo la misura sulla mappa: la misura potrebbe non valere più.</summary>
    private bool _mapLinesChanged;

    public bool ShowMapCard => IsMapMode && LineA is not null && LineB is not null;

    public bool HasMapMeasurement => _mapMeasurement is not null;

    /// <summary>Guida: il riquadro della mappa è acceso finché manca la misura (o va rifatta).</summary>
    public bool IsMapStepActive => ShowMapCard && (_mapMeasurement is null || _mapLinesChanged || DistanceMeters is not > 0);

    public string MapButtonText => HasMapMeasurement ? Loc.T("Rimisura sulla mappa") : Loc.T("Apri la mappa");

    public string MapHint => HasMapMeasurement
        ? Loc.T("La distanza tra le linee viene dalla mappa. Puoi rimisurarla o correggerla qui sotto.")
        : Info?.Location is not null
            ? Loc.T("Apri la mappa (si apre sulla posizione registrata nel video) e clicca dove la linea A e la linea B incontrano lo stesso bordo della strada: la distanza tra i due punti diventa la distanza tra le linee.")
            : Loc.T("Apri la mappa, cerca il luogo della ripresa e clicca dove la linea A e la linea B incontrano lo stesso bordo della strada: la distanza tra i due punti diventa la distanza tra le linee.");

    public string MapResultText => _mapMeasurement is { } m
        ? Loc.S($"Sulla mappa: {m.Meters:0.00} m tra A e B (precisione dei clic circa ± {m.PrecisionMeters:0.00} m).")
        : string.Empty;

    public string? MapWarning =>
        _mapLinesChanged
            ? Loc.T("Hai spostato le linee dopo la misura sulla mappa: rimisura per essere sicuro che i punti corrispondano.")
        : _mapMeasurement is { } m && DistanceMeters is { } d && Math.Abs(d - Math.Round(m.Meters, 2)) > 0.005
            ? Loc.T("La distanza è stata modificata a mano dopo la misura sulla mappa.")
        : null;

    /// <summary>Titolo e spiegazione del riquadro della distanza, secondo il metodo.</summary>
    public string DistanceCardTitle => IsMapMode ? Loc.T("Distanza tra le linee") : Loc.T("Distanza misurata sul campo");

    public string DistanceCardHint => IsMapMode
        ? Loc.T("Misurata sulla mappa. Se conosci la distanza con più precisione puoi correggerla.")
        : Loc.T("Distanza reale tra le due linee lungo la direzione di marcia. È il dato da cui dipende la precisione: misurala con cura.");

    partial void OnIsMapModeChanged(bool value)
    {
        if (value)
        {
            IsDistanceKnownMode = false;
            EstimateDistanceFromVehicle = false;
            IsVanishingMode = false;
        }

        OnCalibrationMethodChanged();
        RefreshMapState();
    }

    /// <summary>Le linee sono cambiate: con una misura già fatta sulla mappa, va ricontrollata.</summary>
    private void OnLinesChangedForMap()
    {
        if (IsMapMode && _mapMeasurement is not null)
        {
            _mapLinesChanged = true;
        }

        RefreshMapState();
    }

    [RelayCommand]
    private void OpenMap()
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

        var window = new MapCalibrationWindow(request) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();

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
            ShowStatus(Loc.T("Mappa non disponibile"), error, InfoBarSeverity.Error);
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
        if (CurrentFrame is not BitmapSource frame || VideoWidth <= 0)
        {
            return null;
        }

        try
        {
            double scale = Math.Min(1, 720.0 / frame.PixelWidth);
            int w = (int)Math.Round(frame.PixelWidth * scale), h = (int)Math.Round(frame.PixelHeight * scale);
            double k = w / (double)VideoWidth;   // pixel dell'immagine per pixel del video
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawImage(frame, new Rect(0, 0, w, h));
                foreach ((Core.Geometry.LineSegment? line, Color color) in new[] { (LineA, Color.FromRgb(0x4C, 0xC2, 0xFF)), (LineB, Color.FromRgb(0xFF, 0xB5, 0x47)) })
                {
                    if (line is { } l)
                    {
                        var p1 = new Point(l.Start.X * k, l.Start.Y * k);
                        var p2 = new Point(l.End.X * k, l.End.Y * k);
                        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 6), p1, p2);
                        dc.DrawLine(new Pen(new SolidColorBrush(color), 3), p1, p2);
                    }
                }
            }

            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
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
        if (!IsMapMode || _mapMeasurement is not { } m)
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
        IsMapMode = false;
        _mapMeasurement = null;
        _mapLinesChanged = false;
        RefreshMapState();
    }

    private void RefreshMapState()
    {
        OnPropertyChanged(nameof(ShowMapCard));
        OnPropertyChanged(nameof(HasMapMeasurement));
        OnPropertyChanged(nameof(IsMapStepActive));
        OnPropertyChanged(nameof(MapButtonText));
        OnPropertyChanged(nameof(MapHint));
        OnPropertyChanged(nameof(MapResultText));
        OnPropertyChanged(nameof(MapWarning));
        OnPropertyChanged(nameof(DistanceCardTitle));
        OnPropertyChanged(nameof(DistanceCardHint));
        OnPropertyChanged(nameof(ShowDistanceCard));
    }
}
