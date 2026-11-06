using System.Windows;
using System.Windows.Threading;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Errore non gestito", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Errore in un'operazione in background", args.Exception);
            args.SetObserved();
        };

        AppLog.Info("Avvio " + AppInfo.Name + " " + typeof(App).Assembly.GetName().Version);

        // Colore primario verde al posto del colore di sistema di Windows (blu).
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x1F, 0xA8, 0x5F), Wpf.Ui.Appearance.ApplicationTheme.Dark);

        // Lingua: quella scelta nell'app, altrimenti quella di Windows (italiano se non disponibile).
        Loc.SetLanguage(AppSettings.Load().Language ?? Loc.SystemLanguage());

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Apertura diretta di un video passato da riga di comando o trascinato sull'eseguibile.
        if (e.Args.Length > 0)
        {
            window.OpenOnStartup(e.Args[0]);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Errore non gestito nell'interfaccia", e.Exception);
        MessageBox.Show(
            Loc.T("Si è verificato un errore imprevisto:\n\n") + e.Exception.Message +
            Loc.T("\n\nL'applicazione prova a continuare. I dettagli sono nel registro:\n") + AppLog.LogDirectory,
            AppInfo.Name,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
