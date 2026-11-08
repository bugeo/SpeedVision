using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TrafficSpeedAnalyzer.App;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Errore non gestito", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Errore in un'operazione in background", args.Exception);
            args.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += OnUiException;

        AppLog.Info("Avvio " + AppInfo.Name + " " + typeof(App).Assembly.GetName().Version + " (Avalonia)");

        // Lingua: quella scelta nell'app, altrimenti quella del sistema (italiano se non disponibile).
        Loc.SetLanguage(AppSettings.Load().Language ?? Loc.SystemLanguage());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            // Video passato da riga di comando o trascinato sull'icona del programma.
            if (desktop.Args is { Length: > 0 } args)
            {
                window.OpenOnStartup(args[0]);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Errore non gestito nell'interfaccia", e.Exception);
        e.Handled = true;
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow w })
        {
            _ = w.ShowErrorAsync(
                Loc.T("Si è verificato un errore imprevisto:\n\n") + e.Exception.Message +
                Loc.T("\n\nL'applicazione prova a continuare. I dettagli sono nel registro:\n") + AppLog.LogDirectory);
        }
    }
}
