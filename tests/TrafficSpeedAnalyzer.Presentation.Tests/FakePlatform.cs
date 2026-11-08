using System.Runtime.CompilerServices;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Presentation.Tests;

/// <summary>Cartella dati temporanea per i test: le impostazioni vere dell'utente non vengono toccate.</summary>
internal static class TestDataDirectory
{
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("SPEEDVISION_DATA_DIR", Path.Combine(Path.GetTempPath(), "speedvision-tests-" + Environment.ProcessId));
}

/// <summary>Servizi di sistema finti: registrano le domande e rispondono come indicato dal test.</summary>
internal sealed class FakePlatform : IPlatformServices
{
    public List<string> Questions { get; } = [];

    public DialogAnswer Answer { get; set; } = DialogAnswer.Yes;

    public string? NextFile { get; set; }

    public List<string> OpenedUrls { get; } = [];

    public IUiDispatcher Ui { get; } = new ImmediateDispatcher();

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

    /// <summary>Al posto dell'immagine il fotogramma stesso: il test vede quale fotogramma è mostrato.</summary>
    public object CreateFrameImage(VideoFrame frame) => frame;

    public Task<MapOutcome> ShowMapAsync(MapRequest request) => Task.FromResult(new MapOutcome(null, null, null));

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action)
        {
            lock (this)
            {
                action();
            }

            return Task.CompletedTask;
        }

        public Task YieldAsync() => Task.CompletedTask;
    }
}
