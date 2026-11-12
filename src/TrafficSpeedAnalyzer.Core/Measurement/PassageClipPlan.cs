namespace TrafficSpeedAnalyzer.Core.Measurement;

/// <summary>Momento del filmato di un passaggio.</summary>
public enum ClipPhase
{
    /// <summary>I secondi prima dell'ingresso: il veicolo arriva verso la linea; il cronometro non c'è ancora.</summary>
    Approach,

    /// <summary>Fermo sul fotogramma dell'ingresso: compare il cronometro a zero.</summary>
    EntryPause,

    /// <summary>Il veicolo va dall'ingresso all'uscita: il cronometro corre.</summary>
    Timing,

    /// <summary>Fermo sul fotogramma dell'uscita: cronometro fermo sul tempo misurato.</summary>
    ExitPause,

    /// <summary>Sempre fermo sull'uscita, dopo la pausa: compare la velocità.</summary>
    SpeedPause,
}

/// <summary>
/// Un fotogramma del filmato: quale fotogramma del video mostrare, in che momento e cosa segna il cronometro
/// (secondi dall'istante d'ingresso; null prima dell'ingresso).
/// </summary>
public readonly record struct ClipFrame(int SourceFrame, ClipPhase Phase, double? Chrono);

/// <summary>Durate del filmato di un passaggio.</summary>
public sealed record PassageClipOptions
{
    public static PassageClipOptions Default { get; } = new();

    /// <summary>Secondi di arrivo prima dell'istante d'ingresso (almeno questi, se il video li ha).</summary>
    public double ApproachSeconds { get; init; } = 3;

    /// <summary>Durata del fermo all'ingresso.</summary>
    public double EntryPauseSeconds { get; init; } = 1.5;

    /// <summary>Durata del fermo all'uscita prima che compaia la velocità.</summary>
    public double ExitPauseSeconds { get; init; } = 2;

    /// <summary>Per quanto resta la velocità sul fermo dell'uscita.</summary>
    public double SpeedPauseSeconds { get; init; } = 3;
}

/// <summary>
/// Sequenza dei fotogrammi del filmato di un passaggio: il veicolo arriva, il video si ferma all'ingresso e compare il
/// cronometro, riparte, si ferma all'uscita con il cronometro fermo sul tempo misurato; dopo 2 secondi compare la velocità. Il filmato scorre alla velocità
/// reale del video: durante il tragitto il cronometro segna il tempo vero di ciascun fotogramma rispetto all'istante
/// d'ingresso stimato, e all'uscita mostra esattamente la durata usata per la velocità.
/// </summary>
public static class PassageClipPlan
{
    /// <param name="frameTimes">Tempo di ogni fotogramma del video, in secondi dall'inizio (stessa base dei passaggi).</param>
    /// <param name="fps">Fotogrammi al secondo del filmato prodotto (per le durate dei fermi).</param>
    public static List<ClipFrame> Build(VehiclePassage passage, IReadOnlyList<double> frameTimes, double fps, PassageClipOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fps, 0);
        options ??= PassageClipOptions.Default;
        int last = frameTimes.Count - 1;
        int entry = (int)Math.Clamp(passage.Entry.FirstBeyond.FrameIndex, 0, last);
        int exit = (int)Math.Clamp(passage.Exit.FirstBeyond.FrameIndex, entry, last);
        double entryTime = passage.Entry.TimeSeconds;
        double duration = passage.Speed.DurationSeconds;

        // Il filmato parte dall'ultimo fotogramma che precede l'ingresso di almeno ApproachSeconds (o dall'inizio del video).
        int start = entry;
        while (start > 0 && entryTime - frameTimes[start] < options.ApproachSeconds)
        {
            start--;
        }

        var frames = new List<ClipFrame>();
        for (int i = start; i < entry; i++)
        {
            frames.Add(new ClipFrame(i, ClipPhase.Approach, null));
        }

        // Il fotogramma dell'ingresso è il primo oltre la linea: il cronometro parte da zero (l'istante stimato
        // cade tra il fotogramma precedente e questo).
        frames.AddRange(Enumerable.Repeat(new ClipFrame(entry, ClipPhase.EntryPause, 0), Count(options.EntryPauseSeconds, fps)));
        for (int i = entry + 1; i < exit; i++)
        {
            frames.Add(new ClipFrame(i, ClipPhase.Timing, Math.Clamp(frameTimes[i] - entryTime, 0, duration)));
        }

        frames.AddRange(Enumerable.Repeat(new ClipFrame(exit, ClipPhase.ExitPause, duration), Count(options.ExitPauseSeconds, fps)));
        frames.AddRange(Enumerable.Repeat(new ClipFrame(exit, ClipPhase.SpeedPause, duration), Count(options.SpeedPauseSeconds, fps)));
        return frames;
    }

    private static int Count(double seconds, double fps) => Math.Max(1, (int)Math.Round(seconds * fps));
}
