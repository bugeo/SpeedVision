namespace TrafficSpeedAnalyzer.Video.Tests;

/// <summary>Test che non richiedono FFmpeg: interpretazione dei dati prodotti da ffprobe/ffmpeg.</summary>
public class ParsingTests
{
    private const string IphoneLikeJson = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "hevc", "width": 1920, "height": 1080,
              "sample_aspect_ratio": "1:1", "avg_frame_rate": "30000/1001", "r_frame_rate": "30/1",
              "time_base": "1/600", "start_time": "0.000000", "duration": "12.345000", "pix_fmt": "yuv420p10le",
              "disposition": { "attached_pic": 0 },
              "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ],
              "tags": { "creation_time": "2026-10-25T21:15:03.000000Z" } },
            { "index": 1, "codec_type": "audio", "codec_name": "aac" }
          ],
          "format": { "duration": "12.400000",
            "tags": { "creation_time": "2026-10-25T21:15:03.000000Z",
                      "com.apple.quicktime.creationdate": "2026-10-25T22:15:03+0100",
                      "com.apple.quicktime.location.ISO6709": "+45.4642+009.1900+120.000/" } }
        }
        """;

    [Fact]
    public void Probe_AppliesRotationAndReadsAppleDate()
    {
        VideoInfo info = VideoProbe.ParseProbeJson(IphoneLikeJson, "c:/video.mov", 1234);
        Assert.Equal(270, info.Rotation);
        Assert.Equal(1080, info.DisplayWidth);
        Assert.Equal(1920, info.DisplayHeight);
        Assert.Equal(1920, info.CodedWidth);
        Assert.Equal(30000.0 / 1001, info.NominalFps, 9);
        Assert.Equal(12.345, info.DurationSeconds, 9);
        Assert.Equal(1.0 / 600, info.TimeBase, 12);
        Assert.Equal("com.apple.quicktime.creationdate", info.RecordingStartSource);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 22, 15, 3, TimeSpan.FromHours(1)), info.RecordingStart);
        Assert.Equal(TimeSpan.FromHours(1), info.RecordingStart!.Value.Offset);
        Assert.Equal(new TrafficSpeedAnalyzer.Core.Calibration.GeoPosition(45.4642, 9.19), info.Location);
    }

    [Fact]
    public void Probe_ReadsAndroidLocation_OrNothing()
    {
        string Json(string tags) => $$"""
            { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 1280, "height": 720, "avg_frame_rate": "30/1" } ],
              "format": { "duration": "5.0", "tags": { {{tags}} } } }
            """;
        Assert.Equal(new TrafficSpeedAnalyzer.Core.Calibration.GeoPosition(-33.8568, 151.2153),
            VideoProbe.ParseProbeJson(Json("\"location\": \"-33.8568+151.2153/\""), "a.mp4", 1).Location);
        Assert.Null(VideoProbe.ParseProbeJson(Json("\"encoder\": \"Lavf\""), "b.mp4", 1).Location);
    }

    [Fact]
    public void Probe_FallsBackToCreationTimeInUtc()
    {
        string json = """
            { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 1280, "height": 720,
                             "avg_frame_rate": "60/1", "time_base": "1/90000" } ],
              "format": { "duration": "5.0", "tags": { "creation_time": "2026-10-25T07:00:00.000000Z" } } }
            """;
        VideoInfo info = VideoProbe.ParseProbeJson(json, "v.mp4", 1);
        Assert.Equal(0, info.Rotation);
        Assert.Equal(1280, info.DisplayWidth);
        Assert.Equal(5.0, info.DurationSeconds, 9);
        Assert.Equal("creation_time", info.RecordingStartSource);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 7, 0, 0, TimeSpan.Zero), info.RecordingStart!.Value.ToUniversalTime());
    }

    [Fact]
    public void Probe_IgnoresUnsetClockAndCoverArt()
    {
        string json = """
            { "streams": [
                { "codec_type": "video", "codec_name": "mjpeg", "width": 300, "height": 300, "disposition": { "attached_pic": 1 } },
                { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480, "avg_frame_rate": "25/1", "time_base": "1/25",
                  "tags": { "creation_time": "1970-01-01T00:00:00.000000Z" } } ],
              "format": {} }
            """;
        VideoInfo info = VideoProbe.ParseProbeJson(json, "v.mp4", 1);
        Assert.Equal("h264", info.CodecName);
        Assert.Null(info.RecordingStart);
    }

    [Fact]
    public void Probe_WithoutVideo_Throws()
    {
        Assert.Throws<VideoProbeException>(() =>
            VideoProbe.ParseProbeJson("""{ "streams": [ { "codec_type": "audio" } ] }""", "a.m4a", 1));
    }

    [Fact]
    public void PacketTimes_AreSortedLaterAndDiscardedPacketsSkipped()
    {
        const string csv = "0,K__\n-1024,_D_\n2048,___\n1024,___\n";
        Assert.True(VideoProbe.TryParsePacketTimes(csv, 1.0 / 15360, out List<double> times));
        Assert.Equal(3, times.Count);
        var index = new FrameIndex(times);
        Assert.Equal(0, index[0], 12);
        Assert.Equal(1024.0 / 15360, index[1], 12);
        Assert.Equal(2048.0 / 15360, index[2], 12);
    }

    [Fact]
    public void PacketTimes_WithMissingPts_RequestFallback()
    {
        Assert.False(VideoProbe.TryParsePacketTimes("N/A,K__\nN/A,___\n", 1.0 / 90000, out _));
    }

    [Fact]
    public void ShowInfo_UsesExactIntegerPts()
    {
        const string line = "[Parsed_showinfo_2 @ 0x55602ce62e80] n:   0 pts:  23552 pts_time:1.53333 duration:   1024 duration_time:0.0666667 fmt:bgr24";
        Assert.True(DecodeStream.TryParseShowInfo(line, 1.0 / 15360, out double t));
        Assert.Equal(23552.0 / 15360, t, 12);
    }

    [Fact]
    public void ShowInfo_FallsBackToPtsTime_WhenTimeBaseDiffers()
    {
        const string line = "[Parsed_showinfo_2 @ 0x1] n:   3 pts:  45 pts_time:1.5 duration: 1";
        Assert.True(DecodeStream.TryParseShowInfo(line, 1.0 / 15360, out double t));
        Assert.Equal(1.5, t, 12);
    }

    [Theory]
    [InlineData("[Parsed_showinfo_2 @ 0x1] config in time_base: 1/15360, frame_rate: 30/1")]
    [InlineData("[Parsed_showinfo_2 @ 0x1] color_range:pc color_space:gbr")]
    [InlineData("Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'x.mp4':")]
    public void ShowInfo_IgnoresOtherLines(string line)
    {
        Assert.False(DecodeStream.TryParseShowInfo(line, 1.0 / 15360, out _));
    }

    [Fact]
    public void FrameIndex_ConstantRate()
    {
        var index = new FrameIndex(Enumerable.Range(0, 300).Select(i => i / 30.0));
        Assert.False(index.Stats.IsVariable);
        Assert.Equal(30, index.Stats.MeanFps, 6);
        Assert.Equal(10, index.NearestIndex(10 / 30.0 + 0.01));
        Assert.Equal(11, index.NearestIndex(10 / 30.0 + 0.02));
        Assert.Equal(10, index.IndexAtOrBefore(10 / 30.0 + 0.03));
        Assert.Equal(0, index.NearestIndex(-5));
        Assert.Equal(299, index.NearestIndex(1e6));
    }

    [Fact]
    public void FrameIndex_DetectsVariableRate()
    {
        var times = new List<double>();
        double t = 0;
        var rng = new Random(1);
        for (int i = 0; i < 300; i++)
        {
            times.Add(t);
            t += (1 / 30.0) * (0.85 + 0.3 * rng.NextDouble());
        }

        Assert.True(new FrameIndex(times).Stats.IsVariable);
    }

    [Fact]
    public void FitWithin_KeepsAspectAndEvenSizes()
    {
        Assert.Equal((1920, 1080), VideoSession.FitWithin(3840, 2160, 1920, 1080));
        Assert.Equal((608, 1080), VideoSession.FitWithin(1080, 1920, 1920, 1080));
        Assert.Equal((640, 480), VideoSession.FitWithin(640, 480, 1920, 1080));
    }
}
