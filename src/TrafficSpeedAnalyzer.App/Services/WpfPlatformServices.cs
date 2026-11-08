using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.App.Services;

/// <summary>Servizi di sistema per i view model nell'app WPF per Windows.</summary>
public sealed class WpfPlatformServices : IPlatformServices
{
    public IUiDispatcher Ui { get; } = new WpfUiDispatcher();

    public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = Filter(filters),
            InitialDirectory = initialDirectory ?? string.Empty,
            CheckFileExists = true,
        };
        return Task.FromResult(dialog.ShowDialog(Owner) == true ? dialog.FileName : null);
    }

    public Task<string?> PickSaveFileAsync(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string defaultExtension, string? initialDirectory = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = Filter(filters),
            FileName = suggestedName,
            InitialDirectory = initialDirectory ?? string.Empty,
        };
        return Task.FromResult(dialog.ShowDialog(Owner) == true ? dialog.FileName : null);
    }

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = initialDirectory ?? string.Empty,
        };
        return Task.FromResult(dialog.ShowDialog(Owner) == true ? dialog.FolderName : null);
    }

    public Task<DialogAnswer> AskAsync(string message, DialogKind kind, bool allowCancel = false)
    {
        MessageBoxResult r = Owner is { } owner
            ? MessageBox.Show(owner, message, AppInfo.Name, allowCancel ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo, Icon(kind))
            : MessageBox.Show(message, AppInfo.Name, allowCancel ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo, Icon(kind));
        return Task.FromResult(r switch
        {
            MessageBoxResult.Yes => DialogAnswer.Yes,
            MessageBoxResult.No => DialogAnswer.No,
            _ => allowCancel ? DialogAnswer.Cancel : DialogAnswer.No,
        });
    }

    public bool TryCopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false; // appunti occupati da un'altra applicazione
        }
    }

    public bool TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            AppLog.Error($"Impossibile aprire {url}", e);
            return false;
        }
    }

    public void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    public object CreateFrameImage(VideoFrame frame)
    {
        BitmapSource bmp = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null, frame.Pixels, frame.Stride);
        bmp.Freeze();
        return bmp;
    }

    public Task<MapOutcome> ShowMapAsync(MapRequest request)
    {
        var window = new MapCalibrationWindow(request) { Owner = Owner };
        window.ShowDialog();
        return Task.FromResult(new MapOutcome(window.Result, window.LastView, window.Error));
    }

    private static Window? Owner => Application.Current?.MainWindow;

    private static MessageBoxImage Icon(DialogKind kind) => kind == DialogKind.Warning ? MessageBoxImage.Warning : MessageBoxImage.Question;

    /// <summary>Filtri nel formato di Windows: «Video|*.mp4;*.mov|Tutti i file|*.*».</summary>
    private static string Filter(IReadOnlyList<FileFilter> filters) =>
        string.Join("|", filters.Select(f => f.Name + "|" + string.Join(";", f.Patterns)));

    private sealed class WpfUiDispatcher : IUiDispatcher
    {
        private static Dispatcher Dispatcher => Application.Current.Dispatcher;

        public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

        public Task YieldAsync() => Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;
    }
}
