using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Detection;

/// <summary>
/// Seconda ricerca delle ruote, basata sui contorni invece che sui grigi: una ruota è un'ellisse (un cerchio
/// visto un po' di sbieco) il cui bordo inferiore separa lo pneumatico dall'asfalto. Lungo quel bordo la
/// variazione di luminosità è perpendicolare al contorno, qualunque sia il colore: funziona anche quando
/// pneumatico e passaruota sono scuri allo stesso modo o quando il cerchio è scuro, casi in cui il confronto
/// tra grigi di <see cref="WheelLocator.Locate"/> non trova la ruota.
/// Non decide quale sia la ruota anteriore: restituisce i cerchi più probabili; la scelta si fa sull'intera
/// traccia (<see cref="Tracking.WheelTrajectory"/>), dove una ruota vera si muove con il veicolo.
/// </summary>
internal static class WheelOutlineFinder
{
    /// <summary>Raggio verticale della ruota rispetto all'altezza del riquadro, dalle auto lontane a quelle vicine.</summary>
    public const double MinRadiusFraction = 0.08, MaxRadiusFraction = 0.40;

    /// <summary>Punteggio minimo (rapporto tra il contrasto lungo il contorno e quello tipico della zona).</summary>
    public const double MinScore = 0.15;

    /// <summary>Altezza del riquadro, in pixel, su cui si fa la ricerca (le immagini più grandi si riducono).</summary>
    private const double WorkHeight = 110;

    private const int Angles = 32;
    private const int RadiusSteps = 18;
    private static readonly double[] Aspects = [1.0, 0.85, 0.7];

    /// <summary>Quota del centro: un raggio sopra il fondo del riquadro, più o meno questa frazione del raggio.</summary>
    private static readonly double[] Lifts = [-0.1, 0, 0.1, 0.2, 0.3];

    /// <summary>Peso dei lati dell'ellisse rispetto al bordo inferiore (sui lati il mosso confonde il contorno).</summary>
    private const double SideWeight = 0.3;

    /// <summary>Confine tra bordo inferiore e lati: seno dell'angolo (0 = di lato, 1 = in basso).</summary>
    private const double LowerArc = 0.45;

    private static readonly double[] Cos = Enumerable.Range(0, Angles).Select(i => Math.Cos(2 * Math.PI * i / Angles)).ToArray();
    private static readonly double[] Sin = Enumerable.Range(0, Angles).Select(i => Math.Sin(2 * Math.PI * i / Angles)).ToArray();

    /// <summary>Le ellissi più probabili nel riquadro (al massimo <paramref name="max"/>, ben distanziate).</summary>
    public static List<WheelCandidate> Find(byte[] bgr, int width, int height, int stride, BoundingBox box, int max = 5)
    {
        var result = new List<WheelCandidate>();
        if (box.Height < WheelLocator.MinBoxHeight || box.Width < box.Height * MinRadiusFraction * 2)
        {
            return result;
        }

        double rMax = MaxRadiusFraction * box.Height;
        int x0 = Math.Clamp((int)Math.Floor(box.X - (0.3 * rMax)), 0, width - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(box.Right + (0.3 * rMax)), x0 + 1, width);
        int y0 = Math.Clamp((int)Math.Floor(box.Bottom - (2.6 * rMax)), 0, height - 1);
        int y1 = Math.Clamp((int)Math.Ceiling(box.Bottom + (0.4 * rMax)), y0 + 1, height);
        int shrink = Math.Max(1, (int)Math.Round(box.Height / WorkHeight));
        var g = new Gradient(bgr, stride, x0, y0, x1 - x0, y1 - y0, shrink);
        if (g.Typical <= 0)
        {
            return result;
        }

        var all = new List<(double Score, double Cx, double Cy, double Ry, double Rx)>();
        Span<double> nx = stackalloc double[Angles];
        Span<double> ny = stackalloc double[Angles];
        for (int ri = 0; ri < RadiusSteps; ri++)
        {
            double ry = box.Height * MinRadiusFraction * Math.Pow(MaxRadiusFraction / MinRadiusFraction, ri / (RadiusSteps - 1.0));
            foreach (double aspect in Aspects)
            {
                double rx = ry * aspect;
                for (int a = 0; a < Angles; a++)
                {
                    // Normale all'ellisse nel punto di angolo a.
                    double ux = Cos[a] / rx, uy = Sin[a] / ry, n = Math.Sqrt((ux * ux) + (uy * uy));
                    nx[a] = ux / n;
                    ny[a] = uy / n;
                }

                double step = Math.Max(shrink, rx / 6);
                foreach (double lift in Lifts)
                {
                    double cy = box.Bottom - ry - (lift * ry);
                    for (double cx = box.X + (0.5 * rx); cx <= box.Right - (0.5 * rx); cx += step)
                    {
                        double s = g.Score(cx, cy, rx, ry, nx, ny);
                        if (s >= MinScore)
                        {
                            all.Add((s, cx, cy, ry, rx));
                        }
                    }
                }
            }
        }

        foreach (var c in all.OrderByDescending(c => c.Score))
        {
            if (result.Count >= max)
            {
                break;
            }

            if (result.All(p => Math.Abs(p.Center.X - c.Cx) > 0.8 * Math.Max(p.Radius, c.Ry)))
            {
                result.Add(new WheelCandidate(new Point2(Refine(g, c.Cx, c.Cy, c.Rx, c.Ry, Math.Max(shrink, c.Rx / 6)), c.Cy), c.Ry, c.Score));
            }
        }

        return result;
    }

    /// <summary>Centro orizzontale a meno di un passo: vertice della parabola per i tre punteggi vicini.</summary>
    private static double Refine(Gradient g, double cx, double cy, double rx, double ry, double step)
    {
        Span<double> nx = stackalloc double[Angles];
        Span<double> ny = stackalloc double[Angles];
        for (int a = 0; a < Angles; a++)
        {
            double ux = Cos[a] / rx, uy = Sin[a] / ry, n = Math.Sqrt((ux * ux) + (uy * uy));
            nx[a] = ux / n;
            ny[a] = uy / n;
        }

        double h = step / 2;
        double l = g.Score(cx - h, cy, rx, ry, nx, ny), m = g.Score(cx, cy, rx, ry, nx, ny), r = g.Score(cx + h, cy, rx, ry, nx, ny);
        double den = l - (2 * m) + r;
        if (den >= 0 || double.IsInfinity(l) || double.IsInfinity(r))
        {
            return cx;
        }

        return cx + (Math.Clamp(0.5 * (l - r) / den, -1, 1) * h);
    }

    /// <summary>Gradiente della luminosità di una porzione dell'immagine (ridotta e appena sfocata).</summary>
    private sealed class Gradient
    {
        private readonly float[] _gx, _gy;
        private readonly int _x0, _y0, _w, _h, _s;

        /// <summary>Intensità tipica dei contorni nella zona (90° percentile): rende il punteggio indipendente da luce e contrasto.</summary>
        public double Typical { get; }

        public Gradient(byte[] bgr, int stride, int x0, int y0, int w, int h, int shrink)
        {
            _x0 = x0;
            _y0 = y0;
            _s = shrink;
            _w = Math.Max(3, w / shrink);
            _h = Math.Max(3, h / shrink);
            var gray = new float[_w * _h];
            float norm = 1f / (shrink * shrink);
            for (int y = 0; y < _h; y++)
            {
                for (int x = 0; x < _w; x++)
                {
                    float sum = 0;
                    for (int yy = 0; yy < shrink; yy++)
                    {
                        int row = ((y0 + (y * shrink) + yy) * stride) + ((x0 + (x * shrink)) * 3);
                        for (int xx = 0; xx < shrink; xx++)
                        {
                            int o = row + (xx * 3);
                            sum += (0.114f * bgr[o]) + (0.587f * bgr[o + 1]) + (0.299f * bgr[o + 2]);
                        }
                    }

                    gray[(y * _w) + x] = sum * norm;
                }
            }

            gray = Smooth(gray, _w, _h);
            _gx = new float[gray.Length];
            _gy = new float[gray.Length];
            var mag = new float[gray.Length];
            for (int y = 0; y < _h; y++)
            {
                int ym = Math.Max(0, y - 1), yp = Math.Min(_h - 1, y + 1);
                for (int x = 0; x < _w; x++)
                {
                    int xm = Math.Max(0, x - 1), xp = Math.Min(_w - 1, x + 1);
                    float P(int xx, int yy) => gray[(yy * _w) + xx];
                    // Sobel, diviso 8: variazione per pixel.
                    float gx = ((P(xp, ym) + (2 * P(xp, y)) + P(xp, yp)) - (P(xm, ym) + (2 * P(xm, y)) + P(xm, yp))) / 8f;
                    float gy = ((P(xm, yp) + (2 * P(x, yp)) + P(xp, yp)) - (P(xm, ym) + (2 * P(x, ym)) + P(xp, ym))) / 8f;
                    int i = (y * _w) + x;
                    _gx[i] = gx;
                    _gy[i] = gy;
                    mag[i] = MathF.Sqrt((gx * gx) + (gy * gy));
                }
            }

            Array.Sort(mag);
            Typical = mag[(int)(0.9 * (mag.Length - 1))] + 1.0;
        }

        /// <summary>
        /// Quanto il contorno dell'ellisse segue un bordo: per ogni punto, la variazione perpendicolare al
        /// contorno meno quella lungo il contorno (un bordo dritto che taglia l'ellisse non conta). Il bordo
        /// inferiore pesa di più dei lati. Meno infinito se l'ellisse esce troppo dalla porzione.
        /// </summary>
        public double Score(double cx, double cy, double rx, double ry, ReadOnlySpan<double> nx, ReadOnlySpan<double> ny)
        {
            double lower = 0, side = 0;
            int nLower = 0, nSide = 0, outside = 0;
            for (int a = 0; a < Angles; a++)
            {
                double x = ((cx + (rx * Cos[a]) - _x0) / _s) - 0.5 + (0.5 / _s);
                double y = ((cy + (ry * Sin[a]) - _y0) / _s) - 0.5 + (0.5 / _s);
                if (x < 0 || y < 0 || x > _w - 1.001 || y > _h - 1.001)
                {
                    outside++;
                    continue;
                }

                int ix = (int)x, iy = (int)y;
                double fx = x - ix, fy = y - iy;
                int i0 = (iy * _w) + ix;
                double Sample(float[] p) => ((1 - fy) * (((1 - fx) * p[i0]) + (fx * p[i0 + 1])))
                    + (fy * (((1 - fx) * p[i0 + _w]) + (fx * p[i0 + _w + 1])));
                double gx = Sample(_gx), gy = Sample(_gy);
                double v = Math.Abs((gx * nx[a]) + (gy * ny[a])) - Math.Abs((-gx * ny[a]) + (gy * nx[a]));
                if (Sin[a] > LowerArc)
                {
                    lower += v;
                    nLower++;
                }
                else if (Math.Abs(Sin[a]) <= LowerArc)
                {
                    side += v;
                    nSide++;
                }
            }

            if (outside > Angles / 4 || nLower == 0)
            {
                return double.NegativeInfinity;
            }

            return ((lower / nLower) + (SideWeight * (nSide == 0 ? 0 : side / nSide))) / Typical;
        }

        /// <summary>Sfocatura leggera (media 3×3) per togliere il rumore prima delle derivate.</summary>
        private static float[] Smooth(float[] src, int w, int h)
        {
            var dst = new float[src.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    int n = 0;
                    for (int yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
                    {
                        for (int xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++, n++)
                        {
                            s += src[(yy * w) + xx];
                        }
                    }

                    dst[(y * w) + x] = s / n;
                }
            }

            return dst;
        }
    }
}
