using System.Text;
using System.Text.RegularExpressions;
using TrafficSpeedAnalyzer.Core.Export;
using Xunit;

namespace TrafficSpeedAnalyzer.Core.Tests;

public sealed class PdfWriterTests
{
    // JPEG 32×18 a colori, generato una volta per i test.
    private static readonly byte[] SmallJpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAoHBwgHBgoICAgLCgoLDhgQDg0NDh0VFhEYIx8lJCIfIiEmKzcvJik0KSEiMEExNDk7Pj4+JS5ESUM8SDc9Pjv/2wBDAQoLCw4NDhwQEBw7KCIoOzs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozv/wAARCAASACADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwCrUX2qH+/+hqWqP2KX+8n5muvETqxt7NXLwNHC1eb6xPlta3436MtpNHI2EbJxnpT6r29u8MhZipBGOKsVdGU5QvNWZhjKdGnV5aErx7hRRRWxyhRRRQB//9k=");

    private static byte[] Build(Action<PdfWriter> fill)
    {
        using var ms = new MemoryStream();
        using (var pdf = new PdfWriter(ms, leaveOpen: true))
        {
            fill(pdf);
        }

        return ms.ToArray();
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void Document_HasHeaderTrailerAndPageCount()
    {
        byte[] bytes = Build(pdf =>
        {
            for (int i = 0; i < 3; i++)
            {
                PdfPage page = pdf.NewPage();
                page.Text(40, 60, $"Pagina {i + 1}", 12, bold: i == 0);
                page.FillRect(40, 80, 100, 20, (0.2, 0.6, 0.3));
                page.Line(40, 110, 200, 110, 1, (0, 0, 0));
                page.Jpeg(SmallJpeg, 40, 120, 320, 180);
            }
        });

        string text = Latin1(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("/Count 3", text);
        Assert.Equal(3, Regex.Matches(text, @"/Type /Page\b").Count);
        Assert.Equal(3, Regex.Matches(text, "/Filter /DCTDecode").Count);
    }

    [Fact]
    public void Xref_OffsetsPointToTheirObjects()
    {
        byte[] bytes = Build(pdf =>
        {
            pdf.NewPage().Jpeg(SmallJpeg, 0, 0, 100, 60);
            pdf.NewPage().Text(10, 10, "ciao", 10);
        });
        string text = Latin1(bytes);

        int startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        Assert.StartsWith("xref", text[startxref..]);
        MatchCollection entries = Regex.Matches(text[startxref..], @"(\d{10}) 00000 n");
        Assert.NotEmpty(entries);
        for (int i = 0; i < entries.Count; i++)
        {
            int offset = int.Parse(entries[i].Groups[1].Value);
            Assert.StartsWith($"{i + 1} 0 obj", text[offset..]);
        }
    }

    [Fact]
    public void JpegSize_ReadsDimensions()
    {
        (int w, int h, int c) = PdfWriter.JpegSize(SmallJpeg);
        Assert.Equal((32, 18, 3), (w, h, c));
    }

    [Fact]
    public void JpegSize_RejectsNonJpeg() =>
        Assert.ThrowsAny<Exception>(() => PdfWriter.JpegSize([1, 2, 3, 4]));

    [Fact]
    public void Text_IsEscapedAndWinAnsiEncoded()
    {
        byte[] bytes = Build(pdf => pdf.NewPage().Text(10, 10, "Velocità (km/h) A → B è €", 10));
        string text = Latin1(bytes);
        Assert.Contains("(Velocit\u00e0 \\(km/h\\) A -> B \u00e8 \u0080)", text);
    }

    [Fact]
    public void Wrap_SplitsLongTextWithinWidth()
    {
        string sentence = string.Join(" ", Enumerable.Repeat("parola", 40));
        IReadOnlyList<string> lines = PdfWriter.Wrap(sentence, 10, 150);
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(PdfWriter.TextWidth(l, 10) <= 150));
        Assert.Equal(sentence, string.Join(" ", lines));
    }
}
