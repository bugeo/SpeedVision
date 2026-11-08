using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using TrafficSpeedAnalyzer.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace TrafficSpeedAnalyzer.Desktop.Tests;

/// <summary>L'app vera (stili, icone, palette) con la piattaforma senza schermo e il disegno Skia, per le schermate.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Cartella dati temporanea per i test: le impostazioni vere dell'utente non vengono toccate.</summary>
internal static class TestDataDirectory
{
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("SPEEDVISION_DATA_DIR", Path.Combine(Path.GetTempPath(), "speedvision-desktop-tests-" + Environment.ProcessId));
}
