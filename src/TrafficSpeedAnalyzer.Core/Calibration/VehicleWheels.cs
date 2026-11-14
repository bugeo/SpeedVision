using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Calibration;

/// <summary>Punti in cui le due ruote verso la camera toccano terra (a sinistra e a destra nell'immagine).</summary>
public readonly record struct WheelContacts(Point2 Left, Point2 Right)
{
    /// <summary>Distanza tra i due punti lungo la strada (pixel), cioè il passo visto nell'immagine.</summary>
    public double AlongRoad(double roadAngle) =>
        Math.Abs(RoadPerspective.ToRoad(Right, roadAngle).U - RoadPerspective.ToRoad(Left, roadAngle).U);
}

/// <summary>Le due ruote di un veicolo per la calibrazione con il passo.</summary>
public static class VehicleWheels
{
    /// <summary>
    /// Passo visto rispetto alla larghezza del riquadro: nelle auto il passo è circa il 55–70 % della lunghezza;
    /// fuori da questo intervallo una delle due ruote è sbagliata.
    /// </summary>
    public const double MinFraction = 0.4, MaxFraction = 0.9;

    /// <summary>
    /// I due punti a terra, se la rete ha trovato entrambe le ruote e sono plausibili (sotto il veicolo, a una
    /// distanza compatibile con un passo); altrimenti null.
    /// </summary>
    public static WheelContacts? FromCandidates(IReadOnlyList<WheelCandidate> wheels, BoundingBox box)
    {
        Point2[] contacts = [.. wheels.Select(w => w.Contact).OrderBy(p => p.X)];
        if (contacts.Length != 2)
        {
            return null;
        }

        Point2 left = contacts[0], right = contacts[1];
        double dx = right.X - left.X;
        bool inside = contacts.All(p => p.X >= box.X - (0.05 * box.Width) && p.X <= box.Right + (0.05 * box.Width)
                                        && p.Y >= box.Y + (0.4 * box.Height) && p.Y <= box.Bottom + (0.1 * box.Height));
        return inside && dx >= MinFraction * box.Width && dx <= MaxFraction * box.Width ? new WheelContacts(left, right) : null;
    }
}
