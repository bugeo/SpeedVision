using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Tracking;

namespace TrafficSpeedAnalyzer.Core.Privacy;

/// <summary>
/// Zone da pixellare nelle immagini anonimizzate: l'intero riquadro di ogni veicolo tracciato
/// (con un piccolo margine), così targhe e persone a bordo non restano visibili, e testa e spalle
/// delle persone riconosciute nell'immagine (pedoni, ciclisti, motociclisti).
/// </summary>
public static class PrivacyMasking
{
    /// <summary>Margine aggiunto al riquadro per assorbire l'imprecisione del rilevamento.</summary>
    public const double Margin = 0.08;

    /// <summary>Distanza massima, in fotogrammi, per interpolare il riquadro tra due rilevamenti.</summary>
    public const int MaxInterpolationGap = 15;

    /// <summary>Zona da pixellare per un veicolo: tutto il riquadro, allargato del margine e limitato all'immagine.</summary>
    public static IReadOnlyList<BoundingBox> RegionsFor(BoundingBox box, double imageWidth, double imageHeight)
    {
        double mx = box.Width * Margin;
        double my = box.Height * Margin;
        BoundingBox r = Clamp(new BoundingBox(box.X - mx, box.Y - my, box.Width + (2 * mx), box.Height + (2 * my)), imageWidth, imageHeight);
        return r.Width >= 1 && r.Height >= 1 ? [r] : [];
    }

    /// <summary>Parte alta del riquadro di una persona da coprire (testa e spalle), in frazione dell'altezza.</summary>
    public const double HeadFraction = 0.35;

    /// <summary>
    /// Zona da pixellare per una persona riconosciuta: testa e spalle, cioè la parte alta del riquadro, allargata
    /// a lato e verso l'alto. Vale anche se la persona è piegata o seduta (bici, moto): la testa resta in alto.
    /// Se il riquadro è basso e largo (persona tagliata dal bordo inferiore) si copre di più, fino a tutto il riquadro.
    /// </summary>
    public static IReadOnlyList<BoundingBox> FaceRegionsFor(BoundingBox person, double imageWidth, double imageHeight)
    {
        if (person.Width <= 0 || person.Height <= 0)
        {
            return [];
        }

        double h = Math.Min(person.Height, Math.Max(HeadFraction * person.Height, 0.9 * person.Width));
        double mx = 0.15 * person.Width;
        double top = 0.1 * h;
        BoundingBox r = Clamp(new BoundingBox(person.X - mx, person.Y - top, person.Width + (2 * mx), h + top), imageWidth, imageHeight);
        return r.Width >= 1 && r.Height >= 1 ? [r] : [];
    }

    /// <summary>
    /// Zona per un veicolo misurato a mano: c'è solo il punto a terra, non il riquadro.
    /// Si copre un rettangolo della taglia tipica di un'auto attorno al punto.
    /// </summary>
    public static BoundingBox RegionAroundPoint(double x, double y, double imageWidth, double imageHeight)
    {
        double w = 0.32 * imageHeight, h = 0.2 * imageHeight;
        return Clamp(new BoundingBox(x - (w / 2), y - h + (0.03 * imageHeight), w, h), imageWidth, imageHeight);
    }

    /// <summary>Riquadro del veicolo nel fotogramma indicato; interpola tra due rilevamenti vicini.</summary>
    public static BoundingBox? BoxAt(IReadOnlyList<TrackPoint> points, long frameIndex)
    {
        TrackPoint? before = null, after = null;
        foreach (TrackPoint p in points)
        {
            if (p.FrameIndex == frameIndex)
            {
                return p.Box;
            }

            if (p.FrameIndex < frameIndex && (before is null || p.FrameIndex > before.FrameIndex))
            {
                before = p;
            }
            else if (p.FrameIndex > frameIndex && (after is null || p.FrameIndex < after.FrameIndex))
            {
                after = p;
            }
        }

        if (before is null || after is null || after.FrameIndex - before.FrameIndex > MaxInterpolationGap)
        {
            return null;
        }

        double t = (frameIndex - before.FrameIndex) / (double)(after.FrameIndex - before.FrameIndex);
        BoundingBox a = before.Box, b = after.Box;
        return new BoundingBox(
            a.X + ((b.X - a.X) * t),
            a.Y + ((b.Y - a.Y) * t),
            a.Width + ((b.Width - a.Width) * t),
            a.Height + ((b.Height - a.Height) * t));
    }

    /// <summary>
    /// Pixella una zona di un'immagine (pixel interlacciati, es. BGR) sostituendo ogni blocco con il suo colore medio.
    /// I blocchi sono abbastanza grandi (almeno 1/8 della zona) da rendere illeggibili targhe e volti.
    /// </summary>
    public static void Pixelate(byte[] pixels, int width, int height, int stride, int bytesPerPixel, BoundingBox region)
    {
        int x0 = Math.Clamp((int)Math.Floor(region.X), 0, width);
        int y0 = Math.Clamp((int)Math.Floor(region.Y), 0, height);
        int x1 = Math.Clamp((int)Math.Ceiling(region.X + region.Width), 0, width);
        int y1 = Math.Clamp((int)Math.Ceiling(region.Y + region.Height), 0, height);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        int block = BlockSize(x1 - x0, y1 - y0, height);
        var sum = new long[bytesPerPixel];
        for (int by = y0; by < y1; by += block)
        {
            for (int bx = x0; bx < x1; bx += block)
            {
                int ex = Math.Min(bx + block, x1), ey = Math.Min(by + block, y1);
                Array.Clear(sum);
                for (int yy = by; yy < ey; yy++)
                {
                    int row = yy * stride;
                    for (int xx = bx; xx < ex; xx++)
                    {
                        int o = row + (xx * bytesPerPixel);
                        for (int c = 0; c < bytesPerPixel; c++)
                        {
                            sum[c] += pixels[o + c];
                        }
                    }
                }

                int n = (ex - bx) * (ey - by);
                for (int yy = by; yy < ey; yy++)
                {
                    int row = yy * stride;
                    for (int xx = bx; xx < ex; xx++)
                    {
                        int o = row + (xx * bytesPerPixel);
                        for (int c = 0; c < bytesPerPixel; c++)
                        {
                            pixels[o + c] = (byte)(sum[c] / n);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Lato del blocco: almeno 1/8 del lato minore della zona e almeno 1/90 dell'altezza dell'immagine.</summary>
    public static int BlockSize(int regionWidth, int regionHeight, int imageHeight) =>
        Math.Max(Math.Max(6, imageHeight / 90), Math.Min(regionWidth, regionHeight) / 8);

    private static BoundingBox Clamp(BoundingBox r, double w, double h)
    {
        double x0 = Math.Clamp(r.X, 0, w), y0 = Math.Clamp(r.Y, 0, h);
        double x1 = Math.Clamp(r.X + r.Width, 0, w), y1 = Math.Clamp(r.Y + r.Height, 0, h);
        return new BoundingBox(x0, y0, x1 - x0, y1 - y0);
    }
}
