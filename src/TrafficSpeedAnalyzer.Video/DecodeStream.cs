using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Video;

/// <summary>
/// Processo ffmpeg che decodifica il video da una posizione e scrive i fotogrammi BGR su stdout.
/// Il filtro showinfo stampa su stderr il timestamp originale (-copyts) di ogni fotogramma,
/// così ogni immagine arriva con il suo tempo reale.
/// </summary>
internal sealed partial class DecodeStream : IAsyncDisposable
{
    private static readonly TimeSpan PtsTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly Stream _stdout;
    private readonly Channel<double> _pts = Channel.CreateUnbounded<double>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<string> _errorTail = new();
    private readonly double _timeBase;
    private readonly int _frameSize;
    private bool _disposed;

    private DecodeStream(Process process, double timeBase, int frameSize)
    {
        _process = process;
        _stdout = process.StandardOutput.BaseStream;
        _timeBase = timeBase;
        _frameSize = frameSize;
    }

    public int Width { get; private init; }

    public int Height { get; private init; }

    /// <summary>Indice dell'ultimo fotogramma letto (-1 se nessuno).</summary>
    public int LastIndex { get; set; } = -1;

    public static DecodeStream Start(FfmpegTools tools, VideoInfo info, double? seekSeconds, int width, int height)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-v", "info", "-copyts" };
        if (seekSeconds is { } ss && ss > 0)
        {
            args.Add("-ss");
            args.Add(ss.ToString("0.######", CultureInfo.InvariantCulture));
        }

        args.AddRange(
        [
            "-i", info.FilePath,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-fps_mode", "passthrough",
            "-vf", FormattableString.Invariant($"scale={width}:{height}:flags=bicubic,setsar=1,showinfo"),
            "-pix_fmt", "bgr24",
            "-f", "rawvideo",
            "pipe:1",
        ]);

        ProcessStartInfo psi = ProcessRunner.CreateStartInfo(tools.FfmpegPath, args);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException(Loc.T("Impossibile avviare ffmpeg."));
        }

        // Lo stdout è disponibile solo dopo l'avvio del processo.
        var stream = new DecodeStream(process, info.TimeBase, width * height * 3) { Width = width, Height = height };
        process.ErrorDataReceived += (_, e) => stream.OnStderrLine(e.Data);
        process.BeginErrorReadLine();
        return stream;
    }

    /// <summary>Legge il fotogramma successivo; null a fine video.</summary>
    public async Task<(byte[] Pixels, double Pts)?> ReadNextAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] buffer = new byte[_frameSize];
        int read = await _stdout.ReadAtLeastAsync(buffer, _frameSize, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (read < _frameSize)
        {
            if (read == 0 && _process.HasExited && _process.ExitCode != 0 && !_disposed)
            {
                throw new VideoProbeException(Loc.T("ffmpeg ha interrotto la decodifica. ") + ErrorSummary());
            }

            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PtsTimeout);
        try
        {
            double pts = await _pts.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            return (buffer, pts);
        }
        catch (ChannelClosedException)
        {
            throw new VideoProbeException(Loc.T("Timestamp del fotogramma non disponibile. ") + ErrorSummary());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new VideoProbeException(Loc.T("Timestamp del fotogramma non ricevuto in tempo."));
        }
    }

    internal void OnStderrLine(string? line)
    {
        if (line is null)
        {
            _pts.Writer.TryComplete();
            return;
        }

        if (TryParseShowInfo(line, _timeBase, out double pts))
        {
            _pts.Writer.TryWrite(pts);
            return;
        }

        lock (_errorTail)
        {
            _errorTail.Enqueue(line);
            while (_errorTail.Count > 12)
            {
                _errorTail.Dequeue();
            }
        }
    }

    /// <summary>
    /// Interpreta una riga di showinfo. Usa il pts intero × base dei tempi (precisione piena)
    /// quando è coerente con pts_time, altrimenti pts_time (stampato con 6 cifre significative).
    /// </summary>
    internal static bool TryParseShowInfo(string line, double timeBase, out double seconds)
    {
        seconds = double.NaN;
        if (!line.Contains("Parsed_showinfo", StringComparison.Ordinal))
        {
            return false;
        }

        Match m = ShowInfoRegex().Match(line);
        if (!m.Success)
        {
            return false;
        }

        if (!double.TryParse(m.Groups["time"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ptsTime))
        {
            return false;
        }

        seconds = ptsTime;
        if (timeBase > 0 && long.TryParse(m.Groups["pts"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pts))
        {
            double exact = pts * timeBase;
            if (Math.Abs(exact - ptsTime) <= 1e-5 * Math.Max(1, Math.Abs(ptsTime)) + 1e-6)
            {
                seconds = exact;
            }
        }

        return true;
    }

    private string ErrorSummary()
    {
        lock (_errorTail)
        {
            return string.Join(" | ", _errorTail.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(3));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ProcessRunner.TryKill(_process);
        try
        {
            await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignorato: il processo è stato comunque terminato
        }

        _pts.Writer.TryComplete();
        _process.Dispose();
    }

    [GeneratedRegex(@"\bn:\s*\d+\s+pts:\s*(?<pts>-?\d+)\s+pts_time:\s*(?<time>-?[0-9.eE+\-]+)")]
    private static partial Regex ShowInfoRegex();
}
