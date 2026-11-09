using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Desktop.Controls;
using TrafficSpeedAnalyzer.Core.Localization;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Desktop.Services;

/// <summary>Servizi di sistema per i view model nell'interfaccia Avalonia (Windows, Mac, Linux).</summary>
public sealed class AvaloniaPlatformServices : IPlatformServices
{
    public IUiDispatcher Ui { get; } = new AvaloniaUiDispatcher();

    /// <summary>Finestra a cui appoggiare i dialoghi; se manca si usa la finestra principale.</summary>
    public Window? Owner { get; set; }

    private Window? ActiveOwner =>
        Owner ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters, string? initialDirectory = null)
    {
        if (ActiveOwner?.StorageProvider is not { } sp)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = Types(filters),
            SuggestedStartLocation = await Folder(sp, initialDirectory),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string defaultExtension, string? initialDirectory = null)
    {
        if (ActiveOwner?.StorageProvider is not { } sp)
        {
            return null;
        }

        IStorageFile? file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = defaultExtension.TrimStart('.'),
            FileTypeChoices = Types(filters),
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await Folder(sp, initialDirectory),
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        if (ActiveOwner?.StorageProvider is not { } sp)
        {
            return null;
        }

        IReadOnlyList<IStorageFolder> folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await Folder(sp, initialDirectory),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<DialogAnswer> AskAsync(string message, DialogKind kind, bool allowCancel = false)
    {
        if (ActiveOwner is not { } owner)
        {
            return allowCancel ? DialogAnswer.Cancel : DialogAnswer.No;
        }

        return await MessageDialog.AskAsync(owner, message, kind, allowCancel);
    }

    public bool TryCopyText(string text)
    {
        try
        {
            if (ActiveOwner?.Clipboard is not { } clipboard)
            {
                return false;
            }

            _ = clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception e) when (e is ExternalException or InvalidOperationException)
        {
            return false;
        }
    }

    public bool TryOpenUrl(string url)
    {
        try
        {
            Process.Start(ShellOpen(url));
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            AppLog.Error($"Impossibile aprire {url}", e);
            return false;
        }
    }

    public void OpenFolder(string path)
    {
        try
        {
            Process.Start(ShellOpen(path));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            AppLog.Error($"Impossibile aprire la cartella {path}", e);
        }
    }

    /// <summary>
    /// Fotogramma come immagine Avalonia. Il formato di FFmpeg è BGR a 24 bit; si copia in BGRA a 32 bit,
    /// il formato nativo di Skia, così il disegno non deve convertire a ogni ridisegno.
    /// </summary>
    public object CreateFrameImage(VideoFrame frame)
    {
        int w = frame.Width, h = frame.Height;
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);
        using ILockedFramebuffer fb = bmp.Lock();
        byte[] row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            int src = y * frame.Stride;
            for (int x = 0, d = 0; x < w; x++, d += 4, src += 3)
            {
                row[d] = frame.Pixels[src];
                row[d + 1] = frame.Pixels[src + 1];
                row[d + 2] = frame.Pixels[src + 2];
                row[d + 3] = 255;
            }

            Marshal.Copy(row, 0, fb.Address + (y * fb.RowBytes), row.Length);
        }

        return bmp;
    }

    public async Task<MapOutcome> ShowMapAsync(MapRequest request)
    {
        if (ActiveOwner is not { } owner)
        {
            return new MapOutcome(null, null, null);
        }

        var window = new MapWindow(request);
        await window.ShowDialog(owner);
        return new MapOutcome(window.Result, window.LastView, window.Error);
    }

    /// <summary>Apertura con il programma predefinito del sistema: Esplora file/browser, «open» su Mac, «xdg-open» su Linux.</summary>
    private static ProcessStartInfo ShellOpen(string target)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new ProcessStartInfo("open") { ArgumentList = { target }, UseShellExecute = false };
        }

        if (OperatingSystem.IsLinux())
        {
            return new ProcessStartInfo("xdg-open") { ArgumentList = { target }, UseShellExecute = false };
        }

        return new ProcessStartInfo(target) { UseShellExecute = true };
    }

    private static List<FilePickerFileType> Types(IReadOnlyList<FileFilter> filters) =>
        [.. filters.Select(f => new FilePickerFileType(f.Name) { Patterns = [.. f.Patterns] })];

    private static async Task<IStorageFolder?> Folder(IStorageProvider sp, string? path) =>
        string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? null : await sp.TryGetFolderFromPathAsync(path);

    private sealed class AvaloniaUiDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

        public Task YieldAsync() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();
    }
}
