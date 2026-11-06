using System.Runtime.CompilerServices;

namespace TrafficSpeedAnalyzer.Video;

/// <summary>Lettura sequenziale di un intervallo di fotogrammi, per l'analisi automatica.</summary>
public static class FrameSequence
{
    /// <summary>
    /// Decodifica in ordine i fotogrammi da <paramref name="startIndex"/> a <paramref name="endIndex"/> (inclusi),
    /// alla risoluzione indicata. Ogni fotogramma ha l'indice e il timestamp reali dell'indice del video.
    /// </summary>
    public static async IAsyncEnumerable<VideoFrame> ReadAsync(
        FfmpegTools tools,
        VideoInfo info,
        FrameIndex index,
        int width,
        int height,
        int startIndex,
        int endIndex,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        startIndex = Math.Clamp(startIndex, 0, index.Count - 1);
        endIndex = Math.Clamp(endIndex, startIndex, index.Count - 1);

        double? seek = null;
        if (startIndex > 0)
        {
            // Posizionamento poco prima del primo fotogramma richiesto; i precedenti vengono scartati.
            double s = index[startIndex] - info.StartTimeSeconds - index.IntervalAround(startIndex) * 0.5;
            seek = s > 0 ? s : null;
        }

        await using DecodeStream stream = DecodeStream.Start(tools, info, seek, width, height);
        int last = -1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            (byte[] Pixels, double Pts)? next = await stream.ReadNextAsync(ct).ConfigureAwait(false);
            if (next is null)
            {
                yield break;
            }

            int idx = index.NearestIndex(next.Value.Pts);
            if (idx < startIndex || idx <= last)
            {
                continue;
            }

            if (idx > endIndex)
            {
                yield break;
            }

            last = idx;
            yield return new VideoFrame(idx, index[idx], width, height, next.Value.Pixels);
        }
    }

    /// <summary>Dimensioni di analisi: risoluzione del video limitata a <paramref name="maxWidth"/> × <paramref name="maxHeight"/>.</summary>
    public static (int Width, int Height) AnalysisSize(VideoInfo info, int maxWidth = 1920, int maxHeight = 1080) =>
        VideoSession.FitWithin(info.DisplayWidth, info.DisplayHeight, maxWidth, maxHeight);
}
