using System.Windows;

namespace TrafficSpeedAnalyzer.App;

/// <summary>Finestra «Impostazioni»: lingua e registro. Lavora sullo stesso view model della finestra principale.</summary>
public partial class SettingsWindow
{
    public SettingsWindow() => InitializeComponent();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
