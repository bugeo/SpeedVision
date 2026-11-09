using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Localization;
using TrafficSpeedAnalyzer.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// Un test alla volta: la lingua è globale e le attese dei test condividono il thread dell'interfaccia.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TrafficSpeedAnalyzer.Desktop.Tests;

/// <summary>L'app vera (stili, icone, palette) con la piattaforma senza schermo e il disegno Skia, per le schermate.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            // I test controllano i testi italiani: sulle macchine di GitHub il sistema è in inglese.
            .AfterSetup(_ => Loc.SetLanguage("it"));
}

/// <summary>Cartella dati temporanea per i test: le impostazioni vere dell'utente non vengono toccate.</summary>
internal static class TestDataDirectory
{
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("SPEEDVISION_DATA_DIR", Path.Combine(Path.GetTempPath(), "speedvision-desktop-tests-" + Environment.ProcessId));

        // Lingua italiana, qualunque sia quella del sistema: i test controllano i testi italiani.
        AppSettings settings = AppSettings.Load();
        settings.Language = "it";
        settings.Save();
    }
}
