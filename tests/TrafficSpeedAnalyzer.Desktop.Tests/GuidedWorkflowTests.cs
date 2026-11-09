using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrafficSpeedAnalyzer.App.ViewModels;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Desktop.Controls;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Desktop.Tests;

/// <summary>
/// Il percorso completo dell'utente nella finestra vera, senza schermo: si apre il video a velocità note
/// (due auto a 50 e 72 km/h), si segue la guida premendo ogni volta l'unico pulsante verde, si disegnano le
/// linee A e B con il mouse sul visore, si scrive la distanza e si controllano le velocità nei risultati.
/// A ogni passo si salva una schermata.
/// </summary>
public class GuidedWorkflowTests
{
    private static string VideoPath => Path.Combine(AppContext.BaseDirectory, "Assets", "known_speed.mp4");

    /// <summary>Pulsanti verdi (l'azione da fare ora) visibili e attivi nella finestra.</summary>
    private static List<UiButton> PrimaryButtons(Window w) =>
    [
        .. w.GetVisualDescendants().OfType<UiButton>()
            .Where(b => b.Classes.Contains("primary") && b.IsEffectivelyVisible && b.IsEffectivelyEnabled),
    ];

    /// <summary>Preme l'unico pulsante verde, come farebbe l'utente; controlla che sia uno solo e che sia quello atteso.</summary>
    private static void ClickTheGreenButton(Window w, string expectedText)
    {
        Dispatcher.UIThread.RunJobs();
        List<UiButton> green = PrimaryButtons(w);
        UiButton b = Assert.Single(green);
        Assert.Equal(expectedText, Text(b));
        Assert.True(b.Command?.CanExecute(b.CommandParameter) ?? false, "il pulsante verde non è utilizzabile: " + expectedText);
        b.Command!.Execute(b.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static string Text(UiButton b) => b.Content switch
    {
        string s => s,
        TextBlock t => t.Text ?? string.Empty,
        _ => b.Content?.ToString() ?? string.Empty,
    };

    /// <summary>Clic sinistro nel visore nel punto del video indicato (pixel del video).</summary>
    private static void ClickVideo(Window w, VideoViewport v, Point2 videoPoint)
    {
        Point inViewport = new Point(videoPoint.X, videoPoint.Y).Transform(v.VideoToScreen);
        Point inWindow = v.TranslatePoint(inViewport, w)!.Value;
        w.MouseMove(inWindow);
        w.MouseDown(inWindow, MouseButton.Left);
        w.MouseUp(inWindow, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Trascina con il tasto sinistro da un punto all'altro del video.</summary>
    private static void DragVideo(Window w, VideoViewport v, Point2 from, Point2 to)
    {
        Point Screen(Point2 p) => v.TranslatePoint(new Point(p.X, p.Y).Transform(v.VideoToScreen), w)!.Value;
        w.MouseMove(Screen(from));
        w.MouseDown(Screen(from), MouseButton.Left);
        for (int i = 1; i <= 5; i++)
        {
            w.MouseMove(Screen(Point2.Lerp(from, to, i / 5.0)), RawInputModifiers.LeftMouseButton);
        }

        w.MouseUp(Screen(to), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact(Timeout = 600_000)]
    public async Task FollowingTheGuide_MeasuresTheKnownSpeeds()
    {
        FfmpegTools? tools = await FfmpegLocator.LocateAsync();
        Assert.SkipWhen(tools is null, "FFmpeg non trovato: test saltato.");

        var platform = new TestPlatform { NextFile = VideoPath };
        var w = new MainWindow(platform) { Width = 1480, Height = 920 };
        w.Show();
        MainViewModel vm = w.ViewModel;
        await ScreenshotTests.WaitUntil(() => vm.IsReady, "FFmpeg pronto");

        // Passo 1: video. «Apri video» è l'unico pulsante verde.
        ClickTheGreenButton(w, "Apri video");
        await ScreenshotTests.WaitUntil(() => vm.HasVideo && !vm.IsBusy && vm.CurrentFrame is not null, "video aperto", 60);
        ScreenshotTests.Save(w, "02-video-aperto");

        // Stabilizzazione: domanda sì/no, i due pulsanti della risposta sono verdi insieme.
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Sì, stabilizza", "No, non serve"], PrimaryButtons(w).Select(Text).ToArray());
        PrimaryButtons(w).Single(b => Text(b) == "No, non serve").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        ScreenshotTests.Save(w, "03-inizio-registrazione");

        // Data e ora lette dal video: la guida propone di confermarle (senza data: «Prosegui senza data»).
        ClickTheGreenButton(w, vm.HasClockTime ? "Conferma la data" : "Prosegui senza data");
        await ScreenshotTests.WaitUntil(() => vm.SelectedStep == WorkflowStep.Road, "passo Strada");

        // Passo 2: strada. Ripresa dall'alto: la strada è tutta l'inquadratura. Si trascinano i quattro
        // pallini negli angoli, con il mouse sul visore.
        ScreenshotTests.Save(w, "04-strada");
        VideoViewport viewport = w.GetVisualDescendants().OfType<VideoViewport>().Single();
        // Finché la zona verde è dove l'ha proposta il programma, «I bordi sono a posto» resta spento.
        Assert.Empty(PrimaryButtons(w));
        Assert.False(vm.ConfirmRoadEdgesCommand.CanExecute(null));
        Point2[] targets = [new(4, 4), new(764, 4), new(764, 428), new(4, 428)];
        for (int i = 0; i < 4; i++)
        {
            DragVideo(w, viewport, vm.Road!.Corners[i], targets[i]);
            Assert.True(Point2.Distance(vm.Road!.Corners[i], targets[i]) < 2, $"vertice {i} non spostato: {vm.Road.Corners[i]}");
        }

        ScreenshotTests.Save(w, "04b-strada-bordi");
        ClickTheGreenButton(w, "I bordi sono a posto");
        Assert.Equal(4, PrimaryButtons(w).Count); // i quattro numeri di corsia: una sola domanda
        vm.SetLaneCountCommand.Execute("1");
        Dispatcher.UIThread.RunJobs();
        await ScreenshotTests.WaitUntil(() => vm.SelectedStep == WorkflowStep.Analysis, "passo Analisi");
        ScreenshotTests.Save(w, "05-analisi");

        // Passo 3: analisi con il modello vero (scaricato la prima volta).
        if (!vm.IsModelAvailable)
        {
            await vm.DownloadModelCommand.ExecuteAsync(null);
        }

        ClickTheGreenButton(w, "Analizza tutto");
        await ScreenshotTests.WaitUntil(() => vm.IsAnalyzing, "analisi avviata");
        await ScreenshotTests.WaitUntil(() => !vm.IsAnalyzing && vm.HasCompletedAnalysis, "analisi completata", 500);
        ScreenshotTests.Save(w, "06-analisi-completata");
        ClickTheGreenButton(w, "Procedi >>");

        // Passo 4: calibrazione con due riferimenti misurati sul campo.
        Assert.Equal(WorkflowStep.Calibration, vm.SelectedStep);
        ScreenshotTests.Save(w, "07-calibrazione");
        vm.IsDistanceKnownMode = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ViewportMode.DrawLineA, vm.ViewportMode);

        // Linee orizzontali a y = 180 (A) e y = 380 (B), disegnate con due clic ciascuna sul visore.
        ClickVideo(w, viewport, new Point2(2, 180));
        ClickVideo(w, viewport, new Point2(766, 180));
        Assert.NotNull(vm.LineA);
        ClickVideo(w, viewport, new Point2(2, 380));
        ClickVideo(w, viewport, new Point2(766, 380));
        Assert.NotNull(vm.LineB);
        Assert.Equal(180, vm.LineA!.Value.Start.Y, 1.0);
        Assert.Equal(380, vm.LineB!.Value.End.Y, 1.0);

        // Il cursore va da solo nella casella della distanza: si scrive «20» e Invio = «Procedi».
        await ScreenshotTests.WaitUntil(() => w.FocusManager?.GetFocusedElement() is TextBox { Name: "DistanceBox" }, "cursore nella distanza");
        w.KeyTextInput("20");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("20", vm.DistanceText);
        Assert.True(vm.IsCalibrationValid);
        ScreenshotTests.Save(w, "08-calibrazione-pronta");
        Assert.Equal("Procedi >>", Text(Assert.Single(PrimaryButtons(w))));
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await ScreenshotTests.WaitUntil(() => vm.Passages.Count + vm.HiddenPassageCount >= 2, "velocità calcolate", 30);

        // Passo 5: risultati. Ripresa dall'alto: le ruote non si vedono, quindi con l'interruttore «solo veicoli con
        // la ruota seguita bene» (acceso all'inizio) le due auto non sono mostrate e un avviso lo dice. Spento
        // l'interruttore, compaiono le misure sullo spigolo del riquadro.
        Assert.Equal(WorkflowStep.Results, vm.SelectedStep);
        Assert.True(vm.OnlyWellTrackedWheels);
        Assert.Equal(2, vm.HiddenPassageCount);
        Assert.Empty(vm.Passages);
        Assert.True(vm.ShowWheelFilter);
        ScreenshotTests.Save(w, "09-risultati-solo-ruote");
        vm.OnlyWellTrackedWheels = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, vm.HiddenPassageCount);
        ScreenshotTests.Save(w, "09-risultati");
        double[] speeds = [.. vm.Passages.Select(p => p.Passage.Speed.SpeedKmh).Order()];
        Assert.Equal(2, speeds.Length);
        Assert.InRange(speeds[0], 50 * 0.96, 50 * 1.04);
        Assert.InRange(speeds[1], 72 * 0.96, 72 * 1.04);
        Assert.Equal(2, vm.Passages.Count(p => p.IsWheelReference || p.IsAutomatic));
    }
}
