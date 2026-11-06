using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.Core.Measurement;

/// <summary>
/// Posizione del punto di riferimento di un veicolo in un fotogramma.
/// <paramref name="TimeSeconds"/> è il timestamp reale del fotogramma letto dal file video.
/// </summary>
public readonly record struct TrackObservation(long FrameIndex, double TimeSeconds, Point2 Position);
