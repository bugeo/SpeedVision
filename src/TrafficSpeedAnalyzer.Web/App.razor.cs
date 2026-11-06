using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using TrafficSpeedAnalyzer.Core.Analysis;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Export;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Time;
using TrafficSpeedAnalyzer.Core.Tracking;

namespace TrafficSpeedAnalyzer.Web;

public sealed record PassageRow(int Id, VehiclePassage Passage, VehicleClass Class, float Score);

public sealed record SelfTestItem(bool? Ok, string Text);

public partial class App : IAsyncDisposable
{
    private const string ModelUrl = "models/yolox_s.onnx";
    private const string SampleUrl = "samples/auto_reali_50_e_72kmh_20m.mp4";

    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    private IJSObjectReference? _js;
    private DotNetObjectReference<App>? _self;
    private ElementReference _canvas;
    private ElementReference _videoInput;
    private ElementReference _modelInput;
    private ElementReference _calInput;

    private EngineInfo? _engine;
    private ModelStatus? _model;
    private bool _modelLoading = true;
    private bool _cpuOnly;
    private VideoInfo? _video;
    private int _frameIndex;
    private double _frameTime;

    private LineSegment? _lineA;
    private LineSegment? _lineB;
    private double? _distance;
    private bool _regionAroundLines = true;
    private bool _skipStatic = true;
    private string? _drawMode;
    private IReadOnlyList<CalibrationIssue> _issues = [];

    private StreamingAnalysis? _analysis;
    private bool _running;
    private ProgressInfo? _progress;
    private string? _summaryText;
    private readonly List<(TrackPassageCandidate Candidate, VehiclePassage Passage)> _found = [];
    private List<PassageRow> _rows = [];

    private DateTime? _startLocal;
    private TimeSpan _startOffset = TimeSpan.Zero;
    private string? _startSource;

    private readonly List<SelfTestItem> _selfTest = [];
    private bool _selfTestRunning;
    private bool _showSelfTest;
    private PtsCheck? _selfTestPts;

    /// <summary>Esito complessivo dell'autotest per il pulsante in alto: null = non eseguito o in corso.</summary>
    private bool? SelfTestPassed =>
        _selfTestRunning || !_selfTest.Any(i => i.Ok is not null) ? null : _selfTest.All(i => i.Ok != false);

    private string SelfTestChipClass => _selfTestRunning ? "busy" : SelfTestPassed switch { true => "ok", false => "bad", _ => "" };

    private string SelfTestChipSuffix => _selfTestRunning ? " …" : SelfTestPassed switch { true => " ✓", false => " ✗", _ => "" };

    private void ToggleSelfTestPanel() => _showSelfTest = !_showSelfTest;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    private CalibrationSettings? Calibration =>
        _lineA is { } a && _lineB is { } b && _distance is > 0 && !_issues.Any(i => i.Severity == IssueSeverity.Error)
            ? new CalibrationSettings(a, b, _distance.Value)
            : null;

    private bool CanEdit => _video is { Supported: true } && !_running;

    private bool CanAnalyze => CanEdit && Calibration is not null && _model is { Ok: true };

    private double Percent => _progress is { Total: > 0 } p ? 100.0 * p.Done / p.Total : 0;

    private double? Remaining => _progress is { Done: > 10, FramesPerSecond: > 0 } p
        ? (p.Total - p.Done) / p.FramesPerSecond
        : null;

    private DateTimeOffset? RecordingStart => _startLocal is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), _startOffset) : null;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _self = DotNetObjectReference.Create(this);
        _js = await JS.InvokeAsync<IJSObjectReference>("import", "./js/app.js");
        _engine = await _js.InvokeAsync<EngineInfo>("init", _self, _canvas, _videoInput, _modelInput);
        StateHasChanged();

        _model = await _js.InvokeAsync<ModelStatus>("loadModelFromUrl", ModelUrl);
        _modelLoading = false;
        StateHasChanged();

        if (new Uri(Navigation.Uri).Query.Contains("autotest", StringComparison.OrdinalIgnoreCase) && _model.Ok)
        {
            await RunSelfTestAsync();
        }
    }

    // ------------------------------------------------------------ modello

    private async Task CpuOnlyChangedAsync()
    {
        _modelLoading = true;
        StateHasChanged();
        ModelStatus? m = await _js!.InvokeAsync<ModelStatus?>("setPreferGpu", !_cpuOnly);
        if (m is not null)
        {
            _model = m;
        }

        _modelLoading = false;
    }

    private async Task LoadModelFromFileAsync()
    {
        _modelLoading = true;
        StateHasChanged();
        _model = await _js!.InvokeAsync<ModelStatus>("loadModelFromInput");
        _modelLoading = false;
    }

    // ------------------------------------------------------------ video

    private async Task OpenSampleAsync()
    {
        try
        {
            await _js!.InvokeVoidAsync("openSample", SampleUrl);
        }
        catch (JSException e)
        {
            _video = new VideoInfo { Error = e.Message };
        }
    }

    [JSInvokable]
    public Task OnVideoOpened(VideoInfo info)
    {
        _video = info;
        _frameIndex = 0;
        _frameTime = 0;
        _lineA = null;
        _lineB = null;
        _drawMode = null;
        _progress = null;
        _summaryText = null;
        _found.Clear();
        _rows = [];
        SetRecordingStart(info.CreationDate, info.CreationSource);
        _issues = CalibrationValidator.Validate(_lineA, _lineB, _distance);
        StateHasChanged();
        return Task.CompletedTask;
    }

    private void SetRecordingStart(string? iso, string? source)
    {
        _startLocal = null;
        _startSource = null;
        if (iso is null || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset start))
        {
            return;
        }

        // L'orario dei metadati QuickTime standard è in UTC: mostrato nel fuso del browser.
        if (source is not null && source.StartsWith("mvhd", StringComparison.Ordinal))
        {
            start = start.ToLocalTime();
        }

        _startLocal = start.DateTime;
        _startOffset = start.Offset;
        _startSource = source;
    }

    private async Task SeekAsync(ChangeEventArgs e)
    {
        if (!int.TryParse(e.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return;
        }

        FrameShown? shown = await _js!.InvokeAsync<FrameShown?>("showFrame", index);
        if (shown is not null)
        {
            _frameIndex = shown.Index;
            _frameTime = shown.Time;
        }
    }

    private async Task DownloadPtsAsync()
    {
        string text = await _js!.InvokeAsync<string>("ptsText");
        await _js!.InvokeVoidAsync("downloadText", Path.GetFileNameWithoutExtension(_video!.Name) + "_pts-browser.txt", text);
    }

    // ------------------------------------------------------------ calibrazione

    private async Task ToggleDrawModeAsync(string which)
    {
        _drawMode = _drawMode == which ? null : which;
        await _js!.InvokeVoidAsync("setDrawMode", _drawMode);
    }

    [JSInvokable]
    public async Task OnLineDrawn(string which, double x1, double y1, double x2, double y2)
    {
        var line = new LineSegment(new Point2(x1, y1), new Point2(x2, y2));
        if (which == "A")
        {
            _lineA = line;
        }
        else
        {
            _lineB = line;
        }

        _drawMode = null;
        await _js!.InvokeVoidAsync("setDrawMode", (string?)null);
        await CalibrationChangedAsync();
        StateHasChanged();
    }

    private async Task CalibrationChangedAsync()
    {
        _issues = CalibrationValidator.Validate(_lineA, _lineB, _distance);
        await PushLinesAsync();
    }

    private BoundingBox? CurrentRegion()
    {
        if (_video is null || Calibration is not { } cal)
        {
            return null;
        }

        return _regionAroundLines
            ? AnalysisRegion.FromCalibration(cal, _video.DisplayWidth, _video.DisplayHeight)
            : new BoundingBox(0, 0, _video.DisplayWidth, _video.DisplayHeight);
    }

    private async Task PushLinesAsync()
    {
        if (_js is null)
        {
            return;
        }

        BoundingBox? r = CurrentRegion();
        await _js.InvokeVoidAsync("setLines",
            _lineA is { } a ? CalibrationFileData.ToArray(a) : null,
            _lineB is { } b ? CalibrationFileData.ToArray(b) : null,
            r is { } rr ? new[] { rr.X, rr.Y, rr.Width, rr.Height } : null);
    }

    private async Task LoadCalibrationAsync()
    {
        string? json = await _js!.InvokeAsync<string?>("readTextFile", _calInput);
        if (json is null || _video is null)
        {
            return;
        }

        try
        {
            CalibrationFileData f = CalibrationFileData.Parse(json);
            (LineSegment a, LineSegment b) = f.ScaledTo(_video.DisplayWidth, _video.DisplayHeight);
            _lineA = a;
            _lineB = b;
            _distance = f.DistanceMeters;
            await CalibrationChangedAsync();
            if (!f.HasSameAspect(_video.DisplayWidth, _video.DisplayHeight))
            {
                _issues = [.. _issues, new CalibrationIssue(IssueSeverity.Warning, "ASPECT", "La calibrazione è stata salvata per un video con proporzioni diverse: controlla le linee.")];
            }
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException)
        {
            _issues = [.. _issues, new CalibrationIssue(IssueSeverity.Error, "FILE", "File di calibrazione non valido.")];
        }
    }

    private async Task SaveCalibrationAsync()
    {
        if (Calibration is not { } cal || _video is null)
        {
            return;
        }

        var f = CalibrationFileData.From(_video.DisplayWidth, _video.DisplayHeight, cal.LineA, cal.LineB, cal.DistanceMeters, _video.Name);
        await _js!.InvokeVoidAsync("downloadText", Path.GetFileNameWithoutExtension(_video.Name) + ".tsacal.json", f.ToJson(), "application/json");
    }

    // ------------------------------------------------------------ analisi

    private async Task StartAnalysisAsync()
    {
        if (!CanAnalyze || CurrentRegion() is not { } region)
        {
            return;
        }

        _analysis = new StreamingAnalysis(Calibration!, region);
        _found.Clear();
        _rows = [];
        _summaryText = null;
        _progress = new ProgressInfo { Total = _video!.FrameCount };
        _running = true;
        _drawMode = null;
        await _js!.InvokeVoidAsync("setDrawMode", (string?)null);
        await PushLinesAsync();

        bool started = await _js!.InvokeAsync<bool>("analyze", new
        {
            region = new[] { region.X, region.Y, region.Width, region.Height },
            start = 0,
            end = _video.FrameCount - 1,
            skipStatic = _skipStatic,
            maxSkipped = 15,
            minScore = 0.1,
            includeBicycles = false,
        });
        if (!started)
        {
            _running = false;
            _summaryText = "Impossibile avviare l'analisi.";
        }
    }

    private async Task CancelAsync() => await _js!.InvokeVoidAsync("cancel");

    /// <summary>Rilevamenti di un fotogramma: [x, y, w, h, classe, punteggio] ripetuti.</summary>
    /// <returns>[tracce attive, n, poi per ogni veicolo seguito: id, x, y, w, h, confermato, km/h (−1 se non ancora misurato)].</returns>
    [JSInvokable]
    public double[] OnFrameDetections(long frameIndex, double time, double[] packed)
    {
        if (_analysis is null)
        {
            return [0, 0];
        }

        var dets = new List<VehicleDetection>(packed.Length / 6);
        for (int i = 0; i + 5 < packed.Length; i += 6)
        {
            var box = new BoundingBox(packed[i], packed[i + 1], packed[i + 2], packed[i + 3]);
            dets.Add(new VehicleDetection(box, (VehicleClass)(int)packed[i + 4], (float)packed[i + 5]));
        }

        IReadOnlyList<TrackedVehicle> tracked = _analysis.AddDetections(frameIndex, time, dets);
        Collect(_analysis.TakeNewPassages());

        var result = new double[2 + tracked.Count * 7];
        result[0] = _analysis.ActiveTracks;
        result[1] = tracked.Count;
        for (int i = 0; i < tracked.Count; i++)
        {
            TrackedVehicle v = tracked[i];
            int o = 2 + i * 7;
            result[o] = v.TrackId;
            result[o + 1] = v.Detection.Box.X;
            result[o + 2] = v.Detection.Box.Y;
            result[o + 3] = v.Detection.Box.Width;
            result[o + 4] = v.Detection.Box.Height;
            result[o + 5] = v.IsConfirmed ? 1 : 0;
            result[o + 6] = _analysis.SpeedOf(v.TrackId) ?? -1;
        }

        return result;
    }

    [JSInvokable]
    public void OnProgress(ProgressInfo p)
    {
        _progress = p;
        _frameIndex = p.FrameIndex;
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnAnalysisFinished(AnalysisSummaryInfo s)
    {
        _running = false;
        if (_analysis is not null)
        {
            Collect(_analysis.Complete());
        }

        if (s.Error is not null)
        {
            _summaryText = "Errore durante l'analisi: " + s.Error;
        }
        else
        {
            _progress = new ProgressInfo
            {
                Done = s.Done, Total = s.Total, FrameIndex = s.LastFrame, Detected = s.Detected, Skipped = s.Skipped,
                FramesPerSecond = s.FramesPerSecond, MeanDetectMs = s.MeanDetectMs,
            };
            _frameIndex = s.LastFrame;
            string rejected = _analysis is { RejectedTracks: > 0 } a
                ? $" Tracce scartate: {a.RejectedTracks} ({string.Join("; ", a.RejectionReasons.Select(kv => $"{kv.Key} ×{kv.Value}"))})."
                : "";
            _summaryText = (s.Cancelled ? "Analisi interrotta. " : "Analisi completata. ")
                + $"{_rows.Count} passaggi in {FormatDuration(s.ElapsedMs / 1000)} con {s.Provider}.{rejected}"
                + (s.ProviderNote is { } pn ? " " + pn + "." : "");
            if (_model is not null && s.Provider is not null)
            {
                _model.Provider = s.Provider;
                _model.Note = s.ProviderNote ?? _model.Note;
            }
        }

        if (_selfTestRunning)
        {
            _selfTestRunning = false;
            EvaluateSelfTest(s);
        }

        StateHasChanged();
        await Task.CompletedTask;
    }

    private void Collect(IReadOnlyList<TrackPassageCandidate> candidates)
    {
        foreach (TrackPassageCandidate c in candidates)
        {
            if (c.Result?.Passage is { } p)
            {
                _found.Add((c, p));
            }
        }

        // Numerazione in ordine di ingresso, come nel CSV.
        _rows = _found
            .OrderBy(f => f.Passage.Entry.TimeSeconds)
            .Select((f, i) => new PassageRow(i + 1, f.Passage with { Id = i + 1 }, f.Candidate.Class, f.Candidate.MeanScore))
            .ToList();
    }

    private async Task ExportCsvAsync()
    {
        byte[] bytes = CsvExporter.BuildCsvBytes(_rows.Select(r => r.Passage), RecordingStart);
        await _js!.InvokeVoidAsync("download", Path.GetFileNameWithoutExtension(_video!.Name) + "_passaggi.csv", bytes, "text/csv;charset=utf-8");
    }

    // ------------------------------------------------------------ autotest

    private async Task RunSelfTestAsync()
    {
        _showSelfTest = true;
        _selfTest.Clear();
        _selfTest.Add(new SelfTestItem(null, $"Motore: {_model?.Provider}{(string.IsNullOrEmpty(_model?.Note) ? "" : " — " + _model!.Note)}"));
        StateHasChanged();

        await OpenSampleAsync();
        if (_video is not { Supported: true } v)
        {
            _selfTest.Add(new SelfTestItem(false, "Il video di prova non si apre: " + (_video?.Error ?? _video?.Reason)));
            return;
        }

        _selfTestPts = await _js!.InvokeAsync<PtsCheck>("ptsCheck", SampleUrl + ".ffprobe.json");
        _selfTest.Add(new SelfTestItem(
            _selfTestPts.Actual == _selfTestPts.Expected && _selfTestPts.MaxError < 2e-6,
            $"Timestamp dei fotogrammi uguali a FFprobe: {_selfTestPts.Actual}/{_selfTestPts.Expected}, scarto massimo {_selfTestPts.MaxError * 1e6:0.0} µs"));

        // Stessa calibrazione dei test dell'app desktop: A a y = 180, B a y = 380, 20 m.
        _lineA = new LineSegment(new Point2(0, 180), new Point2(v.DisplayWidth, 180));
        _lineB = new LineSegment(new Point2(0, 380), new Point2(v.DisplayWidth, 380));
        _distance = 20;
        _regionAroundLines = true;
        _skipStatic = true;
        await CalibrationChangedAsync();
        _selfTestRunning = true;
        await StartAnalysisAsync();
    }

    private void EvaluateSelfTest(AnalysisSummaryInfo s)
    {
        if (s.Error is not null)
        {
            _selfTest.Add(new SelfTestItem(false, "Analisi non riuscita: " + s.Error));
            return;
        }

        _selfTest.Add(new SelfTestItem(s.Done == _video!.FrameCount, $"Fotogrammi decodificati: {s.Done}/{_video.FrameCount} ({s.Skipped} senza movimento, saltati)"));
        _selfTest.Add(new SelfTestItem(null, $"Modello: {s.MeanDetectMs:0.0} ms/fotogramma; analisi a {s.FramesPerSecond:0.0} fps"));
        _selfTest.Add(new SelfTestItem(_rows.Count == 2, $"Veicoli misurati: {_rows.Count} (attesi 2)"));

        // Valori veri: velocità e istanti di attraversamento di B (ingresso) e A (uscita).
        (double Kmh, double TB, double TA)[] truth = [(50.0, 1.148, 2.588), (72.0, 3.65, 4.65)];
        for (int i = 0; i < Math.Min(2, _rows.Count); i++)
        {
            VehiclePassage p = _rows[i].Passage;
            double kmh = p.Speed.SpeedKmh;
            bool speedOk = kmh >= truth[i].Kmh * 0.96 && kmh <= truth[i].Kmh * 1.04 && p.Direction == TravelDirection.BToA;
            _selfTest.Add(new SelfTestItem(speedOk,
                $"Veicolo {i + 1}: {kmh.ToString("0.00", It)} km/h (atteso {truth[i].Kmh.ToString("0.0", It)}, scarto {((kmh / truth[i].Kmh - 1) * 100).ToString("+0.00;-0.00", It)} %), verso {(p.Direction == TravelDirection.BToA ? "B → A" : "A → B")}"));
            double dIn = p.Entry.TimeSeconds - truth[i].TB;
            double dOut = p.Exit.TimeSeconds - truth[i].TA;
            _selfTest.Add(new SelfTestItem(dIn is >= -0.15 and <= 0.05 && dOut is >= -0.15 and <= 0.05,
                $"Veicolo {i + 1}: istanti spostati di {dIn * 1000:+0;-0} ms e {dOut * 1000:+0;-0} ms (atteso uno scarto costante di qualche decina di ms)"));
        }
    }

    // ------------------------------------------------------------ formati

    private string FormatClock(double videoSeconds) => CsvExporter.FormatTime(videoSeconds, RecordingStart);

    private static string FormatVideoTime(double seconds) => TimeFormatting.FormatVideoTime(seconds);

    private static string FormatDuration(double seconds)
    {
        TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours} h {ts.Minutes:00} min"
            : ts.TotalMinutes >= 1 ? $"{ts.Minutes} min {ts.Seconds:00} s"
            : $"{ts.TotalSeconds.ToString("0.0", It)} s";
    }

    private static string FormatBytes(double bytes) =>
        bytes >= 1 << 30 ? $"{(bytes / (1 << 30)).ToString("0.00", It)} GB"
        : bytes >= 1 << 20 ? $"{(bytes / (1 << 20)).ToString("0.0", It)} MB"
        : $"{(bytes / 1024).ToString("0", It)} KB";

    private static string ClassName(VehicleClass c) => c switch
    {
        VehicleClass.Car => "Auto",
        VehicleClass.Motorcycle => "Moto",
        VehicleClass.Bus => "Autobus",
        VehicleClass.Truck => "Camion",
        VehicleClass.Bicycle => "Bicicletta",
        _ => "Altro",
    };

    public async ValueTask DisposeAsync()
    {
        if (_js is not null)
        {
            try
            {
                await _js.InvokeVoidAsync("cancel");
                await _js.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _self?.Dispose();
    }
}
