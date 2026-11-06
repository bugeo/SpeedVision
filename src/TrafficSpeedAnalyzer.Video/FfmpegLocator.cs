using System.Runtime.InteropServices;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Video;

/// <summary>Percorsi degli eseguibili FFmpeg verificati.</summary>
public sealed record FfmpegTools(string FfmpegPath, string FfprobePath, string Version);

/// <summary>
/// Cerca ffmpeg e ffprobe sul PC: cartella indicata dall'utente, cartella "ffmpeg" accanto
/// all'applicazione, PATH, installazioni winget.
/// </summary>
public static class FfmpegLocator
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string Exe(string name) => IsWindows ? name + ".exe" : name;

    public static async Task<FfmpegTools?> LocateAsync(string? preferredDirectory = null, CancellationToken ct = default)
    {
        foreach (string dir in CandidateDirectories(preferredDirectory))
        {
            string ffmpeg = Path.Combine(dir, Exe("ffmpeg"));
            string ffprobe = Path.Combine(dir, Exe("ffprobe"));
            if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
            {
                continue;
            }

            string? version = await TryGetVersionAsync(ffmpeg, ct).ConfigureAwait(false);
            if (version is not null && await TryGetVersionAsync(ffprobe, ct).ConfigureAwait(false) is not null)
            {
                return new FfmpegTools(ffmpeg, ffprobe, version);
            }
        }

        return null;
    }

    internal static IEnumerable<string> CandidateDirectories(string? preferredDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> Raw()
        {
            if (!string.IsNullOrWhiteSpace(preferredDirectory))
            {
                yield return preferredDirectory;
                yield return Path.Combine(preferredDirectory, "bin");
            }

            string appDir = AppContext.BaseDirectory;
            yield return Path.Combine(appDir, "ffmpeg");
            yield return Path.Combine(appDir, "ffmpeg", "bin");
            yield return appDir;

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (path is not null)
            {
                foreach (string p in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return p.Trim('"');
                }
            }

            if (IsWindows)
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                yield return Path.Combine(local, "Microsoft", "WinGet", "Links");

                string packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(packages))
                {
                    IEnumerable<string> bins;
                    try
                    {
                        bins = Directory.EnumerateDirectories(packages, "*FFmpeg*")
                            .SelectMany(pkg => Directory.EnumerateDirectories(pkg, "bin", SearchOption.AllDirectories))
                            .ToList();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        bins = [];
                    }

                    foreach (string bin in bins)
                    {
                        yield return bin;
                    }
                }

                yield return @"C:\ffmpeg\bin";
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin");
                yield return @"C:\ProgramData\chocolatey\bin";
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                yield return Path.Combine(userProfile, "scoop", "shims");
            }
            else
            {
                yield return "/usr/bin";
                yield return "/usr/local/bin";
                yield return "/opt/homebrew/bin";
            }
        }

        foreach (string dir in Raw())
        {
            string full;
            try
            {
                full = Path.GetFullPath(dir);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Add(full))
            {
                yield return full;
            }
        }
    }

    private static async Task<string?> TryGetVersionAsync(string exe, CancellationToken ct)
    {
        try
        {
            ProcessResult r = await ProcessRunner.RunAsync(exe, ["-hide_banner", "-version"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (r.ExitCode != 0)
            {
                return null;
            }

            string first = r.StandardOutput.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            return first.Length > 0 ? first : Loc.T("versione sconosciuta");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }
}
