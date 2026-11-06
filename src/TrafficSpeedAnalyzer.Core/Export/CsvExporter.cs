using System.Globalization;
using System.Text;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Time;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Export;

/// <summary>
/// CSV dei passaggi, pensato per Excel nella lingua scelta: UTF-8 con BOM, separatore e decimali della
/// lingua (in italiano ";" e virgola, in inglese "," e punto), fine riga CRLF. Intestazioni tradotte.
/// Colonne: id progressivo, verso di marcia (A → B o B → A), orario ingresso, orario uscita, tempo impiegato, velocità.
/// </summary>
public static class CsvExporter
{
    /// <summary>Separatore di campo della lingua scelta (";" in italiano, "," in inglese).</summary>
    public static char Separator
    {
        get
        {
            string s = Loc.Culture.TextInfo.ListSeparator;
            char c = s.Length > 0 ? s[0] : ';';
            // Con la virgola decimale il separatore non può essere la virgola.
            return c == ',' && Loc.Culture.NumberFormat.NumberDecimalSeparator == "," ? ';' : c;
        }
    }

    public static string[] Header =>
    [
        "Id",
        Loc.T("Verso"),
        Loc.T("Orario ingresso"),
        Loc.T("Orario uscita"),
        Loc.T("Tempo impiegato (s)"),
        Loc.T("Velocità (km/h)"),
    ];

    /// <summary>
    /// Costruisce il contenuto testuale del CSV (senza BOM).
    /// Se <paramref name="recordingStart"/> è nullo, gli orari sono il tempo del video (hh:mm:ss.fff).
    /// Con <paramref name="lanes"/> (id del passaggio → corsia, 1 = la più vicina) si aggiunge la colonna «Corsia».
    /// </summary>
    public static string BuildCsv(IEnumerable<VehiclePassage> passages, DateTimeOffset? recordingStart, IReadOnlyDictionary<int, int>? lanes = null)
    {
        var sb = new StringBuilder();
        string[] h = Header;
        string[] header = lanes is null ? h : [h[0], h[1], Loc.T("Corsia"), .. h[2..]];
        AppendRow(sb, header);
        foreach (VehiclePassage p in passages.OrderBy(p => p.Id))
        {
            var row = new List<string>
            {
                p.Id.ToString(CultureInfo.InvariantCulture),
                FormatDirection(p.Direction),
            };
            if (lanes is not null)
            {
                row.Add(lanes.TryGetValue(p.Id, out int lane) ? lane.ToString(CultureInfo.InvariantCulture) : string.Empty);
            }

            row.Add(FormatTime(p.Entry.TimeSeconds, recordingStart));
            row.Add(FormatTime(p.Exit.TimeSeconds, recordingStart));
            row.Add(p.Speed.DurationSeconds.ToString("F3", Loc.Culture));
            row.Add(p.Speed.SpeedKmh.ToString("F1", Loc.Culture));
            AppendRow(sb, row);
        }

        return sb.ToString();
    }

    public static byte[] BuildCsvBytes(IEnumerable<VehiclePassage> passages, DateTimeOffset? recordingStart, IReadOnlyDictionary<int, int>? lanes = null)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(BuildCsv(passages, recordingStart, lanes));
        return [.. preamble, .. body];
    }

    public static async Task WriteAsync(string path, IEnumerable<VehiclePassage> passages, DateTimeOffset? recordingStart, CancellationToken ct = default, IReadOnlyDictionary<int, int>? lanes = null)
    {
        byte[] bytes = BuildCsvBytes(passages, recordingStart, lanes);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    public static string FormatTime(double videoSeconds, DateTimeOffset? recordingStart) =>
        recordingStart is { } start
            ? TimeFormatting.FormatClock(start, videoSeconds)
            : TimeFormatting.FormatVideoTimeLong(videoSeconds);

    public static string FormatDirection(TravelDirection direction) =>
        direction == TravelDirection.AToB ? "A → B" : "B → A";

    internal static string Escape(string field)
    {
        bool needsQuotes = field.IndexOfAny([Separator, '"', '\r', '\n']) >= 0;
        return needsQuotes ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(Separator);
            }

            sb.Append(Escape(fields[i]));
        }

        sb.Append("\r\n");
    }
}
