using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
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

    [Fact]
    public void Contact_UsesGivenGroundPoint_AndScalesIt()
    {
        var fromCircle = new WheelCandidate(new Point2(100, 200), 30, 80);
        Assert.Equal(new Point2(100, 230), fromCircle.Contact);

        // La rete dà direttamente il punto a terra: vale quello, non centro + raggio.
        var fromNetwork = new WheelCandidate(new Point2(100, 200), 30, 90, new Point2(103, 236));
        Assert.Equal(new Point2(103, 236), fromNetwork.Contact);
        Assert.Equal(new Point2(206, 472), fromNetwork.Scale(2, 2).Contact);
    }
}
