using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Video;

/// <summary>
/// Accesso esatto ai fotogrammi per indice. Legge in sequenza quando possibile (riproduzione,
/// avanzamento di un fotogramma) e riposiziona ffmpeg solo quando serve. Mantiene una cache
/// dei fotogrammi recenti per tornare indietro senza decodificare di nuovo.
/// </summary>
public sealed class FrameReader : IAsyncDisposable
{
    /// <summary>Oltre questa distanza in avanti conviene riposizionare invece di leggere in sequenza.</summary>
    private const int SequentialReadLimit = 45;

    private readonly FfmpegTools _tools;
    private readonly VideoInfo _info;
    private readonly FrameIndex _index;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FrameCache _cache;
    private DecodeStream? _stream;
    private bool _disposed;

    public FrameReader(FfmpegTools tools, VideoInfo info, FrameIndex index, int outputWidth, int outputHeight, long cacheBudgetBytes = 384L * 1024 * 1024)
    {
        if (outputWidth <= 0 || outputHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputWidth), "Dimensioni di uscita non valide.");
        }

        _tools = tools;
        _info = info;
        _index = index;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        long frameBytes = (long)outputWidth * outputHeight * 3;
        _cache = new FrameCache((int)Math.Clamp(cacheBudgetBytes / frameBytes, 4, 240));
    }

    public int OutputWidth { get; }

    public int OutputHeight { get; }

    public FrameIndex Index => _index;

    /// <summary>
    /// Restituisce il fotogramma con l'indice richiesto. Se il decoder non produce quel
    /// fotogramma (caso raro di file danneggiati), restituisce il più vicino disponibile:
    /// controllare sempre <see cref="VideoFrame.Index"/>.
    /// </summary>
    public async Task<VideoFrame> GetFrameAsync(int index, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _index.Count);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGet(index, out VideoFrame? cached))
            {
                return cached;
            }

            if (_stream is { } s && s.LastIndex < index && index - s.LastIndex <= SequentialReadLimit)
            {
                (VideoFrame? frame, _) = await ReadUntilAsync(s, index, ct).ConfigureAwait(false);
                if (frame is not null)
                {
                    return frame;
                }
            }

            return await SeekAndReadAsync(index, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Una lettura interrotta lascia la pipe a metà fotogramma: si riparte da capo.
            await ResetStreamAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<VideoFrame> SeekAndReadAsync(int target, CancellationToken ct)
    {
        double targetTime = _index[target];
        double interval = _index.IntervalAround(target);
        double relative = targetTime - _info.StartTimeSeconds;

        // Primo tentativo: posizionamento preciso appena prima del fotogramma.
        // Se ffmpeg parte oltre (comportamento diverso del contenitore), si arretra.
        double?[] seeks =
        [
            relative - interval * 0.25,
            relative - 2.0,
            relative - 10.0,
            null,
        ];

        VideoFrame? closest = null;
        foreach (double? seek in seeks)
        {
            await ResetStreamAsync().ConfigureAwait(false);
            double? ss = seek is { } v && v > 0 ? v : null;
            _stream = DecodeStream.Start(_tools, _info, ss, OutputWidth, OutputHeight);

            (VideoFrame? frame, bool overshotAtStart) = await ReadUntilAsync(_stream, target, ct, firstReadAfterSeek: true).ConfigureAwait(false);
            if (frame is not null && frame.Index == target)
            {
                return frame;
            }

            if (frame is not null && !overshotAtStart)
            {
                closest = frame; // il fotogramma richiesto non è stato prodotto: si usa il successivo
                return closest;
            }

            if (ss is null)
            {
                break;
            }
        }

        if (closest is not null)
        {
            return closest;
        }

        throw new VideoProbeException(Loc.S($"Impossibile leggere il fotogramma {target + 1}."));
    }

    /// <summary>
    /// Legge fotogrammi finché non raggiunge <paramref name="target"/>.
    /// Restituisce il fotogramma raggiunto (o il primo oltre l'obiettivo) e se lo stream
    /// è partito già oltre l'obiettivo.
    /// </summary>
    private async Task<(VideoFrame? Frame, bool OvershotAtStart)> ReadUntilAsync(
        DecodeStream stream, int target, CancellationToken ct, bool firstReadAfterSeek = false)
    {
        bool first = firstReadAfterSeek;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            (byte[] Pixels, double Pts)? next = await stream.ReadNextAsync(ct).ConfigureAwait(false);
            if (next is null)
            {
                await ResetStreamAsync().ConfigureAwait(false);
                return (null, false);
            }

            int idx = _index.NearestIndex(next.Value.Pts);
            var frame = new VideoFrame(idx, _index[idx], stream.Width, stream.Height, next.Value.Pixels);
            stream.LastIndex = Math.Max(stream.LastIndex, idx);

            if (idx > target && first)
            {
                return (frame, true);
            }

            first = false;

            // Tiene in cache solo i fotogrammi vicini all'obiettivo, utili per tornare indietro.
            if (target - idx < _cache.Capacity)
            {
                _cache.Add(frame);
            }

            if (idx >= target)
            {
                return (frame, false);
            }
        }
    }

    private async Task ResetStreamAsync()
    {
        if (_stream is not null)
        {
            DecodeStream old = _stream;
            _stream = null;
            await old.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetStreamAsync().ConfigureAwait(false);
            _cache.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>Cache LRU dei fotogrammi decodificati.</summary>
    private sealed class FrameCache(int capacity)
    {
        private readonly Dictionary<int, LinkedListNode<VideoFrame>> _map = new();
        private readonly LinkedList<VideoFrame> _lru = new();

        public int Capacity { get; } = capacity;

        public bool TryGet(int index, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VideoFrame? frame)
        {
            if (_map.TryGetValue(index, out LinkedListNode<VideoFrame>? node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                frame = node.Value;
                return true;
            }

            frame = null;
            return false;
        }

        public void Add(VideoFrame frame)
        {
            if (_map.TryGetValue(frame.Index, out LinkedListNode<VideoFrame>? existing))
            {
                _lru.Remove(existing);
                _map.Remove(frame.Index);
            }

            _map[frame.Index] = _lru.AddFirst(frame);
            while (_map.Count > Capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Index);
            }
        }

        public void Clear()
        {
            _map.Clear();
            _lru.Clear();
        }
    }
}
