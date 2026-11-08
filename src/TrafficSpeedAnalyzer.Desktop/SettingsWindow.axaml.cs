using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace TrafficSpeedAnalyzer.Desktop;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
