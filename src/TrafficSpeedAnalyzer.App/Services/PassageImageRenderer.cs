using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Privacy;
using TrafficSpeedAnalyzer.Core.Geometry;
using LineSegment = TrafficSpeedAnalyzer.Core.Geometry.LineSegment;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Video;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.Services;

/// <summary>Testi da sovrimprimere su un'immagine di ingresso o uscita.</summary>
public sealed record PassageImageText(string Header, string MainTime, string Detail, string? Footer);

/// <summary>
/// Disegna il fotogramma a piena risoluzione con le linee, il punto di riferimento del veicolo
/// e il tempo sovrimpresso, e lo codifica in JPEG. Con <c>pixelate</c> le zone indicate
/// (coordinate del video) vengono pixellate: versione anonimizzata.
/// </summary>
public static class PassageImageRenderer
{
    public static readonly Color LineAColor = Color.FromRgb(0x4C, 0xC2, 0xFF);
    public static readonly Color LineBColor = Color.FromRgb(0xFF, 0xB5, 0x47);
    public static readonly Color PointColor = Color.FromRgb(0xFF, 0x5C, 0x8A);

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
        double sx = w / (double)videoWidth;
        double sy = h / (double)videoHeight;
        Point Map(Point2 p) => new(p.X * sx, p.Y * sy);

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

        BitmapSource source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, pixels, frame.Stride);
        double unit = Math.Max(1.0, h / 1080.0);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, w, h));

            DrawLine(dc, calibration.LineA, LineAColor, "A", highlightedLine == DetectionLine.A, unit, Map);
            DrawLine(dc, calibration.LineB, LineBColor, "B", highlightedLine == DetectionLine.B, unit, Map);

            // Punto di riferimento del veicolo nel fotogramma salvato.
            Point rp = Map(referencePoint);
            var ring = new Pen(new SolidColorBrush(PointColor), 3 * unit);
            ring.Freeze();
            dc.DrawEllipse(null, new Pen(Brushes.Black, 6 * unit), rp, 14 * unit, 14 * unit);
            dc.DrawEllipse(null, ring, rp, 14 * unit, 14 * unit);
            dc.DrawEllipse(new SolidColorBrush(PointColor), null, rp, 3 * unit, 3 * unit);

            DrawTextPanel(dc, text, unit);
        }

        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void DrawLine(DrawingContext dc, LineSegment line, Color color, string label, bool highlighted, double unit, Func<Point2, Point> map)
    {
        Point a = map(line.Start);
        Point b = map(line.End);
        var shadow = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), (highlighted ? 8 : 5) * unit);
        var pen = new Pen(new SolidColorBrush(color), (highlighted ? 4.5 : 2.5) * unit);
        if (!highlighted)
        {
            pen.DashStyle = new DashStyle([4, 3], 0);
        }

        dc.DrawLine(shadow, a, b);
        dc.DrawLine(pen, a, b);

        // Etichetta tonda all'estremo iniziale.
        double r = 13 * unit;
        Point c = new(a.X, a.Y - r - 6 * unit);
        dc.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.Black, 1.5 * unit), c, r, r);
        FormattedText ft = Text(label, 15 * unit, Brushes.Black, bold: true);
        dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
    }

    private static void DrawTextPanel(DrawingContext dc, PassageImageText text, double unit)
    {
        double pad = 18 * unit;
        FormattedText header = Text(text.Header, 20 * unit, Brushes.White, bold: true);
        FormattedText main = Text(text.MainTime, 54 * unit, Brushes.White, bold: true, mono: true);
        FormattedText detail = Text(text.Detail, 16 * unit, new SolidColorBrush(Color.FromRgb(0xD0, 0xD6, 0xE0)));
        FormattedText? footer = text.Footer is null ? null : Text(text.Footer, 22 * unit, new SolidColorBrush(LineBColor), bold: true);

        double width = new[] { header.Width, main.Width, detail.Width, footer?.Width ?? 0 }.Max() + 2 * pad;
        double height = pad + header.Height + 2 * unit + main.Height + 4 * unit + detail.Height + (footer is null ? 0 : 8 * unit + footer.Height) + pad;

        var panel = new Rect(16 * unit, 16 * unit, width, height);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 12, 16, 24)), new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1 * unit), panel, 10 * unit, 10 * unit);

        double x = panel.X + pad;
        double y = panel.Y + pad;
        dc.DrawText(header, new Point(x, y));
        y += header.Height + 2 * unit;
        dc.DrawText(main, new Point(x, y));
        y += main.Height + 4 * unit;
        dc.DrawText(detail, new Point(x, y));
        if (footer is not null)
        {
            y += detail.Height + 8 * unit;
            dc.DrawText(footer, new Point(x, y));
        }
    }

    private static FormattedText Text(string s, double size, Brush brush, bool bold = false, bool mono = false)
    {
        var typeface = new Typeface(
            new FontFamily(mono ? "Cascadia Mono, Consolas" : "Segoe UI Variable Display, Segoe UI"),
            FontStyles.Normal,
            bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontStretches.Normal);
        return new FormattedText(s, Loc.Culture, FlowDirection.LeftToRight, typeface, size, brush, 1.0);
    }
}
