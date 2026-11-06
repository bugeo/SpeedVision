using System.Globalization;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Video;

/// <summary>
/// Movimento di un fotogramma rispetto al fotogramma di riferimento, ricavato dai campi di
/// movimento locali del filtro vidstabdetect: mediana dei vettori (robusta rispetto ai veicoli
/// che si muovono nella scena).
/// </summary>
public sealed record StabFrameMotion(int Frame, int Fields, double Dx, double Dy)
{
    public double Magnitude => Math.Sqrt(Dx * Dx + Dy * Dy);
}

/// <summary>Lettura dei file di trasformazioni (.trf, formato testo "VID.STAB 1") di vidstabdetect.</summary>
public static class VidStabFile
{
    /// <summary>
    /// Una voce per ogni riga "Frame N (List M [(LM vx vy x y size contrast match),…])".
    /// I fotogrammi senza campi utilizzabili hanno spostamento NaN.
    /// </summary>
    public static List<StabFrameMotion> Parse(string text)
    {
        var result = new List<StabFrameMotion>();
        var dx = new List<double>();
        var dy = new List<double>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("Frame ", StringComparison.Ordinal))
            {
                continue;
            }

            int sp = line.IndexOf(' ', 6);
            if (sp < 0 || !int.TryParse(line.AsSpan(6, sp - 6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame))
            {
                continue;
            }

            dx.Clear();
            dy.Clear();
            int pos = 0;
            while ((pos = line.IndexOf("(LM ", pos, StringComparison.Ordinal)) >= 0)
            {
                int end = line.IndexOf(')', pos);
                if (end < 0)
                {
                    break;
                }

                string[] parts = line[(pos + 4)..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double vx)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double vy))
                {
                    dx.Add(vx);
                    dy.Add(vy);
                }

                pos = end + 1;
            }

            result.Add(dx.Count == 0
                ? new StabFrameMotion(frame, 0, double.NaN, double.NaN)
                : new StabFrameMotion(frame, dx.Count, Median(dx), Median(dy)));
        }

        return result;
    }

    internal static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : 0.5 * (values[(n / 2) - 1] + values[n / 2]);
    }
}
