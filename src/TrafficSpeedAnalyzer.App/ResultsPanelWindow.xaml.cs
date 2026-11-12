using System.Windows;
using TrafficSpeedAnalyzer.App.ViewModels;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App;

/// <summary>
/// Finestra aperta dai pulsanti in fondo ai risultati: filtro, limite di velocità o statistiche. Lavora sullo stesso view
/// model della finestra principale; alla chiusura il riquadro torna chiuso.
/// </summary>
public partial class ResultsPanelWindow
{
    public ResultsPanelWindow(MainViewModel vm, ResultsPanel panel)
    {
        InitializeComponent();
        DataContext = vm;
        vm.OpenResultsPanel = panel;
        Title = panel switch
        {
            ResultsPanel.Filter => Loc.T("Filtro"),
            ResultsPanel.Limit => Loc.T("Limite di velocità"),
            _ => Loc.T("Statistiche"),
        };
        WindowTitle.Title = Title;
        Closed += (_, _) => vm.OpenResultsPanel = ResultsPanel.None;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
