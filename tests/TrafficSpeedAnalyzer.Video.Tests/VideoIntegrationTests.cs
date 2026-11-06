namespace TrafficSpeedAnalyzer.Video.Tests;

/// <summary>
/// Test con FFmpeg reale su video generati con contenuto noto.
/// Vengono saltati (non superati) se FFmpeg non è installato.
/// </summary>
public class VideoIntegrationTests(SyntheticVideos videos) : IClassFixture<SyntheticVideos>
{
    private FfmpegTools Tools
    {
        get
        {
            if (videos.SkipReason is not null)
            {
                Assert.Skip(videos.SkipReason);
            }

            return videos.Tools!;
        }
    }

    [Fact]
    public async Task Probe_ReadsBasicMetadata()
    {
        VideoInfo info = await VideoProbe.ProbeAsync(Tools, videos.ConstantRatePath, TestContext.Current.CancellationToken);
        Assert.Equal(SyntheticVideos.Width, info.DisplayWidth);
        Assert.Equal(SyntheticVideos.Height, info.DisplayHeight);
        Assert.Equal(30, info.NominalFps, 3);
        Assert.InRange(info.DurationSeconds, 2.9, 3.1);
        Assert.True(info.TimeBase > 0);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 21, 15, 0, TimeSpan.Zero), info.RecordingStart!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task Index_ConstantRate_HasEveryFrameAtExactTimes()
    {
        VideoInfo info = await VideoProbe.ProbeAsync(Tools, videos.ConstantRatePath, TestContext.Current.CancellationToken);
        FrameIndex index = await VideoProbe.BuildIndexAsync(Tools, info, TestContext.Current.CancellationToken);
        Assert.Equal(90, index.Count);
        Assert.False(index.Stats.IsVariable);
        for (int i = 0; i < index.Count; i++)
        {
            Assert.Equal(info.StartTimeSeconds + i / 30.0, index[i], 4);
        }
    }

    [Fact]
    public async Task Index_VariableRate_IsDetected()
    {
        VideoInfo info = await VideoProbe.ProbeAsync(Tools, videos.VariableRatePath, TestContext.Current.CancellationToken);
        FrameIndex index = await VideoProbe.BuildIndexAsync(Tools, info, TestContext.Current.CancellationToken);
        Assert.Equal(90, index.Count);
        Assert.True(index.Stats.IsVariable);
        double[] intervals = Enumerable.Range(1, index.Count - 1).Select(i => index[i] - index[i - 1]).ToArray();
        // Il video ha tratti a 30 fps e tratti più lenti (lo schema esatto dipende dalla versione di FFmpeg).
        Assert.Equal(1 / 30.0, intervals.Min(), 3);
        Assert.True(intervals.Max() > 1.5 * intervals.Min(), $"intervallo massimo {intervals.Max():0.000} s");
    }

    [Theory]
    [InlineData("cfr")]
    [InlineData("vfr")]
    public async Task Reader_ReturnsExactlyTheRequestedFrame_InAnyOrder(string which)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = which == "cfr" ? videos.ConstantRatePath : videos.VariableRatePath;
        VideoInfo info = await VideoProbe.ProbeAsync(Tools, path, ct);
        FrameIndex index = await VideoProbe.BuildIndexAsync(Tools, info, ct);
        await using var reader = new FrameReader(Tools, info, index, info.DisplayWidth, info.DisplayHeight);

        int origin = SyntheticVideos.BarLeftEdge(await reader.GetFrameAsync(0, ct));
        Assert.True(origin >= 0, "barra non trovata nel primo fotogramma");

        // sequenza mista: salti in avanti, avanzamento singolo, ritorni indietro, ultimo fotogramma
        int[] requests = [45, 46, 47, 44, 89, 10, 9, 33, 0, 60, 61, 31];
        foreach (int i in requests)
        {
            VideoFrame frame = await reader.GetFrameAsync(i, ct);
            Assert.Equal(i, frame.Index);
            Assert.Equal(index[i], frame.TimeSeconds, 9);
            Assert.Equal(origin + i * SyntheticVideos.PixelsPerFrame, SyntheticVideos.BarLeftEdge(frame));
        }
    }

    [Fact]
    public async Task Reader_DownscaledPreview_KeepsFrameIdentity()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        VideoInfo info = await VideoProbe.ProbeAsync(Tools, videos.ConstantRatePath, ct);
        FrameIndex index = await VideoProbe.BuildIndexAsync(Tools, info, ct);
        await using var reader = new FrameReader(Tools, info, index, 400, 32);

        int origin = SyntheticVideos.BarLeftEdge(await reader.GetFrameAsync(0, ct));
        foreach (int i in new[] { 20, 21, 70, 5 })
        {
            VideoFrame frame = await reader.GetFrameAsync(i, ct);
            Assert.Equal(400, frame.Width);
            Assert.Equal(i, frame.Index);
            Assert.InRange(SyntheticVideos.BarLeftEdge(frame) - origin, i * 4 - 1, i * 4 + 1);
        }
    }

    [Fact]
    public async Task Session_OpensAndProvidesFullResolutionFrames()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using VideoSession session = await VideoSession.OpenAsync(Tools, videos.ConstantRatePath, 400, 400, null, ct);
        Assert.Equal(400, session.Preview.OutputWidth);
        VideoFrame full = await session.GetFullResolutionFrameAsync(30, ct);
        Assert.Equal(SyntheticVideos.Width, full.Width);
        Assert.Equal(30, full.Index);
    }

    [Fact]
    public async Task Probe_NonVideoFile_FailsWithClearError()
    {
        FfmpegTools tools = Tools;
        string path = Path.Combine(Path.GetTempPath(), "tsa-not-a-video-" + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllTextAsync(path, "questo non è un video", TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<VideoProbeException>(() => VideoProbe.ProbeAsync(tools, path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
