using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Localization;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Privacy;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Video dei passaggi: un filmato MP4 con i tragitti dei veicoli corretti uno dopo l'altro. Per ciascuno il video si ferma
/// all'ingresso e compare il cronometro, riparte, si ferma all'uscita con il cronometro fermo sul tempo misurato e la
/// velocità. Con «Offusca auto e persone» (attivo di partenza) i veicoli sono pixellati, così il video si può condividere.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Indirizzo della strada, scritto in basso a sinistra nel video (vuoto = non scritto).</summary>
    [ObservableProperty]
    private string _videoAddress = string.Empty;

    /// <summary>Nel video auto e persone sono pixellate (attivo all'apertura della finestra «Genera video»).</summary>
    [ObservableProperty]
    private bool _videoBlurVehicles = true;

    /// <summary>Data del video, scritta in basso a destra (vuota = non scritta).</summary>
    [ObservableProperty]
    private string _videoDateText = string.Empty;

    /// <summary>
    /// Prima della finestra «Genera video»: propone l'ultimo indirizzo usato e la data rilevata all'importazione del
    /// video (o indicata nel passo Video), se c'è.
    /// </summary>
    public void PrepareVideoCaption()
    {
        VideoBlurVehicles = true;
        if (string.IsNullOrWhiteSpace(VideoAddress))
        {
            VideoAddress = _settings.LastVideoAddress ?? string.Empty;
        }

        if (EffectiveRecordingStart is { } start)
        {
            VideoDateText = start.ToString("dd/MM/yyyy", It);
        }
    }

    /// <summary>La finestra chiede indirizzo e data prima di generare il video (la apre l'interfaccia).</summary>
    public event EventHandler? VideoCaptionRequested;

    /// <summary>Pulsante «Genera video»: prepara indirizzo e data e chiede all'interfaccia di mostrarli.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private void RequestVideo()
    {
        PrepareVideoCaption();
        VideoCaptionRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Larghezza e altezza massime del video prodotto.</summary>
    private const int PassageVideoMaxWidth = 1920, PassageVideoMaxHeight = 1080;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportVideoAsync()
    {
        if (_session is null || Tools is not { } tools || Calibration is not { } cal || Passages.Count == 0)
        {
            return;
        }

        string baseName = Path.GetFileNameWithoutExtension(SourceFileName);
        string? path = await _platform.PickSaveFileAsync(
            Loc.T("Salva il video dei passaggi"),
            [new FileFilter(Loc.T("Video MP4"), ["*.mp4"])],
            baseName + "_" + Loc.T("passaggi") + ".mp4",
            ".mp4",
            Directory.Exists(_settings.LastExportFolder) ? _settings.LastExportFolder : _settings.LastVideoFolder);
        if (path is null)
        {
            return;
        }

        StopPlayback();
        _settings.LastVideoAddress = VideoAddress.Trim();
        List<PassageRow> rows = [.. Passages.OrderBy(p => p.Passage!.Entry.TimeSeconds)];
        VideoSession session = _session;
        IsBusy = true;
        BusyText = Loc.T("Preparazione del video dei passaggi…");
        try
        {
            var progress = new Progress<int>(percent => BusyText = percent >= 100 ? Loc.T("Aggiunta del suono dei veicoli…") : Loc.S($"Video dei passaggi: {percent}%"));
            bool blur = VideoBlurVehicles;
            int count = await Task.Run(() => WritePassageVideoAsync(tools, session, cal, rows, path, blur, progress));
            _settings.LastExportFolder = Path.GetDirectoryName(path) ?? _settings.LastExportFolder;
            _settings.Save();
            AppLog.Info($"Video dei passaggi: {count} veicoli in {path}");
            ShowStatus(Loc.T("Video dei passaggi pronto"), Loc.S($"{count} passaggi in {path}"), StatusSeverity.Success);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("Video dei passaggi non riuscito", e);
            ShowStatus(Loc.T("Video dei passaggi non riuscito"), e.Message, StatusSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Scrive il video (fuori dal thread dell'interfaccia) e restituisce quanti passaggi contiene.</summary>
    private async Task<int> WritePassageVideoAsync(FfmpegTools tools, VideoSession session, CalibrationSettings cal, IReadOnlyList<PassageRow> rows, string path, bool blur, IProgress<int> progress)
    {
        VideoInfo info = session.Info;
        FrameIndex index = session.Index;
        (int w, int h) = VideoSession.FitWithin(info.DisplayWidth, info.DisplayHeight, PassageVideoMaxWidth, PassageVideoMaxHeight);
        double fps = info.NominalFps > 0 ? info.NominalFps : index.Stats.MeanFps > 0 ? index.Stats.MeanFps : 30;
        double t0 = index.FirstTime;
        double[] times = [.. index.Times.Select(t => t - t0)];

        // Prima il piano di tutti i passaggi, per indicare l'avanzamento sul totale dei fotogrammi.
        var plans = rows.Select(r => (Row: r, Frames: PassageClipPlan.Build(r.Passage!, times, fps))).ToList();
        int total = plans.Sum(p => p.Frames.Count), done = 0;

        await using var reader = new FrameReader(tools, info, index, w, h, cacheBudgetBytes: 64L * 1024 * 1024);
        await using VideoEncoder encoder = await VideoEncoder.StartAsync(tools, path, w, h, fps);
        using var renderer = new PassageVideoRenderer(w, h, VideoWidth, VideoHeight);
        string distance = cal.DistanceMeters.ToString("0.00", It) + " m";
        int maxGap = (int)Math.Ceiling(fps * 4);
        foreach ((PassageRow row, List<ClipFrame> frames) in plans)
        {
            VehiclePassage p = row.Passage!;
            string lane = Road is { Lanes: > 1 } && row.Lane >= 0 ? " · " + Loc.S($"Corsia {row.Lane + 1}") : string.Empty;
            var text = new PassageVideoText(
                Header: Loc.S($"Veicolo {row.IdText}") + lane + " · " + row.DirectionText,
                DistanceLabel: Loc.T("Distanza A–B"),
                Distance: distance,
                TimeLabel: Loc.T("Tempo"),
                FormatChrono: s => s.ToString("0.000", It) + " s",
                SpeedLabel: Loc.T("Velocità"),
                Speed: p.Speed.SpeedKmh.ToString("0.0", It) + " km/h",
                Address: VideoAddress.Trim(),
                Date: VideoDateText.Trim());

            VideoFrame? frame = null;
            foreach (ClipFrame clip in frames)
            {
                // Nei fermi lo stesso fotogramma si ripete: si legge una volta sola.
                if (frame is null || frame.Index != clip.SourceFrame)
                {
                    frame = await reader.GetFrameAsync(clip.SourceFrame);
                }

                Point2? reference = row.Saved.Track.TryGet(frame.Index, out TrackObservation obs) ? obs.Position : null;
                BoundingBox? box = row.Saved.Points is { Count: > 0 } points ? PrivacyMasking.BoxAt(points, frame.Index) : null;
                byte[] bgra = renderer.Render(frame, cal, p, clip, reference, box, blur ? VideoPrivacyRegionsAt(frame.Index, maxGap) : [], text);
                await encoder.WriteFrameAsync(bgra);
                if (++done % 15 == 0)
                {
                    progress.Report((int)(99.0 * done / total));
                }
            }
        }

        await encoder.CompleteAsync();

        // Suono: l'audio del video originale mentre il video scorre (il rumore dei veicoli), silenzio durante i fermi e
        // una campanella quando il veicolo tocca la linea d'ingresso e quella d'uscita. L'audio si prende dal file
        // originale (il video stabilizzato non ha audio; i tempi dei fotogrammi sono gli stessi); se è muto, restano
        // solo le campanelle.
        VideoInfo original = _sourceInfo ?? info;
        progress.Report(100);
        try
        {
            string? source = await PassageAudio.HasAudioAsync(tools, original.FilePath) ? original.FilePath : null;
            await PassageAudio.AddAsync(tools, path, source, AudioPieces(plans.SelectMany(p => p.Frames), index, original.StartTimeSeconds, fps));
        }
        catch (Exception e) when (e is IOException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            // Il video resta valido, solo senza suono.
            AppLog.Error("Audio del video dei passaggi non aggiunto", e);
        }

        return plans.Count;
    }

    /// <summary>
    /// Zone da pixellare nel video: come nelle immagini, più i veicoli seguiti appena prima che la traccia inizi e appena
    /// dopo che finisce (fino a <paramref name="maxGap"/> fotogrammi), con il riquadro stimato dal loro moto. Il video parte
    /// alcuni secondi prima dell'ingresso, quando il veicolo può non essere ancora seguito.
    /// </summary>
    private List<BoundingBox> VideoPrivacyRegionsAt(long frameIndex, int maxGap)
    {
        List<BoundingBox> regions = PrivacyRegionsAt(frameIndex);
        foreach (SavedTrack t in _savedTracks)
        {
            if (t.Points is { Count: > 1 } points && PrivacyMasking.ExtrapolatedBox(points, frameIndex, maxGap) is { } box)
            {
                regions.AddRange(PrivacyMasking.RegionsFor(box, VideoWidth, VideoHeight));
            }
        }

        return regions;
    }

    /// <summary>
    /// Pezzi della colonna sonora: per ogni tratto di fotogrammi consecutivi che scorrono, l'audio originale dal primo
    /// fotogramma (stessa durata del tratto nel video prodotto); per ciascun fermo, silenzio con una campanella all'inizio
    /// (il veicolo tocca la linea d'ingresso o quella d'uscita) e un bip quando compare la velocità.
    /// </summary>
    internal static List<AudioPiece> AudioPieces(IEnumerable<ClipFrame> frames, FrameIndex index, double fileStart, double fps)
    {
        var pieces = new List<AudioPiece>();
        int count = 0, first = -1, last = -1;
        ClipPhase phase = ClipPhase.Approach;
        static bool IsPause(ClipPhase p) => p is ClipPhase.EntryPause or ClipPhase.ExitPause or ClipPhase.SpeedPause;
        void Flush()
        {
            if (count > 0)
            {
                pieces.Add(IsPause(phase)
                    ? new AudioPiece(null, count / fps, Bell: phase != ClipPhase.SpeedPause, Beep: phase == ClipPhase.SpeedPause)
                    : new AudioPiece(index[first] - fileStart, count / fps));
            }

            count = 0;
        }

        foreach (ClipFrame f in frames)
        {
            bool continues = IsPause(f.Phase)
                ? f.Phase == phase
                : !IsPause(phase) && f.SourceFrame == last + 1;
            if (count > 0 && !continues)
            {
                Flush();
            }

            if (count == 0)
            {
                first = f.SourceFrame;
                phase = f.Phase;
            }

            last = f.SourceFrame;
            count++;
        }

        Flush();
        return pieces;
    }
}
