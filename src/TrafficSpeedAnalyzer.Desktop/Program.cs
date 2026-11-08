using Avalonia;

namespace TrafficSpeedAnalyzer.Desktop;

internal static class Program
{
    /// <summary>Avvio: un eventuale argomento è il video da aprire (per esempio trascinato sull'icona).</summary>
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Usato anche dall'anteprima dello XAML e dai test.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
