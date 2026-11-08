using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Calibrazione «con due riferimenti a terra e punto di fuga»: l'utente clicca il riferimento A, il
/// riferimento B (due segni a terra di cui ha misurato la distanza lungo la strada) e poi il punto di fuga
/// delle linee trasversali alla strada. Le linee A e B passano per i riferimenti e puntano al punto di fuga,
/// così la distanza tra le due linee è la stessa su tutte le corsie; la distanza si inserisce come per i
/// due riferimenti misurati sul campo.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Metodo scelto: due riferimenti a terra e punto di fuga.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalibrationMethod), nameof(IsMethodStepActive), nameof(ShowDistanceCard))]
    private bool _isVanishingMode;

    [ObservableProperty]
    private Point2? _refPointA;

    [ObservableProperty]
    private Point2? _refPointB;

    [ObservableProperty]
    private Point2? _vanishingPoint;

    /// <summary>Riquadro dei riferimenti e del punto di fuga.</summary>
    public bool ShowVanishingCard => IsVanishingMode;

    /// <summary>Guida: il riquadro è acceso finché manca uno dei tre punti.</summary>
    public bool IsVanishingStepActive => IsVanishingMode && (RefPointA is null || RefPointB is null || VanishingPoint is null);

    public bool HasVanishingPoints => RefPointA is not null || RefPointB is not null || VanishingPoint is not null;

    // Mostrati sul video solo nella calibrazione con questo metodo.
    public Point2? ShownRefPointA => ShowVanishingOverlay ? RefPointA : null;

    public Point2? ShownRefPointB => ShowVanishingOverlay ? RefPointB : null;

    public Point2? ShownVanishingPoint => ShowVanishingOverlay ? VanishingPoint : null;

    private bool ShowVanishingOverlay => IsVanishingMode && SelectedStep == WorkflowStep.Calibration;

    public string RefPointAStatus => RefPointA is null ? Loc.T("Da posizionare") : Loc.T("Posizionato");

    public string RefPointBStatus => RefPointB is null ? Loc.T("Da posizionare") : Loc.T("Posizionato");

    public string VanishingPointStatus => VanishingPoint is null ? Loc.T("Da posizionare") : Loc.T("Posizionato");

    /// <summary>Cosa fare adesso, sotto il titolo del riquadro.</summary>
    public string VanishingHint =>
        RefPointA is null
            ? Loc.T("Clicca il riferimento A: un segno a terra, come l'inizio di una striscia.")
        : RefPointB is null
            ? Loc.T("Clicca il riferimento B: un altro segno a terra, a distanza nota da A.")
        : VanishingPoint is null
            ? Loc.T("Muovi il mouse finché le rette seguono le linee trasversali della strada (strisce, giunti) e clicca.")
        : Loc.T("Scrivi qui sotto la distanza misurata tra A e B.");

    partial void OnIsVanishingModeChanged(bool value)
    {
        if (value)
        {
            IsDistanceKnownMode = false;
            EstimateDistanceFromVehicle = false;
            LineA = null;
            LineB = null;
            if (VanishingPoint is not null)
            {
                ApplyVanishingLines();   // punti già indicati: si ritrovano le linee
            }
        }

        OnCalibrationMethodChanged();
        RefreshVanishingState();
    }

    partial void OnRefPointAChanged(Point2? value) => RefreshVanishingState();

    partial void OnRefPointBChanged(Point2? value) => RefreshVanishingState();

    partial void OnVanishingPointChanged(Point2? value) => RefreshVanishingState();

    /// <summary>Clic sul video nelle modalità dei tre punti.</summary>
    [RelayCommand]
    private void ReferencePlaced(Point2 p)
    {
        switch (ViewportMode)
        {
            case ViewportMode.PlaceRefA:
                RefPointA = Clamp(p);
                break;
            case ViewportMode.PlaceRefB:
                RefPointB = Clamp(p);
                break;
            case ViewportMode.PlaceVanishing:
                VanishingPoint = p;
                ApplyVanishingLines();
                break;
            default:
                return;
        }

        ViewportMode = NextCalibrationMode();
    }

    /// <summary>«Rimetti i punti»: si ricomincia dal riferimento A.</summary>
    [RelayCommand]
    private void ResetVanishingPoints()
    {
        RefPointA = null;
        RefPointB = null;
        VanishingPoint = null;
        LineA = null;
        LineB = null;
        ViewportMode = NextCalibrationMode();
    }

    /// <summary>Linee A e B: dai riferimenti verso il punto di fuga, lunghe quanto la strada (se indicata).</summary>
    private void ApplyVanishingLines()
    {
        if (RefPointA is not { } a || RefPointB is not { } b || VanishingPoint is not { } v)
        {
            return;
        }

        LineSegment Toward(Point2 p)
        {
            Point2 d = v - p;
            if (d.Length < 1)
            {
                d = new Point2(0, -1);
            }

            // Senza strada: dal riferimento un po' all'indietro e verso il punto di fuga, fino a poco prima.
            Point2 near = p - (d * 0.25), far = p + (d * 0.6);
            return ClipToImage(near, far) is { } c ? new LineSegment(c.A, c.B) : new LineSegment(p, p + (d * 0.1));
        }

        (LineSegment la, LineSegment lb) = AcrossRoad((Toward(a), Toward(b)));
        LineA = la;
        LineB = lb;
    }

    /// <summary>Distanza mancante: si evidenzia il riquadro della distanza anche con questo metodo.</summary>
    private bool IsVanishingDistanceStep => IsVanishingMode && LineA is not null && LineB is not null && DistanceMeters is not > 0;

    private void RefreshVanishingState()
    {
        OnPropertyChanged(nameof(ShowVanishingCard));
        OnPropertyChanged(nameof(IsVanishingStepActive));
        OnPropertyChanged(nameof(HasVanishingPoints));
        OnPropertyChanged(nameof(ShownRefPointA));
        OnPropertyChanged(nameof(ShownRefPointB));
        OnPropertyChanged(nameof(ShownVanishingPoint));
        OnPropertyChanged(nameof(RefPointAStatus));
        OnPropertyChanged(nameof(RefPointBStatus));
        OnPropertyChanged(nameof(VanishingPointStatus));
        OnPropertyChanged(nameof(VanishingHint));
        OnPropertyChanged(nameof(IsDistanceStepActive));
        OnPropertyChanged(nameof(ShowDistanceCard));
    }
}
