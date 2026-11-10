using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.App.ViewModels;
using TrafficSpeedAnalyzer.Desktop.Controls;

namespace TrafficSpeedAnalyzer.Desktop.Tests;

/// <summary>Apre la finestra vera senza schermo e salva una schermata per ogni stato importante.</summary>
public class ScreenshotTests
{
    internal static string ShotsDir => Path.Combine(AppContext.BaseDirectory, "screenshots");

    internal static void Save(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(ShotsDir);
        using WriteableBitmap? frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(ShotsDir, name + ".png"));
    }

    internal static async Task WaitUntil(Func<bool> condition, string what, double seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(seconds), "Tempo scaduto: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task EmptyWindow_ShowsTheDropZone()
    {
        var platform = new TestPlatform();
        var w = new MainWindow(platform) { Width = 1480, Height = 920 };
        w.Show();
        await WaitUntil(() => w.ViewModel.FfmpegChecked, "ricerca di FFmpeg");
        Save(w, "01-vuota");
        Assert.True(w.ViewModel.ShowEmptyState || w.ViewModel.IsFfmpegMissing);
    }

    [AvaloniaFact]
    public async Task Settings_ChangeTheLanguageAtOnce()
    {
        var platform = new TestPlatform();
        var w = new MainWindow(platform) { Width = 1480, Height = 920 };
        w.Show();
        await WaitUntil(() => w.ViewModel.FfmpegChecked, "ricerca di FFmpeg");
        UiButton open = w.GetVisualDescendants().OfType<UiButton>().Single(b => b.Name == "OpenVideoButton");
        Assert.Equal("Apri video", open.Content);

        var settings = new SettingsWindow { DataContext = w.ViewModel };
        settings.Show(w);
        Dispatcher.UIThread.RunJobs();
        Save(settings, "10-impostazioni");

        // Modello e opzioni dell'analisi sono nelle impostazioni; il modello di partenza è YOLOX-S.
        ComboBox model = settings.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ModelBox");
        Assert.Equal("yolox_s", w.ViewModel.SelectedModel.Id);
        Assert.Same(w.ViewModel.SelectedModel, model.SelectedItem);

        // Fotogrammi minimi con la ruota per lato: 2 di partenza, modificabili dalla casella.
        NumericUpDown frames = settings.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "WheelFramesBox");
        Assert.Equal(2, w.ViewModel.WheelFramesPerSide);
        Assert.Equal(2m, frames.Value);
        frames.Value = 3;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, w.ViewModel.WheelFramesPerSide);
        frames.Value = 2;
        Dispatcher.UIThread.RunJobs();

        // Inglese: i testi cambiano subito, senza riaprire le finestre.
        w.ViewModel.SelectedLanguage = w.ViewModel.Languages.Single(l => l.Code == "en");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Open video", open.Content);
        Save(w, "11-inglese");
        settings.Close();
        w.ViewModel.SelectedLanguage = w.ViewModel.Languages.Single(l => l.Code == "it");
    }

    [AvaloniaFact]
    public async Task Question_ShowsYesAndNo_AndReturnsTheAnswer()
    {
        var w = new MainWindow(new TestPlatform()) { Width = 1000, Height = 700 };
        w.Show();
        Task<DialogAnswer> answer = MessageDialog.AskAsync(w, "Ci sono misure non esportate che andranno perse. Chiudere comunque?", DialogKind.Warning, allowCancel: false);
        Dispatcher.UIThread.RunJobs();
        MessageDialog dialog = Assert.Single(w.OwnedWindows.OfType<MessageDialog>());
        Save(dialog, "12-domanda");
        UiButton no = dialog.GetVisualDescendants().OfType<UiButton>().Single(b => (b.Content as string) == "No");
        no.Command?.Execute(null);
        no.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(DialogAnswer.No, await answer);
    }
}
