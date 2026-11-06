using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace TrafficSpeedAnalyzer.Core.Localization;

/// <summary>Lingua dell'interfaccia: codice, nome nella lingua stessa e cultura per numeri e date.</summary>
public sealed record AppLanguage(string Code, string NativeName, string CultureName)
{
    public CultureInfo Culture => CultureInfo.GetCultureInfo(CultureName);

    public override string ToString() => NativeName;
}

/// <summary>
/// Traduzione dei testi. La chiave è il testo italiano (lingua di partenza): <c>Loc.T("Procedi")</c>
/// per i testi fissi e <c>Loc.S($"Auto {n} di {tot}")</c> per quelli con valori, che diventano la chiave
/// <c>"Auto {0} di {1}"</c>. Le traduzioni sono in <c>Localization/strings_xx.json</c> (risorse incorporate);
/// un testo senza traduzione resta in italiano. Anche numeri e date seguono la lingua scelta (<see cref="Culture"/>).
/// </summary>
public static class Loc
{
    public static IReadOnlyList<AppLanguage> Languages { get; } =
    [
        new("it", "Italiano", "it-IT"),
        new("en", "English", "en-GB"),
        new("fr", "Français", "fr-FR"),
        new("de", "Deutsch", "de-DE"),
        new("es", "Español", "es-ES"),
    ];

    private static volatile IReadOnlyDictionary<string, string> _table = new Dictionary<string, string>();

    private static AppLanguage _current = Languages[0];

    /// <summary>Cambio di lingua: l'interfaccia aggiorna i testi.</summary>
    public static event Action? Changed;

    public static AppLanguage Current => _current;

    public static string Language => _current.Code;

    /// <summary>Cultura per numeri e date nella lingua scelta.</summary>
    public static CultureInfo Culture => _current.Culture;

    /// <summary>Lingua di Windows se è tra quelle disponibili, altrimenti italiano.</summary>
    public static string SystemLanguage()
    {
        string code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == code) ? code : "it";
    }

    /// <summary>Imposta la lingua (codice a due lettere); un codice sconosciuto vale italiano.</summary>
    public static void SetLanguage(string? code)
    {
        AppLanguage lang = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[0];
        _table = lang.Code == "it" ? new Dictionary<string, string>() : Load(lang.Code);
        _current = lang;
        Changed?.Invoke();
    }

    /// <summary>Testo fisso tradotto.</summary>
    public static string T(string italian) =>
        _table.TryGetValue(italian, out string? t) && t.Length > 0 ? t : italian;

    /// <summary>Testo fisso (senza valori).</summary>
    public static string S(string italian) => T(italian);

    /// <summary>Testo con valori: i valori sono formattati nella cultura della lingua scelta.</summary>
    public static string S(LocFormat text)
    {
        (string format, object?[] args) = text.Build();
        string translated = T(format);
        try
        {
            return string.Format(Culture, translated, args);
        }
        catch (FormatException)
        {
            // Traduzione con segnaposto sbagliati: meglio l'italiano che un errore.
            return string.Format(Culture, format, args);
        }
    }

    /// <summary>Chiavi della lingua indicata (per i test di copertura).</summary>
    public static IReadOnlyDictionary<string, string> Table(string code) => code == "it" ? new Dictionary<string, string>() : Load(code);

    private static Dictionary<string, string> Load(string code)
    {
        Assembly asm = typeof(Loc).Assembly;
        using Stream? s = asm.GetManifestResourceStream($"strings_{code}.json");
        if (s is null)
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
    }
}

/// <summary>
/// Costruisce dalla stringa interpolata la chiave con segnaposto numerati (<c>{0}</c>, <c>{1:0.0}</c>…)
/// e l'elenco dei valori, così il testo si traduce prima di inserire i valori.
/// </summary>
[InterpolatedStringHandler]
public ref struct LocFormat
{
    private readonly StringBuilder _format;
    private readonly List<object?> _args;

    public LocFormat(int literalLength, int formattedCount)
    {
        _format = new StringBuilder(literalLength + (formattedCount * 4));
        _args = new List<object?>(formattedCount);
    }

    public readonly void AppendLiteral(string s) => _format.Append(s.Replace("{", "{{").Replace("}", "}}"));

    public readonly void AppendFormatted<T>(T value) => Add(value, 0, null);

    public readonly void AppendFormatted<T>(T value, string? format) => Add(value, 0, format);

    public readonly void AppendFormatted<T>(T value, int alignment) => Add(value, alignment, null);

    public readonly void AppendFormatted<T>(T value, int alignment, string? format) => Add(value, alignment, format);

    private readonly void Add(object? value, int alignment, string? format)
    {
        _format.Append('{').Append(_args.Count.ToString(CultureInfo.InvariantCulture));
        if (alignment != 0)
        {
            _format.Append(',').Append(alignment.ToString(CultureInfo.InvariantCulture));
        }

        if (format is not null)
        {
            _format.Append(':').Append(format);
        }

        _format.Append('}');
        _args.Add(value);
    }

    internal readonly (string Format, object?[] Args) Build() => (_format.ToString(), _args.ToArray());
}
