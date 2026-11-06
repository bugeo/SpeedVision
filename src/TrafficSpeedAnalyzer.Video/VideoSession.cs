using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Video;

/// <summary>
/// Video aperto: metadati, indice dei fotogrammi, lettore per l'anteprima (ridimensionata)
/// e lettore a piena risoluzione per l'esportazione delle immagini.
/// </summary>
public sealed class VideoSession : IAsyncDisposable
{
    private readonly FfmpegTools _tools;
    private FrameReader? _fullResReader;
    private readonly SemaphoreSlim _fullResLock = new(1, 1);

    private VideoSession(FfmpegTools tools, VideoInfo info, FrameIndex index, FrameReader preview)
    {
        _tools = tools;
        Info = info;
        Index = index;
        Preview = preview;
    }

    public VideoInfo Info { get; }

    public FrameIndex Index { get; }

    public FrameReader Preview { get; }

    public FfmpegTools Tools => _tools;

    public static async Task<VideoSession> OpenAsync(
        FfmpegTools tools,
        string path,
        int previewMaxWidth = 1920,
        int previewMaxHeight = 1080,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        VideoInfo? recordingMetadataFrom = null)
    {
        progress?.Report(Loc.T("Lettura dei metadati…"));
        VideoInfo info = await VideoProbe.ProbeAsync(tools, path, ct).ConfigureAwait(false);
        if (recordingMetadataFrom is not null)
        {
            // Video derivato (per esempio stabilizzato): l'orario di registrazione è quello dell'originale.
            info = info with
            {
                RecordingStart = recordingMetadataFrom.RecordingStart,
                RecordingStartSource = recordingMetadataFrom.RecordingStartSource,
            };
        }

        progress?.Report(Loc.T("Indicizzazione dei fotogrammi…"));
        FrameIndex index = await VideoProbe.BuildIndexAsync(tools, info, ct).ConfigureAwait(false);

        (int w, int h) = FitWithin(info.DisplayWidth, info.DisplayHeight, previewMaxWidth, previewMaxHeight);
        var preview = new FrameReader(tools, info, index, w, h);
        var session = new VideoSession(tools, info, index, preview);

        progress?.Report(Loc.T("Decodifica del primo fotogramma…"));
        try
        {
            await preview.GetFrameAsync(0, ct).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return session;
    }

    /// <summary>Fotogramma a piena risoluzione (per le immagini esportate).</summary>
    public async Task<VideoFrame> GetFullResolutionFrameAsync(int index, CancellationToken ct = default)
    {
        await _fullResLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _fullResReader ??= new FrameReader(
                _tools, Info, Index, Even(Info.DisplayWidth), Even(Info.DisplayHeight), cacheBudgetBytes: 64L * 1024 * 1024);
        }
        finally
        {
            _fullResLock.Release();
        }

        return await _fullResReader.GetFrameAsync(index, ct).ConfigureAwait(false);
    }

    /// <summary>Dimensioni che stanno nel riquadro mantenendo le proporzioni (sempre pari).</summary>
    public static (int Width, int Height) FitWithin(int width, int height, int maxWidth, int maxHeight)
    {
        double scale = Math.Min(1.0, Math.Min(maxWidth / (double)width, maxHeight / (double)height));
        return (Even((int)Math.Round(width * scale)), Even((int)Math.Round(height * scale)));
    }

    private static int Even(int v) => Math.Max(2, v - (v % 2));

    public async ValueTask DisposeAsync()
    {
        await Preview.DisposeAsync().ConfigureAwait(false);
        if (_fullResReader is not null)
        {
            await _fullResReader.DisposeAsync().ConfigureAwait(false);
        }

        _fullResLock.Dispose();
    }
}
