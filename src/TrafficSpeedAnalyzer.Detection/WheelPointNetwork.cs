using Microsoft.ML.OnnxRuntime;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Detection;

/// <summary>
/// Rete neurale che trova i punti di contatto a terra delle due ruote verso la camera (quella a sinistra e quella a
/// destra nell'immagine). Lavora sulla parte bassa del riquadro del veicolo, ridotta a 256×96 pixel, e restituisce per
/// ciascuna ruota il punto e la probabilità che sia visibile. È piccola (circa 420 000 parametri, 1,7 MB) e gira sul
/// processore in pochi millisecondi per veicolo; il modello è incluso nel programma.
/// Allenata su fotogrammi sintetici con i punti veri (Simulatore strada 3D) e su veicoli reali etichettati a mano.
/// </summary>
public sealed class WheelPointNetwork : IDisposable
{
    /// <summary>Dimensioni del ritaglio d'ingresso.</summary>
    public const int InputWidth = 256, InputHeight = 96;

    /// <summary>Probabilità minima perché una ruota sia considerata visibile.</summary>
    public const double MinVisibility = 0.5;

    /// <summary>Altezza minima del riquadro (pixel): sotto, le ruote sono troppo piccole.</summary>
    public const double MinBoxHeight = 40;

    /// <summary>Raggio della ruota stimato dall'altezza del riquadro (la rete dà solo il punto a terra).</summary>
    public const double RadiusFraction = 0.2;

    private const string ResourceName = "wheelnet.onnx";
    private const int MaxBatch = 16;

    private static readonly Lazy<WheelPointNetwork> SharedInstance = new(() => new WheelPointNetwork(LoadEmbeddedModel()));

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    public WheelPointNetwork(byte[] model)
    {
        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = 2 };
        _session = new InferenceSession(model, so);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    /// <summary>Rete incorporata nel programma, caricata alla prima richiesta.</summary>
    public static WheelPointNetwork Shared => SharedInstance.Value;

    public static byte[] LoadEmbeddedModel()
    {
        using Stream s = typeof(WheelPointNetwork).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Modello delle ruote mancante nel programma.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Zona dell'immagine che la rete guarda: parte bassa del riquadro con un po' di margine ai lati e sotto
    /// (le stesse proporzioni usate nell'allenamento).
    /// </summary>
    public static BoundingBox CropRect(BoundingBox box) =>
        new(box.X - (0.08 * box.Width), box.Y + (0.30 * box.Height), box.Width * 1.16, box.Height * 0.82);

    /// <summary>
    /// Ruote visibili di ciascun riquadro, in coordinate dell'immagine; lista vuota per i riquadri troppo piccoli.
    /// </summary>
    /// <param name="bgr">Immagine a 3 byte per pixel (BGR).</param>
    public IReadOnlyList<WheelCandidate>[] Locate(byte[] bgr, int width, int height, int stride, IReadOnlyList<BoundingBox> boxes)
    {
        var result = new IReadOnlyList<WheelCandidate>[boxes.Count];
        var todo = new List<int>();
        for (int i = 0; i < boxes.Count; i++)
        {
            result[i] = [];
            if (boxes[i].Height >= MinBoxHeight && boxes[i].Width > 0)
            {
                todo.Add(i);
            }
        }

        for (int start = 0; start < todo.Count; start += MaxBatch)
        {
            int n = Math.Min(MaxBatch, todo.Count - start);
            int plane = InputWidth * InputHeight;
            var input = new float[n * 3 * plane];
            for (int k = 0; k < n; k++)
            {
                FillInput(bgr, width, height, stride, CropRect(boxes[todo[start + k]]), input.AsSpan(k * 3 * plane, 3 * plane));
            }

            float[] output = Run(input, n);
            for (int k = 0; k < n; k++)
            {
                BoundingBox box = boxes[todo[start + k]];
                result[todo[start + k]] = Decode(output.AsSpan(k * 6, 6), box);
            }
        }

        return result;
    }

    private float[] Run(float[] input, int n)
    {
        using OrtValue value = OrtValue.CreateTensorValueFromMemory(input, [n, 3, InputHeight, InputWidth]);
        using var options = new RunOptions();
        using IDisposableReadOnlyCollection<OrtValue> outputs = _session.Run(options, [_inputName], [value], [_outputName]);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    /// <summary>Uscita della rete per un riquadro: (x, y, logit di visibilità) per la ruota sinistra e la destra, in pixel del ritaglio.</summary>
    internal static List<WheelCandidate> Decode(ReadOnlySpan<float> o, BoundingBox box)
    {
        BoundingBox crop = CropRect(box);
        double sx = crop.Width / InputWidth, sy = crop.Height / InputHeight;
        double radius = RadiusFraction * box.Height;
        var wheels = new List<WheelCandidate>(2);
        for (int j = 0; j < 2; j++)
        {
            double visible = 1 / (1 + Math.Exp(-o[(j * 3) + 2]));
            if (visible < MinVisibility)
            {
                continue;
            }

            var contact = new Point2(crop.X + (o[j * 3] * sx), crop.Y + (o[(j * 3) + 1] * sy));
            wheels.Add(new WheelCandidate(new Point2(contact.X, contact.Y - radius), radius, 100 * visible, contact));
        }

        return wheels;
    }

    /// <summary>
    /// Ritaglio 256×96 in RGB 0–1, piano per piano: ogni pixel d'ingresso (u, v) prende il punto (x0 + u·sx, y0 + v·sy)
    /// dell'immagine, interpolato tra i quattro pixel vicini, con il bordo dell'immagine ripetuto fuori dai margini
    /// (come cv2.warpAffine con BORDER_REPLICATE usato nell'allenamento).
    /// </summary>
    internal static void FillInput(byte[] bgr, int width, int height, int stride, BoundingBox crop, Span<float> dst)
    {
        int plane = InputWidth * InputHeight;
        double sx = crop.Width / InputWidth, sy = crop.Height / InputHeight;
        for (int v = 0; v < InputHeight; v++)
        {
            double fy = crop.Y + (v * sy);
            int y0 = (int)Math.Floor(fy);
            float wy = (float)(fy - y0);
            int ya = Math.Clamp(y0, 0, height - 1), yb = Math.Clamp(y0 + 1, 0, height - 1);
            int rowA = ya * stride, rowB = yb * stride;
            for (int u = 0; u < InputWidth; u++)
            {
                double fx = crop.X + (u * sx);
                int x0 = (int)Math.Floor(fx);
                float wx = (float)(fx - x0);
                int xa = Math.Clamp(x0, 0, width - 1) * 3, xb = Math.Clamp(x0 + 1, 0, width - 1) * 3;
                int o = (v * InputWidth) + u;
                for (int c = 0; c < 3; c++)
                {
                    float top = bgr[rowA + xa + c] + ((bgr[rowA + xb + c] - bgr[rowA + xa + c]) * wx);
                    float bottom = bgr[rowB + xa + c] + ((bgr[rowB + xb + c] - bgr[rowB + xa + c]) * wx);
                    // BGR → piani R, G, B; arrotondato a 8 bit come l'immagine ritagliata nell'allenamento
                    dst[((2 - c) * plane) + o] = MathF.Round(top + ((bottom - top) * wy)) / 255f;
                }
            }
        }
    }

    public void Dispose() => _session.Dispose();
}
