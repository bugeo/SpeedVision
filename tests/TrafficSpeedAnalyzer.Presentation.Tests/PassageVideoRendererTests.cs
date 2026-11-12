using SkiaSharp;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Presentation.Tests;

/// <summary>Fotogrammi del video dei passaggi: veicoli pixellati, cronometro solo dall'ingresso, velocità all'uscita.</summary>
public class PassageVideoRendererTests
{
    private const int W = 1280, H = 720;

    private static readonly CalibrationSettings Calibration = new(
        new LineSegment(new Point2(600, 200), new Point2(600, 1000)),
        new LineSegment(new Point2(1300, 200), new Point2(1300, 1000)),
        20);

    private static readonly PassageVideoText Text = new("Veicolo 0001 · A → B", "Distanza A–B", "20,00 m", "Tempo", s => s.ToString("0.000") + " s", "Velocità", "50,0 km/h", "Via Roma 12, Milano", "10/11/2026");

    private static VehiclePassage Passage()
    {
        var entry = new CrossingEstimate(1.0, CrossingMethod.Interpolated, 0.001, new TrackObservation(30, 1.0, new Point2(600, 700)), 3, 1);
        var exit = new CrossingEstimate(2.44, CrossingMethod.Interpolated, 0.001, new TrackObservation(74, 2.46, new Point2(1300, 700)), 3, -1);
        return new VehiclePassage(1, TravelDirection.AToB, entry, exit, SpeedCalculator.Compute(20, 1.0, 2.44));
    }

    /// <summary>Fotogramma grigio con una scacchiera nel riquadro del «veicolo» (video 1920×1080: 900–1100, 500–700).</summary>
    private static VideoFrame Frame()
    {
        var px = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                byte v = x is >= 600 and < 733 && y is >= 333 and < 466 ? (byte)(((x / 6) + (y / 6)) % 2 == 0 ? 255 : 0) : (byte)90;
                int i = ((y * W) + x) * 3;
                px[i] = px[i + 1] = px[i + 2] = v;
            }
        }

        return new VideoFrame(0, 0, W, H, px);
    }

    private static byte[] Render(ClipFrame clip, string? save = null)
    {
        using var r = new PassageVideoRenderer(W, H, 1920, 1080);
        byte[] bgra = (byte[])r.Render(Frame(), Calibration, Passage(), clip, new Point2(1000, 700), new BoundingBox(900, 500, 200, 200), [new BoundingBox(900, 500, 200, 200)], Text).Clone();
        if (save is not null)
        {
            using var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bmp.GetPixels(), bgra.Length);
            using SKData png = bmp.Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, save), png.ToArray());
        }

        return bgra;
    }

    private static byte Px(byte[] bgra, int x, int y) => bgra[(((y * W) + x) * 4) + 1];

    [Fact]
    public void Render_PixelatesVehiclesAndShowsTheReadoutsAtTheRightTime()
    {
        byte[] approach = Render(new ClipFrame(10, ClipPhase.Approach, null), "video-arrivo.png");
        byte[] entry = Render(new ClipFrame(30, ClipPhase.EntryPause, 0), "video-ingresso.png");
        byte[] exit = Render(new ClipFrame(74, ClipPhase.ExitPause, 1.44));
        byte[] speed = Render(new ClipFrame(74, ClipPhase.SpeedPause, 1.44), "video-velocita.png");

        // Scacchiera pixellata: righe vicine con lo stesso valore nel blocco.
        Assert.Equal(Px(approach, 640, 360), Px(approach, 643, 363));

        // In alto al centro, tre riquadri a posti fissi: la distanza sempre, il tempo dall'ingresso, la velocità solo
        // dopo la pausa dell'uscita. Nient'altro in basso.
        static int Covered(byte[] b, int y) => Enumerable.Range(0, W).Count(x => Px(b, x, y) != 90);
        int y0 = 60;
        Assert.True(Covered(entry, y0) > Covered(approach, y0) + 50);
        Assert.Equal(Covered(entry, y0), Covered(exit, y0));
        Assert.True(Covered(speed, y0) > Covered(exit, y0) + 50);
        Assert.Equal(90, Px(speed, W / 2, H - 60));

        // In basso, staccati dal bordo: la via a sinistra, la data a destra, niente al centro.
        int yb = H - 70;
        Assert.NotEqual(90, Px(speed, 40, yb));
        Assert.NotEqual(90, Px(speed, W - 40, yb));
        Assert.Equal(90, Px(speed, W / 2, yb));
    }

    [Fact]
    public void AudioPieces_SoundWhileRunningSilenceAndBellsAtTheLines()
    {
        var index = new FrameIndex(Enumerable.Range(0, 300).Select(i => 10 + (i / 30.0)));   // il file parte da 10 s
        List<ClipFrame> clip = PassageClipPlan.Build(
            new VehiclePassage(1, TravelDirection.AToB,
                new CrossingEstimate(3.31, CrossingMethod.Interpolated, 0.001, new TrackObservation(100, 100 / 30.0, new Point2(0, 0)), 3, 1),
                new CrossingEstimate(5.32, CrossingMethod.Interpolated, 0.001, new TrackObservation(160, 160 / 30.0, new Point2(0, 0)), 3, -1),
                SpeedCalculator.Compute(20, 3.31, 5.32)),
            [.. Enumerable.Range(0, 300).Select(i => i / 30.0)], 30);

        List<AudioPiece> pieces = App.ViewModels.MainViewModel.AudioPieces(clip, index, fileStart: 10, fps: 30);

        Assert.Equal(5, pieces.Count);
        Assert.Equal(9 / 30.0, pieces[0].SourceStart!.Value, 9);    // arrivo, 3 s prima dell'ingresso
        Assert.Equal(91 / 30.0, pieces[0].Duration, 9);
        Assert.Null(pieces[1].SourceStart);                          // fermo all'ingresso, con la campanella
        Assert.True(pieces[1].Bell && pieces[3].Bell && !pieces[0].Bell && !pieces[2].Bell);
        Assert.True(pieces[4].Beep && !pieces[4].Bell && pieces[4].SourceStart is null);   // velocità: bip
        Assert.Equal(101 / 30.0, pieces[2].SourceStart!.Value, 9);  // tragitto
        Assert.Null(pieces[3].SourceStart);                          // fermo all'uscita
        Assert.Equal(clip.Count / 30.0, pieces.Sum(p => p.Duration), 9);
    }
}
