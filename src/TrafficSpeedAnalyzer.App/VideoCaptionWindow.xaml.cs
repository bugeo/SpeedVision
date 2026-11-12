using System.Windows;

namespace TrafficSpeedAnalyzer.App;

/// <summary>Finestra «Genera video»: indirizzo e data da scrivere in basso nel video. DialogResult true se si procede.</summary>
public partial class VideoCaptionWindow
{
    public VideoCaptionWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => AddressBox.Focus();
    }

    private void OnGenerateClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
