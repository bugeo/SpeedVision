using SkiaSharp;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Privacy;
using TrafficSpeedAnalyzer.Video;
using LineSegment = TrafficSpeedAnalyzer.Core.Geometry.LineSegment;

namespace TrafficSpeedAnalyzer.App.Services;

/// <summary>Testi da sovrimprimere su un'immagine di ingresso o uscita.</summary>
public sealed record PassageImageText(string Header, string MainTime, string Detail, string? Footer);

/// <summary>
/// Disegna il fotogramma a piena risoluzione con le linee, il punto di riferimento del veicolo
/// e il tempo sovrimpresso, e lo codifica in JPEG. Con <c>pixelate</c> le zone indicate
/// (coordinate del video) vengono pixellate: versione anonimizzata.
/// Usa SkiaSharp, lo stesso motore grafico su Windows, Mac e Linux.
/// </summary>
public static class PassageImageRenderer
{
    public static readonly SKColor LineAColor = new(0x4C, 0xC2, 0xFF);
    public static readonly SKColor LineBColor = new(0xFF, 0xB5, 0x47);
    public static readonly SKColor PointColor = new(0xFF, 0x5C, 0x8A);

    private static readonly SKColor Shadow = new(0, 0, 0, 150);

    // Caratteri: il primo disponibile sul sistema (Windows, Mac, Linux), altrimenti quello predefinito.
    private static readonly string[] SansFamilies = ["Segoe UI Variable Display", "Segoe UI", "Helvetica Neue", "Helvetica", "DejaVu Sans", "Liberation Sans"];
    private static readonly string[] MonoFamilies = ["Cascadia Mono", "Consolas", "Menlo", "SF Mono", "DejaVu Sans Mono", "Liberation Mono"];

    /// <param name="videoWidth">Larghezza del sistema di coordinate delle linee (pixel del video).</param>
    public static byte[] RenderJpeg(
        VideoFrame frame,
        int videoWidth,
        int videoHeight,
        CalibrationSettings calibration,
        DetectionLine highlightedLine,
        Point2 referencePoint,
        PassageImageText text,
        IReadOnlyList<BoundingBox>? pixelate = null,
        int quality = 92)
    {
        int w = frame.Width;
        int h = frame.Height;
        float sx = w / (float)videoWidth;
        float sy = h / (float)videoHeight;
        SKPoint Map(Point2 p) => new((float)(p.X * sx), (float)(p.Y * sy));

        // Versione anonimizzata: si pixella una copia dei pixel prima di disegnare linee e testi.
        byte[] pixels = frame.Pixels;
        if (pixelate is { Count: > 0 })
        {
            pixels = (byte[])frame.Pixels.Clone();
            foreach (BoundingBox r in pixelate)
            {
                PrivacyMasking.Pixelate(pixels, w, h, frame.Stride, 3, new BoundingBox(r.X * sx, r.Y * sy, r.Width * sx, r.Height * sy));
            }
        }

        using SKBitmap bitmap = ToBitmap(pixels, w, h, frame.Stride);
        using var canvas = new SKCanvas(bitmap);
        float unit = Math.Max(1f, h / 1080f);

        DrawLine(canvas, Map(calibration.LineA.Start), Map(calibration.LineA.End), LineAColor, "A", highlightedLine == DetectionLine.A, unit);
        DrawLine(canvas, Map(calibration.LineB.Start), Map(calibration.LineB.End), LineBColor, "B", highlightedLine == DetectionLine.B, unit);

        // Punto di riferimento del veicolo nel fotogramma salvato.
        SKPoint rp = Map(referencePoint);
        using (SKPaint ringShadow = Stroke(SKColors.Black, 6 * unit))
        using (SKPaint ring = Stroke(PointColor, 3 * unit))
        using (SKPaint dot = Fill(PointColor))
        {
            canvas.DrawCircle(rp, 14 * unit, ringShadow);
            canvas.DrawCircle(rp, 14 * unit, ring);
            canvas.DrawCircle(rp, 3 * unit, dot);
        }

        DrawTextPanel(canvas, text, unit);
        canvas.Flush();
        return Encode(bitmap, quality);
    }

    /// <summary>
    /// Fotogramma rimpicciolito (al massimo <paramref name="maxWidth"/> pixel di larghezza) con le linee A e B:
    /// l'anteprima che resta accanto alla mappa durante la calibrazione.
    /// </summary>
    public static byte[] RenderSnapshotJpeg(VideoFrame frame, int videoWidth, LineSegment? lineA, LineSegment? lineB, int maxWidth = 720, int quality = 85)
    {
        float scale = Math.Min(1f, maxWidth / (float)frame.Width);
        int w = (int)Math.Round(frame.Width * scale);
        int h = (int)Math.Round(frame.Height * scale);
        float k = w / (float)videoWidth;   // pixel dell'immagine per pixel del video

        using SKBitmap full = ToBitmap(frame.Pixels, frame.Width, frame.Height, frame.Stride);
        using var small = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(small))
        {
            using SKImage image = SKImage.FromBitmap(full);
            canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
            foreach ((LineSegment? line, SKColor color) in new[] { (lineA, LineAColor), (lineB, LineBColor) })
            {
                if (line is { } l)
                {
                    var p1 = new SKPoint((float)(l.Start.X * k), (float)(l.Start.Y * k));
                    var p2 = new SKPoint((float)(l.End.X * k), (float)(l.End.Y * k));
                    using SKPaint shadow = Stroke(Shadow, 6);
                    using SKPaint pen = Stroke(color, 3);
                    canvas.DrawLine(p1, p2, shadow);
                    canvas.DrawLine(p1, p2, pen);
                }
            }

            canvas.Flush();
        }

        return Encode(small, quality);
    }

    private static void DrawLine(SKCanvas canvas, SKPoint a, SKPoint b, SKColor color, string label, bool highlighted, float unit)
    {
        float width = (highlighted ? 4.5f : 2.5f) * unit;
        using (SKPaint shadow = Stroke(Shadow, (highlighted ? 8 : 5) * unit))
        using (SKPaint pen = Stroke(color, width))
        {
            if (!highlighted)
            {
                // Tratteggio come nel programma: trattini di 4 spessori, spazi di 3.
                pen.PathEffect = SKPathEffect.CreateDash([4 * width, 3 * width], 0);
            }

            canvas.DrawLine(a, b, shadow);
            canvas.DrawLine(a, b, pen);
        }

        // Etichetta tonda all'estremo iniziale.
        float r = 13 * unit;
        var c = new SKPoint(a.X, a.Y - r - 6 * unit);
        using (SKPaint fill = Fill(color))
        using (SKPaint border = Stroke(SKColors.Black, 1.5f * unit))
        {
            canvas.DrawCircle(c, r, fill);
            canvas.DrawCircle(c, r, border);
        }

        using var ft = new TextLine(label, 15 * unit, SKColors.Black, bold: true, mono: false);
        ft.Draw(canvas, c.X - ft.Width / 2, c.Y - ft.Height / 2);
    }

    private static void DrawTextPanel(SKCanvas canvas, PassageImageText text, float unit)
    {
        float pad = 18 * unit;
        using var header = new TextLine(text.Header, 20 * unit, SKColors.White, bold: true, mono: false);
        using var main = new TextLine(text.MainTime, 54 * unit, SKColors.White, bold: true, mono: true);
        using var detail = new TextLine(text.Detail, 16 * unit, new SKColor(0xD0, 0xD6, 0xE0), bold: false, mono: false);
        using TextLine? footer = text.Footer is null ? null : new TextLine(text.Footer, 22 * unit, LineBColor, bold: true, mono: false);

        float width = new[] { header.Width, main.Width, detail.Width, footer?.Width ?? 0 }.Max() + 2 * pad;
        float height = pad + header.Height + 2 * unit + main.Height + 4 * unit + detail.Height + (footer is null ? 0 : 8 * unit + footer.Height) + pad;

        var panel = SKRect.Create(16 * unit, 16 * unit, width, height);
        using (SKPaint back = Fill(new SKColor(12, 16, 24, 200)))
        using (SKPaint border = Stroke(new SKColor(255, 255, 255, 90), 1 * unit))
        {
            canvas.DrawRoundRect(panel, 10 * unit, 10 * unit, back);
            canvas.DrawRoundRect(panel, 10 * unit, 10 * unit, border);
        }

        float x = panel.Left + pad;
        float y = panel.Top + pad;
        header.Draw(canvas, x, y);
        y += header.Height + 2 * unit;
        main.Draw(canvas, x, y);
        y += main.Height + 4 * unit;
        detail.Draw(canvas, x, y);
        if (footer is not null)
        {
            y += detail.Height + 8 * unit;
            footer.Draw(canvas, x, y);
        }
    }

    /// <summary>Pixel BGR (3 byte) del video in una bitmap BGRA di SkiaSharp.</summary>
    private static SKBitmap ToBitmap(byte[] bgr, int w, int h, int stride)
    {
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int s = y * stride, d = y * w * 4;
            for (int x = 0; x < w; x++, s += 3, d += 4)
            {
                bgra[d] = bgr[s];
                bgra[d + 1] = bgr[s + 1];
                bgra[d + 2] = bgr[s + 2];
                bgra[d + 3] = 255;
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap, int quality)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    private static SKPaint Stroke(SKColor color, float width) =>
        new() { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = true };

    private static SKPaint Fill(SKColor color) =>
        new() { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };

    private static SKTypeface Typeface(bool bold, bool mono)
    {
        var style = new SKFontStyle(bold ? SKFontStyleWeight.SemiBold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        foreach (string family in mono ? MonoFamilies : SansFamilies)
        {
            SKTypeface? t = SKFontManager.Default.MatchFamily(family, style);
            if (t is not null)
            {
                return t;
            }
        }

        return SKFontManager.Default.MatchFamily(null, style) ?? SKTypeface.Default;
    }

    /// <summary>Una riga di testo misurata: larghezza e altezza come la riga di testo in WPF (interlinea del carattere).</summary>
    private sealed class TextLine : IDisposable
    {
        private readonly string _text;
        private readonly SKFont _font;
        private readonly SKPaint _paint;
        private readonly float _ascent;

        public TextLine(string text, float size, SKColor color, bool bold, bool mono)
        {
            _text = text;
            _font = new SKFont(Typeface(bold, mono), size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
            _paint = new SKPaint { Color = color, IsAntialias = true };
            SKFontMetrics m = _font.Metrics;
            _ascent = -m.Ascent;
            Height = m.Descent - m.Ascent + m.Leading;
            Width = _font.MeasureText(text);
        }

        public float Width { get; }

        public float Height { get; }

        /// <summary>Disegna la riga con l'angolo in alto a sinistra in (x, y).</summary>
        public void Draw(SKCanvas canvas, float x, float y) => canvas.DrawText(_text, x, y + _ascent, SKTextAlign.Left, _font, _paint);

        public void Dispose()
        {
            _font.Dispose();
            _paint.Dispose();
        }
    }
}
