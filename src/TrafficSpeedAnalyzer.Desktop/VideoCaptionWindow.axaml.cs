using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TrafficSpeedAnalyzer.Desktop;

/// <summary>Finestra «Genera video»: indirizzo e data da scrivere in basso nel video. Restituisce true se si procede.</summary>
public partial class VideoCaptionWindow : Window
{
    public VideoCaptionWindow()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("AddressBox")?.Focus();
    }

    private void OnGenerateClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
