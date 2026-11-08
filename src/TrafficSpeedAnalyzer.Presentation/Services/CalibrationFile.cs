using System.IO;
using System.Text.Json;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.Services;

/// <summary>
/// Calibrazione salvata su file, per riusarla con altri video girati dalla stessa posizione.
/// Le coordinate sono in pixel del video; insieme vengono salvate le dimensioni del video
/// per riscalarle se la risoluzione è diversa ma con le stesse proporzioni.
/// </summary>
public sealed record CalibrationFile(
    int VideoWidth,
    int VideoHeight,
    double[] LineA,
    double[] LineB,
    double DistanceMeters,
    string? SourceVideo,
    DateTimeOffset SavedAt)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static CalibrationFile From(int width, int height, LineSegment a, LineSegment b, double distance, string? video) =>
        new(width, height, ToArray(a), ToArray(b), distance, video, DateTimeOffset.Now);

    public async Task SaveAsync(string path) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, Json));

    public static async Task<CalibrationFile> LoadAsync(string path)
    {
        CalibrationFile? f = JsonSerializer.Deserialize<CalibrationFile>(await File.ReadAllTextAsync(path));
        if (f is null || f.LineA is not { Length: 4 } || f.LineB is not { Length: 4 } || f.VideoWidth <= 0 || f.VideoHeight <= 0)
        {
            throw new InvalidDataException(Loc.T("Il file non contiene una calibrazione valida."));
        }

        return f;
    }

    /// <summary>Linee riscalate sulle dimensioni del video corrente.</summary>
    public (LineSegment A, LineSegment B) ScaledTo(int width, int height)
    {
        double sx = width / (double)VideoWidth;
        double sy = height / (double)VideoHeight;
        return (FromArray(LineA, sx, sy), FromArray(LineB, sx, sy));
    }

    public bool HasSameAspect(int width, int height) =>
        Math.Abs(width / (double)height - VideoWidth / (double)VideoHeight) < 0.01;

    private static double[] ToArray(LineSegment s) => [s.Start.X, s.Start.Y, s.End.X, s.End.Y];

    private static LineSegment FromArray(double[] v, double sx, double sy) =>
        new(new Point2(v[0] * sx, v[1] * sy), new Point2(v[2] * sx, v[3] * sy));
}
