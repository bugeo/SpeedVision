using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Detection;

// Contratti comuni a rilevatore (ONNX, progetto Detection) e tracker (Core/Tracking).

public enum VehicleClass
{
    Car,
    Motorcycle,
    Bus,
    Truck,
    Bicycle,
    Other,
}

/// <summary>Riquadro in pixel del fotogramma (angolo in alto a sinistra, larghezza, altezza).</summary>
public readonly record struct BoundingBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + Width / 2;

    public double CenterY => Y + Height / 2;

    public double Area => Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>Centro del lato inferiore: approssima il punto di contatto con la strada.</summary>
    public Point2 BottomCenter => new(X + Width / 2, Y + Height);

    /// <summary>Vertice in basso a sinistra.</summary>
    public Point2 BottomLeft => new(X, Y + Height);

    /// <summary>Vertice in basso a destra.</summary>
    public Point2 BottomRight => new(X + Width, Y + Height);

    public static BoundingBox FromCenter(double cx, double cy, double w, double h) => new(cx - w / 2, cy - h / 2, w, h);

    public static double IoU(BoundingBox a, BoundingBox b)
    {
        double x1 = Math.Max(a.X, b.X);
        double y1 = Math.Max(a.Y, b.Y);
        double x2 = Math.Min(a.Right, b.Right);
        double y2 = Math.Min(a.Bottom, b.Bottom);
        double inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        double union = a.Area + b.Area - inter;
        return union > 0 ? inter / union : 0;
    }
}

/// <param name="Wheels">Ruote trovate nel riquadro (<see cref="WheelLocator"/>), null se non cercate.</param>
public sealed record VehicleDetection(BoundingBox Box, VehicleClass Class, float Score, IReadOnlyList<WheelCandidate>? Wheels = null);

/// <summary>Rilevatore di veicoli su un singolo fotogramma (immagine BGR 8 bit).</summary>
public interface IVehicleDetector : IDisposable
{
    string ModelName { get; }

    /// <summary>Esecutore effettivamente in uso, per esempio "GPU (DirectML)" o "CPU".</summary>
    string ExecutionProvider { get; }

    /// <param name="region">Zona da analizzare (in pixel dell'immagine); null = intero fotogramma.</param>
    IReadOnlyList<VehicleDetection> Detect(byte[] bgr24, int width, int height, BoundingBox? region = null);
}

/// <summary>Veicolo seguito dal tracker nel fotogramma corrente.</summary>
public sealed record TrackedVehicle(int TrackId, VehicleDetection Detection, bool IsConfirmed)
{
    public Point2 ReferencePoint => Detection.Box.BottomCenter;
}
