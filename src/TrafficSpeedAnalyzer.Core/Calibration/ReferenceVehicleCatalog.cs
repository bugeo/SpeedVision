using System.Globalization;
using System.Text;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Calibration;

/// <summary>
/// Veicolo di dimensioni note, usato come metro per la calibrazione: la distanza tra i punti in cui le ruote toccano
/// terra è il passo (<see cref="WheelbaseMm"/>).
/// </summary>
public sealed record ReferenceVehicle(
    string Make,
    string Model,
    string Generation,
    int FromYear,
    int ToYear,
    int LengthMm,
    int WheelbaseMm,
    string Type)
{
    /// <summary>Tipo dei veicoli con il passo inserito dall'utente.</summary>
    public const string ManualType = "manuale";

    /// <summary>Passi accettati per l'inserimento a mano (metri): dalle microcar ai furgoni lunghi.</summary>
    public const double MinManualMeters = 1.5, MaxManualMeters = 6.0;

    /// <summary>Veicolo di cui l'utente conosce solo il passo (distanza tra i centri delle ruote).</summary>
    public static ReferenceVehicle Manual(double wheelbaseMeters) =>
        new(Loc.T("Auto"), Loc.T("con passo inserito a mano"), string.Empty, 0, 0, 0, (int)Math.Round(wheelbaseMeters * 1000), ManualType);

    public bool IsManual => Type == ManualType;

    public double LengthMeters => LengthMm / 1000.0;

    /// <summary>Passo in metri; 0 se l'elenco non lo indica.</summary>
    public double WheelbaseMeters => WheelbaseMm / 1000.0;

    /// <summary>Il passo è noto: l'auto si può usare per la calibrazione.</summary>
    public bool HasWheelbase => WheelbaseMm > 0;

    public string Years => IsManual ? Loc.T("anno non indicato") : ToYear == 0
        ? string.Create(CultureInfo.InvariantCulture, $"dal {FromYear}")
        : string.Create(CultureInfo.InvariantCulture, $"{FromYear}–{ToYear}");

    public string DisplayName => Make + " " + Model + (Generation.Length > 0 ? " " + Generation : string.Empty);

    /// <summary>Descrizione con anni e lunghezza, per l'elenco di scelta.</summary>
    public string Details => HasWheelbase
        ? Loc.S($"{Years} · passo {WheelbaseMeters:0.00} m")
        : Loc.S($"{Years} · passo non indicato");

    /// <summary>Testo normalizzato per la ricerca (minuscolo, senza accenti).</summary>
    public string SearchKey { get; } = ReferenceVehicleCatalog.Normalize($"{Make} {Model} {Generation} {Type}");
}

/// <summary>Esito della lettura dell'elenco delle auto: veicoli validi e problemi trovati (righe scartate, file mancante).</summary>
public sealed record ReferenceVehicleList(IReadOnlyList<ReferenceVehicle> Vehicles, IReadOnlyList<string> Problems, string Source);

/// <summary>
/// Elenco dei veicoli di lunghezza nota (da schede tecniche dei costruttori, allestimento base: tra
/// allestimenti possono esserci differenze di qualche centimetro). Si legge dal file esterno
/// <see cref="FileName"/>, accanto al programma, con il punto e virgola come separatore: si può
/// modificare con Excel o con un editor di testo, senza ricompilare.
/// <para>
/// Prima riga: intestazioni (in qualsiasi ordine) <c>Marca;Modello;Generazione;Dal anno;Al anno;Lunghezza mm;Passo mm;Tipo</c>.
/// Obbligatorie Marca, Modello e Lunghezza mm; «Al anno» vuoto = ancora in produzione. Righe vuote o che iniziano con
/// <c>#</c> sono ignorate. Testo UTF-8 o Windows-1252 (il «CSV» salvato da Excel in italiano); campi tra virgolette ammessi.
/// Le righe non valide vengono scartate e segnalate in <see cref="Problems"/>, senza bloccare il programma.
/// </para>
/// </summary>
public static class ReferenceVehicleCatalog
{
    public const string FileName = "veicoli_riferimento.csv";

    public const char Separator = ';';

    /// <summary>Lunghezze accettate nel file (millimetri).</summary>
    public const int MinLengthMm = 2000, MaxLengthMm = 20000;

    private static readonly object Sync = new();
    private static ReferenceVehicleList? _current;

    /// <summary>Percorso del file: nella cartella del programma.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    private static ReferenceVehicleList Current
    {
        get
        {
            lock (Sync)
            {
                return _current ??= Read(DefaultPath);
            }
        }
    }

    public static IReadOnlyList<ReferenceVehicle> All => Current.Vehicles;

    /// <summary>Problemi dell'ultima lettura: file mancante o illeggibile, righe scartate.</summary>
    public static IReadOnlyList<string> Problems => Current.Problems;

    /// <summary>File (o origine) da cui è stato letto l'elenco.</summary>
    public static string Source => Current.Source;

    /// <summary>Sostituisce l'elenco con quello del file indicato.</summary>
    public static ReferenceVehicleList Load(string path)
    {
        ReferenceVehicleList list = Read(path);
        lock (Sync)
        {
            _current = list;
        }

        return list;
    }

    /// <summary>
    /// Sostituisce l'elenco con il testo CSV indicato (per esempio scaricato dal sito nella versione web,
    /// dove non c'è una cartella del programma).
    /// </summary>
    public static ReferenceVehicleList LoadCsv(string text, string source)
    {
        ReferenceVehicleList list = Parse(text, source);
        lock (Sync)
        {
            _current = list;
        }

        return list;
    }

    /// <summary>Veicoli che contengono tutte le parole cercate (in qualsiasi ordine).</summary>
    public static IEnumerable<ReferenceVehicle> Search(string? query)
    {
        string[] words = Normalize(query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? All : All.Where(v => words.All(w => v.SearchKey.Contains(w, StringComparison.Ordinal)));
    }

    internal static string Normalize(string text)
    {
        string d = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (char c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return sb.ToString();
    }

    /// <summary>Legge il file senza cambiare l'elenco in uso.</summary>
    public static ReferenceVehicleList Read(string path)
    {
        if (!File.Exists(path))
        {
            return new ReferenceVehicleList([], [Loc.S($"File dell'elenco auto non trovato: {path}")], path);
        }

        try
        {
            return Parse(DecodeText(File.ReadAllBytes(path)), path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ReferenceVehicleList([], [Loc.S($"Impossibile leggere il file {path}: {e.Message}")], path);
        }
    }

    /// <summary>UTF-8 (con o senza BOM) se valido, altrimenti Windows-1252 come il «CSV» di Excel in italiano.</summary>
    public static string DecodeText(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(span);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(span);
        }
    }

    // Intestazioni riconosciute (minuscole, solo lettere): italiano e inglese.
    private static readonly Dictionary<string, string> Columns = new()
    {
        ["marca"] = "make", ["make"] = "make",
        ["modello"] = "model", ["model"] = "model",
        ["generazione"] = "generation", ["serie"] = "generation", ["generation"] = "generation",
        ["dalanno"] = "from", ["daanno"] = "from", ["annoinizio"] = "from", ["fromyear"] = "from",
        ["alanno"] = "to", ["aanno"] = "to", ["annofine"] = "to", ["toyear"] = "to",
        ["lunghezzamm"] = "length", ["lunghezza"] = "length", ["lengthmm"] = "length", ["length"] = "length",
        ["passomm"] = "wheelbase", ["passo"] = "wheelbase", ["wheelbasemm"] = "wheelbase", ["wheelbase"] = "wheelbase",
        ["tipo"] = "type", ["type"] = "type",
    };

    /// <summary>Legge il testo CSV (separatore <see cref="Separator"/>).</summary>
    public static ReferenceVehicleList Parse(string text, string source)
    {
        var problems = new List<string>();
        var vehicles = new List<ReferenceVehicle>();
        List<(int Line, string[] Fields)> rows = SplitRows(text)
            .Where(r => r.Fields.Any(f => f.Trim().Length > 0) && !r.Fields[0].TrimStart().StartsWith('#'))
            .ToList();
        if (rows.Count == 0)
        {
            return new ReferenceVehicleList([], [Loc.S($"Il file {source} è vuoto.")], source);
        }

        string[] header = rows[0].Fields;
        if (header.Length == 1 && header[0].Contains(','))
        {
            return new ReferenceVehicleList([], [Loc.S($"Il file {source} deve usare il punto e virgola (;) come separatore.")], source);
        }

        var index = new Dictionary<string, int>();
        for (int c = 0; c < header.Length; c++)
        {
            string key = new string(Normalize(header[c]).Where(char.IsLetter).ToArray());
            if (Columns.TryGetValue(key, out string? column))
            {
                index.TryAdd(column, c);
            }
        }

        string[] missing = new[] { ("make", "Marca"), ("model", "Modello"), ("length", "Lunghezza mm") }
            .Where(m => !index.ContainsKey(m.Item1))
            .Select(m => m.Item2)
            .ToArray();
        if (missing.Length > 0)
        {
            return new ReferenceVehicleList([], [Loc.S($"Nel file {source} mancano le colonne: {string.Join(", ", missing)}.")], source);
        }

        foreach ((int line, string[] f) in rows.Skip(1))
        {
            string Get(string column) => index.TryGetValue(column, out int c) && c < f.Length ? f[c].Trim() : string.Empty;

            string make = Get("make"), model = Get("model");
            if (make.Length == 0 || model.Length == 0)
            {
                problems.Add(Loc.S($"Riga {line}: marca o modello mancante."));
                continue;
            }

            if (ParseInt(Get("length")) is not { } length || length < MinLengthMm || length > MaxLengthMm)
            {
                problems.Add(Loc.S($"Riga {line}: lunghezza non valida («{Get("length")}»): servono i millimetri, tra {MinLengthMm} e {MaxLengthMm}."));
                continue;
            }

            int? wheelbase = Get("wheelbase").Length == 0 ? 0 : ParseInt(Get("wheelbase"));
            if (wheelbase is not { } wb || wb < 0 || wb >= length)
            {
                problems.Add(Loc.S($"Riga {line}: passo non valido («{Get("wheelbase")}»)."));
                continue;
            }

            int? from = Get("from").Length == 0 ? 0 : ParseInt(Get("from"));
            int? to = Get("to").Length == 0 ? 0 : ParseInt(Get("to"));
            if (from is not { } y0 || to is not { } y1 || (y0 != 0 && y0 is < 1900 or > 2100) || (y1 != 0 && (y1 is < 1900 or > 2100 || y1 < y0)))
            {
                problems.Add(Loc.S($"Riga {line}: anni non validi («{Get("from")}», «{Get("to")}»)."));
                continue;
            }

            string type = Get("type");
            vehicles.Add(new ReferenceVehicle(make, model, Get("generation"), y0, y1, length, wb, type.Length == 0 ? "auto" : type));
        }

        return new ReferenceVehicleList(vehicles, problems, source);
    }

    /// <summary>Numero intero; ammessi i separatori delle migliaia (3.538 o 3 538), come li scrive Excel.</summary>
    private static int? ParseInt(string s)
    {
        string digits = s.Replace(".", string.Empty).Replace(" ", string.Empty).Replace("\u00A0", string.Empty).Replace("\u202F", string.Empty);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : null;
    }

    /// <summary>Righe e campi del CSV, con il numero della riga nel file. Campi tra virgolette con ; e "" ammessi.</summary>
    private static IEnumerable<(int Line, string[] Fields)> SplitRows(string text)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        int line = 1, start = 1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.ToString().Trim().Length == 0:
                    field.Clear();
                    quoted = true;
                    break;
                case Separator:
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return (start, fields.ToArray());
                    fields.Clear();
                    line++;
                    start = line;
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (start, fields.ToArray());
        }
    }
}
