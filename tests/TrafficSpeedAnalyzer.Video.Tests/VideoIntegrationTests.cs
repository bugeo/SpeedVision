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

    [Fact]
    public async Task VideoEncoder_WritesAPlayableMp4WithEveryFrame()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "tsa-enc-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            const int w = 160, h = 90, n = 45;
            await using (VideoEncoder enc = await VideoEncoder.StartAsync(Tools, path, w, h, 30, ct))
            {
                var frame = new byte[w * h * 4];
                for (int i = 0; i < n; i++)
                {
                    Array.Fill(frame, (byte)(i * 5));
                    await enc.WriteFrameAsync(frame, ct);
                }

                await enc.CompleteAsync(ct);
            }

            Assert.False(File.Exists(path + ".part.mp4"));
            VideoInfo info = await VideoProbe.ProbeAsync(Tools, path, ct);
            Assert.Equal((w, h), (info.DisplayWidth, info.DisplayHeight));
            Assert.Equal(30, info.NominalFps, 3);
            FrameIndex index = await VideoProbe.BuildIndexAsync(Tools, info, ct);
            Assert.Equal(n, index.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VideoEncoder_LeavesNoFileWhenNotCompleted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "tsa-enc-" + Guid.NewGuid().ToString("N") + ".mp4");
        await using (VideoEncoder enc = await VideoEncoder.StartAsync(Tools, path, 64, 64, 25, ct))
        {
            await enc.WriteFrameAsync(new byte[64 * 64 * 4], ct);
        }

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".part.mp4"));
    }

    [Fact]
    public async Task PassageAudio_AddsTheSourceSoundWithTheVideoDuration()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(Path.GetTempPath(), "tsa-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Sorgente: 4 s di tono; video: 2 s (60 fotogrammi).
            string source = Path.Combine(dir, "sorgente.m4a");
            ProcessResult gen = await ProcessRunner.RunAsync(Tools.FfmpegPath,
                ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-c:a", "aac", "-y", source], TimeSpan.FromSeconds(60), ct);
            Assert.Equal(0, gen.ExitCode);
            Assert.True(await PassageAudio.HasAudioAsync(Tools, source, ct));

            string video = Path.Combine(dir, "video.mp4");
            await using (VideoEncoder enc = await VideoEncoder.StartAsync(Tools, video, 64, 64, 30, ct))
            {
                for (int i = 0; i < 60; i++)
                {
                    await enc.WriteFrameAsync(new byte[64 * 64 * 4], ct);
                }

                await enc.CompleteAsync(ct);
            }

            Assert.False(await PassageAudio.HasAudioAsync(Tools, video, ct));
            await PassageAudio.AddAsync(Tools, video, source, [new AudioPiece(1.0, 0.5), new AudioPiece(null, 1.0), new AudioPiece(2.5, 0.5)], ct);

            Assert.True(await PassageAudio.HasAudioAsync(Tools, video, ct));
            VideoInfo info = await VideoProbe.ProbeAsync(Tools, video, ct);
            Assert.InRange(info.DurationSeconds, 1.9, 2.15);
            Assert.Equal(60, (await VideoProbe.BuildIndexAsync(Tools, info, ct)).Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PassageAudio_TrackCopiesPiecesAndFillsSilence()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string src = Path.GetTempFileName(), dst = Path.GetTempFileName();
        try
        {
            // 1 s di campioni a 48 kHz stereo 16 bit, ogni byte = 7.
            await File.WriteAllBytesAsync(src, Enumerable.Repeat((byte)7, 48000 * 4).ToArray(), ct);
            await PassageAudio.BuildTrackAsync(src, dst, [new AudioPiece(0.5, 0.25), new AudioPiece(null, 0.25), new AudioPiece(0.9, 0.25)], ct);

            byte[] t = await File.ReadAllBytesAsync(dst, ct);
            Assert.Equal(36000 * 4, t.Length);
            Assert.All(t[..(12000 * 4)], b => Assert.Equal(7, b));            // audio
            Assert.All(t[(12000 * 4)..(24000 * 4)], b => Assert.Equal(0, b)); // fermo: silenzio
            Assert.All(t[(24000 * 4)..(28800 * 4)], b => Assert.Equal(7, b)); // fino alla fine dell'audio (0,9–1,0 s)
            Assert.All(t[(28800 * 4)..], b => Assert.Equal(0, b));            // oltre la fine: silenzio
        }
        finally
        {
            File.Delete(src);
            File.Delete(dst);
        }
    }

    [Fact]
    public async Task PassageAudio_BellsOnlyWhenTheSourceIsSilent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(Path.GetTempPath(), "tsa-bell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string video = Path.Combine(dir, "video.mp4");
            await using (VideoEncoder enc = await VideoEncoder.StartAsync(Tools, video, 64, 64, 30, ct))
            {
                for (int i = 0; i < 90; i++)
                {
                    await enc.WriteFrameAsync(new byte[64 * 64 * 4], ct);
                }

                await enc.CompleteAsync(ct);
            }

            await PassageAudio.AddAsync(Tools, video, null, [new AudioPiece(0, 1), new AudioPiece(null, 1, Bell: true), new AudioPiece(null, 1, Bell: true)], ct);

            Assert.True(await PassageAudio.HasAudioAsync(Tools, video, ct));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PassageAudio_BellRingsAndFadesOut()
    {
        var pcm = new byte[48000 * 4 * 2];   // 2 s di silenzio
        PassageAudio.MixBell(pcm);

        static double Peak(byte[] b, int from, int to) =>
            Enumerable.Range(from, to - from).Max(i => Math.Abs((int)(short)(b[i * 4] | (b[(i * 4) + 1] << 8))));
        Assert.InRange(Peak(pcm, 0, 4800), 5000, 20000);         // suona subito, senza saturare
        Assert.True(Peak(pcm, 43200, 57600) < Peak(pcm, 0, 4800) / 10);   // dopo circa 1 s si è spenta
        Assert.Equal(0, Peak(pcm, 60000, 96000));                // poi silenzio
    }
}
