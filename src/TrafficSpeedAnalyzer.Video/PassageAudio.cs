using System.Globalization;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Video;

/// <summary>
/// Un pezzo della colonna sonora: audio del video originale da <see cref="SourceStart"/> (secondi dall'inizio del file)
/// per <see cref="Duration"/> secondi, oppure silenzio se <see cref="SourceStart"/> è null. All'inizio del pezzo suona una
/// campanella con <see cref="Bell"/>, un bip con <see cref="Beep"/>.
/// </summary>
public readonly record struct AudioPiece(double? SourceStart, double Duration, bool Bell = false, bool Beep = false);

/// <summary>
/// Aggiunge a un video già scritto l'audio del video originale, a pezzi (il rumore dei veicoli mentre il video scorre,
/// silenzio durante i fermi), e le campanelle. L'audio originale viene decodificato una volta in un file PCM temporaneo; i pezzi si
/// copiano da lì, così la colonna sonora ha esattamente la durata del video, senza comandi ffmpeg lunghi.
/// </summary>
public static class PassageAudio
{
    private const int Rate = 48000, BytesPerSample = 4;   // stereo, 16 bit

    /// <summary>Il file ha almeno una traccia audio.</summary>
    public static async Task<bool> HasAudioAsync(FfmpegTools tools, string path, CancellationToken ct = default)
    {
        ProcessResult r = await ProcessRunner.RunAsync(tools.FfprobePath,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=index", "-of", "csv=p=0", path],
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return r.ExitCode == 0 && r.StandardOutput.Trim().Length > 0;
    }

    /// <summary>
    /// Sostituisce <paramref name="videoPath"/> con lo stesso video più la colonna sonora fatta dai pezzi.
    /// Senza <paramref name="sourcePath"/> (video originale muto) restano solo le campanelle.
    /// </summary>
    public static async Task AddAsync(FfmpegTools tools, string videoPath, string? sourcePath, IReadOnlyList<AudioPiece> pieces, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "speedvision-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string source = Path.Combine(dir, "sorgente.raw");
            string track = Path.Combine(dir, "traccia.raw");
            string muxed = Path.Combine(dir, "video.mp4");
            string rate = Rate.ToString(CultureInfo.InvariantCulture);

            if (sourcePath is not null)
            {
                await RunAsync(tools, ["-i", sourcePath, "-map", "0:a:0", "-vn", "-ac", "2", "-ar", rate, "-f", "s16le", "-y", source], ct).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllBytesAsync(source, [], ct).ConfigureAwait(false);
            }

            await BuildTrackAsync(source, track, pieces, ct).ConfigureAwait(false);
            await RunAsync(tools,
                ["-i", videoPath, "-f", "s16le", "-ar", rate, "-ac", "2", "-i", track,
                 "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "160k",
                 "-shortest", "-movflags", "+faststart", "-y", muxed], ct).ConfigureAwait(false);
            File.Move(muxed, videoPath, overwrite: true);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // file temporanei rimasti: non è un errore grave
            }
        }
    }

    /// <summary>
    /// Copia i pezzi dal PCM originale; i silenzi e le parti oltre la fine dell'audio sono zeri; le campanelle si
    /// sommano all'inizio dei pezzi che le chiedono.
    /// </summary>
    internal static async Task BuildTrackAsync(string sourcePcm, string trackPcm, IReadOnlyList<AudioPiece> pieces, CancellationToken ct)
    {
        await using FileStream src = File.OpenRead(sourcePcm);
        await using FileStream dst = File.Create(trackPcm);
        double elapsed = 0;
        long written = 0;
        foreach (AudioPiece piece in pieces)
        {
            // Posizioni dal tempo totale, non sommando pezzi arrotondati: nessuna deriva rispetto al video.
            elapsed += piece.Duration;
            long length = ((long)Math.Round(elapsed * Rate) * BytesPerSample) - written;
            if (length <= 0)
            {
                continue;
            }

            var buffer = new byte[length];
            if (piece.SourceStart is { } s)
            {
                long readPos = Math.Max(0, (long)Math.Round(s * Rate)) * BytesPerSample;
                if (readPos < src.Length)
                {
                    src.Position = readPos;
                    await src.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                }
            }

            if (piece.Bell)
            {
                MixBell(buffer);
            }

            if (piece.Beep)
            {
                Mix(buffer, BeepSamples.Value);
            }

            await dst.WriteAsync(buffer, ct).ConfigureAwait(false);
            written += length;
        }
    }

    /// <summary>Campanella sintetica (1,2 s): parziali di una campana con smorzamento esponenziale, sommata ai campioni.</summary>
    internal static void MixBell(byte[] pcm) => Mix(pcm, BellSamples.Value);

    /// <summary>Somma un suono mono ai due canali, saturando ai limiti dei 16 bit.</summary>
    private static void Mix(byte[] pcm, short[] bell)
    {
        int frames = Math.Min(bell.Length, pcm.Length / BytesPerSample);
        for (int i = 0; i < frames; i++)
        {
            for (int ch = 0; ch < 2; ch++)
            {
                int o = (i * BytesPerSample) + (ch * 2);
                int v = (short)(pcm[o] | (pcm[o + 1] << 8)) + bell[i];
                v = Math.Clamp(v, short.MinValue, short.MaxValue);
                pcm[o] = (byte)v;
                pcm[o + 1] = (byte)(v >> 8);
            }
        }
    }

    /// <summary>Bip (0,25 s a 1 kHz) con attacco e rilascio morbidi: compare la velocità.</summary>
    private static readonly Lazy<short[]> BeepSamples = new(() =>
    {
        var samples = new short[(int)(0.25 * Rate)];
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)Rate;
            double env = Math.Min(1, Math.Min(t / 0.01, (0.25 - t) / 0.03));
            samples[i] = (short)(0.25 * short.MaxValue * env * Math.Sin(2 * Math.PI * 1000 * t));
        }

        return samples;
    });

    private static readonly Lazy<short[]> BellSamples = new(() =>
    {
        const double seconds = 1.2, f0 = 1046.5;   // do6
        (double Ratio, double Gain, double Decay)[] partials = [(1, 1, 3.5), (2.0, 0.45, 5), (2.76, 0.35, 6.5), (5.4, 0.18, 9), (8.93, 0.08, 12)];
        var samples = new short[(int)(seconds * Rate)];
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)Rate;
            double v = 0;
            foreach ((double ratio, double gain, double decay) in partials)
            {
                v += gain * Math.Exp(-decay * t) * Math.Sin(2 * Math.PI * f0 * ratio * t);
            }

            double attack = Math.Min(1, t / 0.004);   // niente clic all'attacco
            samples[i] = (short)(0.32 * short.MaxValue * attack * v / 2.06);
        }

        return samples;
    });

    private static async Task RunAsync(FfmpegTools tools, IEnumerable<string> args, CancellationToken ct)
    {
        ProcessResult r = await ProcessRunner.RunAsync(tools.FfmpegPath, ["-hide_banner", "-nostdin", "-v", "error", .. args], TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new IOException(Loc.T("ffmpeg non è riuscito a scrivere il video: ") + r.StandardError.Trim());
        }
    }
}
