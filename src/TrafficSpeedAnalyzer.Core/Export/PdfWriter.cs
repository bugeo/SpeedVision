using System.Globalization;
using System.Text;

namespace TrafficSpeedAnalyzer.Core.Export;

/// <summary>
/// Scrittore PDF minimo e senza dipendenze, per il rapporto delle misure: pagine A4, testo in Helvetica
/// (normale e grassetto, codifica WinAnsi), rettangoli pieni, linee e immagini JPEG incorporate così come sono.
/// Le pagine si scrivono su uno <see cref="Stream"/> man mano che si aggiungono, così anche un rapporto con
/// molte immagini non resta tutto in memoria. Coordinate in punti (1/72 di pollice) con l'origine in alto a
/// sinistra, come sullo schermo.
/// </summary>
public sealed class PdfWriter : IDisposable
{
    public const double A4Width = 595.28, A4Height = 841.89;

    private const int CatalogId = 1, PagesId = 2, FontRegularId = 3, FontBoldId = 4;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Stream _out;
    private readonly bool _leaveOpen;
    private readonly List<long> _offsets = [0, 0, 0, 0, 0];   // indice = numero dell'oggetto (lo 0 non si usa)
    private readonly List<int> _pageIds = [];
    private PdfPage? _current;
    private bool _finished;

    public PdfWriter(Stream output, bool leaveOpen = false)
    {
        _out = output;
        _leaveOpen = leaveOpen;
        WriteRaw("%PDF-1.4\n%âãÏÓ\n");
    }

    public int PageCount => _pageIds.Count + (_current is null ? 0 : 1);

    /// <summary>Nuova pagina A4 verticale; la pagina precedente viene chiusa e scritta.</summary>
    public PdfPage NewPage()
    {
        FlushPage();
        _current = new PdfPage(this);
        return _current;
    }

    /// <summary>Chiude il documento: scrive le ultime parti, i caratteri, l'indice e la tabella dei riferimenti.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        FlushPage();
        _offsets[FontRegularId] = _out.Position;
        WriteRaw($"{FontRegularId} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        _offsets[FontBoldId] = _out.Position;
        WriteRaw($"{FontBoldId} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");
        _offsets[PagesId] = _out.Position;
        string kids = string.Join(" ", _pageIds.Select(id => id.ToString(Inv) + " 0 R"));
        WriteRaw($"{PagesId} 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {_pageIds.Count} >>\nendobj\n");
        _offsets[CatalogId] = _out.Position;
        WriteRaw($"{CatalogId} 0 obj\n<< /Type /Catalog /Pages {PagesId} 0 R >>\nendobj\n");

        long xref = _out.Position;
        var sb = new StringBuilder();
        sb.Append(Inv, $"xref\n0 {_offsets.Count}\n0000000000 65535 f \n");
        for (int i = 1; i < _offsets.Count; i++)
        {
            sb.Append(_offsets[i].ToString("D10", Inv)).Append(" 00000 n \n");
        }

        sb.Append(Inv, $"trailer\n<< /Size {_offsets.Count} /Root {CatalogId} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        WriteRaw(sb.ToString());
        _out.Flush();
        _finished = true;
    }

    public void Dispose()
    {
        Finish();
        if (!_leaveOpen)
        {
            _out.Dispose();
        }
    }

    // ───────────── Interno ─────────────

    internal int AddObject(byte[] dictionaryHead, byte[]? stream)
    {
        int id = _offsets.Count;
        _offsets.Add(_out.Position);
        WriteRaw($"{id} 0 obj\n");
        _out.Write(dictionaryHead);
        if (stream is not null)
        {
            WriteRaw("\nstream\n");
            _out.Write(stream);
            WriteRaw("\nendstream");
        }

        WriteRaw("\nendobj\n");
        return id;
    }

    private void FlushPage()
    {
        if (_current is not { } page)
        {
            return;
        }

        byte[] content = page.Content();
        int contentId = AddObject(Encoding.ASCII.GetBytes($"<< /Length {content.Length} >>"), content);
        string xobjects = string.Join(" ", page.Images.Select(i => $"/{i.Name} {i.Id} 0 R"));
        string dict =
            $"<< /Type /Page /Parent {PagesId} 0 R /MediaBox [0 0 {F(A4Width)} {F(A4Height)}] " +
            $"/Resources << /Font << /F1 {FontRegularId} 0 R /F2 {FontBoldId} 0 R >> /XObject << {xobjects} >> >> " +
            $"/Contents {contentId} 0 R >>";
        _pageIds.Add(AddObject(Encoding.ASCII.GetBytes(dict), null));
        _current = null;
    }

    private void WriteRaw(string s) => _out.Write(Encoding.Latin1.GetBytes(s));

    internal static string F(double v) => v.ToString("0.###", Inv);

    /// <summary>Dimensioni in pixel di un JPEG (marcatore SOF), senza decodificarlo.</summary>
    public static (int Width, int Height, int Components) JpegSize(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new ArgumentException("Non è un file JPEG.", nameof(jpeg));
        }

        int i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                i++;
                continue;
            }

            byte marker = jpeg[i + 1];
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7 || marker == 0xFF)
            {
                i += marker == 0xFF ? 1 : 2;
                continue;
            }

            int length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            bool sof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (sof)
            {
                int height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                int width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                return (width, height, jpeg[i + 9]);
            }

            i += 2 + length;
        }

        throw new ArgumentException("JPEG senza dimensioni (marcatore SOF mancante).", nameof(jpeg));
    }

    /// <summary>
    /// Testo nella codifica WinAnsi dei caratteri standard del PDF. I caratteri che non vi esistono vengono
    /// sostituiti da un equivalente («→» con «->», «≥» con «>=»), altrimenti da «?».
    /// </summary>
    internal static byte[] WinAnsi(string text)
    {
        var bytes = new List<byte>(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '€': bytes.Add(0x80); break;
                case '‚': bytes.Add(0x82); break;
                case '„': bytes.Add(0x84); break;
                case '…': bytes.Add(0x85); break;
                case '‘': bytes.Add(0x91); break;
                case '’': bytes.Add(0x92); break;
                case '“': bytes.Add(0x93); break;
                case '”': bytes.Add(0x94); break;
                case '•': bytes.Add(0x95); break;
                case '–': bytes.Add(0x96); break;
                case '—': bytes.Add(0x97); break;
                case '™': bytes.Add(0x99); break;
                case '→': bytes.AddRange("->"u8); break;
                case '←': bytes.AddRange("<-"u8); break;
                case '≥': bytes.AddRange(">="u8); break;
                case '≤': bytes.AddRange("<="u8); break;
                case ' ': bytes.Add(0x20); break;
                default:
                    bytes.Add(c is >= ' ' and <= '~' or >= '¡' and <= 'ÿ' ? (byte)c : (byte)'?');
                    break;
            }
        }

        return [.. bytes];
    }

    /// <summary>Larghezza approssimata del testo in Helvetica (per andare a capo e allineare).</summary>
    public static double TextWidth(string text, double size, bool bold = false)
    {
        double units = 0;
        foreach (char c in text)
        {
            units += c switch
            {
                ' ' => 278,
                'i' or 'j' or 'l' or '\'' or '|' or '.' or ',' or ':' or ';' or '!' => 240,
                'f' or 't' or 'r' or 'I' or '(' or ')' or '[' or ']' or '-' => 320,
                'm' or 'M' or 'W' => 840,
                'w' => 720,
                >= 'A' and <= 'Z' => 680,
                >= '0' and <= '9' => 556,
                _ => 540,
            };
        }

        return units / 1000 * size * (bold ? 1.06 : 1.0);
    }

    /// <summary>Divide il testo in righe che stanno nella larghezza indicata.</summary>
    public static IReadOnlyList<string> Wrap(string text, double size, double width, bool bold = false)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && TextWidth(candidate, size, bold) > width)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }

            lines.Add(line.ToString());
        }

        return lines;
    }
}

/// <summary>Una pagina del rapporto: i comandi di disegno vengono raccolti e scritti alla chiusura della pagina.</summary>
public sealed class PdfPage
{
    private readonly PdfWriter _writer;
    private readonly MemoryStream _content = new();

    internal PdfPage(PdfWriter writer) => _writer = writer;

    internal List<(string Name, int Id)> Images { get; } = [];

    public double Width => PdfWriter.A4Width;

    public double Height => PdfWriter.A4Height;

    /// <summary>Testo con la linea di base a <paramref name="y"/> punti dall'alto. Colore in grigio (0 nero, 1 bianco) o RGB.</summary>
    public void Text(double x, double y, string text, double size, bool bold = false, (double R, double G, double B)? color = null)
    {
        (double r, double g, double b) = color ?? (0.1, 0.1, 0.12);
        Write($"BT {Rgb(r, g, b)} rg /{(bold ? "F2" : "F1")} {PdfWriter.F(size)} Tf {PdfWriter.F(x)} {PdfWriter.F(Height - y)} Td (");
        foreach (byte c in PdfWriter.WinAnsi(text))
        {
            if (c is (byte)'(' or (byte)')' or (byte)'\\')
            {
                _content.WriteByte((byte)'\\');
            }

            _content.WriteByte(c);
        }

        Write(") Tj ET\n");
    }

    /// <summary>Testo che va a capo nella larghezza indicata; restituisce la y sotto l'ultima riga.</summary>
    public double Paragraph(double x, double y, double width, string text, double size, bool bold = false, (double R, double G, double B)? color = null, double lineHeight = 1.3)
    {
        foreach (string line in PdfWriter.Wrap(text, size, width, bold))
        {
            Text(x, y, line, size, bold, color);
            y += size * lineHeight;
        }

        return y;
    }

    /// <summary>Rettangolo pieno (x, y in alto a sinistra).</summary>
    public void FillRect(double x, double y, double w, double h, (double R, double G, double B) color) =>
        Write($"{Rgb(color.R, color.G, color.B)} rg {PdfWriter.F(x)} {PdfWriter.F(Height - y - h)} {PdfWriter.F(w)} {PdfWriter.F(h)} re f\n");

    public void Line(double x1, double y1, double x2, double y2, double width, (double R, double G, double B) color) =>
        Write($"{Rgb(color.R, color.G, color.B)} RG {PdfWriter.F(width)} w {PdfWriter.F(x1)} {PdfWriter.F(Height - y1)} m {PdfWriter.F(x2)} {PdfWriter.F(Height - y2)} l S\n");

    /// <summary>Immagine JPEG nel rettangolo indicato (x, y in alto a sinistra). Il file viene incorporato così com'è.</summary>
    public void Jpeg(byte[] jpeg, double x, double y, double w, double h)
    {
        (int pw, int ph, int components) = PdfWriter.JpegSize(jpeg);
        string colorSpace = components switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" };
        string head = $"<< /Type /XObject /Subtype /Image /Width {pw} /Height {ph} /ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>";
        int id = _writer.AddObject(Encoding.ASCII.GetBytes(head), jpeg);
        string name = "Im" + Images.Count.ToString(CultureInfo.InvariantCulture);
        Images.Add((name, id));
        Write($"q {PdfWriter.F(w)} 0 0 {PdfWriter.F(h)} {PdfWriter.F(x)} {PdfWriter.F(Height - y - h)} cm /{name} Do Q\n");
    }

    internal byte[] Content() => _content.ToArray();

    private static string Rgb(double r, double g, double b) => $"{PdfWriter.F(r)} {PdfWriter.F(g)} {PdfWriter.F(b)}";

    private void Write(string s) => _content.Write(Encoding.ASCII.GetBytes(s));
}
