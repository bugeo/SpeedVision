using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Desktop.Services;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Desktop.Tests;

/// <summary>
/// Servizi di sistema per i test: immagini e dispatcher veri di Avalonia, dialoghi finti che registrano
/// le domande e rispondono come indicato dal test.
/// </summary>
internal sealed class TestPlatform : IPlatformServices
{
    private readonly AvaloniaPlatformServices _real = new();

    public List<string> Questions { get; } = [];

    public DialogAnswer Answer { get; set; } = DialogAnswer.Yes;

    public string? NextFile { get; set; }

    public List<string> OpenedUrls { get; } = [];

    public IUiDispatcher Ui => _real.Ui;

    public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters, string? initialDirectory = null) => Task.FromResult(NextFile);

    public Task<string?> PickSaveFileAsync(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string defaultExtension, string? initialDirectory = null) => Task.FromResult(NextFile);

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult(NextFile);

    public Task<DialogAnswer> AskAsync(string message, DialogKind kind, bool allowCancel = false)
    {
        Questions.Add(message);
        return Task.FromResult(Answer);
    }

    public bool TryCopyText(string text) => true;

    public bool TryOpenUrl(string url)
    {
        OpenedUrls.Add(url);
        return true;
    }

    public void OpenFolder(string path)
    {
    }

    public object CreateFrameImage(VideoFrame frame) => _real.CreateFrameImage(frame);

    public Task<MapOutcome> ShowMapAsync(MapRequest request) => _real.ShowMapAsync(request);
}
