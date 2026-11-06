using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Privacy;
using TrafficSpeedAnalyzer.Core.Tracking;

namespace TrafficSpeedAnalyzer.Core.Tests;

public class PrivacyMaskingTests
{
    [Fact]
    public void Region_CoversTheWholeBoxWithMargin_InsideTheImage()
    {
        var box = new BoundingBox(100, 200, 400, 150);
        BoundingBox r = Assert.Single(PrivacyMasking.RegionsFor(box, 1920, 1080));
        Assert.True(r.X < box.X && r.Y < box.Y && r.Right > box.Right && r.Bottom > box.Bottom);   // tutto il riquadro, angoli compresi
        Assert.Equal(400 * (1 + (2 * PrivacyMasking.Margin)), r.Width, 6);

        // Riquadro sul bordo: la zona non esce dall'immagine.
        BoundingBox edge = Assert.Single(PrivacyMasking.RegionsFor(new BoundingBox(1800, 1000, 300, 120), 1920, 1080));
        Assert.True(edge.X >= 0 && edge.Y >= 0 && edge.Right <= 1920 && edge.Bottom <= 1080);
    }

    [Fact]
    public void BoxAt_InterpolatesOnlyAcrossShortGaps()
    {
        TrackPoint P(long f, double x) => new(f, f / 30.0, new BoundingBox(x, 100, 200, 80), VehicleClass.Car, 0.9f, false);
        List<TrackPoint> pts = [P(10, 100), P(20, 300), P(60, 900)];

        Assert.Equal(100, PrivacyMasking.BoxAt(pts, 10)!.Value.X);
        Assert.Equal(200, PrivacyMasking.BoxAt(pts, 15)!.Value.X, 6);
        Assert.Null(PrivacyMasking.BoxAt(pts, 40));   // 40 fotogrammi senza rilevamenti
        Assert.Null(PrivacyMasking.BoxAt(pts, 5));    // prima del primo rilevamento
    }

    [Fact]
    public void Pixelate_ReplacesDetail_OnlyInsideTheRegion()
    {
        const int w = 64, h = 64, bpp = 3;
        var px = new byte[w * h * bpp];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)(((x + y) % 2) * 255);   // scacchiera fine: massimo dettaglio
                px[((y * w) + x) * bpp] = v;
                px[((y * w) + x) * bpp + 1] = v;
                px[((y * w) + x) * bpp + 2] = v;
            }
        }

        PrivacyMasking.Pixelate(px, w, h, w * bpp, bpp, new BoundingBox(16, 16, 32, 32));

        // Dentro: ogni blocco ha un colore uniforme (≈ grigio medio).
        int block = PrivacyMasking.BlockSize(32, 32, h);
        byte first = px[((16 * w) + 16) * bpp];
        Assert.InRange(first, 120, 135);
        for (int y = 16; y < 16 + block; y++)
        {
            for (int x = 16; x < 16 + block; x++)
            {
                Assert.Equal(first, px[((y * w) + x) * bpp]);
            }
        }

        // Fuori: la scacchiera è intatta.
        Assert.Equal(0, px[0]);
        Assert.Equal(255, px[bpp]);
    }

    [Fact]
    public void FaceRegion_CoversHeadAndShoulders_NotTheLegs()
    {
        // Pedone in piedi: riquadro alto e stretto.
        var person = new BoundingBox(500, 300, 80, 200);
        BoundingBox r = Assert.Single(PrivacyMasking.FaceRegionsFor(person, 1920, 1080));
        Assert.True(r.X < person.X && r.Right > person.Right, "più largo del riquadro");
        Assert.True(r.Y < person.Y, "sopra la testa");
        Assert.InRange(r.Bottom, person.Y + (0.3 * person.Height), person.Y + (0.4 * person.Height));   // fino alle spalle
    }

    [Fact]
    public void FaceRegion_ShortWideBox_IsCoveredMore_AndStaysInsideTheImage()
    {
        // Persona tagliata dal bordo inferiore: si vede solo il busto, si copre quasi tutto.
        var cut = new BoundingBox(1850, 1000, 100, 80);
        BoundingBox r = Assert.Single(PrivacyMasking.FaceRegionsFor(cut, 1920, 1080));
        Assert.True(r.X >= 0 && r.Y >= 0 && r.Right <= 1920 && r.Bottom <= 1080);
        Assert.Equal(1080, r.Bottom, 6);
        Assert.Empty(PrivacyMasking.FaceRegionsFor(new BoundingBox(10, 10, 0, 50), 1920, 1080));
    }

    [Fact]
    public void BlockSize_MakesSmallRegionsUnreadable()
    {
        Assert.True(PrivacyMasking.BlockSize(40, 12, 1080) >= 12);   // una targa piccola diventa 1 blocco in altezza
        Assert.Equal(50, PrivacyMasking.BlockSize(400, 600, 1080));
    }
}
