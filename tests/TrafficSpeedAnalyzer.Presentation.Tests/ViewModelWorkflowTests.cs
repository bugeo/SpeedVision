using System.Diagnostics;
using TrafficSpeedAnalyzer.App;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.App.ViewModels;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Presentation.Tests;

/// <summary>
/// Il view model senza interfaccia: si apre un video generato con FFmpeg (un riquadro bianco che avanza di
/// 8 pixel per fotogramma), si calibra, si misura a mano e si controlla la velocità. È lo stesso percorso
/// dell'utente, con i servizi di sistema finti: la logica non dipende da WPF.
/// </summary>
public sealed class ViewModelWorkflowTests : IAsyncLifetime
{
    private const int Width = 640, Height = 360, Box = 40, PixelsPerFrame = 8;

    // Linee A e B verticali a 240 pixel di distanza, che valgono 24 m: 10 pixel al metro.
    // 8 pixel per fotogramma a 30 fps = 240 pixel/s = 24 m/s = 86,4 km/h.
    private const double ExpectedKmh = 86.4;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsa-vm-tests-" + Guid.NewGuid().ToString("N"));
    private FfmpegTools? _tools;

    private string VideoPath => Path.Combine(_dir, "riquadro.mp4");

    public async ValueTask InitializeAsync()
    {
        _tools = await FfmpegLocator.LocateAsync();
        if (_tools is null)
        {
            return;
        }

        Directory.CreateDirectory(_dir);
        string[] args =
        [
            "-hide_banner", "-v", "error", "-y",
            "-f", "lavfi", "-i", $"color=c=black:s={Width}x{Height}:r=30:d=3",
            "-f", "lavfi", "-i", $"color=c=white:s={Box}x{Box}:r=30:d=3",
            "-filter_complex", $"[0][1]overlay=x={PixelsPerFrame}*n:y={(Height - Box) / 2}",
            "-c:v", "libx264", "-qp", "0", "-g", "30", "-pix_fmt", "yuv420p", VideoPath,
        ];
        var psi = new ProcessStartInfo(_tools.FfmpegPath) { RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            _tools = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // cartella già rimossa o file ancora aperto
        }

        return ValueTask.CompletedTask;
    }

    private async Task<(MainViewModel Vm, FakePlatform Platform)> OpenAsync()
    {
        Assert.SkipWhen(_tools is null, "FFmpeg (con libx264) non trovato: test saltato.");
        var platform = new FakePlatform();
        var vm = new MainViewModel(platform);
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Tools);
        await vm.OpenVideoFileAsync(VideoPath);
        Assert.True(vm.HasVideo);
        return (vm, platform);
    }

    private static async Task ShowFrameAsync(MainViewModel vm, int index)
    {
        vm.GoToFrame(index);
        var clock = Stopwatch.StartNew();
        while (!(vm.CurrentIndex == index && vm.CurrentFrame is VideoFrame f && f.Index == index))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"fotogramma {index} non mostrato");
            await Task.Delay(10);
        }
    }

    private static void Calibrate(MainViewModel vm)
    {
        vm.LineA = new LineSegment(new Point2(200, 0), new Point2(200, Height));
        vm.LineB = new LineSegment(new Point2(440, 0), new Point2(440, Height));
        vm.IsDistanceKnownMode = true;
        vm.DistanceText = "24";
        Assert.NotNull(vm.Calibration);
    }

    [Fact]
    public async Task OpenVideo_ShowsTheFirstFrameThroughThePlatformImage()
    {
        (MainViewModel vm, _) = await OpenAsync();

        VideoFrame shown = Assert.IsType<VideoFrame>(vm.CurrentFrame);
        Assert.Equal(0, shown.Index);
        Assert.Equal(Width, vm.VideoWidth);
        Assert.Equal(90, vm.FrameCount);
    }

    [Fact]
    public async Task ManualMeasurement_GivesTheExactSpeed()
    {
        (MainViewModel vm, _) = await OpenAsync();
        Calibrate(vm);
        vm.AutoAdvance = false;

        // Il bordo davanti del riquadro (x = 8 n + 40) passa la linea A al fotogramma 20 e la B al 50.
        foreach (int n in new[] { 18, 19, 21, 22, 48, 49, 51, 52 })
        {
            await ShowFrameAsync(vm, n);
            vm.PlacePointCommand.Execute(new Point2(PixelsPerFrame * n + Box, Height / 2.0));
        }

        Assert.True(vm.SavePassageCommand.CanExecute(null));
        vm.SavePassageCommand.Execute(null);

        PassageRow row = Assert.Single(vm.Passages);
        Assert.Equal(ExpectedKmh, row.Passage.Speed.SpeedKmh, 0.5);
        Assert.True(vm.HasUnsavedMeasurements);
    }

    [Fact]
    public async Task OpeningAnotherVideo_AsksBeforeDiscardingMeasurements_AndKeepsThemOnNo()
    {
        (MainViewModel vm, FakePlatform platform) = await OpenAsync();
        Calibrate(vm);
        vm.AutoAdvance = false;
        await ShowFrameAsync(vm, 19);
        vm.PlacePointCommand.Execute(new Point2(PixelsPerFrame * 19 + Box, Height / 2.0));

        platform.Answer = DialogAnswer.No;
        await vm.OpenVideoFileAsync(VideoPath);

        string question = Assert.Single(platform.Questions);
        Assert.Contains("misure non esportate", question, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, vm.CurrentPointCount);
    }

    [Fact]
    public async Task RoadEdges_CanBeConfirmedOnlyAfterMovingACorner()
    {
        (MainViewModel vm, _) = await OpenAsync();
        vm.SelectedStep = WorkflowStep.Road;
        Assert.NotNull(vm.Road);
        Assert.False(vm.ConfirmRoadEdgesCommand.CanExecute(null));

        // Si sposta un solo vertice di 20 pixel: ora si può confermare.
        Point2[] corners = [.. vm.Road!.Corners];
        corners[0] += new Point2(-20, -20);
        vm.RoadEditedCommand.Execute(vm.Road.WithCorners(corners, vm.VideoWidth, vm.VideoHeight));
        Assert.True(vm.ConfirmRoadEdgesCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenSamples_GoesThroughThePlatformBrowser()
    {
        var platform = new FakePlatform();
        var vm = new MainViewModel(platform);
        vm.OpenSamplesCommand.Execute(null);
        Assert.Equal([AppInfo.SamplesUrl], platform.OpenedUrls);
        await Task.CompletedTask;
    }
}

/// <summary>Pulsante «Supporta lo sviluppo di questa applicazione».</summary>
public class SupportLinkTests
{
    [Fact]
    public void OpenSupport_OpensBuyMeACoffee()
    {
        var platform = new FakePlatform();
        var vm = new TrafficSpeedAnalyzer.App.ViewModels.MainViewModel(platform);
        vm.OpenSupportCommand.Execute(null);
        Assert.Equal(["https://buymeacoffee.com/bugeo"], platform.OpenedUrls);
    }
}
