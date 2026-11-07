using System.IO;
using System.Text.Json;

namespace TrafficSpeedAnalyzer.App.Services;

/// <summary>Preferenze salvate in %LocalAppData%\TrafficSpeedAnalyzer\settings.json.</summary>
public sealed class AppSettings
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrafficSpeedAnalyzer");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Lingua dell'interfaccia ("it", "en", "fr", "de", "es"); null = quella di Windows.</summary>
    public string? Language { get; set; }

    public string? FfmpegDirectory { get; set; }

    public string? LastVideoFolder { get; set; }

    public string? LastExportFolder { get; set; }

    public bool AutoAdvance { get; set; } = true;

    public int AutoAdvanceFrames { get; set; } = 1;

    public string? ModelId { get; set; }

    public bool CpuOnly { get; set; }

    public bool RegionAroundLines { get; set; } = true;

    public bool SkipStaticFrames { get; set; } = true;

    public double StartScore { get; set; } = 0.5;

    /// <summary>Ultima vista della mappa (calibrazione sulla mappa) e livello scelto ("map" o "satellite").</summary>
    public double? MapLatitude { get; set; }

    public double? MapLongitude { get; set; }

    public double? MapZoom { get; set; }

    public string? MapLayer { get; set; }

    /// <summary>Limite di velocità dei risultati, in km/h.</summary>
    public double SpeedLimitKmh { get; set; } = 50;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn("Impostazioni non leggibili, uso i valori predefiniti: " + e.Message);
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Impossibile salvare le impostazioni: " + e.Message);
        }
    }
}
