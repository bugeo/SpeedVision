using System.Globalization;
using System.Text.Json;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Video;

public sealed class VideoProbeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Legge metadati e timestamp dei fotogrammi con ffprobe.</summary>
public static class VideoProbe
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<VideoInfo> ProbeAsync(FfmpegTools tools, string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(Loc.T("File non trovato."), path);
        }

        ProcessResult r = await ProcessRunner.RunAsync(
            tools.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            TimeSpan.FromSeconds(60),
            ct).ConfigureAwait(false);

        if (r.ExitCode != 0)
        {
            throw new VideoProbeException(Loc.T("Il file non è un video leggibile. ") + FirstLine(r.StandardError));
        }

        return ParseProbeJson(r.StandardOutput, path, new FileInfo(path).Length);
    }

    internal static VideoInfo ParseProbeJson(string json, string path, long fileSize)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        JsonElement? video = null;
        if (root.TryGetProperty("streams", out JsonElement streams))
        {
            foreach (JsonElement s in streams.EnumerateArray())
            {
                if (Str(s, "codec_type") != "video")
                {
                    continue;
                }

                // Esclude copertine e miniature incorporate.
                if (s.TryGetProperty("disposition", out JsonElement disp) && Int(disp, "attached_pic") == 1)
                {
                    continue;
                }

                video = s;
                break;
            }
        }

        if (video is not { } v)
        {
            throw new VideoProbeException(Loc.T("Il file non contiene una traccia video."));
        }

        root.TryGetProperty("format", out JsonElement format);

        int width = Int(v, "width") ?? 0;
        int height = Int(v, "height") ?? 0;
        if (width <= 0 || height <= 0)
        {
            throw new VideoProbeException(Loc.T("Dimensioni del video non valide."));
        }

        int rotation = ReadRotation(v);
        double sar = ParseRational(Str(v, "sample_aspect_ratio")) is { } s0 && s0 > 0 ? s0 : 1.0;
        int dispW = (int)Math.Round(width * sar);
        int dispH = height;
        dispW += dispW % 2;
        if (rotation is 90 or 270)
        {
            (dispW, dispH) = (dispH, dispW);
        }

        double fps = ParseRational(Str(v, "avg_frame_rate")) is { } avg && avg > 0
            ? avg
            : ParseRational(Str(v, "r_frame_rate")) ?? 0;

        double duration = ParseDouble(Str(v, "duration")) ?? (format.ValueKind == JsonValueKind.Object ? ParseDouble(Str(format, "duration")) : null) ?? 0;
        double start = ParseDouble(Str(v, "start_time")) ?? 0;
        double timeBase = ParseRational(Str(v, "time_base")) ?? 0;

        (DateTimeOffset? recStart, string? source) = ReadRecordingStart(format, v);

        return new VideoInfo
        {
            FilePath = path,
            CodecName = Str(v, "codec_name") ?? "sconosciuto",
            CodedWidth = width,
            CodedHeight = height,
            DisplayWidth = dispW,
            DisplayHeight = dispH,
            Rotation = rotation,
            NominalFps = fps,
            DurationSeconds = duration,
            StartTimeSeconds = start,
            TimeBase = timeBase,
            RecordingStart = recStart,
            RecordingStartSource = source,
            Location = ReadLocation(format),
            FileSizeBytes = fileSize,
            PixelFormat = Str(v, "pix_fmt"),
        };
    }

    /// <summary>
    /// Costruisce l'indice dei fotogrammi leggendo i pacchetti del flusso video (veloce, senza decodifica).
    /// Se i pacchetti non hanno timestamp, decodifica il video per leggerli dai fotogrammi.
    /// </summary>
    public static async Task<FrameIndex> BuildIndexAsync(FfmpegTools tools, VideoInfo info, CancellationToken ct = default)
    {
        ProcessResult r = await ProcessRunner.RunAsync(
            tools.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts,flags", "-of", "csv=p=0", info.FilePath],
            TimeSpan.FromMinutes(10),
            ct).ConfigureAwait(false);

        if (r.ExitCode == 0 && TryParsePacketTimes(r.StandardOutput, info.TimeBase, out List<double> times) && times.Count > 0)
        {
            return new FrameIndex(times);
        }

        // Riserva: timestamp dai fotogrammi decodificati (più lento ma sempre disponibile).
        ProcessResult rf = await ProcessRunner.RunAsync(
            tools.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", info.FilePath],
            TimeSpan.FromMinutes(30),
            ct).ConfigureAwait(false);

        if (rf.ExitCode != 0)
        {
            throw new VideoProbeException(Loc.T("Impossibile leggere i tempi dei fotogrammi. ") + FirstLine(rf.StandardError));
        }

        var frameTimes = new List<double>();
        foreach (string line in rf.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ParseDouble(line.Split(',')[0]) is { } t)
            {
                frameTimes.Add(t);
            }
        }

        return new FrameIndex(frameTimes);
    }

    /// <summary>Legge le righe "pts,flags" di ffprobe; scarta i pacchetti marcati da scartare (D).</summary>
    internal static bool TryParsePacketTimes(string csv, double timeBase, out List<double> times)
    {
        times = [];
        if (timeBase <= 0)
        {
            return false;
        }

        foreach (string raw in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = raw.Split(',');
            if (parts.Length == 0 || parts[0].Length == 0)
            {
                continue;
            }

            if (!long.TryParse(parts[0], NumberStyles.Integer, Inv, out long pts))
            {
                return false; // "N/A": timestamp mancanti, serve la decodifica
            }

            string flags = parts.Length > 1 ? parts[1] : string.Empty;
            if (flags.Contains('D'))
            {
                continue;
            }

            times.Add(pts * timeBase);
        }

        return true;
    }

    internal static int ReadRotation(JsonElement stream)
    {
        double? rot = null;
        if (stream.TryGetProperty("side_data_list", out JsonElement sdl) && sdl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement sd in sdl.EnumerateArray())
            {
                if (sd.TryGetProperty("rotation", out JsonElement r) && r.ValueKind == JsonValueKind.Number)
                {
                    rot = r.GetDouble();
                    break;
                }
            }
        }

        if (rot is null && stream.TryGetProperty("tags", out JsonElement tags))
        {
            rot = ParseDouble(Str(tags, "rotate"));
        }

        if (rot is null)
        {
            return 0;
        }

        int deg = (int)Math.Round(rot.Value);
        deg = ((deg % 360) + 360) % 360;
        return (int)(Math.Round(deg / 90.0) * 90) % 360;
    }

    /// <summary>Posizione di ripresa: iPhone (com.apple.quicktime.location.ISO6709) o Android (location).</summary>
    internal static TrafficSpeedAnalyzer.Core.Calibration.GeoPosition? ReadLocation(JsonElement format)
    {
        if (format.ValueKind != JsonValueKind.Object || !format.TryGetProperty("tags", out JsonElement tags))
        {
            return null;
        }

        foreach (string key in new[] { "com.apple.quicktime.location.ISO6709", "location", "location-eng" })
        {
            if (TrafficSpeedAnalyzer.Core.Calibration.GeoPosition.ParseIso6709(Str(tags, key)) is { } p)
            {
                return p;
            }
        }

        return null;
    }

    internal static (DateTimeOffset?, string?) ReadRecordingStart(JsonElement format, JsonElement stream)
    {
        // L'orario Apple include il fuso orario locale: è il più affidabile.
        if (format.ValueKind == JsonValueKind.Object && format.TryGetProperty("tags", out JsonElement ftags))
        {
            if (TryParseDate(Str(ftags, "com.apple.quicktime.creationdate"), out DateTimeOffset apple))
            {
                return (apple, "com.apple.quicktime.creationdate");
            }

            if (TryParseDate(Str(ftags, "creation_time"), out DateTimeOffset ct))
            {
                return (ct.ToLocalTime(), "creation_time");
            }
        }

        if (stream.TryGetProperty("tags", out JsonElement stags) && TryParseDate(Str(stags, "creation_time"), out DateTimeOffset sct))
        {
            return (sct.ToLocalTime(), Loc.T("creation_time (traccia)"));
        }

        return (null, null);
    }

    internal static bool TryParseDate(string? s, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        // Formati come "2026-10-25T22:15:03+0100" (Apple) non sono gestiti da Parse: aggiunge i due punti.
        string normalized = s.Trim();
        if (normalized.Length > 5 && (normalized[^5] == '+' || normalized[^5] == '-') && normalized[^4..].All(char.IsDigit))
        {
            normalized = normalized[..^2] + ":" + normalized[^2..];
        }

        if (!DateTimeOffset.TryParse(normalized, Inv, DateTimeStyles.AssumeUniversal, out value))
        {
            return false;
        }

        // Molti dispositivi scrivono 1904-01-01 o 1970-01-01 quando l'orologio non è impostato.
        return value.Year >= 2000;
    }

    internal static double? ParseRational(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        int slash = s.IndexOfAny(['/', ':']);
        if (slash < 0)
        {
            return ParseDouble(s);
        }

        if (ParseDouble(s[..slash]) is { } num && ParseDouble(s[(slash + 1)..]) is { } den && den != 0)
        {
            return num / den;
        }

        return null;
    }

    internal static double? ParseDouble(string? s) =>
        s is not null && double.TryParse(s, NumberStyles.Float, Inv, out double d) && double.IsFinite(d) ? d : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement p)
            ? p.ValueKind switch
            {
                JsonValueKind.String => p.GetString(),
                JsonValueKind.Number => p.GetRawText(),
                _ => null,
            }
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int i)
            ? i
            : null;

    private static string FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
}
