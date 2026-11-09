using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineAStatus))]
    private LineSegment? _lineA;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineBStatus))]
    private LineSegment? _lineB;

    [ObservableProperty]
    private string _distanceText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<CalibrationIssue> _calibrationIssues = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrationSummary))]
    [NotifyCanExecuteChangedFor(nameof(SaveCalibrationCommand))]
    private CalibrationSettings? _calibration;

    public bool IsCalibrationValid => Calibration is not null;

    public double? DistanceMeters => ParseDistance(DistanceText);

    public string LineAStatus => LineA is null ? Loc.T("Da disegnare") : Loc.T("Disegnata");

    // ───────────── Guida visiva: evidenzia il riquadro su cui agire ─────────────

    /// <summary>Mancano una o entrambe le linee: si evidenzia «Linee di rilevamento».</summary>
    public bool IsLinesStepActive => IsDistanceKnownMode && (LineA is null || LineB is null);

    /// <summary>Linee pronte, distanza mancante o non valida: si evidenzia «Distanza tra le linee».</summary>
    public bool IsDistanceStepActive =>
        (IsDistanceKnownMode && LineA is not null && LineB is not null && DistanceMeters is not > 0) || IsVanishingDistanceStep;

    /// <summary>
    /// Entrando nella calibrazione con una linea mancante il visore è già pronto a disegnarla:
    /// il primo clic sul video inizia la linea A (o la B, se A c'è già), senza cercare il pulsante.
    /// </summary>
    private ViewportMode NextCalibrationMode() =>
        IsVanishingMode
            ? RefPointA is null ? ViewportMode.PlaceRefA
            : RefPointB is null ? ViewportMode.PlaceRefB
            : VanishingPoint is null ? ViewportMode.PlaceVanishing
            : ViewportMode.None
        : !IsDistanceKnownMode ? ViewportMode.None
        : LineA is null ? ViewportMode.DrawLineA : LineB is null ? ViewportMode.DrawLineB : ViewportMode.None;

    public string LineBStatus => LineB is null ? Loc.T("Da disegnare") : Loc.T("Disegnata");

    public string CalibrationSummary
    {
        get
        {
            if (Calibration is not { } c)
            {
                return Loc.T("Calibrazione incompleta");
            }

            return Loc.S($"{c.DistanceMeters:0.##} m tra le linee");
        }
    }

    /// <summary>
    /// Errori da mostrare. «Manca la linea / la distanza» non si mostra: lo indica già la cornice verde
    /// del riquadro da completare, e un elenco rosso prima ancora di iniziare spaventa senza aiutare.
    /// </summary>
    public IEnumerable<CalibrationIssue> CalibrationErrors => CalibrationIssues
        .Where(i => i.Severity == IssueSeverity.Error && i.Code is not ("A_MISSING" or "B_MISSING" or "DIST_MISSING"));

    public IEnumerable<CalibrationIssue> CalibrationWarnings => CalibrationIssues.Where(i => i.Severity == IssueSeverity.Warning);

    // ───────────── Orario di inizio registrazione ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRecordingStart), nameof(RecordingStartError), nameof(CurrentClockText), nameof(HasClockTime),
        nameof(IsRecordingStartModified), nameof(IsRecordingStepActive))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmRecordingStartCommand))]
    private string _recordingStartText = string.Empty;

    /// <summary>Orario di inizio usato per CSV e immagini: quello inserito/corretto dall'utente.</summary>
    public DateTimeOffset? EffectiveRecordingStart => ParseRecordingStart(RecordingStartText);

    public bool HasClockTime => EffectiveRecordingStart is not null;

    /// <summary>Messaggio sotto la data: da completare (mancano cifre) oppure non valida (es. 31/02).</summary>
    public string? RecordingStartError =>
        !InputMask.HasDigits(RecordingStartText) || EffectiveRecordingStart is not null ? null
        : RecordingStartText.Contains(InputMask.Empty) ? Loc.T("Completa giorno, mese, anno, ore, minuti e secondi.")
        : Loc.T("Data o ora non valida: controlla i numeri.");

    public string RecordingStartSourceText => Info?.RecordingStartSource switch
    {
        null => Loc.T("Orario non presente nel video: scrivilo se ti serve."),
        "com.apple.quicktime.creationdate" => Loc.T("Letto dai metadati del video (ora locale del telefono). Verificalo."),
        _ => Loc.T("Letto dal video. Alcuni telefoni salvano l'ora di fine: verificalo."),
    };

    partial void OnRecordingStartTextChanged(string value) => RefreshPassageTimes();

    /// <summary>Formato della maschera di inserimento, con separatori fissi in tutte le lingue.</summary>
    internal const string RecordingStartFormat = "dd'/'MM'/'yyyy HH':'mm':'ss";

    internal static DateTimeOffset? ParseRecordingStart(string? text)
    {
        if (!InputMask.HasDigits(text) || text!.Contains(InputMask.Empty))
        {
            return null;   // vuota o ancora da completare
        }

        if (DateTime.TryParseExact(text.Trim(), RecordingStartFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime masked))
        {
            return new DateTimeOffset(masked);
        }

        string[] formats =
        [
            "dd/MM/yyyy HH:mm:ss.fff", "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm",
            "d/M/yyyy H:mm:ss", "d/M/yyyy H:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss",
        ];
        if (DateTime.TryParseExact(text.Trim(), formats, It, DateTimeStyles.AssumeLocal, out DateTime local))
        {
            return new DateTimeOffset(local);
        }

        return null;
    }

    // ───────────── Distanza ─────────────

    partial void OnDistanceTextChanged(string value)
    {
        OnPropertyChanged(nameof(DistanceMeters));
        UpdateCalibration();
        RefreshMapState();
    }

    internal static double? ParseDistance(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string t = text.Trim().TrimEnd('m', 'M').Trim().Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : null;
    }

    // ───────────── Linee ─────────────

    partial void OnLineAChanged(LineSegment? value) => OnLinesChanged();

    partial void OnLineBChanged(LineSegment? value) => OnLinesChanged();

    private void OnLinesChanged()
    {
        // Linee complete (per esempio da una calibrazione caricata): fine della modalità disegno.
        if (LineA is not null && LineB is not null && ViewportMode is ViewportMode.DrawLineA or ViewportMode.DrawLineB)
        {
            ViewportMode = ViewportMode.None;
        }

        UpdateCalibration();
        OnLinesChangedForMap();
    }

    [RelayCommand]
    private void DrawLineA()
    {
        SelectedStep = WorkflowStep.Calibration;
        ViewportMode = ViewportMode == ViewportMode.DrawLineA ? ViewportMode.None : ViewportMode.DrawLineA;
    }

    [RelayCommand]
    private void DrawLineB()
    {
        SelectedStep = WorkflowStep.Calibration;
        ViewportMode = ViewportMode == ViewportMode.DrawLineB ? ViewportMode.None : ViewportMode.DrawLineB;
    }

    [RelayCommand]
    private void CancelDrawing()
    {
        if (ViewportMode is ViewportMode.DrawLineA or ViewportMode.DrawLineB)
        {
            ViewportMode = ViewportMode.None;
        }
    }

    /// <summary>Chiamato dal visore quando una linea viene disegnata o trascinata.</summary>
    [RelayCommand]
    private void LineEdited(LineEdit? edit)
    {
        if (edit is null || edit.Segment.Length < 1)
        {
            return;
        }

        LineSegment clamped = new(Clamp(edit.Segment.Start), Clamp(edit.Segment.End));
        if (edit.Line == DetectionLine.A)
        {
            LineA = clamped;
        }
        else
        {
            LineB = clamped;
        }

        if (ViewportMode is ViewportMode.DrawLineA or ViewportMode.DrawLineB)
        {
            // Dopo A propone subito B, se manca.
            ViewportMode = edit.Line == DetectionLine.A && LineB is null ? ViewportMode.DrawLineB : ViewportMode.None;
        }
    }

    private Point2 Clamp(Point2 p) => new(Math.Clamp(p.X, 0, VideoWidth), Math.Clamp(p.Y, 0, VideoHeight));

    [RelayCommand]
    private void SwapLines() => (LineA, LineB) = (LineB, LineA);

    [RelayCommand]
    private void ClearLines()
    {
        LineA = null;
        LineB = null;
        ViewportMode = ViewportMode.DrawLineA;
    }

    private void UpdateCalibration()
    {
        OnPropertyChanged(nameof(IsLinesStepActive));
        OnPropertyChanged(nameof(IsDistanceStepActive));
        double? distance = EffectiveDistanceMeters;
        IReadOnlyList<CalibrationIssue> issues = CalibrationValidator.Validate(LineA, LineB, distance);
        if (!IsDistanceKnownMode)
        {
            // La distanza è la lunghezza del veicolo: breve per definizione, e le linee arrivano dopo l'analisi.
            issues = issues.Where(i => i.Code is not ("DIST_SHORT" or "DIST_MISSING" or "A_MISSING" or "B_MISSING")).ToList();
        }

        CalibrationIssues = issues;
        OnPropertyChanged(nameof(CalibrationErrors));
        OnPropertyChanged(nameof(CalibrationWarnings));

        Calibration = CalibrationValidator.HasErrors(CalibrationIssues) || LineA is null || LineB is null || distance is null
            ? null
            : new CalibrationSettings(LineA.Value, LineB.Value, distance.Value);

        OnPropertyChanged(nameof(IsCalibrationValid));
        OnCalibrationChanged();
        RefreshEstimateState();
    }

    /// <summary>
    /// Mantiene linee e distanza per il nuovo video. Se le linee venivano da un'auto di riferimento,
    /// la sua lunghezza diventa la distanza (l'auto non è nel nuovo video).
    /// </summary>
    private void KeepCalibrationForNewVideo()
    {
        if (EstimateDistanceFromVehicle && EffectiveDistanceMeters is { } length)
        {
            IsDistanceKnownMode = true;
            DistanceText = length.ToString("0.###", It);
        }
    }

    /// <summary>Nuovo video senza riprendere la calibrazione: tutto torna allo stato iniziale.</summary>
    private void ResetEverythingForNewVideo()
    {
        LineA = null;
        LineB = null;
        DistanceText = string.Empty;
        IsDistanceKnownMode = false;
        EstimateDistanceFromVehicle = false;
        ResetMapForNewVideo();
        ResetFilter();
        AnalysisStatusText = string.Empty;
        AnalysisSpeedText = string.Empty;
        AnalysisCounts = null;
        AnalysisProgressValue = 0;
        BenchmarkText = string.Empty;
    }

    private void ResetCalibrationForNewVideo()
    {
        // Le linee restano se il nuovo video ha le stesse dimensioni (stessa posizione del treppiede).
        if (LineA is { } a && (a.Start.X > VideoWidth || a.End.X > VideoWidth || a.Start.Y > VideoHeight || a.End.Y > VideoHeight))
        {
            LineA = null;
        }

        if (LineB is { } b && (b.Start.X > VideoWidth || b.End.X > VideoWidth || b.Start.Y > VideoHeight || b.End.Y > VideoHeight))
        {
            LineB = null;
        }

        // Punto di fuga: senza le linee (video di altre dimensioni) i punti non valgono più.
        if (IsVanishingMode && (LineA is null || LineB is null))
        {
            RefPointA = null;
            RefPointB = null;
            VanishingPoint = null;
        }

        ViewportMode = SelectedStep == WorkflowStep.Calibration ? NextCalibrationMode() : ViewportMode.None;
        UpdateCalibration();
    }

    // ───────────── Salvataggio calibrazione ─────────────

    [RelayCommand(CanExecute = nameof(IsCalibrationValid))]
    private async Task SaveCalibrationAsync()
    {
        if (Calibration is not { } c)
        {
            return;
        }

        if (IsDistanceProvisional)
        {
            ShowStatus(Loc.T("Distanza non ancora stimata"), Loc.T("Salva la calibrazione dopo aver indicato il modello di un'auto."), StatusSeverity.Warning);
            return;
        }

        string? file = await _platform.PickSaveFileAsync(
            Loc.T("Salva calibrazione"),
            [new FileFilter(Loc.T("Calibrazione"), ["*.tsacal.json"])],
            Path.GetFileNameWithoutExtension(Info is null ? "calibrazione" : SourceFileName) + ".tsacal.json",
            ".tsacal.json",
            _settings.LastVideoFolder);
        if (file is null)
        {
            return;
        }

        try
        {
            await CalibrationFile.From(VideoWidth, VideoHeight, c.LineA, c.LineB, c.DistanceMeters, Info?.FileName).SaveAsync(file);
            ShowStatus(Loc.T("Calibrazione salvata"), Loc.T("Potrai riusarla per altri video girati dalla stessa posizione."), StatusSeverity.Success);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus(Loc.T("Salvataggio non riuscito"), e.Message, StatusSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task LoadCalibrationAsync()
    {
        string? path = await _platform.PickOpenFileAsync(
            Loc.T("Carica calibrazione"),
            [new FileFilter(Loc.T("Calibrazione"), ["*.tsacal.json", "*.json"])],
            _settings.LastVideoFolder);
        if (path is null)
        {
            return;
        }

        try
        {
            CalibrationFile file = await CalibrationFile.LoadAsync(path);
            if (!file.HasSameAspect(VideoWidth, VideoHeight))
            {
                ShowStatus(Loc.T("Proporzioni diverse"),
                    Loc.T("La calibrazione è stata fatta su un video con proporzioni diverse: le linee potrebbero non corrispondere."),
                    StatusSeverity.Warning);
            }

            (LineSegment a, LineSegment b) = file.ScaledTo(VideoWidth, VideoHeight);
            LineA = a;
            LineB = b;
            IsDistanceKnownMode = true;
            DistanceText = file.DistanceMeters.ToString("0.###", It);
            ViewportMode = ViewportMode.None;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowStatus(Loc.T("Calibrazione non valida"), e.Message, StatusSeverity.Error);
        }
    }

    [RelayCommand]
    private void GoToStep(string? step)
    {
        if (Enum.TryParse(step, out WorkflowStep s))
        {
            SelectedStep = s;
        }
    }
}
