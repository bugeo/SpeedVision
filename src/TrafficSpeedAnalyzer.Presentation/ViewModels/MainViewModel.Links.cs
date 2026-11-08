using CommunityToolkit.Mvvm.Input;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>Collegamenti esterni: sito del progetto, video di esempio e pagina per sostenere lo sviluppo (si aprono nel browser).</summary>
public partial class MainViewModel
{
    [RelayCommand]
    private void OpenWebsite() => OpenInBrowser(AppInfo.WebsiteUrl);

    [RelayCommand]
    private void OpenSamples() => OpenInBrowser(AppInfo.SamplesUrl);

    [RelayCommand]
    private void OpenSupport() => OpenInBrowser(AppInfo.SupportUrl);

    private void OpenInBrowser(string url)
    {
        if (!_platform.TryOpenUrl(url))
        {
            AppLog.Warn($"Impossibile aprire {url}");
            ShowStatus(Loc.T("Impossibile aprire il browser"), url, StatusSeverity.Warning);
        }
    }
}
