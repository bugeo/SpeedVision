using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Video;

public sealed class StabilizationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Avanzamento: passo corrente (1…StepCount), descrizione e frazione complessiva 0–1.</summary>
public sealed record StabilizationProgress(int Step, int StepCount, string Description, double Fraction);

/// <summary>Esito della stabilizzazione, salvato accanto al video stabilizzato.</summary>
public sealed record StabilizationReport
{
    public required string OutputPath { get; init; }

    public required long OutputBytes { get; init; }

    public required int FrameCount { get; init; }

    public required string Parameters { get; init; }

    /// <summary>Spostamento massimo misurato nel video originale rispetto al primo fotogramma (pixel).</summary>
    public required double ShakeBeforeMaxPx { get; init; }

    /// <summary>95° percentile dello spostamento nel video originale (pixel).</summary>
    public required double ShakeBeforeP95Px { get; init; }

    /// <summary>Spostamento massimo rimasto dopo la stabilizzazione, misurato con una seconda analisi (pixel).</summary>
    public required double ResidualMaxPx { get; init; }

    /// <summary>Fotogrammi che dopo la stabilizzazione risultano ancora spostati di almeno <see cref="VideoStabilizer.MisalignedThresholdPx"/>.</summary>
    public required IReadOnlyList<int> MisalignedFrames { get; init; }

    /// <summary>Fotogrammi in cui l'analisi ha trovato pochi punti di riferimento (stima meno affidabile).</summary>
    public required int WeakFrames { get; init; }

    public required double ElapsedSeconds { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Vero se il risultato è stato ripreso da una stabilizzazione precedente.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FromCache { get; init; }
}

/// <summary>
/// Stabilizzazione "treppiede" con FFmpeg vidstab: ogni fotogramma viene allineato al primo (non si segue un
/// movimento voluto della camera, come nella stabilizzazione normale).
/// <list type="number">
/// <item>vidstabdetect misura lo spostamento di ogni fotogramma rispetto al primo (tripod=1);</item>
/// <item>vidstabtransform applica la correzione e il video viene ricodificato mantenendo i tempi originali di ogni fotogramma;</item>
/// <item>una seconda misura con vidstabdetect sul risultato verifica che i fotogrammi siano davvero fermi.</item>
/// </list>
/// Il primo fotogramma non viene modificato, quindi le linee disegnate sull'originale restano valide.
/// vidstab corregge spostamento e rotazione; con spostamenti molto grandi della camera o zoom può restare uno
/// scarto, che la verifica segnala.
/// </summary>
public static class VideoStabilizer
{
    /// <summary>Cambia quando cambiano i parametri, per non riusare risultati vecchi.</summary>
    public const string FormatVersion = "5";

    public const double MisalignedThresholdPx = 1.5;

    public const int WeakFieldCount = 5;

    private const string VidStabDetectParams = "shakiness=10:accuracy=15:stepsize=4:mincontrast=0.05";

    private const string Crf = "18";

    /// <summary>Parametri usati, per info.txt.</summary>
    public static string ParametersDescription =>
        Loc.T("treppiede (riferimento: primo fotogramma), FFmpeg vidstab ") + VidStabDetectParams.Replace(':', ' ') + ", H.264 crf " + Crf;

    /// <summary>Che cosa serve a FFmpeg per la stabilizzazione.</summary>
    private sealed record Capabilities(bool VidStab, bool HasFileFormat);

    private static readonly Dictionary<string, Capabilities> SupportCache = [];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Vero se l'FFmpeg installato ha i filtri vidstab e il codificatore H.264.</summary>
    public static async Task<bool> IsSupportedAsync(FfmpegTools tools, CancellationToken ct = default)
    {
        Capabilities c = await GetCapabilitiesAsync(tools, ct).ConfigureAwait(false);
        return c.VidStab;
    }

    private static async Task<Capabilities> GetCapabilitiesAsync(FfmpegTools tools, CancellationToken ct)
    {
        lock (SupportCache)
        {
            if (SupportCache.TryGetValue(tools.FfmpegPath, out var cached))
            {
                return cached;
            }
        }

        Capabilities caps;
        try
        {
            ProcessResult filters = await ProcessRunner.RunAsync(tools.FfmpegPath, ["-hide_banner", "-filters"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            ProcessResult encoders = await ProcessRunner.RunAsync(tools.FfmpegPath, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            bool x264 = encoders.StandardOutput.Contains(" libx264 ", StringComparison.Ordinal);
            bool vidstab = x264 && filters.StandardOutput.Contains(" vidstabdetect ", StringComparison.Ordinal)
                && filters.StandardOutput.Contains(" vidstabtransform ", StringComparison.Ordinal);
            bool fileFormat = false;
            if (vidstab)
            {
                // Le versioni recenti di libvidstab scrivono il .trf in binario: si chiede il testo, se l'opzione esiste.
                ProcessResult help = await ProcessRunner.RunAsync(tools.FfmpegPath, ["-hide_banner", "-h", "filter=vidstabdetect"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                fileFormat = help.StandardOutput.Contains("fileformat", StringComparison.Ordinal);
            }

            caps = new Capabilities(vidstab, fileFormat);
        }
        catch (Exception e) when (e is TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            caps = new Capabilities(false, false);
        }

        lock (SupportCache)
        {
            SupportCache[tools.FfmpegPath] = caps;
        }

        return caps;
    }

    /// <summary>Cartella della cache per questo video (dipende da percorso, dimensione, data di modifica e parametri).</summary>
    public static string CacheFolder(string cacheRoot, VideoInfo source)
    {
        var fi = new FileInfo(source.FilePath);
        string id = string.Join('|', FormatVersion, Path.GetFullPath(source.FilePath).ToUpperInvariant(), fi.Length,
            fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), "vidstab", Crf);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16].ToLowerInvariant();
        string name = new string(Path.GetFileNameWithoutExtension(source.FilePath).Where(char.IsLetterOrDigit).Take(24).ToArray());
        return Path.Combine(cacheRoot, (name.Length > 0 ? name + "_" : string.Empty) + hash);
    }

    /// <summary>Risultato già pronto per questo video, se esiste.</summary>
    public static StabilizationReport? TryLoadCached(string cacheRoot, VideoInfo source)
    {
        string folder = CacheFolder(cacheRoot, source);
        string reportPath = Path.Combine(folder, "report.json");
        try
        {
            if (!File.Exists(reportPath))
            {
                return null;
            }

            StabilizationReport? r = JsonSerializer.Deserialize<StabilizationReport>(File.ReadAllText(reportPath));
            string video = Path.Combine(folder, "stabilized.mp4");
            if (r is null || !File.Exists(video) || new FileInfo(video).Length != r.OutputBytes)
            {
                return null;
            }

            File.SetLastWriteTimeUtc(reportPath, DateTime.UtcNow); // usato di recente: resta in cache
            return r with { OutputPath = video, FromCache = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stabilizza il video (o riusa un risultato precedente). <paramref name="sourceIndex"/> è l'indice dei
    /// fotogrammi dell'originale: serve a verificare che il video stabilizzato abbia gli stessi fotogrammi
    /// con gli stessi tempi.
    /// </summary>
    public static async Task<StabilizationReport> StabilizeAsync(
        FfmpegTools tools,
        VideoInfo source,
        FrameIndex sourceIndex,
        string cacheRoot,
        IProgress<StabilizationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (TryLoadCached(cacheRoot, source) is { } cached)
        {
            return cached;
        }

        Capabilities caps = await GetCapabilitiesAsync(tools, ct).ConfigureAwait(false);
        if (!caps.VidStab)
        {
            throw new StabilizationException(
                Loc.T("La versione di FFmpeg installata non include i filtri vidstab o il codificatore H.264. ") +
                Loc.T("Installa la versione completa: winget install --id Gyan.FFmpeg -e"));
        }

        string folder = CacheFolder(cacheRoot, source);
        Directory.CreateDirectory(folder);
        string input = Path.GetFullPath(source.FilePath);
        int frames = sourceIndex.Count;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        const int steps = 3;

        void Report(int step, string text, double f) =>
            progress?.Report(new StabilizationProgress(step, steps, text, Math.Clamp((step - 1 + Math.Clamp(f, 0, 1)) / steps, 0, 1)));

        try
        {
            // 1. Misura del movimento rispetto al primo fotogramma.
            string measureText = Loc.T("Misura del movimento");
            Report(1, measureText, 0);
            string textFormat = caps.HasFileFormat ? ":fileformat=ascii" : string.Empty;
            await RunFfmpegAsync(tools, folder,
                ["-i", input, "-map", "0:v:0", "-an", "-sn", "-dn",
                 "-vf", "vidstabdetect=" + VidStabDetectParams + textFormat + ":tripod=1:result=pass1.trf",
                 "-f", "null", "-"],
                frames, f => Report(1, measureText, f), ct).ConfigureAwait(false);
            FrameMotion[] before = FromVidStab(VidStabFile.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "pass1.trf"), ct).ConfigureAwait(false)), frames);
            string[] correction = ["-vf", "vidstabtransform=input=pass1.trf:tripod=1:optzoom=0:crop=black:interpol=bicubic"];

            // 2. Compensazione e ricodifica con i tempi originali (-copyts, passthrough, base dei tempi del file).
            string transformText = Loc.T("Compensazione e ricodifica");
            Report(2, transformText, 0);
            int gop = Math.Clamp((int)Math.Round(source.NominalFps > 0 ? source.NominalFps : 30), 1, 120);
            string part = Path.Combine(folder, "stabilized.part.mp4");
            await RunFfmpegAsync(tools, folder,
                ["-copyts", "-i", input, "-map", "0:v:0", "-an", "-sn", "-dn",
                 .. correction,
                 "-fps_mode", "passthrough", "-enc_time_base", "demux",
                 "-c:v", "libx264", "-preset", "veryfast", "-crf", Crf, "-bf", "0", "-g", gop.ToString(CultureInfo.InvariantCulture),
                 "-pix_fmt", "yuv420p", "-map_metadata", "0", "-movflags", "+faststart",
                 "-y", "stabilized.part.mp4"],
                frames, f => Report(2, transformText, f), ct).ConfigureAwait(false);

            // 3. Verifica: stessi fotogrammi e tempi, e il video stabilizzato deve risultare fermo
            //    (seconda misura con vidstabdetect, sempre rispetto al primo fotogramma).
            string verifyText = Loc.T("Verifica del risultato");
            Report(3, verifyText, 0);
            VideoInfo outInfo = await VideoProbe.ProbeAsync(tools, part, ct).ConfigureAwait(false);
            if (outInfo.DisplayWidth != source.DisplayWidth || outInfo.DisplayHeight != source.DisplayHeight)
            {
                throw new StabilizationException(Loc.S($"Il video stabilizzato ha dimensioni diverse dall'originale ({outInfo.DisplayWidth}×{outInfo.DisplayHeight} invece di {source.DisplayWidth}×{source.DisplayHeight})."));
            }

            FrameIndex outIndex = await VideoProbe.BuildIndexAsync(tools, outInfo, ct).ConfigureAwait(false);
            CheckSameTimes(sourceIndex, outIndex);
            await RunFfmpegAsync(tools, folder,
                ["-i", "stabilized.part.mp4", "-map", "0:v:0", "-an", "-sn", "-dn",
                 "-vf", "vidstabdetect=" + VidStabDetectParams + textFormat + ":tripod=1:result=verify.trf",
                 "-f", "null", "-"],
                frames, f => Report(3, verifyText, f * 0.95), ct).ConfigureAwait(false);
            FrameMotion[] after = FromVidStab(VidStabFile.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "verify.trf"), ct).ConfigureAwait(false)), frames);

            string video = Path.Combine(folder, "stabilized.mp4");
            File.Move(part, video, overwrite: true);

            StabilizationReport report = BuildReport(before, after, source.DisplayWidth, source.DisplayHeight,
                video, new FileInfo(video).Length, frames, clock.Elapsed.TotalSeconds);
            await File.WriteAllTextAsync(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(report, JsonOptions), ct).ConfigureAwait(false);
            DeleteQuietly(Path.Combine(folder, "pass1.trf"));
            DeleteQuietly(Path.Combine(folder, "verify.trf"));
            Report(3, verifyText, 1);
            return report;
        }
        catch
        {
            // Nessun risultato a metà nella cache.
            DeleteFolderQuietly(folder);
            throw;
        }
    }

    /// <summary>Movimenti di vidstabdetect (mediana dei campi, solo traslazione) come <see cref="FrameMotion"/>.</summary>
    internal static FrameMotion[] FromVidStab(IReadOnlyList<StabFrameMotion> motions, int frames)
    {
        if (motions.Count < frames / 2)
        {
            throw new StabilizationException(Loc.T("Non è stato possibile leggere le misure di vidstab: il risultato non è verificabile ed è stato scartato."));
        }

        var result = new FrameMotion[frames];
        for (int i = 0; i < frames; i++)
        {
            StabFrameMotion? m = i < motions.Count ? motions[i] : null;
            result[i] = i == 0 || m is null || double.IsNaN(m.Dx)
                ? FrameMotion.Identity(i, m?.Fields ?? 0) with { Reliable = i == 0 }
                : FrameMotion.FromSimilarity(i, 1, 0, m.Dx, m.Dy, m.Fields, m.Fields, m.Fields >= WeakFieldCount);
        }

        return result;
    }

    /// <summary>
    /// Riassunto: spostamento nell'originale e residuo dopo la correzione (massimo tra centro e angoli della
    /// fascia a terra), fotogrammi rimasti spostati e fotogrammi ricostruiti o misurati con pochi punti.
    /// </summary>
    internal static StabilizationReport BuildReport(
        IReadOnlyList<FrameMotion> before,
        IReadOnlyList<FrameMotion> after,
        int width,
        int height,
        string outputPath,
        long outputBytes,
        int frameCount,
        double elapsedSeconds)
    {
        // Il primo fotogramma è il riferimento. Spostamenti misurati sulla metà bassa, dove stanno le linee
        // (con vidstab lo spostamento è lo stesso in tutta l'immagine).
        const double band = 0.5;
        double[] shake = before.Skip(1).Select(m => m.MaxDisplacement(width, height, band)).Order().ToArray();
        double[] residual = after.Skip(1).Where(m => m.Reliable).Select(m => m.MaxDisplacement(width, height, band)).ToArray();
        List<int> misaligned = after
            .Select((m, i) => (m, i))
            .Where(x => x.i > 0 && x.m.Reliable && x.m.MaxDisplacement(width, height, band) >= MisalignedThresholdPx)
            .Select(x => x.i)
            .ToList();

        return new StabilizationReport
        {
            OutputPath = outputPath,
            OutputBytes = outputBytes,
            FrameCount = frameCount,
            Parameters = ParametersDescription,
            ShakeBeforeMaxPx = shake.Length > 0 ? shake[^1] : 0,
            ShakeBeforeP95Px = shake.Length > 0 ? shake[Math.Min(shake.Length - 1, (int)Math.Floor(0.95 * shake.Length))] : 0,
            ResidualMaxPx = residual.Length > 0 ? residual.Max() : 0,
            MisalignedFrames = misaligned,
            WeakFrames = before.Skip(1).Zip(after.Skip(1)).Count(p => !p.First.Reliable || !p.Second.Reliable),
            ElapsedSeconds = elapsedSeconds,
            CreatedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// Stesso numero di fotogrammi e stessi intervalli tra i tempi (entro 2 ms). Uno scarto costante
    /// è ammesso: i tempi mostrati sono relativi al primo fotogramma.
    /// </summary>
    internal static void CheckSameTimes(FrameIndex source, FrameIndex output)
    {
        if (source.Count != output.Count)
        {
            throw new StabilizationException(Loc.S($"Il video stabilizzato ha {output.Count} fotogrammi invece di {source.Count}: risultato scartato."));
        }

        double offset = output[0] - source[0];
        for (int i = 0; i < source.Count; i++)
        {
            if (Math.Abs(output[i] - source[i] - offset) > 0.002)
            {
                throw new StabilizationException(Loc.S($"Il tempo del fotogramma {i + 1} è cambiato durante la stabilizzazione ({source[i]:0.0000} → {output[i] - offset:0.0000} s): risultato scartato."));
            }
        }
    }

    private static async Task RunFfmpegAsync(
        FfmpegTools tools,
        string workingDirectory,
        IEnumerable<string> args,
        int frameCount,
        Action<double> onFraction,
        CancellationToken ct)
    {
        // I file .trf sono indicati con percorsi relativi alla cartella di lavoro: nei parametri dei filtri
        // i percorsi Windows ("C:\…") richiederebbero un escape dei due punti.
        string[] all = ["-hide_banner", "-nostdin", "-v", "error", "-nostats", "-progress", "pipe:1", .. args];
        ProcessResult r = await ProcessRunner.RunStreamingAsync(tools.FfmpegPath, all, workingDirectory, line =>
        {
            if (line.StartsWith("frame=", StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                && frameCount > 0)
            {
                onFraction(n / (double)frameCount);
            }
        }, ct).ConfigureAwait(false);

        if (r.ExitCode != 0)
        {
            string detail = string.Join(" ", r.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
            throw new StabilizationException(Loc.T("FFmpeg non è riuscito a stabilizzare il video. ") + detail);
        }
    }

    // ───────────── Gestione della cache ─────────────

    /// <summary>Spazio occupato dai video stabilizzati.</summary>
    public static long CacheSizeBytes(string cacheRoot)
    {
        try
        {
            return Directory.Exists(cacheRoot)
                ? new DirectoryInfo(cacheRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Elimina i risultati, tranne quelli nelle cartelle indicate (per esempio il video in uso).</summary>
    public static void ClearCache(string cacheRoot, params string[] keepFolders)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        var keep = new HashSet<string>(keepFolders.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (string dir in Directory.EnumerateDirectories(cacheRoot))
        {
            if (!keep.Contains(Path.GetFullPath(dir)))
            {
                DeleteFolderQuietly(dir);
            }
        }
    }

    /// <summary>Tiene solo i <paramref name="keep"/> risultati usati più di recente.</summary>
    public static void PruneCache(string cacheRoot, int keep, params string[] keepFolders)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        var protectedDirs = new HashSet<string>(keepFolders.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        IEnumerable<DirectoryInfo> old = new DirectoryInfo(cacheRoot).EnumerateDirectories()
            .OrderByDescending(d => File.Exists(Path.Combine(d.FullName, "report.json"))
                ? File.GetLastWriteTimeUtc(Path.Combine(d.FullName, "report.json"))
                : d.LastWriteTimeUtc)
            .Skip(keep);
        foreach (DirectoryInfo d in old)
        {
            if (!protectedDirs.Contains(Path.GetFullPath(d.FullName)))
            {
                DeleteFolderQuietly(d.FullName);
            }
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Cancella la cartella. Su Windows un processo ffmpeg appena chiuso può tenere aperto un file ancora per
    /// qualche istante: si riprova per circa due secondi prima di rinunciare.
    /// </summary>
    private static void DeleteFolderQuietly(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
