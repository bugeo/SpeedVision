using System.Globalization;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Time;

public static class TimeFormatting
{
    /// <summary>Arrotonda al millisecondo, gestendo correttamente i riporti (59,9996 s → 1:00,000).</summary>
    public static TimeSpan ToRoundedTimeSpan(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            throw new ArgumentException("Tempo non valido.", nameof(seconds));
        }

        return TimeSpan.FromMilliseconds(Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero));
    }

    /// <summary>Tempo del video: "mm:ss.fff" oppure "h:mm:ss.fff" oltre l'ora.</summary>
    public static string FormatVideoTime(double seconds)
    {
        TimeSpan ts = ToRoundedTimeSpan(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>Tempo del video con ore sempre presenti: "hh:mm:ss.fff" (usato nel CSV).</summary>
    public static string FormatVideoTimeLong(double seconds)
    {
        TimeSpan ts = ToRoundedTimeSpan(Math.Max(0, seconds));
        int hours = (int)ts.TotalHours;
        return hours.ToString("00", CultureInfo.InvariantCulture) + ts.ToString(@"\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>Orario reale: inizio registrazione + tempo del video.</summary>
    public static DateTimeOffset ToClock(DateTimeOffset recordingStart, double videoSeconds) =>
        recordingStart + ToRoundedTimeSpan(videoSeconds);

    public static string FormatClock(DateTimeOffset recordingStart, double videoSeconds) =>
        ToClock(recordingStart, videoSeconds).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Durata in secondi con virgola decimale: "1,437".</summary>
    public static string FormatSeconds(double seconds, int decimals = 3) =>
        seconds.ToString("F" + decimals, Loc.Culture);
}
