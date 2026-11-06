using System.Diagnostics;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Localization;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>Collegamenti esterni: sito del progetto e video di esempio (si aprono nel browser).</summary>
public partial class MainViewModel
{
    [RelayCommand]
    private void OpenWebsite() => OpenInBrowser(AppInfo.WebsiteUrl);

    [RelayCommand]
    private void OpenSamples() => OpenInBrowser(AppInfo.SamplesUrl);

    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            AppLog.Error($"Impossibile aprire {url}", e);
            ShowStatus(Loc.T("Impossibile aprire il browser"), url, InfoBarSeverity.Warning);
        }
    }
}
