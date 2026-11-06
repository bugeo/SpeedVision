using System.Text.RegularExpressions;
using TrafficSpeedAnalyzer.Core.Export;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Core.Tests;

/// <summary>La lingua è globale: questi test non girano in parallelo con gli altri.</summary>
[CollectionDefinition("Lingua", DisableParallelization = true)]
public sealed class LanguageCollection;

[Collection("Lingua")]
public partial class LocalizationTests
{
    [GeneratedRegex(@"(?<!\{)\{\d+[^{}]*\}")]
    private static partial Regex Placeholder();

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    public void Translations_KeepThePlaceholders(string code)
    {
        IReadOnlyDictionary<string, string> table = Loc.Table(code);
        Assert.True(table.Count > 400, $"{code}: {table.Count} testi");
        foreach ((string key, string value) in table)
        {
            Assert.Equal(
                Placeholder().Matches(key).Select(m => m.Value).Order(),
                Placeholder().Matches(value).Select(m => m.Value).Order());
            object[] args = Enumerable.Range(0, 10).Select(i => (object)(i + 0.5)).ToArray();
            _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, value, args);   // nessun errore di formato
        }
    }

    [Fact]
    public void Texts_FollowTheLanguage_AndFallBackToItalian()
    {
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal("Next >>", Loc.T("Procedi >>"));
            Assert.Equal("Frame 1,235 of 2,000", Loc.S($"Fotogramma {1235:N0} di {2000:N0}"));
            Assert.Equal("Testo senza traduzione", Loc.T("Testo senza traduzione"));
            Assert.Equal(',', CsvExporter.Separator);

            Loc.SetLanguage("de");
            Assert.Equal("Spur 2", Loc.S($"Corsia {2}"));
            Assert.Equal(';', CsvExporter.Separator);

            Loc.SetLanguage("xx");   // sconosciuta: italiano
            Assert.Equal("it", Loc.Language);
            Assert.Equal("Distanza tra le linee: 12,5 m", Loc.S($"Distanza tra le linee: {12.5:0.###} m"));
        }
        finally
        {
            Loc.SetLanguage("it");
        }
    }

    [Fact]
    public void InterpolatedKey_HasNumberedPlaceholders()
    {
        double v = 3.14159;
        // La chiave è «Velocità {0:0.0} su {1}»: senza traduzione resta l'italiano formattato.
        Assert.Equal("Velocità 3,1 su A{x}", Loc.S($"Velocità {v:0.0} su {"A"}{{x}}"));
    }
}
