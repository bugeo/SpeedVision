using System.Globalization;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

public enum WorkflowStep
{
    Video,
    Road,
    Analysis,
    Calibration,
    Measurement,
    Results,
}

public enum ViewportMode
{
    None,
    DrawLineA,
    DrawLineB,
    PlacePoint,

    /// <summary>Calibrazione con punto di fuga: clic sul riferimento a terra A.</summary>
    PlaceRefA,

    /// <summary>Clic sul riferimento a terra B.</summary>
    PlaceRefB,

    /// <summary>Clic sul punto di fuga: col mouse si vedono le rette verso A e B.</summary>
    PlaceVanishing,
}

/// <summary>Linea modificata nel visore (trascinamento o nuovo disegno).</summary>
public sealed record LineEdit(DetectionLine Line, LineSegment Segment);

/// <summary>Punto disegnato sopra il video.</summary>
public sealed record OverlayPoint(Point2 Position, bool IsCurrentFrame, bool IsBeyondA, bool IsBeyondB);

/// <summary>Posizioni indicate a mano per un veicolo.</summary>
public sealed class ManualTrack
{
    private readonly SortedDictionary<long, TrackObservation> _byFrame = new();

    public IReadOnlyList<TrackObservation> Observations => _byFrame.Values.ToList();

    public int Count => _byFrame.Count;

    /// <summary>Aggiunge o sostituisce la posizione nel fotogramma indicato.</summary>
    public void Set(TrackObservation obs) => _byFrame[obs.FrameIndex] = obs;

    public bool Remove(long frameIndex) => _byFrame.Remove(frameIndex);

    public bool RemoveLast()
    {
        if (_byFrame.Count == 0)
        {
            return false;
        }

        return _byFrame.Remove(_byFrame.Keys.Last());
    }

    public bool TryGet(long frameIndex, out TrackObservation obs) => _byFrame.TryGetValue(frameIndex, out obs);

    public void Clear() => _byFrame.Clear();

    public ManualTrack Clone()
    {
        var copy = new ManualTrack();
        foreach (TrackObservation o in _byFrame.Values)
        {
            copy.Set(o);
        }

        return copy;
    }
}

public enum PassageSource
{
    /// <summary>Posizioni indicate a mano.</summary>
    Manual,

    /// <summary>Posizioni dal rilevamento automatico (modello + tracker).</summary>
    Automatic,
}

/// <summary>Passaggio salvato: le posizioni del veicolo, ricalcolate a ogni cambio di calibrazione.</summary>
public sealed record SavedTrack(
    int Id,
    ManualTrack Track,
    PassageSource Source = PassageSource.Manual,
    VehicleClass? Class = null,
    float Score = 0,
    IReadOnlyList<Core.Tracking.TrackPoint>? Points = null,
    bool AwaitingCalibration = false,
    int Lane = 0,
    Core.Tracking.ReferencePoint Reference = Core.Tracking.ReferencePoint.BoxCorner,
    bool? ToReview = null);

/// <summary>Riquadro di un veicolo da disegnare sopra il video (coordinate del video).</summary>
/// <param name="Heading">Punto da segnare: +1 vertice basso destro (verso destra), −1 sinistro, 2 entrambi (perni delle linee), 0 centro del lato inferiore (verso non ancora noto).</param>
public sealed record OverlayBox(BoundingBox Box, string Label, bool Confirmed, int Heading = 0);

/// <summary>Riga della tabella dei risultati.</summary>
public sealed class PassageRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool _isOverLimit;

    /// <summary>Velocità oltre il limite indicato nei risultati: la riga è bordata di rosso.</summary>
    public bool IsOverLimit
    {
        get => _isOverLimit;
        set => SetProperty(ref _isOverLimit, value);
    }

    private static CultureInfo It => Loc.Culture;

    public PassageRow(VehiclePassage passage, SavedTrack saved, Func<double, string> formatTime)
    {
        Passage = passage;
        Saved = saved;
        Track = saved.Track;
        EntryText = formatTime(passage.Entry.TimeSeconds);
        ExitText = formatTime(passage.Exit.TimeSeconds);
    }

    public VehiclePassage Passage { get; }

    public ManualTrack Track { get; }

    public SavedTrack Saved { get; }

    public bool IsAutomatic => Saved.Source == PassageSource.Automatic;

    /// <summary>Misura automatica fatta dove la ruota anteriore tocca l'asfalto: nei risultati compare l'icona della ruota.</summary>
    public bool IsWheelReference => IsAutomatic && Saved.Reference == Core.Tracking.ReferencePoint.FrontWheel;

    /// <summary>Punto del veicolo usato dalla misura automatica.</summary>
    public string ReferenceText => Saved.Reference == Core.Tracking.ReferencePoint.FrontWheel ? Loc.T("ruota anteriore") : Loc.T("spigolo del riquadro");

    public string SourceText => IsAutomatic
        ? Loc.T("Automatico") + " · " + ReferenceText + " · " + ClassName(Saved.Class) + " · " + (Saved.Score * 100).ToString("0", It) + "%"
        : Loc.T("Manuale");

    public static string ClassName(VehicleClass? c) => c switch
    {
        VehicleClass.Car => Loc.T("auto"),
        VehicleClass.Motorcycle => Loc.T("moto"),
        VehicleClass.Bus => Loc.T("autobus"),
        VehicleClass.Truck => Loc.T("camion"),
        VehicleClass.Bicycle => Loc.T("bicicletta"),
        _ => Loc.T("veicolo"),
    };

    public int Id => Passage.Id;

    public string IdText => Passage.Id.ToString("0000", It);

    public string DirectionText => Passage.Direction == TravelDirection.AToB ? "A → B" : "B → A";

    public string EntryText { get; }

    public string ExitText { get; }

    public string DurationText => Passage.Speed.DurationSeconds.ToString("0.000", It) + " s";

    /// <summary>Corsia (0 = la più vicina), −1 se fuori strada o non indicata.</summary>
    public int Lane { get; init; } = -1;

    /// <summary>«corsia N» quando la strada ha più corsie, altrimenti vuoto.</summary>
    public string LaneText { get; init; } = string.Empty;

    /// <summary>Velocità nascosta finché la distanza tra le linee è solo provvisoria (da stimare).</summary>
    public bool HideSpeed { get; init; }

    public string SpeedText => HideSpeed ? "—" : Passage.Speed.SpeedKmh.ToString("0.0", It);

    public string UncertaintyText => HideSpeed ? Loc.T("distanza da stimare") : "± " + Passage.Speed.UncertaintyKmh.ToString("0.0", It);

    public string MethodText => Describe(Passage.Entry.Method) + " / " + Describe(Passage.Exit.Method);

    public static string Describe(CrossingMethod m) => m switch
    {
        CrossingMethod.Interpolated => Loc.T("interpolazione"),
        CrossingMethod.Regression => Loc.T("regressione"),
        CrossingMethod.Extrapolated => Loc.T("ricostruito"),
        _ => m.ToString(),
    };
}
