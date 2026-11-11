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

/// <summary>Linguette dei risultati.</summary>
public enum ResultsTab
{
    /// <summary>Passaggi corretti: statistiche, grafico, esportazione e PDF.</summary>
    Valid,

    /// <summary>Passaggi da correggere: misurati, ma con la ruota non seguita bene o tolti dai corretti.</summary>
    Review,

    /// <summary>Veicoli scartati: senza un passaggio valido tra le linee, o eliminati da «Da correggere».</summary>
    Discarded,
}

/// <summary>Passaggio salvato: le posizioni del veicolo, ricalcolate a ogni cambio di calibrazione.</summary>
/// <param name="Discarded">L'utente l'ha spostato tra gli scartati.</param>
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
    bool? ToReview = null,
    bool Discarded = false);

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

    /// <param name="passage">Passaggio tra le linee; null se il veicolo non le attraversa (solo tra gli scartati).</param>
    /// <param name="failure">Perché non c'è un passaggio (veicoli scartati senza passaggio).</param>
    public PassageRow(VehiclePassage? passage, SavedTrack saved, Func<double, string> formatTime, string? failure = null)
    {
        Passage = passage;
        Saved = saved;
        Track = saved.Track;
        IReadOnlyList<TrackObservation> obs = saved.Track.Observations;
        FirstFrame = passage is not null ? passage.Entry.FirstBeyond.FrameIndex : obs.Count > 0 ? obs.Min(o => o.FrameIndex) : 0;
        LastFrame = passage is not null ? passage.Exit.FirstBeyond.FrameIndex : obs.Count > 0 ? obs.Max(o => o.FrameIndex) : 0;
        FirstTime = passage?.Entry.TimeSeconds ?? (obs.Count > 0 ? obs.Min(o => o.TimeSeconds) : 0);
        double last = passage?.Exit.TimeSeconds ?? (obs.Count > 0 ? obs.Max(o => o.TimeSeconds) : 0);
        EntryText = formatTime(FirstTime);
        ExitText = formatTime(last);
        FailureText = failure ?? string.Empty;
    }

    /// <summary>Passaggio tra le linee A e B; null se il veicolo non le attraversa (scartato).</summary>
    public VehiclePassage? Passage { get; }

    public bool HasPassage => Passage is not null;

    /// <summary>Fotogramma d'ingresso (senza passaggio: il primo in cui il veicolo è seguito).</summary>
    public long FirstFrame { get; }

    /// <summary>Fotogramma d'uscita (senza passaggio: l'ultimo in cui il veicolo è seguito).</summary>
    public long LastFrame { get; }

    /// <summary>Istante d'ingresso (senza passaggio: il primo in cui il veicolo è seguito), per l'ordinamento.</summary>
    public double FirstTime { get; }

    /// <summary>Perché il veicolo non ha un passaggio valido; vuoto se ce l'ha.</summary>
    public string FailureText { get; }

    /// <summary>Velocità, se c'è un passaggio (per ordinare e filtrare).</summary>
    public double? SpeedKmh => Passage?.Speed.SpeedKmh;

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

    public int Id => Saved.Id;

    public string IdText => Saved.Id.ToString("0000", It);

    public string DirectionText => Passage is null ? string.Empty : Passage.Direction == TravelDirection.AToB ? "A → B" : "B → A";

    public string EntryText { get; }

    public string ExitText { get; }

    public string DurationText => Passage is null ? Loc.T("non attraversa le linee") : Passage.Speed.DurationSeconds.ToString("0.000", It) + " s";

    /// <summary>Corsia (0 = la più vicina), −1 se fuori strada o non indicata.</summary>
    public int Lane { get; init; } = -1;

    /// <summary>«corsia N» quando la strada ha più corsie, altrimenti vuoto.</summary>
    public string LaneText { get; init; } = string.Empty;

    /// <summary>Velocità nascosta finché la distanza tra le linee è solo provvisoria (da stimare).</summary>
    public bool HideSpeed { get; init; }

    public string SpeedText => HideSpeed || Passage is null ? "—" : Passage.Speed.SpeedKmh.ToString("0.0", It);

    public string UncertaintyText => Passage is null ? string.Empty : HideSpeed ? Loc.T("distanza da stimare") : "± " + Passage.Speed.UncertaintyKmh.ToString("0.0", It);

    public string MethodText => Passage is null ? FailureText.TrimEnd('.') : Describe(Passage.Entry.Method) + " / " + Describe(Passage.Exit.Method);

    public static string Describe(CrossingMethod m) => m switch
    {
        CrossingMethod.Interpolated => Loc.T("interpolazione"),
        CrossingMethod.Regression => Loc.T("regressione"),
        CrossingMethod.Extrapolated => Loc.T("ricostruito"),
        _ => m.ToString(),
    };
}
