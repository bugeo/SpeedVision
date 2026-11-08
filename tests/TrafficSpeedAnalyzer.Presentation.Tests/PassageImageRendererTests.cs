using SkiaSharp;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Video;

namespace TrafficSpeedAnalyzer.Presentation.Tests;

/// <summary>Immagini esportate e anteprima per la mappa, disegnate con SkiaSharp.</summary>
public class PassageImageRendererTests
{
    private const int W = 1920, H = 1080;

    private static readonly CalibrationSettings Calibration = new(
        new LineSegment(new Point2(600, 200), new Point2(600, 1000)),
        new LineSegment(new Point2(1300, 200), new Point2(1300, 1000)),
        20);

    private static readonly PassageImageText Text = new("Ingresso · linea A", "08:30:12.345", "Veicolo 0001 · 50,0 km/h", "50,0 km/h");

    /// <summary>Fotogramma grigio con una scacchiera di 8 pixel nel riquadro del «veicolo» (900–1100, 500–700).</summary>
    private static VideoFrame Frame()
    {
        var px = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                byte v = x is >= 900 and < 1100 && y is >= 500 and < 700 ? (byte)(((x / 8) + (y / 8)) % 2 == 0 ? 255 : 0) : (byte)90;
                int i = (y * W + x) * 3;
                px[i] = px[i + 1] = px[i + 2] = v;
            }
        }

        return new VideoFrame(0, 0, W, H, px);
    }

    private static SKBitmap Decode(byte[] jpeg) => SKBitmap.Decode(jpeg) ?? throw new InvalidOperationException("JPEG non valido");

    [Fact]
    public void Jpeg_HasFrameSize_AndShowsTheLinesInTheirColours()
    {
        byte[] jpeg = PassageImageRenderer.RenderJpeg(Frame(), W, H, Calibration, DetectionLine.A, new Point2(1000, 700), Text);

        using SKBitmap img = Decode(jpeg);
        Assert.Equal(W, img.Width);
        Assert.Equal(H, img.Height);

        // Linea A (evidenziata, piena): azzurra. Linea B: arancione, a tratti (si cerca un pixel colorato lungo la linea).
        SKColor a = img.GetPixel(600, 600);
        Assert.True(a.Blue > 200 && a.Red < 140, $"linea A: {a}");
        Assert.Contains(Enumerable.Range(300, 200), y => img.GetPixel(1300, y) is { Red: > 200, Blue: < 140 });

        // Riquadro con i testi in alto a sinistra, scuro.
        SKColor panel = img.GetPixel(30, 30);
        Assert.True(panel.Red < 60 && panel.Green < 60, $"riquadro: {panel}");
    }

    [Fact]
    public void Anonymized_PixelatesOnlyTheGivenBox()
    {
        VideoFrame frame = Frame();
        var box = new BoundingBox(900, 500, 200, 200);
        using SKBitmap clear = Decode(PassageImageRenderer.RenderJpeg(frame, W, H, Calibration, DetectionLine.A, new Point2(1500, 900), Text));
        using SKBitmap anon = Decode(PassageImageRenderer.RenderJpeg(frame, W, H, Calibration, DetectionLine.A, new Point2(1500, 900), Text, [box]));

        double inside = MeanDifference(clear, anon, 910, 510, 1090, 690);
        double outside = MeanDifference(clear, anon, 200, 700, 500, 950);
        Assert.True(inside > 40, $"dentro il riquadro la scacchiera dev'essere pixellata (differenza {inside:0.0})");
        Assert.True(outside < 3, $"fuori dal riquadro l'immagine non cambia (differenza {outside:0.0})");
    }

    [Fact]
    public void Snapshot_IsAtMost720Wide_WithTheLines()
    {
        byte[] jpeg = PassageImageRenderer.RenderSnapshotJpeg(Frame(), W, Calibration.LineA, Calibration.LineB, maxWidth: 720, quality: 85);
        using SKBitmap img = Decode(jpeg);
        Assert.Equal(720, img.Width);
        Assert.Equal(405, img.Height);

        SKColor a = img.GetPixel(225, 225);   // 600 × 720/1920
        Assert.True(a.Blue > 180, $"linea A: {a}");
    }

    private static double MeanDifference(SKBitmap x, SKBitmap y, int x0, int y0, int x1, int y1)
    {
        double sum = 0;
        int n = 0;
        for (int j = y0; j < y1; j += 3)
        {
            for (int i = x0; i < x1; i += 3)
            {
                sum += Math.Abs(x.GetPixel(i, j).Green - y.GetPixel(i, j).Green);
                n++;
            }
        }

        return sum / n;
    }
}

/// <summary>Risorse incluse nel progetto comune.</summary>
public class ResourceTests
{
    [Fact]
    public void ReportLogo_IsEmbeddedAsJpeg()
    {
        using Stream? s = typeof(TrafficSpeedAnalyzer.App.ViewModels.MainViewModel).Assembly.GetManifestResourceStream("speedvision-logo-report.jpg");
        Assert.NotNull(s);
        Assert.Equal(0xFF, s.ReadByte());
        Assert.Equal(0xD8, s.ReadByte());
    }
}
