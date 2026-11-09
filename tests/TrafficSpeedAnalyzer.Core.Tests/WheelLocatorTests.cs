using TrafficSpeedAnalyzer.Core.Detection;
using Xunit;

namespace TrafficSpeedAnalyzer.Core.Tests;

public sealed class WheelLocatorTests
{
    private const int W = 960, H = 540;

    /// <summary>Asfalto grigio, carrozzeria chiara e due ruote (pneumatico scuro, cerchio chiaro) che toccano il fondo del riquadro.</summary>
    private static (byte[] Pixels, BoundingBox Box) SideViewCar(double frontX, double rearX, double radius, double bottom, bool wheels = true, double blurPx = 0)
    {
        var px = new byte[W * H * 3];
        double left = rearX - (1.6 * radius), right = frontX + (1.7 * radius), top = bottom - (4.2 * radius);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                double v = y > bottom - (0.3 * radius) ? 110 : 150;                        // asfalto / sfondo
                if (x >= left && x <= right && y >= top && y <= bottom - (0.15 * radius))
                {
                    v = 215;                                                                 // carrozzeria
                }

                if (wheels)
                {
                    foreach (double cx in new[] { frontX, rearX })
                    {
                        // Mosso orizzontale: la ruota è la media di più posizioni.
                        double acc = 0;
                        int n = 0;
                        for (double s = -blurPx / 2; s <= (blurPx / 2) + 1e-9; s += Math.Max(1, blurPx / 6), n++)
                        {
                            double d = Math.Sqrt(Math.Pow(x - (cx + s), 2) + Math.Pow(y - (bottom - radius), 2));
                            acc += d < 0.5 * radius ? 185 : d < radius ? 30 : -1;
                        }

                        double inside = 0;
                        int hits = 0;
                        for (double s = -blurPx / 2; s <= (blurPx / 2) + 1e-9; s += Math.Max(1, blurPx / 6))
                        {
                            double d = Math.Sqrt(Math.Pow(x - (cx + s), 2) + Math.Pow(y - (bottom - radius), 2));
                            if (d < radius)
                            {
                                inside += d < 0.5 * radius ? 185 : 30;
                                hits++;
                            }
                        }

                        if (hits > 0)
                        {
                            v = ((inside + ((n - hits) * v)) / n);
                        }
                    }
                }

                int o = ((y * W) + x) * 3;
                px[o] = px[o + 1] = px[o + 2] = (byte)Math.Clamp(v, 0, 255);
            }
        }

        return (px, new BoundingBox(left, top, right - left, bottom - top));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(18)]   // auto veloce: la ruota è allungata dal mosso
    public void FindsBothWheels_WithSubPixelCenter(double blur)
    {
        (byte[] px, BoundingBox box) = SideViewCar(frontX: 700.3, rearX: 330.6, radius: 38, bottom: 420, blurPx: blur);

        IReadOnlyList<WheelCandidate> wheels = WheelLocator.Locate(px, W, H, W * 3, box);

        Assert.Equal(2, wheels.Count);
        WheelCandidate front = wheels.MaxBy(w => w.Center.X)!, rear = wheels.MinBy(w => w.Center.X)!;
        // Con il mosso il centro resta entro il 6% del raggio (qui circa 2 px, cioè 2 cm su una ruota vera).
        double tol = blur > 0 ? 0.06 * 38 : 1.5;
        Assert.InRange(front.Center.X, 700.3 - tol, 700.3 + tol);
        Assert.InRange(rear.Center.X, 330.6 - tol, 330.6 + tol);
        Assert.InRange(front.Radius, 38 * 0.9, 38 * 1.1);
        Assert.InRange(front.Contact.Y, 420 - 4, 420 + 4);   // il contatto è sul fondo del riquadro, cioè a terra
    }

    [Fact]
    public void MovingCar_WheelAdvancesByTheSameAmountEveryFrame()
    {
        // Ciò che conta per la velocità: lo spostamento tra i fotogrammi, non la posizione assoluta.
        var xs = new List<double>();
        for (int i = 0; i < 4; i++)
        {
            (byte[] px, BoundingBox box) = SideViewCar(frontX: 520 + (i * 31.7), rearX: 150 + (i * 31.7), radius: 38, bottom: 420, blurPx: 18);
            xs.Add(WheelLocator.Locate(px, W, H, W * 3, box).MaxBy(w => w.Center.X)!.Center.X);
        }

        for (int i = 1; i < xs.Count; i++)
        {
            Assert.InRange(xs[i] - xs[i - 1], 31.7 - 1, 31.7 + 1);
        }
    }

    [Fact]
    public void NoWheels_WhenTheBoxHasNone()
    {
        (byte[] px, BoundingBox box) = SideViewCar(700, 330, 38, 420, wheels: false);
        Assert.Empty(WheelLocator.Locate(px, W, H, W * 3, box));
    }

    [Fact]
    public void TooSmallBox_IsSkipped()
    {
        (byte[] px, _) = SideViewCar(700, 330, 38, 420);
        Assert.Empty(WheelLocator.Locate(px, W, H, W * 3, new BoundingBox(300, 390, 60, 30)));
    }

    /// <summary>
    /// Veicolo scuro come nei video veri: carrozzeria, passaruota, pneumatico e cerchio quasi dello stesso grigio
    /// scuro, con il mosso. L'unico contorno netto della ruota è il bordo inferiore dello pneumatico sull'asfalto.
    /// </summary>
    private static (byte[] Pixels, BoundingBox Box) DarkCar(double frontX, double rearX, double radius, double bottom, double blurPx)
    {
        var px = new byte[W * H * 3];
        double left = rearX - (1.7 * radius), right = frontX + (1.8 * radius), top = bottom - (4.4 * radius);
        double sill = bottom - (0.9 * radius);   // sotto la carrozzeria, tra le ruote, si vede l'asfalto
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                double acc = 0;
                int n = 0;
                for (double s = -blurPx / 2; s <= (blurPx / 2) + 1e-9; s += Math.Max(1, blurPx / 6), n++)
                {
                    double xs = x - s;
                    double v = y > bottom - (2.5 * radius) ? 125 : 165;                          // asfalto / sfondo
                    if (xs >= left && xs <= right && y >= top && y <= sill)
                    {
                        v = 42;                                                                  // carrozzeria scura
                    }

                    foreach (double cx in new[] { frontX, rearX })
                    {
                        double d = Math.Sqrt(Math.Pow(xs - cx, 2) + Math.Pow(y - (bottom - radius), 2));
                        if (d < 1.15 * radius && y < bottom - radius && xs >= left && xs <= right)
                        {
                            v = 36;                                                              // passaruota
                        }

                        if (d < radius)
                        {
                            v = d < 0.6 * radius ? 48 : 30;                                      // cerchio scuro, pneumatico
                        }
                    }

                    acc += v;
                }

                int o = ((y * W) + x) * 3;
                px[o] = px[o + 1] = px[o + 2] = (byte)Math.Clamp(acc / n, 0, 255);
            }
        }

        return (px, new BoundingBox(left, top, right - left, bottom - top));
    }

    [Fact]
    public void DarkWheels_AreFoundByTheirOutline()
    {
        (byte[] px, BoundingBox box) = DarkCar(frontX: 640.4, rearX: 290.8, radius: 36, bottom: 430, blurPx: 16);

        IReadOnlyList<WheelCandidate> found = WheelLocator.LocateCandidates(px, W, H, W * 3, box);

        // Tra le candidate ci sono entrambe le ruote, con il centro e il punto a terra giusti (il raggio conta poco:
        // per la velocità serve il punto a terra).
        foreach (double x in new[] { 640.4, 290.8 })
        {
            WheelCandidate? w = found.Where(c => Math.Abs(c.Center.X - x) < 0.15 * 36).MaxBy(c => c.Score);
            Assert.NotNull(w);
            Assert.InRange(w.Contact.Y, 430 - (0.12 * 36), 430 + (0.12 * 36));
        }

        // E sono le due più sicure.
        Assert.All(found.Take(2), w => Assert.True(Math.Min(Math.Abs(w.Center.X - 640.4), Math.Abs(w.Center.X - 290.8)) < 0.15 * 36, $"{w}"));
    }

    [Fact]
    public void Candidates_AreFoundInTheUsualCarToo()
    {
        (byte[] px, BoundingBox box) = SideViewCar(frontX: 700.3, rearX: 330.6, radius: 38, bottom: 420, blurPx: 18);
        IReadOnlyList<WheelCandidate> found = WheelLocator.LocateCandidates(px, W, H, W * 3, box);
        Assert.Contains(found, w => Math.Abs(w.Center.X - 700.3) < 0.06 * 38);
        Assert.Contains(found, w => Math.Abs(w.Center.X - 330.6) < 0.06 * 38);
        Assert.True(found.SequenceEqual(found.OrderByDescending(w => w.Score)));
    }
}
