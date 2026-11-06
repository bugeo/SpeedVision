namespace TrafficSpeedAnalyzer.Video;

/// <summary>Fotogramma decodificato in formato BGR 24 bit, con il suo timestamp reale.</summary>
public sealed class VideoFrame(int index, double timeSeconds, int width, int height, byte[] pixels)
{
    public int Index { get; } = index;

    public double TimeSeconds { get; } = timeSeconds;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Stride => Width * 3;

    /// <summary>Pixel BGR riga per riga, senza padding.</summary>
    public byte[] Pixels { get; } = pixels;
}
