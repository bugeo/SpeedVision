using SkiaSharp;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Privacy;
using TrafficSpeedAnalyzer.Video;
using static TrafficSpeedAnalyzer.App.Services.PassageImageRenderer;

namespace TrafficSpeedAnalyzer.App.Services;

/// <summary>
/// Testi di un passaggio nel video, già tradotti e formattati: intestazione del veicolo, titoli dei tre riquadri in alto
/// (distanza A–B, tempo, velocità), distanza e velocità; in basso indirizzo della strada e data (vuoti = non scritti).
/// </summary>
public sealed record PassageVideoText(string Header, string DistanceLabel, string Distance, string TimeLabel, Func<double, string> FormatChrono, string SpeedLabel, string Speed,
    string Address = "", string Date = "");

/// <summary>
/// Disegna i fotogrammi del video dei passaggi: veicoli pixellati, linee A e B, punto del veicolo, logo SpeedVision,
/// intestazione del veicolo e, dall'ingresso in poi, il cronometro; all'uscita anche la velocità.
/// Riusa una sola bitmap: un'istanza per video, non condivisa tra thread.
/// </summary>
public sealed class PassageVideoRenderer : IDisposable
{
    private static readonly SKColor PanelBack = new(12, 16, 24, 205);
    private static readonly SKColor PanelBorder = new(255, 255, 255, 90);
    private static readonly SKColor SpeedColor = new(0x2F, 0xCB, 0x79);

    /// <summary>Riquadro che segue il veicolo: lo stesso verde dei riquadri nel programma.</summary>
    private static readonly SKColor BoxColor = new(0x5E, 0xE0, 0x9A);

    private readonly SKBitmap _bitmap;
    private readonly SKBitmap? _logo;
    private readonly byte[] _bgra;
    private readonly int _videoWidth;
    private readonly int _videoHeight;

    /// <param name="width">Larghezza del video prodotto (= fotogrammi letti).</param>
    /// <param name="videoWidth">Larghezza del video originale: le coordinate di linee e veicoli sono in questi pixel.</param>
    public PassageVideoRenderer(int width, int height, int videoWidth, int videoHeight)
    {
        Width = width;
        Height = height;
        _videoWidth = videoWidth;
        _videoHeight = videoHeight;
        _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        _bgra = new byte[width * height * 4];
        _logo = LoadLogo();
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Fotogramma finito in BGRA (il buffer è riusato al fotogramma successivo).
    /// </summary>
    /// <param name="reference">Punto del veicolo nel fotogramma, in pixel del video originale; null se non noto.</param>
    /// <param name="vehicleBox">Riquadro che segue il veicolo nel fotogramma, in pixel del video originale; null se non c'è (misura a mano).</param>
    /// <param name="pixelate">Zone da pixellare, in pixel del video originale.</param>
    public byte[] Render(VideoFrame frame, CalibrationSettings calibration, VehiclePassage passage, ClipFrame clip, Point2? reference, BoundingBox? vehicleBox, IReadOnlyList<BoundingBox> pixelate, PassageVideoText text)
    {
        if (frame.Width != Width || frame.Height != Height)
        {
            throw new ArgumentException("Fotogramma di dimensioni diverse dal video.");
        }

        float sx = Width / (float)_videoWidth, sy = Height / (float)_videoHeight;
        SKPoint Map(Point2 p) => new((float)(p.X * sx), (float)(p.Y * sy));

        byte[] pixels = frame.Pixels;
        if (pixelate.Count > 0)
        {
            pixels = (byte[])frame.Pixels.Clone();
            foreach (BoundingBox r in pixelate)
            {
                PrivacyMasking.Pixelate(pixels, Width, Height, frame.Stride, 3, new BoundingBox(r.X * sx, r.Y * sy, r.Width * sx, r.Height * sy));
            }
        }

        CopyToBitmap(pixels, frame.Stride);
        using var canvas = new SKCanvas(_bitmap);
        float unit = Math.Max(0.5f, Height / 1080f);

        DetectionLine? highlighted = clip.Phase switch
        {
            ClipPhase.EntryPause => passage.EntryLine,
            ClipPhase.ExitPause or ClipPhase.SpeedPause => passage.ExitLine,
            _ => null,
        };
        DrawLine(canvas, Map(calibration.LineA.Start), Map(calibration.LineA.End), LineAColor, "A", highlighted == DetectionLine.A, unit);
        DrawLine(canvas, Map(calibration.LineB.Start), Map(calibration.LineB.End), LineBColor, "B", highlighted == DetectionLine.B, unit);

        if (vehicleBox is { } vb)
        {
            var rect = SKRect.Create((float)(vb.X * sx), (float)(vb.Y * sy), (float)(vb.Width * sx), (float)(vb.Height * sy));
            using SKPaint shadow = Stroke(new SKColor(0, 0, 0, 150), 5 * unit);
            using SKPaint pen = Stroke(BoxColor, 2.5f * unit);
            canvas.DrawRoundRect(rect, 3 * unit, 3 * unit, shadow);
            canvas.DrawRoundRect(rect, 3 * unit, 3 * unit, pen);
        }

        if (reference is { } rp0)
        {
            SKPoint rp = Map(rp0);
            using SKPaint ringShadow = Stroke(SKColors.Black, 6 * unit);
            using SKPaint ring = Stroke(PointColor, 3 * unit);
            using SKPaint dot = Fill(PointColor);
            canvas.DrawCircle(rp, 14 * unit, ringShadow);
            canvas.DrawCircle(rp, 14 * unit, ring);
            canvas.DrawCircle(rp, 3 * unit, dot);
        }

        // Fascia in alto: logo, tre riquadri e veicolo, tutti della stessa altezza e un po' staccati dal bordo
        // (i lettori video coprono la parte più alta con il titolo e i comandi).
        float top = 56 * unit, height = ReadoutHeight(unit);
        DrawLogo(canvas, top, height, unit);
        DrawHeader(canvas, text.Header, top, height, unit);
        DrawReadouts(canvas, text, clip, top, height, unit);

        // Fascia in basso, anch'essa staccata dal bordo (comandi del lettore): via a sinistra, data a destra.
        float bottom = Height - (56 * unit) - height;
        DrawCaption(canvas, text.Address, 16 * unit, bottom, height, alignRight: false, unit);
        DrawCaption(canvas, text.Date, Width - (16 * unit), bottom, height, alignRight: true, unit);

        canvas.Flush();
        System.Runtime.InteropServices.Marshal.Copy(_bitmap.GetPixels(), _bgra, 0, _bgra.Length);
        return _bgra;
    }

    private void CopyToBitmap(byte[] bgr, int stride)
    {
        for (int y = 0; y < Height; y++)
        {
            int s = y * stride, d = y * Width * 4;
            for (int x = 0; x < Width; x++, s += 3, d += 4)
            {
                _bgra[d] = bgr[s];
                _bgra[d + 1] = bgr[s + 1];
                _bgra[d + 2] = bgr[s + 2];
                _bgra[d + 3] = 255;
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(_bgra, 0, _bitmap.GetPixels(), _bgra.Length);
    }

    /// <summary>Logo in alto a sinistra, su una fascia scura che lo rende leggibile su qualsiasi sfondo.</summary>
    private void DrawLogo(SKCanvas canvas, float top, float height, float unit)
    {
        if (_logo is null)
        {
            return;
        }

        float pad = 12 * unit;
        float h = height - (2 * pad);
        float w = h * _logo.Width / _logo.Height;
        var back = SKRect.Create(16 * unit, top, w + (2 * pad), height);
        using (SKPaint fill = Fill(PanelBack))
        {
            canvas.DrawRoundRect(back, 10 * unit, 10 * unit, fill);
        }

        using SKImage image = SKImage.FromBitmap(_logo);
        canvas.DrawImage(image, SKRect.Create(back.Left + pad, back.Top + pad, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    /// <summary>Veicolo, corsia e verso, in alto a destra.</summary>
    private void DrawHeader(SKCanvas canvas, string header, float top, float height, float unit)
    {
        using var line = new TextLine(header, 20 * unit, SKColors.White, bold: true, mono: false);
        float pad = 14 * unit;
        var panel = SKRect.Create(Width - (16 * unit) - line.Width - (2 * pad), top, line.Width + (2 * pad), height);
        Panel(canvas, panel, unit);
        line.Draw(canvas, panel.Left + pad, panel.MidY - (line.Height / 2));
    }

    /// <summary>Scritta in basso (indirizzo o data) in un riquadro alto come quelli in alto; niente se vuota.</summary>
    private void DrawCaption(SKCanvas canvas, string text, float x, float top, float height, bool alignRight, float unit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var line = new TextLine(text, 24 * unit, SKColors.White, bold: true, mono: false);
        float pad = 16 * unit;
        float width = Math.Min(line.Width + (2 * pad), (Width / 2) - (24 * unit));
        var panel = SKRect.Create(alignRight ? x - width : x, top, width, height);
        Panel(canvas, panel, unit);
        canvas.Save();
        canvas.ClipRect(SKRect.Inflate(panel, -pad / 2, 0));
        line.Draw(canvas, panel.Left + pad, panel.MidY - (line.Height / 2));
        canvas.Restore();
    }

    /// <summary>Altezza comune dei riquadri in alto: quella dei riquadri con titolo e valore.</summary>
    private static float ReadoutHeight(float unit)
    {
        using var label = new TextLine("Ag", 16 * unit, SKColors.White, bold: false, mono: false);
        using var value = new TextLine("0", 34 * unit, SKColors.White, bold: true, mono: true);
        return (12 * unit) + label.Height + (2 * unit) + value.Height + (12 * unit);
    }

    /// <summary>
    /// Tre riquadri separati in alto al centro: distanza A–B (sempre), tempo (dall'ingresso: corre, poi si ferma
    /// all'uscita) e velocità (dopo la pausa che segue l'uscita). I posti restano fissi anche quando un riquadro non c'è
    /// ancora, così niente si sposta.
    /// </summary>
    private void DrawReadouts(SKCanvas canvas, PassageVideoText text, ClipFrame clip, float top, float height, float unit)
    {
        float pad = 12 * unit, gap = 10 * unit;
        var label = new SKColor(0xD0, 0xD6, 0xE0);
        string time = clip.Chrono is { } c ? text.FormatChrono(c) : string.Empty;
        (string Label, string Value, string Sample, SKColor Color, bool Shown)[] blocks =
        [
            (text.DistanceLabel, text.Distance, text.Distance, SKColors.White, true),
            (text.TimeLabel, time, text.FormatChrono(88.888), SKColors.White, clip.Chrono is not null),
            (text.SpeedLabel, text.Speed, text.Speed.Length >= 10 ? text.Speed : "888,8 km/h", SpeedColor, clip.Phase == ClipPhase.SpeedPause),
        ];

        var lines = blocks.Select(b => (
            Label: new TextLine(b.Label, 16 * unit, label, bold: false, mono: false),
            Value: new TextLine(b.Value, 34 * unit, b.Color, bold: true, mono: true),
            Sample: new TextLine(b.Sample, 34 * unit, b.Color, bold: true, mono: true))).ToArray();
        try
        {
            float[] widths = [.. lines.Select(l => Math.Max(l.Label.Width, Math.Max(l.Value.Width, l.Sample.Width)) + (2 * pad))];
            float x = (Width - widths.Sum() - (gap * 2)) / 2;
            for (int i = 0; i < blocks.Length; i++)
            {
                if (blocks[i].Shown)
                {
                    var panel = SKRect.Create(x, top, widths[i], height);
                    Panel(canvas, panel, unit);
                    lines[i].Label.Draw(canvas, panel.MidX - (lines[i].Label.Width / 2), panel.Top + pad);
                    lines[i].Value.Draw(canvas, panel.MidX - (lines[i].Value.Width / 2), panel.Top + pad + lines[i].Label.Height + (2 * unit));
                }

                x += widths[i] + gap;
            }
        }
        finally
        {
            foreach (var l in lines)
            {
                l.Label.Dispose();
                l.Value.Dispose();
                l.Sample.Dispose();
            }
        }
    }

    private static void Panel(SKCanvas canvas, SKRect r, float unit)
    {
        using SKPaint back = Fill(PanelBack);
        using SKPaint border = Stroke(PanelBorder, 1 * unit);
        canvas.DrawRoundRect(r, 10 * unit, 10 * unit, back);
        canvas.DrawRoundRect(r, 10 * unit, 10 * unit, border);
    }

    private static SKBitmap? LoadLogo()
    {
        using Stream? s = typeof(PassageVideoRenderer).Assembly.GetManifestResourceStream("speedvision-logo.png");
        return s is null ? null : SKBitmap.Decode(s);
    }

    public void Dispose()
    {
        _bitmap.Dispose();
        _logo?.Dispose();
    }
}
