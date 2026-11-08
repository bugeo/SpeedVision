using Microsoft.ML.OnnxRuntime;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Detection;

public enum AccelerationMode
{
    /// <summary>
    /// Acceleratore del sistema se disponibile e verificato, altrimenti CPU:
    /// scheda grafica con DirectML su Windows, CoreML (GPU e Neural Engine) sui Mac con chip Apple.
    /// </summary>
    Auto,

    CpuOnly,
}

public sealed record DetectorOptions
{
    public static DetectorOptions Default { get; } = new();

    public AccelerationMode Acceleration { get; init; } = AccelerationMode.Auto;

    /// <summary>Punteggio minimo restituito. Il tracker usa anche rilevamenti deboli (fino a 0,1) per non perdere i veicoli.</summary>
    public float MinScore { get; init; } = 0.1f;

    /// <summary>Sovrapposizione oltre cui due riquadri sono lo stesso veicolo (soppressione dei duplicati).</summary>
    public float NmsIoU { get; init; } = 0.5f;

    public bool IncludeBicycles { get; init; }
}

/// <summary>
/// Rilevatore di veicoli basato su YOLOX (ONNX). Ridimensiona la zona richiesta mantenendo le
/// proporzioni (bordi grigi come in addestramento), esegue il modello e decodifica le uscite.
/// </summary>
public sealed class YoloxDetector : IVehicleDetector
{
    /// <summary>Punteggio minimo per le persone da coprire: basso, perché è meglio coprire qualcosa in più che lasciare un volto scoperto.</summary>
    public const float PeopleMinScore = 0.25f;

    // Indici delle classi COCO.
    private const int Person = 0;
    private const int Bicycle = 1;
    private const int Car = 2;
    private const int Motorcycle = 3;
    private const int Bus = 5;
    private const int Truck = 7;
    private static readonly int[] Strides = [8, 16, 32];
    private static readonly object GpuInitLock = new();

    private readonly InferenceSession _session;

    private readonly DetectorOptions _options;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _size;
    private readonly int _numClasses;
    private readonly float[] _input;
    private readonly (float Gx, float Gy, float Stride)[] _grid;
    private readonly int[] _classes;
    private readonly object _sync = new();
    private readonly RunOptions _runOptions = new();

    public YoloxDetector(string modelPath, string modelName, DetectorOptions? options = null)
    {
        _options = options ?? DetectorOptions.Default;
        ModelName = modelName;

        // La sessione su CPU è sempre disponibile e fa da riferimento per verificare la GPU.
        InferenceSession cpu = CreateCpuSession(modelPath);
        _inputName = cpu.InputMetadata.Keys.First();
        _outputName = cpu.OutputMetadata.Keys.First();
        int[] inDims = cpu.InputMetadata[_inputName].Dimensions;
        if (inDims.Length != 4 || inDims[1] != 3 || inDims[2] <= 0 || inDims[2] != inDims[3])
        {
            cpu.Dispose();
            throw new InvalidDataException(Loc.T("Il modello non ha l'ingresso atteso (1×3×N×N)."));
        }

        _size = inDims[2];
        int[] outDims = cpu.OutputMetadata[_outputName].Dimensions;
        _numClasses = outDims[^1] - 5;
        _grid = BuildGrid(_size);
        if (_numClasses < 8 || (outDims.Length == 3 && outDims[1] > 0 && outDims[1] != _grid.Length))
        {
            cpu.Dispose();
            throw new InvalidDataException(Loc.T("Formato di uscita del modello non riconosciuto."));
        }

        _input = new float[3 * _size * _size];
        _classes = _options.IncludeBicycles ? [Car, Motorcycle, Bus, Truck, Bicycle] : [Car, Motorcycle, Bus, Truck];

        _session = cpu;
        ExecutionProvider = "CPU";
        if (_options.Acceleration == AccelerationMode.Auto && Accelerator() is { } accel)
        {
            InferenceSession? gpu;
            string note;
            lock (GpuInitLock)
            {
                // Un'inizializzazione alla volta: in parallelo alcuni driver DirectML si bloccano.
                (gpu, note) = TryCreateVerifiedGpuSession(modelPath, cpu, accel.Candidates);
            }

            if (gpu is not null)
            {
                _session = gpu;
                ExecutionProvider = accel.Name;
                cpu.Dispose();
            }
            else
            {
                ExecutionProvider = "CPU" + note;
            }
        }
    }

    public string ModelName { get; }

    public string ExecutionProvider { get; }

    public int InputSize => _size;

    private static InferenceSession CreateCpuSession(string path) =>
        new(path, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });

    /// <summary>
    /// Acceleratore del sistema operativo, con le configurazioni da provare in ordine.
    /// Nessuno su Linux e sui Mac Intel: lì si usa la CPU.
    /// </summary>
    private static (string Name, IReadOnlyList<Func<SessionOptions>> Candidates)? Accelerator()
    {
        if (OperatingSystem.IsWindows())
        {
            // DirectML con livelli di ottimizzazione decrescenti.
            return (Loc.T("GPU (DirectML)"),
                [.. new[] { GraphOptimizationLevel.ORT_ENABLE_ALL, GraphOptimizationLevel.ORT_ENABLE_BASIC, GraphOptimizationLevel.ORT_DISABLE_ALL }
                    .Select(level => (Func<SessionOptions>)(() =>
                    {
                        var so = new SessionOptions
                        {
                            GraphOptimizationLevel = level,
                            EnableMemoryPattern = false,
                            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                        };
                        so.AppendExecutionProvider_DML(0);
                        return so;
                    }))]);
        }

        if (OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
        {
            // CoreML: prima GPU e Neural Engine insieme, poi solo GPU (calcoli più vicini a quelli della CPU).
            return (Loc.T("Chip Apple (CoreML)"),
                [.. new[] { "ALL", "CPUAndGPU" }.Select(units => (Func<SessionOptions>)(() =>
                    {
                        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                        so.AppendExecutionProvider("CoreML", new Dictionary<string, string>
                        {
                            ["ModelFormat"] = "MLProgram",
                            ["MLComputeUnits"] = units,
                        });
                        return so;
                    }))]);
        }

        return null;
    }

    /// <summary>
    /// Prova l'acceleratore con le configurazioni indicate e lo accetta solo se, su un'immagine
    /// di prova, dà gli stessi risultati della CPU. Alcune combinazioni di driver e DirectML
    /// producono uscite non valide senza segnalare errori; CoreML può calcolare a precisione ridotta.
    /// </summary>
    private (InferenceSession?, string) TryCreateVerifiedGpuSession(string path, InferenceSession reference, IReadOnlyList<Func<SessionOptions>> candidates)
    {
        FillValidationInput(_input);
        float[] expected = Run(reference);
        string note = Loc.T(" (scheda grafica non disponibile)");
        foreach (Func<SessionOptions> candidate in candidates)
        {
            InferenceSession? gpu = null;
            try
            {
                using SessionOptions so = candidate();
                gpu = new InferenceSession(path, so);
                float[] actual = Run(gpu);
                if (OutputsMatch(expected, actual))
                {
                    return (gpu, string.Empty);
                }

                note = Loc.T(" (la scheda grafica dava risultati non validi)");
            }
            catch (Exception e) when (e is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException or NotSupportedException)
            {
                // acceleratore non disponibile su questo computer
            }

            gpu?.Dispose();
        }

        return (null, note);
    }

    /// <summary>Immagine di prova deterministica: sfumature e blocchi, valori 0–255.</summary>
    private void FillValidationInput(float[] tensor)
    {
        int plane = _size * _size;
        var rng = new Random(1234);
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < _size; y++)
            {
                for (int x = 0; x < _size; x++)
                {
                    float v = ((x * (c + 1) + y * 2) % 256 + ((x / 32 + y / 32) % 2) * 60 + rng.Next(20)) % 256;
                    tensor[c * plane + y * _size + x] = v;
                }
            }
        }
    }

    internal static bool OutputsMatch(float[] expected, float[] actual)
    {
        if (expected.Length != actual.Length || expected.Length == 0)
        {
            return false;
        }

        double sumAbs = 0;
        double maxScoreDiff = 0;
        int stride = 85;
        for (int i = 0; i < expected.Length; i++)
        {
            float a = actual[i];
            if (!float.IsFinite(a))
            {
                return false;
            }

            double d = Math.Abs(expected[i] - a);
            sumAbs += d;
            if (i % stride >= 4)
            {
                maxScoreDiff = Math.Max(maxScoreDiff, d); // obiettività e classi sono probabilità 0–1
            }
        }

        return sumAbs / expected.Length < 0.05 && maxScoreDiff < 0.1;
    }

    public IReadOnlyList<VehicleDetection> Detect(byte[] bgr24, int width, int height, BoundingBox? region = null)
    {
        if (bgr24.Length < width * height * 3)
        {
            throw new ArgumentException("Dimensione dell'immagine non coerente.", nameof(bgr24));
        }

        Roi roi = Roi.From(region, width, height);
        lock (_sync)
        {
            float scale = Letterbox.Fill(bgr24, width, height, roi, _input, _size);
            float[] output = Run(_session);
            return Decode(output, roi, scale);
        }
    }

    /// <summary>
    /// Persone nell'intero fotogramma (classe COCO «person»), per coprirne il volto nelle immagini anonimizzate.
    /// Non entra nell'analisi della velocità: serve solo all'esportazione.
    /// </summary>
    public IReadOnlyList<BoundingBox> DetectPeople(byte[] bgr24, int width, int height, float minScore = PeopleMinScore)
    {
        if (bgr24.Length < width * height * 3)
        {
            throw new ArgumentException("Dimensione dell'immagine non coerente.", nameof(bgr24));
        }

        Roi roi = Roi.From(null, width, height);
        lock (_sync)
        {
            float scale = Letterbox.Fill(bgr24, width, height, roi, _input, _size);
            float[] output = Run(_session);
            return Nms.Apply(DecodeCandidates(output, _grid, _numClasses, roi, scale, [Person], minScore), _options.NmsIoU)
                .Select(d => d.Box)
                .ToList();
        }
    }

    private float[] Run(InferenceSession session)
    {
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(_input, [1, 3, _size, _size]);
        using IDisposableReadOnlyCollection<OrtValue> outputs = session.Run(_runOptions, [_inputName], [input], [_outputName]);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    private List<VehicleDetection> Decode(float[] output, Roi roi, float scale) =>
        Nms.Apply(DecodeCandidates(output, _grid, _numClasses, roi, scale, _classes, _options.MinScore), _options.NmsIoU);

    /// <summary>
    /// Riquadri delle classi COCO indicate con punteggio (obiettività × classe) almeno <paramref name="minScore"/>,
    /// in coordinate dell'immagine e limitati alla zona analizzata; prima della soppressione dei duplicati.
    /// </summary>
    internal static List<VehicleDetection> DecodeCandidates(
        float[] output, (float Gx, float Gy, float Stride)[] grid, int numClasses, Roi roi, float scale, int[] classes, float minScore)
    {
        int stride = 5 + numClasses;
        int anchors = Math.Min(grid.Length, output.Length / stride);
        var candidates = new List<VehicleDetection>();

        for (int k = 0; k < anchors; k++)
        {
            int o = k * stride;
            float obj = output[o + 4];
            if (obj < minScore)
            {
                continue;
            }

            int bestClass = -1;
            float best = 0;
            foreach (int c in classes)
            {
                float s = obj * output[o + 5 + c];
                if (s > best)
                {
                    best = s;
                    bestClass = c;
                }
            }

            if (bestClass < 0 || best < minScore)
            {
                continue;
            }

            (float gx, float gy, float st) = grid[k];
            float cx = (output[o] + gx) * st;
            float cy = (output[o + 1] + gy) * st;
            float w = MathF.Exp(output[o + 2]) * st;
            float h = MathF.Exp(output[o + 3]) * st;

            // Da coordinate del modello a coordinate dell'immagine originale, limitate alla zona analizzata.
            double x1 = Math.Clamp((cx - w / 2) / scale + roi.X, roi.X, roi.X + roi.Width);
            double y1 = Math.Clamp((cy - h / 2) / scale + roi.Y, roi.Y, roi.Y + roi.Height);
            double x2 = Math.Clamp((cx + w / 2) / scale + roi.X, roi.X, roi.X + roi.Width);
            double y2 = Math.Clamp((cy + h / 2) / scale + roi.Y, roi.Y, roi.Y + roi.Height);
            if (x2 - x1 < 2 || y2 - y1 < 2)
            {
                continue;
            }

            candidates.Add(new VehicleDetection(new BoundingBox(x1, y1, x2 - x1, y2 - y1), MapClass(bestClass), best));
        }

        return candidates;
    }

    private static VehicleClass MapClass(int coco) => coco switch
    {
        Car => VehicleClass.Car,
        Motorcycle => VehicleClass.Motorcycle,
        Bus => VehicleClass.Bus,
        Truck => VehicleClass.Truck,
        Bicycle => VehicleClass.Bicycle,
        _ => VehicleClass.Other,
    };

    internal static (float, float, float)[] BuildGrid(int size)
    {
        var list = new List<(float, float, float)>();
        foreach (int s in Strides)
        {
            int n = size / s;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    list.Add((x, y, s));
                }
            }
        }

        return [.. list];
    }

    public void Dispose()
    {
        _session.Dispose();
        _runOptions.Dispose();
    }
}

/// <summary>Zona rettangolare in pixel interi, interna all'immagine.</summary>
internal readonly record struct Roi(int X, int Y, int Width, int Height)
{
    public static Roi From(BoundingBox? region, int imageWidth, int imageHeight)
    {
        if (region is not { } r)
        {
            return new Roi(0, 0, imageWidth, imageHeight);
        }

        int x1 = Math.Clamp((int)Math.Floor(r.X), 0, imageWidth - 1);
        int y1 = Math.Clamp((int)Math.Floor(r.Y), 0, imageHeight - 1);
        int x2 = Math.Clamp((int)Math.Ceiling(r.Right), x1 + 1, imageWidth);
        int y2 = Math.Clamp((int)Math.Ceiling(r.Bottom), y1 + 1, imageHeight);
        return new Roi(x1, y1, x2 - x1, y2 - y1);
    }
}

internal static class Letterbox
{
    public const float PadValue = 114f;

    /// <summary>
    /// Copia la zona dell'immagine BGR nel tensore NCHW (valori 0–255, ordine BGR come YOLOX),
    /// ridimensionata con interpolazione bilineare e allineata in alto a sinistra; il resto è grigio.
    /// Restituisce il fattore di scala applicato.
    /// </summary>
    public static float Fill(byte[] src, int width, int height, Roi roi, float[] tensor, int size)
    {
        float scale = Math.Min(size / (float)roi.Width, size / (float)roi.Height);
        int outW = Math.Min(size, (int)Math.Round(roi.Width * scale));
        int outH = Math.Min(size, (int)Math.Round(roi.Height * scale));
        int plane = size * size;
        Array.Fill(tensor, PadValue);

        int stride = width * 3;
        Parallel.For(0, outH, y =>
        {
            float sy = Math.Clamp((y + 0.5f) / scale - 0.5f, 0, roi.Height - 1) + roi.Y;
            int y0 = (int)sy;
            int y1 = Math.Min(y0 + 1, height - 1);
            float fy = sy - y0;
            int row0 = y0 * stride;
            int row1 = y1 * stride;
            int outRow = y * size;
            for (int x = 0; x < outW; x++)
            {
                float sx = Math.Clamp((x + 0.5f) / scale - 0.5f, 0, roi.Width - 1) + roi.X;
                int x0 = (int)sx;
                int x1 = Math.Min(x0 + 1, width - 1);
                float fx = sx - x0;
                int a = row0 + x0 * 3;
                int b = row0 + x1 * 3;
                int c = row1 + x0 * 3;
                int d = row1 + x1 * 3;
                for (int ch = 0; ch < 3; ch++)
                {
                    float top = src[a + ch] + (src[b + ch] - src[a + ch]) * fx;
                    float bottom = src[c + ch] + (src[d + ch] - src[c + ch]) * fx;
                    tensor[ch * plane + outRow + x] = top + (bottom - top) * fy;
                }
            }
        });

        return scale;
    }
}

internal static class Nms
{
    /// <summary>Soppressione dei riquadri sovrapposti, indipendente dalla classe (un'auto non può essere anche un camion).</summary>
    public static List<VehicleDetection> Apply(List<VehicleDetection> detections, float iouThreshold)
    {
        detections.Sort((a, b) => b.Score.CompareTo(a.Score));
        var keep = new List<VehicleDetection>();
        foreach (VehicleDetection d in detections)
        {
            bool suppressed = false;
            foreach (VehicleDetection k in keep)
            {
                if (BoundingBox.IoU(d.Box, k.Box) > iouThreshold)
                {
                    suppressed = true;
                    break;
                }
            }

            if (!suppressed)
            {
                keep.Add(d);
            }
        }

        return keep;
    }
}
