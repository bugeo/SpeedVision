using System.Globalization;
using System.IO;

namespace TrafficSpeedAnalyzer.App.Services;

/// <summary>Registro minimale su file in %LocalAppData%\TrafficSpeedAnalyzer\logs.</summary>
public static class AppLog
{
    private static readonly object Sync = new();

    public static string LogDirectory => Path.Combine(AppSettings.DataDirectory, "logs");

    private static string LogFile => Path.Combine(LogDirectory, "app.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? e = null) => Write("ERROR", e is null ? message : message + Environment.NewLine + e);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = new FileInfo(LogFile);
                if (file.Exists && file.Length > 2 * 1024 * 1024)
                {
                    File.Move(LogFile, Path.Combine(LogDirectory, "app.old.log"), overwrite: true);
                }

                string line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                File.AppendAllText(LogFile, line);
            }
        }
        catch (IOException)
        {
            // il registro non deve mai bloccare l'applicazione
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
