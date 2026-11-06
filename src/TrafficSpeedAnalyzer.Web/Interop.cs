using System.Text.Json;
using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Web;

// Dati scambiati con wwwroot/js/app.js (nomi in camelCase lato JavaScript).

public sealed class EngineInfo
{
    public bool WebCodecs { get; set; }
    public bool WebGpu { get; set; }
    public bool CrossOriginIsolated { get; set; }
    public int Cores { get; set; }
    public string? UserAgent { get; set; }
    public string? Build { get; set; }
}

public sealed class ModelStatus
{
    public bool Ok { get; set; }
    public bool Missing { get; set; }
    public string? Error { get; set; }
    public string? Name { get; set; }
    public bool Verified { get; set; }
    public string? Provider { get; set; }
    public string? Note { get; set; }
    public int Size { get; set; }
    public int Threads { get; set; }
    public double LoadMs { get; set; }
    public string? Log { get; set; }
    public string? Build { get; set; }
}

public sealed class VideoInfo
{
    public string? Error { get; set; }
    public string Name { get; set; } = "";
    public double FileSize { get; set; }
    public string Codec { get; set; } = "";
    public int CodedWidth { get; set; }
    public int CodedHeight { get; set; }
    public int Rotation { get; set; }
    public int DisplayWidth { get; set; }
    public int DisplayHeight { get; set; }
    public int FrameCount { get; set; }
    public double Duration { get; set; }
    public double MeanFps { get; set; }
    public double MinFrameInterval { get; set; }
    public double MaxFrameInterval { get; set; }
    public double FirstTime { get; set; }
    public string? CreationDate { get; set; }
    public string? CreationSource { get; set; }
    public bool Supported { get; set; }
    public bool Hardware { get; set; }
    public string? Reason { get; set; }
}

public sealed class FrameShown
{
    public int Index { get; set; }
    public double Time { get; set; }
}

public sealed class ProgressInfo
{
    public int Done { get; set; }
    public int Total { get; set; }
    public int FrameIndex { get; set; }
    public int Detected { get; set; }
    public int Skipped { get; set; }
    public double FramesPerSecond { get; set; }
    public double MeanDetectMs { get; set; }
}

public sealed class AnalysisSummaryInfo
{
    public string? Error { get; set; }
    public int Done { get; set; }
    public int Total { get; set; }
    public int Detected { get; set; }
    public int Skipped { get; set; }
    public bool Cancelled { get; set; }
    public double ElapsedMs { get; set; }
    public int LastFrame { get; set; }
    public double FramesPerSecond { get; set; }
    public double MeanDetectMs { get; set; }
    public string? Provider { get; set; }
    public string? ProviderNote { get; set; }
}

public sealed class PtsCheck
{
    public int Expected { get; set; }
    public int Actual { get; set; }
    public double MaxError { get; set; }
}

/// <summary>
/// Calibrazione salvata su file: stesso formato .tsacal.json dell'app desktop
/// (TrafficSpeedAnalyzer.App.Services.CalibrationFile), così i file sono intercambiabili.
/// </summary>
public sealed record CalibrationFileData(
    int VideoWidth,
    int VideoHeight,
    double[] LineA,
    double[] LineB,
    double DistanceMeters,
    string? SourceVideo,
    DateTimeOffset SavedAt)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static CalibrationFileData From(int width, int height, LineSegment a, LineSegment b, double distance, string? video) =>
        new(width, height, ToArray(a), ToArray(b), distance, video, DateTimeOffset.Now);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static CalibrationFileData Parse(string json)
    {
        CalibrationFileData? f = JsonSerializer.Deserialize<CalibrationFileData>(json);
        if (f is null || f.LineA is not { Length: 4 } || f.LineB is not { Length: 4 } || f.VideoWidth <= 0 || f.VideoHeight <= 0)
        {
            throw new InvalidDataException("Il file non contiene una calibrazione valida.");
        }

        return f;
    }

    public bool HasSameAspect(int width, int height) =>
        Math.Abs(width / (double)height - VideoWidth / (double)VideoHeight) < 0.01;

    public (LineSegment A, LineSegment B) ScaledTo(int width, int height)
    {
        double sx = width / (double)VideoWidth;
        double sy = height / (double)VideoHeight;
        return (FromArray(LineA, sx, sy), FromArray(LineB, sx, sy));
    }

    public static double[] ToArray(LineSegment s) => [s.Start.X, s.Start.Y, s.End.X, s.End.Y];

    private static LineSegment FromArray(double[] v, double sx, double sy) =>
        new(new Point2(v[0] * sx, v[1] * sy), new Point2(v[2] * sx, v[3] * sy));
}
