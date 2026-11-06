namespace TrafficSpeedAnalyzer.Video;

/// <summary>Metadati del video letti con ffprobe.</summary>
public sealed record VideoInfo
{
    public required string FilePath { get; init; }

    public required string CodecName { get; init; }

    /// <summary>Larghezza con cui il fotogramma viene mostrato (rotazione e proporzioni applicate).</summary>
    public required int DisplayWidth { get; init; }

    public required int DisplayHeight { get; init; }

    public required int CodedWidth { get; init; }

    public required int CodedHeight { get; init; }

    /// <summary>Rotazione indicata nei metadati, in gradi (0, 90, 180, 270).</summary>
    public int Rotation { get; init; }

    /// <summary>Frame rate medio dichiarato dal contenitore.</summary>
    public double NominalFps { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary>Timestamp del primo fotogramma nel file.</summary>
    public double StartTimeSeconds { get; init; }

    /// <summary>Base dei tempi del flusso video (secondi per unità di pts).</summary>
    public double TimeBase { get; init; }

    /// <summary>Data e ora di inizio registrazione dai metadati, se presente.</summary>
    public DateTimeOffset? RecordingStart { get; init; }

    /// <summary>Campo dei metadati da cui è stato letto l'orario.</summary>
    public string? RecordingStartSource { get; init; }

    /// <summary>Posizione GPS di ripresa dai metadati (iPhone e molti Android), se presente.</summary>
    public TrafficSpeedAnalyzer.Core.Calibration.GeoPosition? Location { get; init; }

    public long FileSizeBytes { get; init; }

    public string? PixelFormat { get; init; }

    public string FileName => Path.GetFileName(FilePath);
}
