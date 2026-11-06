using System.Text;
using TrafficSpeedAnalyzer.Core.Calibration;

namespace TrafficSpeedAnalyzer.Core.Tests;

/// <summary>
/// Elenco delle auto dal file esterno veicoli_riferimento.csv (separatore «;»). Questi test usano solo
/// la lettura (<see cref="ReferenceVehicleCatalog.Parse"/>, <see cref="ReferenceVehicleCatalog.Read"/>):
/// non cambiano l'elenco in uso, condiviso con gli altri test.
/// </summary>
public class ReferenceVehicleCatalogTests
{
    private const string Header = "Marca;Modello;Generazione;Dal anno;Al anno;Lunghezza mm;Passo mm;Tipo";

    [Fact]
    public void ShippedFile_IsNextToTheProgram_AndValid()
    {
        Assert.True(File.Exists(ReferenceVehicleCatalog.DefaultPath), ReferenceVehicleCatalog.DefaultPath);
        ReferenceVehicleList list = ReferenceVehicleCatalog.Read(ReferenceVehicleCatalog.DefaultPath);
        Assert.Empty(list.Problems);
        Assert.Equal(214, list.Vehicles.Count);
        Assert.Equal(list.Vehicles.Count, ReferenceVehicleCatalog.All.Count);
        Assert.Contains(list.Vehicles, v => v.Make == "Citroën");   // accenti letti correttamente (UTF-8 con BOM)
        ReferenceVehicle panda = Assert.Single(list.Vehicles, v => v.Model == "Grande Panda");
        Assert.Equal((2025, 0, 3999, 2540, "auto"), (panda.FromYear, panda.ToYear, panda.LengthMm, panda.WheelbaseMm, panda.Type));
    }

    [Fact]
    public void Parse_AcceptsWhatExcelWrites()
    {
        string csv = string.Join("\r\n",
            "# Elenco di prova",
            "Tipo;Lunghezza mm;Marca;Modello;Al anno;Dal anno",          // colonne in altro ordine, senza Generazione e Passo
            "suv;4.050;Fiat;\"500X; Cross\";;2015",                       // migliaia con il punto, «;» tra virgolette, ancora in produzione
            "",
            ";3 990;Renault;\"Clio \"\"V\"\"\";2026;2019",                // tipo vuoto, migliaia con lo spazio, virgolette raddoppiate
            "");
        ReferenceVehicleList list = ReferenceVehicleCatalog.Parse(csv, "prova.csv");
        Assert.Empty(list.Problems);
        Assert.Equal(2, list.Vehicles.Count);
        ReferenceVehicle a = list.Vehicles[0], b = list.Vehicles[1];
        Assert.Equal(("Fiat", "500X; Cross", 4050, 2015, 0, "suv", 0), (a.Make, a.Model, a.LengthMm, a.FromYear, a.ToYear, a.Type, a.WheelbaseMm));
        Assert.Equal(("Clio \"V\"", 3990, "auto", 2019, 2026), (b.Model, b.LengthMm, b.Type, b.FromYear, b.ToYear));
    }

    [Fact]
    public void Parse_EnglishHeaders_AreAccepted()
    {
        ReferenceVehicleList list = ReferenceVehicleCatalog.Parse("Make;Model;Length mm\nVW;Golf;4284\n", "en.csv");
        Assert.Equal("Golf", Assert.Single(list.Vehicles).Model);
    }

    [Fact]
    public void Parse_SkipsInvalidRows_AndSaysWhichLine()
    {
        string csv = string.Join("\n",
            Header,
            "Fiat;Panda;;2012;2024;3653;2300;auto",     // riga 2: valida
            ";Senza marca;;;;4000;;auto",               // riga 3
            "Fiat;Panda;;;;3,65;;auto",                 // riga 4: metri invece di millimetri
            "Fiat;Panda;;;;365;;auto",                  // riga 5: troppo corta
            "Fiat;Panda;;2024;2012;3653;;auto",         // riga 6: anni invertiti
            "Fiat;Panda;;;;3653;3700;auto",             // riga 7: passo più lungo dell'auto
            "Fiat;Tipo;;2016;;4532;2638;auto");         // riga 8: valida
        ReferenceVehicleList list = ReferenceVehicleCatalog.Parse(csv, "prova.csv");
        Assert.Equal(["Panda", "Tipo"], list.Vehicles.Select(v => v.Model));
        Assert.Equal(5, list.Problems.Count);
        Assert.Equal([3, 4, 5, 6, 7], list.Problems.Select(p => int.Parse(p.Split(':')[0]["Riga ".Length..])));
        Assert.Contains("3,65", list.Problems[1]);
    }

    [Fact]
    public void Parse_WrongSeparatorOrMissingColumns_GiveAClearMessage()
    {
        ReferenceVehicleList comma = ReferenceVehicleCatalog.Parse("Marca,Modello,Lunghezza mm\nFiat,Panda,3653\n", "virgole.csv");
        Assert.Empty(comma.Vehicles);
        Assert.Contains("punto e virgola", Assert.Single(comma.Problems));

        ReferenceVehicleList missing = ReferenceVehicleCatalog.Parse("Marca;Modello\nFiat;Panda\n", "corto.csv");
        Assert.Empty(missing.Vehicles);
        Assert.Contains("Lunghezza mm", Assert.Single(missing.Problems));

        Assert.NotEmpty(ReferenceVehicleCatalog.Parse("\r\n\r\n", "vuoto.csv").Problems);
    }

    [Fact]
    public void Read_MissingFile_IsReported_NotThrown()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        ReferenceVehicleList list = ReferenceVehicleCatalog.Read(path);
        Assert.Empty(list.Vehicles);
        Assert.Contains(path, Assert.Single(list.Problems));
    }

    [Fact]
    public void DecodeText_ReadsUtf8AndExcelWindows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "Škoda;Citroën;€";
        Assert.Equal(text, ReferenceVehicleCatalog.DecodeText(Encoding.GetEncoding(1252).GetBytes(text)));
        Assert.Equal(text, ReferenceVehicleCatalog.DecodeText(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray()));
        Assert.Equal(text, ReferenceVehicleCatalog.DecodeText(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>Messaggi sull'elenco delle auto nella lingua scelta (la lingua è globale: niente parallelo).</summary>
[Collection("Lingua")]
public class ReferenceVehicleCatalogLanguageTests
{
    [Fact]
    public void Problems_AreTranslated()
    {
        try
        {
            Localization.Loc.SetLanguage("en");
            ReferenceVehicleList list = ReferenceVehicleCatalog.Parse("Marca;Modello;Lunghezza mm\nFiat;Panda;365\n", "x.csv");
            Assert.Equal("Line 2: invalid length («365»): millimetres between 2000 and 20000 are required.", Assert.Single(list.Problems));
        }
        finally
        {
            Localization.Loc.SetLanguage("it");
        }
    }
}
