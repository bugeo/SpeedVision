using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Geometry;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Corsie della strada indicata dall'utente: corsia di ogni veicolo (per i risultati, il filtro e
/// l'esportazione) e scelta delle auto da proporre nella calibrazione (la prima nella corsia più vicina,
/// la seconda nella più lontana, che serve solo a orientare le linee A e B).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Strada con più corsie indicata nel passo «Strada».</summary>
    private bool HasLanes => Road is { Lanes: > 1 };

    /// <summary>Indice della corsia più lontana (0 senza strada o con una sola corsia).</summary>
    private int FarthestLane => (Road?.Lanes ?? 1) - 1;

    /// <summary>Corsia di un veicolo, dai suoi punti a terra (0 se la strada non è indicata, −1 se fuori strada).</summary>
    private int TrackLane(SavedTrack t) => LaneOf(t.Track.Observations.Select(o => o.Position).ToList());

    private int TrackLane(int trackId) => _savedTracks.FirstOrDefault(t => t.Id == trackId) is { } t ? TrackLane(t) : -1;

    /// <summary>La strada è cambiata: corsie dei veicoli e caselle del filtro si aggiornano.</summary>
    private void OnLanesChanged()
    {
        SyncLaneFilters();
        if (_savedTracks.Count > 0)
        {
            RebuildPassages();
        }
    }

    /// <summary>
    /// Linee A e B lunghe quanto la strada: dal bordo vicino al bordo lontano (più il margine «appena fuori»),
    /// lungo la direzione delle linee calcolate. Senza strada restano come sono.
    /// </summary>
    private (LineSegment A, LineSegment B) AcrossRoad((LineSegment A, LineSegment B) lines)
    {
        if (Road is not { } road)
        {
            return lines;
        }

        LineSegment Extend(LineSegment l) =>
            road.SpanAcross(l.Start, l.End - l.Start) is { } s && ClipToImage(s.Near, s.Far) is { } c
                ? new LineSegment(c.A, c.B)
                : l;

        return (Extend(lines.A), Extend(lines.B));
    }

    /// <summary>Segmento ritagliato ai bordi dell'immagine (Liang–Barsky), null se è tutto fuori.</summary>
    private (Point2 A, Point2 B)? ClipToImage(Point2 a, Point2 b)
    {
        double t0 = 0, t1 = 1;
        Point2 d = b - a;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-12)
            {
                return q >= 0;
            }

            double r = q / p;
            if (p < 0)
            {
                t0 = Math.Max(t0, r);
            }
            else
            {
                t1 = Math.Min(t1, r);
            }

            return t0 <= t1;
        }

        return Clip(-d.X, a.X) && Clip(d.X, VideoWidth - a.X) && Clip(-d.Y, a.Y) && Clip(d.Y, VideoHeight - a.Y)
            ? (a + (d * t0), a + (d * t1))
            : null;
    }
}
