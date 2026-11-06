namespace TrafficSpeedAnalyzer.Video.Tests;

/// <summary>
/// Genera con FFmpeg piccoli video di prova con contenuto noto: una barra bianca che avanza
/// di 8 pixel per fotogramma su sfondo nero. Dalla posizione della barra si ricava quale
/// fotogramma è stato decodificato davvero.
/// </summary>
public sealed class SyntheticVideos : IAsyncLifetime
{
    public const int Width = 800;
    public const int Height = 64;
    public const int PixelsPerFrame = 8;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsa-tests-" + Guid.NewGuid().ToString("N"));

    public FfmpegTools? Tools { get; private set; }

    public string? SkipReason { get; private set; }

    /// <summary>90 fotogrammi a 30 fps costanti, con B-frame e GOP di 30.</summary>
    public string ConstantRatePath => Path.Combine(_dir, "cfr" + Extension);

    /// <summary>30 fotogrammi a 30 fps, poi 60 fotogrammi a 15 fps (frame rate variabile).</summary>
    public string VariableRatePath => Path.Combine(_dir, "vfr" + Extension);

    private string Extension { get; set; } = ".mp4";

    public async ValueTask InitializeAsync()
    {
        Tools = await FfmpegLocator.LocateAsync();
        if (Tools is null)
        {
            SkipReason = "FFmpeg non trovato: installalo (winget install Gyan.FFmpeg) per eseguire i test video.";
            return;
        }

        Directory.CreateDirectory(_dir);

        // Preferisce H.264 in MP4 (come uno smartphone); se libx264 manca usa FFV1 senza perdite.
        string[][] codecs =
        [
            ["-c:v", "libx264", "-qp", "0", "-g", "30", "-bf", "2", "-pix_fmt", "yuv420p"],
            ["-c:v", "ffv1", "-pix_fmt", "yuv420p"],
        ];
        string[] extensions = [".mp4", ".mkv"];

        for (int i = 0; i < codecs.Length; i++)
        {
            Extension = extensions[i];
            bool ok = await Generate(ConstantRatePath, "[0][1]overlay=x=8*n:y=0", codecs[i], vfr: false)
                   && await Generate(VariableRatePath, "[0][1]overlay=x=8*n:y=0,setpts='if(lt(N,30),N/30,1+(N-30)/15)/TB'", codecs[i], vfr: true);
            if (ok)
            {
                return;
            }
        }

        SkipReason = "FFmpeg trovato ma non è riuscito a generare i video di prova.";
    }

    private async Task<bool> Generate(string output, string filter, string[] codec, bool vfr)
    {
        var args = new List<string>
        {
            "-hide_banner", "-v", "error", "-y",
            "-f", "lavfi", "-i", $"color=c=black:s={Width}x{Height}:r=30:d=3",
            "-f", "lavfi", "-i", $"color=c=white:s={PixelsPerFrame}x{Height}:r=30:d=3",
            "-filter_complex", filter,
        };
        if (vfr)
        {
            args.AddRange(["-fps_mode", "vfr"]);
        }

        args.AddRange(codec);
        args.AddRange(["-metadata", "creation_time=2026-10-25T21:15:00.000000Z", output]);

        ProcessResult r = await ProcessRunner.RunAsync(Tools!.FfmpegPath, args, TimeSpan.FromSeconds(60), CancellationToken.None);
        return r.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // file ancora aperto: verrà rimosso dalla pulizia dei temporanei
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Colonna del bordo sinistro della barra bianca, misurata sulla riga centrale.</summary>
    public static int BarLeftEdge(VideoFrame frame)
    {
        int row = frame.Height / 2;
        int offset = row * frame.Stride;
        for (int x = 0; x < frame.Width; x++)
        {
            int p = offset + x * 3;
            if (frame.Pixels[p] > 128 && frame.Pixels[p + 1] > 128 && frame.Pixels[p + 2] > 128)
            {
                return x;
            }
        }

        return -1;
    }
}
