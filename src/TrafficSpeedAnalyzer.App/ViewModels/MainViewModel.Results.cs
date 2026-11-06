using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Export;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Privacy;
using TrafficSpeedAnalyzer.Core.Time;
using TrafficSpeedAnalyzer.Video;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using TrafficSpeedAnalyzer.Core.Localization;
using TrafficSpeedAnalyzer.App;

namespace TrafficSpeedAnalyzer.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly List<SavedTrack> _savedTracks = [];
    private bool _hasUnexportedChanges;
    private string? _lastExportFolder;

    /// <summary>Passaggi mostrati ed esportati (dopo il filtro).</summary>
    public ObservableCollection<PassageRow> Passages { get; } = [];

    /// <summary>Tutti i passaggi validi con la calibrazione attuale, prima del filtro.</summary>
    private readonly List<PassageRow> _allRows = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeletePassageCommand), nameof(ShowEntryFrameCommand), nameof(ShowExitFrameCommand))]
    private PassageRow? _selectedPassage;

    [ObservableProperty]
    private int _invalidPassageCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenExportFolderCommand))]
    private bool _hasExportFolder;

    public bool HasPassages => Passages.Count > 0;

    /// <summary>Con il filtro attivo il pulsante dice quanti passaggi verranno esportati.</summary>
    public string ExportButtonText => IsFilterActive
        ? Loc.S($"Esporta i {Passages.Count} passaggi filtrati…")
        : Loc.T("Esporta CSV, PDF e immagini…");

    public string PassageCountText => (Passages.Count switch
    {
        0 => Loc.T("Nessun passaggio"),
        1 => "1 passaggio",
        int n => string.Create(It, $"{n} passaggi"),
    }) + (Passages.Count != _allRows.Count ? Loc.S($" su {_allRows.Count} (filtro attivo)") : string.Empty);

    public string MeanSpeedText => Passages.Count == 0 || IsDistanceProvisional ? "–" : Passages.Average(p => p.Passage.Speed.SpeedKmh).ToString("0.0", It);

    public string MinSpeedText => Passages.Count == 0 || IsDistanceProvisional ? "–" : Passages.Min(p => p.Passage.Speed.SpeedKmh).ToString("0.0", It);

    public string MaxSpeedText => Passages.Count == 0 || IsDistanceProvisional ? "–" : Passages.Max(p => p.Passage.Speed.SpeedKmh).ToString("0.0", It);

    public string InvalidPassagesText => Loc.S($"{InvalidPassageCount} misure non sono valide con la calibrazione attuale e sono escluse. Ripristina la calibrazione o eliminale.");

    private int? _lastShownPassageId;

    partial void OnSelectedPassageChanged(PassageRow? value)
    {
        RefreshOverlay();

        // Cliccando un risultato il video va all'ingresso del veicolo. Solo quando cambia veicolo:
        // la riselezione automatica dopo un ricalcolo non deve spostare il video.
        if (value is not null && value.Id != _lastShownPassageId && SelectedStep == WorkflowStep.Results && !IsAnalyzing)
        {
            GoToFrame((int)value.Passage.Entry.FirstBeyond.FrameIndex);
        }

        _lastShownPassageId = value?.Id;
    }

    /// <summary>Ricalcola tutti i passaggi salvati (cambiano se cambia la calibrazione).</summary>
    private void RebuildPassages()
    {
        int? selectedId = SelectedPassage?.Id;
        _allRows.Clear();
        int invalid = 0;
        if (Calibration is { } cal)
        {
            foreach (SavedTrack saved in _savedTracks)
            {

                PassageOptions options = saved.Source == PassageSource.Automatic
                    ? Core.Tracking.TrackPassageOptions.Default.Passage
                    : PassageOptions.Default;
                PassageResult r = PassageAnalyzer.Analyze(saved.Id, saved.Track.Observations, cal, options);
                if (r.Passage is { } p)
                {
                    int lane = TrackLane(saved);
                    _allRows.Add(new PassageRow(p, saved, FormatTime)
                    {
                        HideSpeed = IsDistanceProvisional,
                        LaneText = Road is { Lanes: > 1 } && lane >= 0 ? string.Create(It, $"corsia {lane + 1}") : string.Empty,
                        Lane = lane,
                    });
                }
                else if (!saved.AwaitingCalibration)
                {
                    invalid++;
                }
            }
        }
        else
        {
            invalid = _savedTracks.Count(t => !t.AwaitingCalibration);
        }

        InvalidPassageCount = invalid;
        _hasUnexportedChanges = true;
        ApplyPassageFilter(selectedId);
    }

    /// <summary>Aggiorna l'elenco mostrato secondo il filtro (velocità minima e tipo di veicolo).</summary>
    private void ApplyPassageFilter(int? selectedId = null)
    {
        selectedId ??= SelectedPassage?.Id;
        Passages.Clear();
        foreach (PassageRow row in _allRows.Where(PassesFilter))
        {
            Passages.Add(row);
        }

        SelectedPassage = Passages.FirstOrDefault(p => p.Id == selectedId);
        OnPropertyChanged(nameof(HasPassages));
        OnPropertyChanged(nameof(HasAnyPassage));
        OnPropertyChanged(nameof(HasTrackedVehicles));
        OnPropertyChanged(nameof(ShowAnalysisProceed));
        OnPropertyChanged(nameof(ShowGoToAnalysis));
        OnPropertyChanged(nameof(IsReferenceStepActive));
        OnPropertyChanged(nameof(DistanceEstimateHint));
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(ExportButtonText));
        OnPropertyChanged(nameof(PassageCountText));
        OnPropertyChanged(nameof(MeanSpeedText));
        OnPropertyChanged(nameof(MinSpeedText));
        OnPropertyChanged(nameof(MaxSpeedText));
        RefreshHistogram();
        OnPropertyChanged(nameof(InvalidPassagesText));
        ExportResultsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshPassageTimes()
    {
        RebuildPassages();
        AnalyzeCurrentTrack();
    }

    private bool HasSelection() => SelectedPassage is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeletePassage()
    {
        if (SelectedPassage is not { } row)
        {
            return;
        }

        _savedTracks.RemoveAll(t => t.Id == row.Id);
        RebuildPassages();
    }

    [RelayCommand]
    private void DeleteInvalidPassages()
    {
        var validIds = _allRows.Select(p => p.Id).ToHashSet();
        _savedTracks.RemoveAll(t => !validIds.Contains(t.Id));
        RebuildPassages();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowEntryFrame()
    {
        if (SelectedPassage is { } row)
        {
            GoToFrame((int)row.Passage.Entry.FirstBeyond.FrameIndex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowExitFrame()
    {
        if (SelectedPassage is { } row)
        {
            GoToFrame((int)row.Passage.Exit.FirstBeyond.FrameIndex);
        }
    }

    /// <summary>Riapre un passaggio salvato nella misura manuale per correggerlo.</summary>
    [RelayCommand]
    private void EditPassage(PassageRow? row)
    {
        row ??= SelectedPassage;
        if (row is null)
        {
            return;
        }

        if (_currentTrack.Count > 0)
        {
            ShowStatus(Loc.T("Misura in corso"), Loc.T("Salva o annulla il veicolo che stai misurando prima di modificarne un altro."), InfoBarSeverity.Warning);
            return;
        }

        _savedTracks.RemoveAll(t => t.Id == row.Id);
        _editBackup = row.Saved;
        _currentTrack = row.Track.Clone();
        EditingId = row.Id;
        RebuildPassages();
        SelectedStep = WorkflowStep.Measurement;
        AnalyzeCurrentTrack();
        GoToFrame((int)row.Passage.Entry.FirstBeyond.FrameIndex);
        ShowStatus(Loc.T("Modifica del veicolo ") + row.IdText, Loc.T("Correggi i punti e salva di nuovo. «Nuovo veicolo» annulla la modifica."), InfoBarSeverity.Informational);
    }

    private bool CanExport() => HasPassages && !IsBusy && !IsAnalyzing && !IsDistanceProvisional;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportResultsAsync()
    {
        if (_session is null || Calibration is not { } cal || Passages.Count == 0)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("Scegli dove salvare CSV, PDF e immagini"),
            InitialDirectory = Directory.Exists(_settings.LastExportFolder) ? _settings.LastExportFolder : _settings.LastVideoFolder ?? string.Empty,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        StopPlayback();
        string baseName = Path.GetFileNameWithoutExtension(SourceFileName);
        string folder = Path.Combine(dialog.FolderName, string.Create(CultureInfo.InvariantCulture, $"{baseName}_misure_{DateTime.Now:yyyyMMdd_HHmmss}"));
        List<PassageRow> rows = [.. Passages.OrderBy(p => p.Id)];
        DateTimeOffset? start = EffectiveRecordingStart;
        VideoSession session = _session;

        IsBusy = true;
        try
        {
            Directory.CreateDirectory(folder);
            string clearFolder = Path.Combine(folder, ClearImagesFolder);
            string anonFolder = Path.Combine(folder, AnonymizedImagesFolder);
            Directory.CreateDirectory(clearFolder);
            Directory.CreateDirectory(anonFolder);
            // I due rapporti PDF (immagini in chiaro e veicoli pixellati) si scrivono mentre si esportano le immagini:
            // una pagina per veicolo.
            byte[]? logo = ReportLogo();
            bool showLane = Road is { Lanes: > 1 };
            var summary = new PdfReportSummary(
                SourceFileName,
                start?.ToString("dd/MM/yyyy HH:mm:ss", It),
                cal.DistanceMeters,
                CalibrationMethodText,
                FilterDescription,
                rows,
                [.. SpeedHistogram]);
            await using (FileStream clearStream = File.Create(Path.Combine(folder, Loc.T("rapporto") + ".pdf")))
            await using (FileStream anonStream = File.Create(Path.Combine(folder, Loc.T("rapporto_anonimizzato") + ".pdf")))
            using (var clearReport = new PdfReport(clearStream, SourceFileName, rows.Count, showLane, start, anonymized: false, logo))
            using (var anonReport = new PdfReport(anonStream, SourceFileName, rows.Count, showLane, start, anonymized: true, logo))
            {
                clearReport.AddSummary(summary);
                anonReport.AddSummary(summary);
                for (int i = 0; i < rows.Count; i++)
                {
                    BusyText = Loc.S($"Esportazione immagini {i + 1} di {rows.Count}…");
                    PassageImages images = await ExportImagesAsync(session, cal, rows[i], start, clearFolder, anonFolder);
                    clearReport.AddPassage(rows[i], images.ClearEntry, images.ClearExit, i + 2);
                    anonReport.AddPassage(rows[i], images.AnonEntry, images.AnonExit, i + 2);
                }
            }

            BusyText = Loc.T("Scrittura del CSV…");
            IReadOnlyDictionary<int, int>? lanes = Road is { Lanes: > 1 }
                ? rows.Where(r => r.Lane >= 0).ToDictionary(r => r.Id, r => r.Lane + 1)
                : null;
            await CsvExporter.WriteAsync(Path.Combine(folder, Loc.T("passaggi") + ".csv"), rows.Select(r => r.Passage), start, default, lanes);
            await CalibrationFile.From(VideoWidth, VideoHeight, cal.LineA, cal.LineB, cal.DistanceMeters, SourceFileName)
                .SaveAsync(Path.Combine(folder, "calibrazione.tsacal.json"));
            await File.WriteAllTextAsync(Path.Combine(folder, "info.txt"), BuildInfoText(session, cal, rows, start), new UTF8Encoding(true));

            _settings.LastExportFolder = dialog.FolderName;
            _settings.Save();
            _lastExportFolder = folder;
            HasExportFolder = true;
            _hasUnexportedChanges = false;
            RefreshStepProgress();
            AppLog.Info($"Esportati {rows.Count} passaggi in {folder}");
            ShowStatus(Loc.T("Esportazione completata"), Loc.S($"{rows.Count} passaggi salvati in {folder}"), InfoBarSeverity.Success);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or VideoProbeException or InvalidOperationException)
        {
            AppLog.Error("Esportazione non riuscita", e);
            ShowStatus(Loc.T("Esportazione non riuscita"), e.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string ClearImagesFolder => Loc.T("immagini");

    private static string AnonymizedImagesFolder => Loc.T("immagini_anonimizzate");

    /// <summary>
    /// Zone da pixellare in un fotogramma: tutti i veicoli tracciati presenti (anche quelli nascosti dal filtro).
    /// Per i veicoli automatici si usa il riquadro; per quelli misurati a mano, un'area attorno al punto indicato.
    /// </summary>
    private List<BoundingBox> PrivacyRegionsAt(long frameIndex)
    {
        var regions = new List<BoundingBox>();
        foreach (SavedTrack t in _savedTracks)
        {
            if (t.Points is { Count: > 0 } points)
            {
                if (PrivacyMasking.BoxAt(points, frameIndex) is { } box)
                {
                    regions.AddRange(PrivacyMasking.RegionsFor(box, VideoWidth, VideoHeight));
                }
            }
            else if (t.Track.TryGet(frameIndex, out TrackObservation obs))
            {
                regions.Add(PrivacyMasking.RegionAroundPoint(obs.Position.X, obs.Position.Y, VideoWidth, VideoHeight));
            }
        }

        return regions;
    }

    /// <summary>Logo per la prima pagina del rapporto PDF (risorsa dell'applicazione); null se non disponibile.</summary>
    private static byte[]? ReportLogo()
    {
        try
        {
            using Stream? stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/speedvision-logo-report.jpg"))?.Stream;
            if (stream is null)
            {
                return null;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch (IOException e)
        {
            AppLog.Error("Logo del rapporto non disponibile", e);
            return null;
        }
    }

    /// <summary>Metodo di calibrazione scelto, con lo stesso testo dell'opzione nell'interfaccia.</summary>
    private string CalibrationMethodText =>
        IsMapMode ? Loc.T("Con due riferimenti trovati sulla mappa")
        : IsVanishingMode ? Loc.T("Con due riferimenti a terra e punto di fuga")
        : EstimateDistanceFromVehicle ? Loc.T("Con un'auto che riconosco")
        : Loc.T("Con due riferimenti misurati sul campo");

    /// <summary>Immagini JPEG di un passaggio, in chiaro e anonimizzate, per i rapporti PDF.</summary>
    private sealed record PassageImages(byte[] ClearEntry, byte[] ClearExit, byte[] AnonEntry, byte[] AnonExit);

    /// <summary>Salva le immagini in chiaro e anonimizzate e le restituisce per i rapporti PDF.</summary>
    private async Task<PassageImages> ExportImagesAsync(VideoSession session, CalibrationSettings cal, PassageRow row, DateTimeOffset? start, string clearFolder, string anonFolder)
    {
        VehiclePassage p = row.Passage;
        var clear = new byte[2][];
        var anonymized = new byte[2][];
        foreach (bool isEntry in new[] { true, false })
        {
            CrossingEstimate e = isEntry ? p.Entry : p.Exit;
            DetectionLine line = isEntry ? p.EntryLine : p.ExitLine;
            int frameIndex = (int)e.FirstBeyond.FrameIndex;

            VideoFrame frame = await Task.Run(() => session.GetFullResolutionFrameAsync(frameIndex));
            double frameRelative = VideoRelative(frame.TimeSeconds);

            string label = isEntry ? Loc.T("INGRESSO") : Loc.T("USCITA");
            var text = new PassageImageText(
                Header: Loc.S($"Veicolo {row.IdText} · {label} · linea {line} · {row.DirectionText}"),
                MainTime: FormatTimeFor(start, e.TimeSeconds),
                Detail: Loc.S($"Istante stimato di attraversamento ({PassageRow.Describe(e.Method)}). Fotogramma n. {frameIndex + 1} delle {FormatTimeFor(start, frameRelative)}"),
                Footer: isEntry ? null : Loc.S($"Tempo {p.Speed.DurationSeconds:0.000} s · {p.Speed.SpeedKmh:0.0} km/h"));

            // Il disegno con WPF avviene sul thread dell'interfaccia.
            string name = string.Create(CultureInfo.InvariantCulture, $"{p.Id:0000}_{(isEntry ? Loc.T("ingresso") : Loc.T("uscita"))}.jpg");
            byte[] jpeg = PassageImageRenderer.RenderJpeg(frame, VideoWidth, VideoHeight, cal, line, e.FirstBeyond.Position, text);
            await File.WriteAllBytesAsync(Path.Combine(clearFolder, name), jpeg);
            clear[isEntry ? 0 : 1] = jpeg;

            byte[] anon = PassageImageRenderer.RenderJpeg(frame, VideoWidth, VideoHeight, cal, line, e.FirstBeyond.Position, text, PrivacyRegionsAt(frameIndex));
            await File.WriteAllBytesAsync(Path.Combine(anonFolder, name), anon);
            anonymized[isEntry ? 0 : 1] = anon;
        }

        return new PassageImages(clear[0], clear[1], anonymized[0], anonymized[1]);
    }

    private static string FormatTimeFor(DateTimeOffset? start, double relativeSeconds) =>
        start is { } s ? TimeFormatting.FormatClock(s, relativeSeconds) : TimeFormatting.FormatVideoTime(relativeSeconds);

    private string BuildInfoText(VideoSession session, CalibrationSettings cal, IReadOnlyList<PassageRow> rows, DateTimeOffset? start)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.S($"{AppInfo.Name} — esportazione misure"));
        sb.AppendLine(Loc.S($"Data esportazione: {DateTime.Now:dd/MM/yyyy HH:mm:ss}"));
        sb.AppendLine(Loc.T("Video: ") + SourceFilePath);
        if (IsStabilized && _stabilizationReport is { } stab)
        {
            sb.AppendLine(Loc.T("Stabilizzazione: sì, analisi eseguita sul video stabilizzato (") + stab.Parameters + ").");
            sb.AppendLine(Loc.S($"  Vibrazione originale max {stab.ShakeBeforeMaxPx:0.0} px (95% entro {stab.ShakeBeforeP95Px:0.0} px); dopo la stabilizzazione {stab.MisalignedFrames.Count} fotogrammi con scarto ≥ 1,5 px."));
            sb.AppendLine(Loc.T("  Tempi dei fotogrammi identici all'originale (verificati). Le immagini mostrano il video stabilizzato: ai bordi la parte di inquadratura persa dalla correzione è riempita ripetendo i pixel del bordo."));
        }

        sb.AppendLine(Loc.T("Risoluzione: ") + ResolutionText + " · " + FpsText);
        sb.AppendLine(start is { } s
            ? Loc.T("Inizio registrazione: ") + s.ToString("dd/MM/yyyy HH:mm:ss zzz", It)
            : Loc.T("Inizio registrazione: non indicato (orari = tempo del video)"));
        sb.AppendLine(Loc.S($"Distanza tra le linee: {cal.DistanceMeters:0.###} m"));
        foreach (string line in DistanceEstimateInfoLines())
        {
            sb.AppendLine(line);
        }

        sb.AppendLine(Loc.S($"Passaggi: {rows.Count} · velocità media {rows.Average(r => r.Passage.Speed.SpeedKmh):0.0} km/h"));
        if (FilterDescription is { } filter)
        {
            sb.AppendLine(Loc.S($"Filtro applicato: {filter} ({rows.Count} passaggi esportati su {_allRows.Count})."));
        }
        sb.AppendLine();
        int auto = rows.Count(r => r.IsAutomatic);
        sb.AppendLine(Loc.S($"Metodo: {auto} passaggi da rilevamento automatico ({LastModelDescription ?? "YOLOX"}), {rows.Count - auto} da posizioni indicate a mano."));
        sb.AppendLine(Loc.T("Punto di riferimento automatico: vertice in basso sul davanti del riquadro del veicolo (il destro per chi va da sinistra a destra, il sinistro per chi va da destra a sinistra), lo stesso per l'ingresso e per l'uscita."));
        sb.AppendLine(Loc.T("Istante di attraversamento stimato con precisione inferiore al fotogramma"));
        sb.AppendLine(Loc.T("(interpolazione o regressione sulle posizioni attorno alla linea)."));
        sb.AppendLine();
        sb.AppendLine(Loc.T("Avvertenze: misure non omologate, non utilizzabili per sanzioni."));
        sb.AppendLine(Loc.S($"Cartella «{ClearImagesFolder}»: immagini in chiaro. Possono contenere targhe e persone (dati personali, GDPR): conservale e condividile con cautela."));
        sb.AppendLine(Loc.S($"Cartella «{AnonymizedImagesFolder}»: le stesse immagini con pixellato l'intero riquadro di ogni veicolo tracciato."));
        sb.AppendLine(Loc.S($"«{Loc.T("rapporto")}.pdf» e «{Loc.T("rapporto_anonimizzato")}.pdf»: una pagina per veicolo, con le immagini in chiaro e con i veicoli pixellati."));
        sb.AppendLine(Loc.T("  Per le misure manuali (senza riquadro) si copre un'area della taglia di un'auto attorno al punto indicato."));
        sb.AppendLine(Loc.T("  Veicoli non tracciati (es. parcheggiati), pedoni e targhe fuori da queste zone restano visibili: controlla le immagini prima di condividerle."));
        return sb.ToString();
    }

    [RelayCommand(CanExecute = nameof(HasExportFolder))]
    private void OpenExportFolder()
    {
        if (_lastExportFolder is not null && Directory.Exists(_lastExportFolder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastExportFolder}\"") { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(AppLog.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLog.LogDirectory}\"") { UseShellExecute = true });
    }
}
