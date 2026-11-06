using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Detection.Tests;

/// <summary>Test senza modello né FFmpeg.</summary>
public class UnitTests
{
    [Fact]
    public void Letterbox_KeepsAspectAndPadsWithGray()
    {
        // Immagine 8×4 a tinta unita (B=10, G=20, R=30), modello 16×16: scala 2, metà inferiore grigia.
        const int w = 8, h = 4, size = 16;
        var img = new byte[w * h * 3];
        for (int i = 0; i < img.Length; i += 3)
        {
            img[i] = 10;
            img[i + 1] = 20;
            img[i + 2] = 30;
        }

        var tensor = new float[3 * size * size];
        float scale = Letterbox.Fill(img, w, h, new Roi(0, 0, w, h), tensor, size);
        Assert.Equal(2f, scale);
        int plane = size * size;
        Assert.Equal(10f, tensor[0 * plane + 3 * size + 5], 3);
        Assert.Equal(20f, tensor[1 * plane + 3 * size + 5], 3);
        Assert.Equal(30f, tensor[2 * plane + 7 * size + 15], 3);
        Assert.Equal(Letterbox.PadValue, tensor[0 * plane + 8 * size + 0]);
        Assert.Equal(Letterbox.PadValue, tensor[2 * plane + 15 * size + 15]);
    }

    [Fact]
    public void Letterbox_UsesOnlyTheRegion()
    {
        // Metà sinistra nera, metà destra bianca: con la zona a destra il tensore è tutto bianco.
        const int w = 20, h = 10, size = 10;
        var img = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 10; x < w; x++)
            {
                int p = (y * w + x) * 3;
                img[p] = img[p + 1] = img[p + 2] = 255;
            }
        }

        var tensor = new float[3 * size * size];
        Letterbox.Fill(img, w, h, new Roi(10, 0, 10, 10), tensor, size);
        Assert.All(tensor, v => Assert.Equal(255f, v, 3));
    }

    [Fact]
    public void DecodeCandidates_KeepsOnlyTheRequestedClasses()
    {
        // Uscita costruita a mano per un modello 32 × 32: ancora 0 = persona, ancora 1 = auto.
        const int size = 32, classes = 80, stride = 85;
        (float, float, float)[] grid = YoloxDetector.BuildGrid(size);
        var output = new float[grid.Length * stride];
        void Put(int anchor, int cocoClass, float score)
        {
            int o = anchor * stride;
            output[o] = 0.5f;                       // centro della cella
            output[o + 1] = 0.5f;
            output[o + 2] = MathF.Log(0.5f);        // lato = mezza cella
            output[o + 3] = MathF.Log(1.0f);
            output[o + 4] = 1f;
            output[o + 5 + cocoClass] = score;
        }

        Put(0, 0, 0.9f);   // persona
        Put(1, 2, 0.8f);   // auto
        var roi = new Roi(0, 0, size, size);

        VehicleDetection person = Assert.Single(YoloxDetector.DecodeCandidates(output, grid, classes, roi, 1f, [0], 0.25f));
        Assert.Equal(new BoundingBox(2, 0, 4, 8), person.Box);
        Assert.Equal(0.9f, person.Score, 4);

        VehicleDetection car = Assert.Single(YoloxDetector.DecodeCandidates(output, grid, classes, roi, 1f, [2, 3, 5, 7], 0.1f));
        Assert.Equal(VehicleClass.Car, car.Class);
        Assert.Empty(YoloxDetector.DecodeCandidates(output, grid, classes, roi, 1f, [0], 0.95f));
    }

    [Fact]
    public void Roi_IsClampedToImage()
    {
        Roi r = Roi.From(new BoundingBox(-10, 5.5, 50, 1000), 30, 20);
        Assert.Equal(new Roi(0, 5, 30, 15), r);
        Assert.Equal(new Roi(0, 0, 30, 20), Roi.From(null, 30, 20));
    }

    [Fact]
    public void Nms_RemovesOverlappingDuplicates_AcrossClasses()
    {
        var list = new List<VehicleDetection>
        {
            new(new BoundingBox(0, 0, 100, 50), VehicleClass.Truck, 0.6f),
            new(new BoundingBox(2, 1, 100, 50), VehicleClass.Car, 0.9f),
            new(new BoundingBox(300, 0, 100, 50), VehicleClass.Car, 0.5f),
        };
        List<VehicleDetection> kept = Nms.Apply(list, 0.5f);
        Assert.Equal(2, kept.Count);
        Assert.Equal(VehicleClass.Car, kept[0].Class);
        Assert.Equal(0.9f, kept[0].Score);
    }

    [Fact]
    public void MotionGate_DetectsChangeOnlyInsideRegion()
    {
        const int w = 64, h = 32;
        var a = new byte[w * h * 3];
        var b = (byte[])a.Clone();
        // cambia un blocco nella metà destra
        for (int y = 0; y < 16; y++)
        {
            for (int x = 40; x < 60; x++)
            {
                int p = (y * w + x) * 3;
                b[p] = b[p + 1] = b[p + 2] = 200;
            }
        }

        var gate = new MotionGate(sampleStep: 2);
        var left = new BoundingBox(0, 0, 32, 32);
        Assert.True(gate.HasMotion(a, w, h, left));   // primo fotogramma: sempre vero
        Assert.False(gate.HasMotion(b, w, h, left));  // cambiamento fuori dalla zona
        var right = new BoundingBox(32, 0, 32, 32);
        gate.Reset();
        gate.HasMotion(a, w, h, right);
        Assert.True(gate.HasMotion(b, w, h, right));
        Assert.False(gate.HasMotion(b, w, h, right)); // nessun nuovo cambiamento
    }

    [Fact]
    public void Region_ContainsLinesWithMargin_AndTruncationIsDetected()
    {
        var cal = new CalibrationSettings(
            new LineSegment(new Point2(400, 300), new Point2(420, 500)),
            new LineSegment(new Point2(800, 300), new Point2(820, 500)),
            20);
        BoundingBox r = AnalysisRegion.FromCalibration(cal, 1920, 1080);
        Assert.True(r.X < 400 && r.Right > 820 && r.Y < 300 && r.Bottom > 500);
        Assert.True(r.X >= 0 && r.Right <= 1920 && r.Y >= 0 && r.Bottom <= 1080);

        Assert.True(AnalysisRegion.IsTruncated(new BoundingBox(r.X, 400, 50, 50), r));          // bordo sinistro
        Assert.True(AnalysisRegion.IsTruncated(new BoundingBox(600, r.Bottom - 50, 50, 50), r)); // bordo inferiore
        Assert.False(AnalysisRegion.IsTruncated(new BoundingBox(600, r.Y, 50, 50), r));          // solo in alto: utilizzabile
        Assert.False(AnalysisRegion.IsTruncated(new BoundingBox(600, 400, 50, 50), r));
    }

    [Fact]
    public void GpuValidation_AcceptsTinyDifferences_RejectsWrongOutputs()
    {
        var rng = new Random(5);
        float[] expected = Enumerable.Range(0, 85 * 100).Select(_ => (float)rng.NextDouble()).ToArray();
        float[] close = expected.Select(v => v + 1e-4f).ToArray();
        float[] zeros = new float[expected.Length];
        float[] nan = (float[])expected.Clone();
        nan[10] = float.NaN;

        Assert.True(YoloxDetector.OutputsMatch(expected, close));
        Assert.False(YoloxDetector.OutputsMatch(expected, zeros));
        Assert.False(YoloxDetector.OutputsMatch(expected, nan));
        Assert.False(YoloxDetector.OutputsMatch(expected, expected.Take(85).ToArray()));
    }

    [Fact]
    public void Catalog_HasValidEntries()
    {
        foreach (ModelInfo m in ModelCatalog.All)
        {
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
            Assert.StartsWith("https://github.com/Megvii-BaseDetection/YOLOX/releases/", m.Url);
            Assert.True(m.SizeBytes > 1_000_000);
        }

        Assert.Same(ModelCatalog.Small, ModelCatalog.Find("yolox_s"));
    }
}
