using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Detection;

/// <summary>
/// Rileva in modo economico se nella zona analizzata è cambiato qualcosa rispetto al fotogramma
/// precedente, per saltare il modello nei tratti di strada vuota.
/// </summary>
public sealed class MotionGate(double changedFraction = 0.003, int pixelThreshold = 18, int sampleStep = 4)
{
    private byte[]? _previous;
    private Roi _previousRoi;

    /// <summary>Frazione di campioni cambiati nell'ultimo confronto.</summary>
    public double LastChangedFraction { get; private set; }

    public bool HasMotion(byte[] bgr, int width, int height, BoundingBox? region)
    {
        Roi roi = Roi.From(region, width, height);
        int cols = Math.Max(1, roi.Width / sampleStep);
        int rows = Math.Max(1, roi.Height / sampleStep);
        var gray = new byte[cols * rows];
        int stride = width * 3;
        for (int r = 0; r < rows; r++)
        {
            int y = roi.Y + r * sampleStep;
            int rowStart = y * stride;
            for (int c = 0; c < cols; c++)
            {
                int p = rowStart + (roi.X + c * sampleStep) * 3;
                gray[r * cols + c] = (byte)((bgr[p] * 29 + bgr[p + 1] * 150 + bgr[p + 2] * 77) >> 8);
            }
        }

        byte[]? prev = _previous;
        bool sameRoi = prev is not null && prev.Length == gray.Length && _previousRoi == roi;
        _previous = gray;
        _previousRoi = roi;
        if (!sameRoi)
        {
            LastChangedFraction = 1;
            return true;
        }

        int changed = 0;
        for (int i = 0; i < gray.Length; i++)
        {
            if (Math.Abs(gray[i] - prev![i]) > pixelThreshold)
            {
                changed++;
            }
        }

        LastChangedFraction = changed / (double)gray.Length;
        return LastChangedFraction >= changedFraction;
    }

    public void Reset() => _previous = null;
}

/// <summary>Zona del fotogramma da analizzare attorno alle linee di rilevamento.</summary>
public static class AnalysisRegion
{
    /// <summary>
    /// Rettangolo che contiene le due linee, allargato in ogni direzione di <paramref name="marginFraction"/>
    /// della sua dimensione maggiore, così i veicoli vicini alle linee sono interi. Coordinate del video.
    /// </summary>
    public static BoundingBox FromCalibration(CalibrationSettings calibration, double videoWidth, double videoHeight, double marginFraction = 0.6)
    {
        Point2[] pts = [calibration.LineA.Start, calibration.LineA.End, calibration.LineB.Start, calibration.LineB.End];
        double minX = pts.Min(p => p.X);
        double maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y);
        double maxY = pts.Max(p => p.Y);
        double margin = Math.Max(maxX - minX, maxY - minY) * marginFraction;
        double x1 = Math.Clamp(minX - margin, 0, videoWidth);
        double y1 = Math.Clamp(minY - margin, 0, videoHeight);
        double x2 = Math.Clamp(maxX + margin, 0, videoWidth);
        double y2 = Math.Clamp(maxY + margin, 0, videoHeight);
        return new BoundingBox(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }

    /// <summary>
    /// Vero se il riquadro è tagliato in un modo che falsa il punto di riferimento (vertici bassi):
    /// bordo inferiore, sinistro o destro della zona. Un veicolo tagliato solo in alto ha comunque
    /// il lato inferiore e i suoi vertici corretti, quindi resta utilizzabile.
    /// </summary>
    public static bool IsTruncated(BoundingBox box, BoundingBox region, double margin = 2)
    {
        return box.X <= region.X + margin
            || box.Right >= region.Right - margin
            || box.Bottom >= region.Bottom - margin;
    }

    public static BoundingBox Scale(BoundingBox b, double sx, double sy) => new(b.X * sx, b.Y * sy, b.Width * sx, b.Height * sy);
}
