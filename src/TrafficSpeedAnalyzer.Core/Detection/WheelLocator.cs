using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Detection;

/// <summary>Ruota trovata nel riquadro di un veicolo (coordinate dell'immagine).</summary>
/// <param name="Score">Contrasto tra pneumatico scuro, cerchio e dintorni (livelli di grigio, 0–255): più alto = più sicuro.</param>
public sealed record WheelCandidate(Point2 Center, double Radius, double Score)
{
    /// <summary>Punto in cui la ruota tocca l'asfalto: sta sul piano della strada, come le linee A e B.</summary>
    public Point2 Contact => new(Center.X, Center.Y + Radius);

    public WheelCandidate Scale(double sx, double sy) =>
        new(new Point2(Center.X * sx, Center.Y * sy), Radius * Math.Sqrt(sx * sy), Score);
}

/// <summary>
/// Cerca le ruote nella parte bassa del riquadro di un veicolo ripreso di lato, senza modelli addestrati:
/// una ruota è un disco con lo pneumatico scuro, il cerchio più chiaro al centro e intorno l'asfalto (sotto)
/// o la carrozzeria (sopra), che tocca il fondo del riquadro. Si cercano quindi cerchi con il centro a un
/// raggio di distanza dal fondo del riquadro, di raggio proporzionato all'altezza del veicolo; il mosso
/// orizzontale allunga la ruota ma ne lascia il centro al suo posto.
/// Il punto di contatto a terra della ruota anteriore è un riferimento migliore dello spigolo del riquadro:
/// sta sulla strada (niente parallasse rispetto alle linee) ed è sempre lo stesso punto del veicolo, mentre
/// lo spigolo è il paraurti, sollevato da terra, e il bordo del riquadro cambia con l'angolo di ripresa.
/// </summary>
public static class WheelLocator
{
    /// <summary>Contrasto minimo per accettare una ruota.</summary>
    public const double MinScore = 30;

    /// <summary>Altezza minima del riquadro (pixel) sotto la quale le ruote sono troppo piccole per essere trovate.</summary>
    public const double MinBoxHeight = 48;

    /// <summary>Raggio della ruota rispetto all'altezza del riquadro: dalle auto basse e lontane ai furgoni alti.</summary>
    public const double MinRadiusFraction = 0.10, MaxRadiusFraction = 0.27;

    private const int Angles = 32;
    private static readonly double[] Cos = Enumerable.Range(0, Angles).Select(i => Math.Cos(2 * Math.PI * i / Angles)).ToArray();
    private static readonly double[] Sin = Enumerable.Range(0, Angles).Select(i => Math.Sin(2 * Math.PI * i / Angles)).ToArray();

    /// <summary>
    /// Ruote nel riquadro (al massimo due, la più sicura per prima), con il centro raffinato a meno di un pixel.
    /// </summary>
    /// <param name="bgr">Immagine a 3 byte per pixel (BGR).</param>
    public static IReadOnlyList<WheelCandidate> Locate(byte[] bgr, int width, int height, int stride, BoundingBox box, int maxWheels = 2)
    {
        if (box.Height < MinBoxHeight || box.Width < 1.5 * box.Height * MinRadiusFraction * 2)
        {
            return [];
        }

        // Grigio della parte bassa del riquadro (con un margine sotto e ai lati). La ricerca a passi larghi
        // si fa su una copia ridotta (riquadro alto circa 160 pixel), il raffinamento a piena risoluzione.
        double rMax = MaxRadiusFraction * box.Height;
        int gx0 = Math.Clamp((int)Math.Floor(box.X - (0.3 * rMax)), 0, width - 1);
        int gx1 = Math.Clamp((int)Math.Ceiling(box.Right + (0.3 * rMax)), gx0 + 1, width);
        int gy0 = Math.Clamp((int)Math.Floor(box.Bottom - (2.5 * rMax)), 0, height - 1);
        int gy1 = Math.Clamp((int)Math.Ceiling(box.Bottom + (0.4 * rMax)), gy0 + 1, height);
        int shrink = Math.Max(1, (int)(box.Height / 160));
        var g = new GrayPatch(bgr, stride, gx0, gy0, gx1 - gx0, gy1 - gy0, shrink, blur: 1);

        // Ricerca a passi larghi: raggio, quota del centro rispetto al fondo, posizione orizzontale.
        var coarse = new List<WheelCandidate>();
        for (int ri = 0; ri < 9; ri++)
        {
            double r = box.Height * (MinRadiusFraction + ((MaxRadiusFraction - MinRadiusFraction) * ri / 8.0));
            double step = Math.Max(1.5, r / 5);
            foreach (double dy in (ReadOnlySpan<double>)[-0.1, 0, 0.1])
            {
                double cy = box.Bottom - (r * (1 + dy));
                for (double cx = box.X + (0.7 * r); cx <= box.Right - (0.7 * r); cx += step)
                {
                    double s = Score(g, cx, cy, r);
                    if (s > MinScore * 0.6)
                    {
                        coarse.Add(new WheelCandidate(new Point2(cx, cy), r, s));
                    }
                }
            }
        }

        // Le migliori, lontane tra loro (due ruote dello stesso veicolo distano diversi raggi), poi raffinate.
        var picked = new List<WheelCandidate>();
        var tried = new List<WheelCandidate>();
        foreach (WheelCandidate c in coarse.OrderByDescending(c => c.Score))
        {
            if (tried.Count >= 2 * maxWheels + 2)
            {
                break;   // le altre sono più deboli di quelle già scartate
            }

            if (picked.All(p => Math.Abs(p.Center.X - c.Center.X) > 2.2 * Math.Max(p.Radius, c.Radius))
                && tried.All(p => Math.Abs(p.Center.X - c.Center.X) > p.Radius))
            {
                tried.Add(c);
                // Raffinamento a piena risoluzione in una finestra attorno alla ruota.
                int fx0 = Math.Clamp((int)(c.Center.X - (1.6 * c.Radius)), 0, width - 1);
                int fx1 = Math.Clamp((int)(c.Center.X + (1.6 * c.Radius)) + 1, fx0 + 1, width);
                int fy0 = Math.Clamp((int)(c.Center.Y - (1.6 * c.Radius)), 0, height - 1);
                int fy1 = Math.Clamp((int)(c.Center.Y + (1.6 * c.Radius)) + 1, fy0 + 1, height);
                var fine = new GrayPatch(bgr, stride, fx0, fy0, fx1 - fx0, fy1 - fy0, 1, blur: Math.Max(1, (int)(c.Radius / 40)));
                WheelCandidate refined = Refine(fine, c);
                if (refined.Score >= MinScore)
                {
                    picked.Add(refined);
                }
            }

            if (picked.Count >= maxWheels)
            {
                break;
            }
        }

        return picked;
    }

    /// <summary>Raffinamento locale: passo di un pixel (e 2% del raggio), poi vertice della parabola in orizzontale.</summary>
    private static WheelCandidate Refine(GrayPatch g, WheelCandidate c)
    {
        WheelCandidate best = c;
        double step = Math.Max(1.5, c.Radius / 5);
        for (int pass = 0; pass < 3; pass++)
        {
            WheelCandidate start = best;
            // Passi sempre più fini: posizione (px), raggio e quota (frazione del raggio).
            (double span, double dStep, double rStep, double yStep) = pass switch
            {
                0 => (step, Math.Max(1, step / 6), 0.05, 0.06),
                1 => (Math.Max(1, step / 6), 0.5, 0.02, 0.025),
                _ => (0.5, 0.25, 0.01, 0.0125),
            };
            for (double dr = -rStep; dr <= rStep + 1e-9; dr += rStep)
            {
                double r = start.Radius * (1 + dr);
                for (double dy = -yStep * r; dy <= (yStep * r) + 1e-9; dy += yStep * r)
                {
                    for (double dx = -span; dx <= span + 1e-9; dx += dStep)
                    {
                        var p = new Point2(start.Center.X + dx, start.Center.Y + dy);
                        double s = Score(g, p.X, p.Y, r);
                        if (s > best.Score)
                        {
                            best = new WheelCandidate(p, r, s);
                        }
                    }
                }
            }
        }

        // Centro orizzontale: metà del tratto in cui il punteggio resta vicino al massimo. Con il mosso il
        // punteggio è quasi piatto per qualche pixel (la ruota è «allungata»): il massimo può cadere in un punto
        // qualsiasi del tratto, la sua metà invece è il centro vero della ruota.
        double tolerance = Math.Max(1.5, 0.05 * best.Score);
        double reach = 0.6 * best.Radius;
        double lo = 0, hi = 0;
        for (double d = 0.25; d <= reach; d += 0.25)
        {
            if (Score(g, best.Center.X - d, best.Center.Y, best.Radius) < best.Score - tolerance)
            {
                break;
            }

            lo = d;
        }

        for (double d = 0.25; d <= reach; d += 0.25)
        {
            if (Score(g, best.Center.X + d, best.Center.Y, best.Radius) < best.Score - tolerance)
            {
                break;
            }

            hi = d;
        }

        return best with { Center = new Point2(best.Center.X + ((hi - lo) / 2), best.Center.Y) };
    }

    /// <summary>
    /// Punteggio di un cerchio: dintorni e cerchio più chiari dello pneumatico, pneumatico uniforme.
    /// Si usano medie «tagliate» (senza gli estremi) per non farsi ingannare da riflessi e ombre.
    /// </summary>
    internal static double Score(GrayPatch g, double cx, double cy, double r)
    {
        Span<double> tire = stackalloc double[Angles];
        Span<double> outer = stackalloc double[Angles];
        Span<double> inner = stackalloc double[Angles];
        if (!g.Ring(cx, cy, 0.84 * r, tire) || !g.Ring(cx, cy, 1.2 * r, outer) || !g.Ring(cx, cy, 0.4 * r, inner))
        {
            return double.NegativeInfinity;
        }

        double t = TrimmedMean(tire), o = TrimmedMean(outer), i = TrimmedMean(inner);
        double spread = 0;
        foreach (double v in tire)
        {
            spread += Math.Abs(v - t);
        }

        spread /= Angles;
        return (o - t) + (0.8 * (i - t)) - (0.8 * spread);
    }

    private static double TrimmedMean(Span<double> v)
    {
        v.Sort();
        int k = v.Length / 4;
        double sum = 0;
        for (int i = k; i < v.Length - k; i++)
        {
            sum += v[i];
        }

        return sum / (v.Length - (2 * k));
    }

    /// <summary>Porzione dell'immagine in livelli di grigio, sfocata, con lettura bilineare.</summary>
    internal sealed class GrayPatch
    {
        private readonly float[] _px;
        private readonly int _x0, _y0, _w, _h, _s;

        /// <param name="shrink">Riduzione (media di shrink×shrink pixel), per cercare in fretta nei riquadri grandi.</param>
        public GrayPatch(byte[] bgr, int stride, int x0, int y0, int w, int h, int shrink, int blur)
        {
            _x0 = x0;
            _y0 = y0;
            _s = Math.Max(1, shrink);
            _w = Math.Max(2, w / _s);
            _h = Math.Max(2, h / _s);
            var raw = new float[_w * _h];
            float norm = 1f / (_s * _s);
            for (int y = 0; y < _h; y++)
            {
                for (int x = 0; x < _w; x++)
                {
                    float sum = 0;
                    for (int yy = 0; yy < _s; yy++)
                    {
                        int row = ((y0 + (y * _s) + yy) * stride) + ((x0 + (x * _s)) * 3);
                        for (int xx = 0; xx < _s; xx++)
                        {
                            int o = row + (xx * 3);
                            sum += (0.114f * bgr[o]) + (0.587f * bgr[o + 1]) + (0.299f * bgr[o + 2]);
                        }
                    }

                    raw[(y * _w) + x] = sum * norm;
                }
            }

            _px = BoxBlur(BoxBlur(raw, _w, _h, blur), _w, _h, blur);
        }

        /// <summary>Valori lungo una circonferenza; false se esce dalla porzione per più di un quarto.</summary>
        public bool Ring(double cx, double cy, double r, Span<double> values)
        {
            int outside = 0;
            for (int a = 0; a < values.Length; a++)
            {
                double x = ((cx + (r * Cos[a]) - _x0) / _s) - 0.5 + (0.5 / _s), y = ((cy + (r * Sin[a]) - _y0) / _s) - 0.5 + (0.5 / _s);
                if (x < 0 || y < 0 || x > _w - 1.001 || y > _h - 1.001)
                {
                    outside++;
                    x = Math.Clamp(x, 0, _w - 1.001);
                    y = Math.Clamp(y, 0, _h - 1.001);
                }

                int ix = (int)x, iy = (int)y;
                double fx = x - ix, fy = y - iy;
                int i0 = (iy * _w) + ix;
                values[a] = ((1 - fy) * (((1 - fx) * _px[i0]) + (fx * _px[i0 + 1])))
                    + (fy * (((1 - fx) * _px[i0 + _w]) + (fx * _px[i0 + _w + 1])));
            }

            return outside <= values.Length / 4;
        }

        private static float[] BoxBlur(float[] src, int w, int h, int r)
        {
            var tmp = new float[src.Length];
            var dst = new float[src.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    int n = 0;
                    for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++, n++)
                    {
                        s += src[(y * w) + k];
                    }

                    tmp[(y * w) + x] = s / n;
                }
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    int n = 0;
                    for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++, n++)
                    {
                        s += tmp[(k * w) + x];
                    }

                    dst[(y * w) + x] = s / n;
                }
            }

            return dst;
        }
    }
}
