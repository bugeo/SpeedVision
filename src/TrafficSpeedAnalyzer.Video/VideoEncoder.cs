using System.Diagnostics;
using System.Globalization;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Video;

/// <summary>
/// Processo ffmpeg che riceve fotogrammi BGRA su stdin e li scrive in un file MP4 a fotogrammi costanti.
/// Usa H.264 (libx264) se ffmpeg lo include, altrimenti MPEG-4, sempre presente. Il file viene scritto prima con un
/// nome provvisorio e rinominato solo a lavoro concluso, così non resta mai un video a metà.
/// </summary>
public sealed class VideoEncoder : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly string _partPath;
    private readonly string _path;
    private readonly int _frameBytes;
    private readonly Queue<string> _errorTail = new();
    private bool _completed;

    private VideoEncoder(Process process, string partPath, string path, int frameBytes)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _partPath = partPath;
        _path = path;
        _frameBytes = frameBytes;
    }

    public int Width { get; private init; }

    public int Height { get; private init; }

    /// <summary>Codifica usata: «H.264» o «MPEG-4».</summary>
    public string Codec { get; private init; } = string.Empty;

    /// <summary>Avvia ffmpeg. Larghezza e altezza devono essere pari.</summary>
    public static async Task<VideoEncoder> StartAsync(FfmpegTools tools, string path, int width, int height, double fps, CancellationToken ct = default)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException(Loc.T("Le dimensioni del video devono essere pari."));
        }

        bool x264 = await HasX264Async(tools, ct).ConfigureAwait(false);
        string partPath = path + ".part.mp4";
        string rate = fps.ToString("0.######", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-v", "error",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", FormattableString.Invariant($"{width}x{height}"), "-r", rate,
            "-i", "pipe:0",
            "-an",
        };
        args.AddRange(x264
            ? ["-c:v", "libx264", "-preset", "veryfast", "-crf", "21"]
            : ["-c:v", "mpeg4", "-q:v", "3"]);
        args.AddRange(["-pix_fmt", "yuv420p", "-movflags", "+faststart", "-f", "mp4", "-y", partPath]);

        ProcessStartInfo psi = ProcessRunner.CreateStartInfo(tools.FfmpegPath, args);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = false;
        var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException(Loc.T("Impossibile avviare ffmpeg."));
        }

        var encoder = new VideoEncoder(process, partPath, path, width * height * 4)
        {
            Width = width,
            Height = height,
            Codec = x264 ? "H.264" : "MPEG-4",
        };
        process.ErrorDataReceived += (_, e) => encoder.OnStderrLine(e.Data);
        process.BeginErrorReadLine();
        return encoder;
    }

    /// <summary>Aggiunge un fotogramma (BGRA, righe contigue di <see cref="Width"/>·4 byte).</summary>
    public async Task WriteFrameAsync(ReadOnlyMemory<byte> bgra, CancellationToken ct = default)
    {
        if (bgra.Length != _frameBytes)
        {
            throw new ArgumentException(Loc.T("Fotogramma di dimensioni diverse da quelle del video."));
        }

        try
        {
            await _input.WriteAsync(bgra, ct).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            throw new IOException(Loc.T("ffmpeg ha smesso di accettare fotogrammi: ") + ErrorTail(), e);
        }
    }

    /// <summary>Chiude il video e lo rinomina con il nome definitivo.</summary>
    public async Task CompleteAsync(CancellationToken ct = default)
    {
        await _input.FlushAsync(ct).ConfigureAwait(false);
        _input.Close();
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (_process.ExitCode != 0 || !File.Exists(_partPath))
        {
            throw new IOException(Loc.T("ffmpeg non è riuscito a scrivere il video: ") + ErrorTail());
        }

        File.Move(_partPath, _path, overwrite: true);
        _completed = true;
    }

    /// <summary>ffmpeg include il codificatore H.264.</summary>
    private static async Task<bool> HasX264Async(FfmpegTools tools, CancellationToken ct)
    {
        try
        {
            ProcessResult encoders = await ProcessRunner.RunAsync(tools.FfmpegPath, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            return encoders.StandardOutput.Contains(" libx264 ", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void OnStderrLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_errorTail)
        {
            _errorTail.Enqueue(line);
            if (_errorTail.Count > 5)
            {
                _errorTail.Dequeue();
            }
        }
    }

    private string ErrorTail()
    {
        lock (_errorTail)
        {
            return string.Join(" | ", _errorTail.TakeLast(3));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            ProcessRunner.TryKill(_process);
            try
            {
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignorato: il processo è stato comunque terminato
            }
        }

        _process.Dispose();
        if (!_completed)
        {
            try
            {
                File.Delete(_partPath);
            }
            catch (IOException)
            {
                // un video a metà rimasto nella cartella non è un errore grave
            }
        }
    }
}
