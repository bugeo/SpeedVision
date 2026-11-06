using System.IO;
using TrafficSpeedAnalyzer.App.ViewModels;
using TrafficSpeedAnalyzer.Core.Export;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.Services;

/// <summary>Dati della prima pagina del rapporto.</summary>
public sealed record PdfReportSummary(
    string VideoName,
    string? RecordingStart,
    double DistanceMeters,
    string CalibrationMethod,
    string? Filter,
    IReadOnlyList<PassageRow> Rows,
    IReadOnlyList<HistogramBin> Histogram);

/// <summary>
/// Rapporto PDF delle misure: una pagina di riepilogo (video, calibrazione, statistiche e grafico), poi una pagina
/// per ogni veicolo con gli stessi dati del CSV e le immagini di ingresso e di uscita. Se ne creano due versioni:
/// con le immagini in chiaro e con i veicoli pixellati (<see cref="Anonymized"/>).
/// Le pagine si aggiungono una alla volta durante l'esportazione.
/// </summary>
public sealed class PdfReport : IDisposable
{
    private const double Margin = 40;

    private static readonly (double, double, double) Ink = (0.10, 0.10, 0.12);
    private static readonly (double, double, double) Muted = (0.42, 0.44, 0.48);
    private static readonly (double, double, double) Accent = (0.12, 0.66, 0.37);
    private static readonly (double, double, double) Rule = (0.85, 0.86, 0.88);
    private static readonly (double, double, double) Band = (0.95, 0.96, 0.97);
    private static readonly (double, double, double) LogoBackground = (1 / 255.0, 8 / 255.0, 25 / 255.0);   // fondo del logo

    private readonly PdfWriter _pdf;
    private readonly string _videoName;
    private readonly int _totalPages;
    private readonly bool _showLane;
    private readonly DateTimeOffset? _recordingStart;
    private readonly byte[]? _logoJpeg;

    /// <param name="anonymized">Rapporto con i veicoli pixellati (true) o con le immagini in chiaro (false).</param>
    /// <param name="logoJpeg">Logo di SpeedVision (JPEG su fondo scuro) per l'intestazione della prima pagina.</param>
    public PdfReport(Stream output, string videoName, int passageCount, bool showLane, DateTimeOffset? recordingStart, bool anonymized, byte[]? logoJpeg = null)
    {
        Anonymized = anonymized;
        _logoJpeg = logoJpeg;
        _pdf = new PdfWriter(output);
        _videoName = videoName;
        _totalPages = passageCount + 1;
        _showLane = showLane;
        _recordingStart = recordingStart;
    }

    /// <summary>Versione con i veicoli pixellati.</summary>
    public bool Anonymized { get; }

    private double ContentWidth => PdfWriter.A4Width - (2 * Margin);

    public void AddSummary(PdfReportSummary s)
    {
        PdfPage page;
        double y;
        if (_logoJpeg is { } logo)
        {
            // Fascia scura con il logo al posto dell'intestazione: il logo ha la scritta «Speed» bianca.
            page = _pdf.NewPage();
            const double bandH = 78;
            (int lw, int lh, _) = PdfWriter.JpegSize(logo);
            double logoH = bandH - 16, logoW = logoH * lw / lh;
            page.FillRect(Margin, Margin - 6, ContentWidth, bandH, LogoBackground);
            page.Jpeg(logo, Margin + 12, Margin - 6 + ((bandH - logoH) / 2), logoW, logoH);
            y = Margin + bandH + 30;
        }
        else
        {
            page = NewPage();
            y = Margin + 46;
        }

        page.Text(Margin, y, Loc.T("Rapporto delle misure di velocità"), 20, bold: true, color: Ink);
        y += 26;

        var info = new List<(string, string)>
        {
            (Loc.T("Video"), s.VideoName),
            (Loc.T("Data esportazione"), DateTime.Now.ToString("dd/MM/yyyy HH:mm", Loc.Culture)),
            (Loc.T("Inizio registrazione"), s.RecordingStart ?? Loc.T("non indicato (orari = tempo del video)")),
            (Loc.T("Distanza tra le linee"), s.DistanceMeters.ToString("0.###", Loc.Culture) + " m"),
            (Loc.T("Calibrazione"), s.CalibrationMethod),
            (Loc.T("Immagini"), Anonymized ? Loc.T("veicoli pixellati") : Loc.T("in chiaro")),
        };
        if (s.Filter is { } f)
        {
            info.Add((Loc.T("Filtro"), f));
        }

        y = Table(page, y, info, columns: 1);

        // Statistiche
        y += 14;
        List<double> speeds = s.Rows.Where(r => !r.HideSpeed).Select(r => r.Passage.Speed.SpeedKmh).ToList();
        double tileW = (ContentWidth - 20) / 4;
        (string Label, string Value)[] tiles =
        [
            (Loc.T("Passaggi"), s.Rows.Count.ToString(Loc.Culture)),
            (Loc.T("Media"), speeds.Count > 0 ? speeds.Average().ToString("0.0", Loc.Culture) + " km/h" : "–"),
            (Loc.T("Minima"), speeds.Count > 0 ? speeds.Min().ToString("0.0", Loc.Culture) + " km/h" : "–"),
            (Loc.T("Massima"), speeds.Count > 0 ? speeds.Max().ToString("0.0", Loc.Culture) + " km/h" : "–"),
        ];
        for (int i = 0; i < tiles.Length; i++)
        {
            double x = Margin + (i * (tileW + 6.67));
            page.FillRect(x, y, tileW, 54, Band);
            page.Text(x + 10, y + 18, tiles[i].Label, 9, color: Muted);
            page.Text(x + 10, y + 40, tiles[i].Value, 16, bold: true, color: Ink);
        }

        y += 54 + 26;

        // Grafico: passaggi per fascia di velocità
        if (s.Histogram.Count > 0)
        {
            page.Text(Margin, y, Loc.T("Distribuzione delle velocità"), 12, bold: true, color: Ink);
            y += 12;
            double chartH = 150, chartTop = y + 14;
            int max = Math.Max(1, s.Histogram.Max(b => b.Count));
            double colW = ContentWidth / s.Histogram.Count;
            page.Line(Margin, chartTop + chartH, Margin + ContentWidth, chartTop + chartH, 0.8, Muted);
            for (int i = 0; i < s.Histogram.Count; i++)
            {
                HistogramBin b = s.Histogram[i];
                double x = Margin + (i * colW);
                double h = chartH * b.Count / max;
                if (b.Count > 0)
                {
                    page.FillRect(x + 1.5, chartTop + chartH - h, colW - 3, h, Accent);
                    string n = b.Count.ToString(Loc.Culture);
                    page.Text(x + ((colW - PdfWriter.TextWidth(n, 8)) / 2), chartTop + chartH - h - 3, n, 8, color: Muted);
                }

                if (b.Label.Length > 0)
                {
                    page.Text(x + 1, chartTop + chartH + 11, b.Label, 8, color: Muted);
                }
            }

            y = chartTop + chartH + 26;
            page.Text(Margin, y, Loc.T("Velocità (km/h) · numero di passaggi per fascia di 5 km/h"), 8.5, color: Muted);
            y += 20;
        }

        page.Paragraph(Margin, y, ContentWidth,
            Anonymized
                ? Loc.T("Nelle pagine seguenti, una per veicolo: i dati della misura (gli stessi del CSV) e le immagini di ingresso e di uscita, con tutti i veicoli pixellati.")
                : Loc.T("Nelle pagine seguenti, una per veicolo: i dati della misura (gli stessi del CSV) e le immagini di ingresso e di uscita in chiaro. Possono contenere targhe e persone (dati personali): per condividerle usa il rapporto anonimizzato."),
            9.5, color: Ink);
        Footer(page, 1);
    }

    /// <summary>Pagina di un veicolo: dati della misura e immagini anonimizzate di ingresso e di uscita.</summary>
    public void AddPassage(PassageRow row, byte[] entryJpeg, byte[] exitJpeg, int pageNumber)
    {
        PdfPage page = NewPage();
        double y = Margin + 46;
        page.Text(Margin, y, Loc.S($"Veicolo {row.IdText}"), 18, bold: true, color: Ink);
        string speed = row.HideSpeed ? "—" : row.Passage.Speed.SpeedKmh.ToString("0.0", Loc.Culture) + " km/h";
        page.Text(PdfWriter.A4Width - Margin - PdfWriter.TextWidth(speed, 18, true), y, speed, 18, bold: true, color: Accent);
        y += 18;

        var data = new List<(string, string)>
        {
            (Loc.T("Verso"), CsvExporter.FormatDirection(row.Passage.Direction)),
        };
        if (_showLane && row.Lane >= 0)
        {
            data.Add((Loc.T("Corsia"), (row.Lane + 1).ToString(Loc.Culture)));
        }

        data.Add((Loc.T("Orario ingresso"), CsvExporter.FormatTime(row.Passage.Entry.TimeSeconds, _recordingStart)));
        data.Add((Loc.T("Orario uscita"), CsvExporter.FormatTime(row.Passage.Exit.TimeSeconds, _recordingStart)));
        data.Add((Loc.T("Tempo impiegato (s)"), row.Passage.Speed.DurationSeconds.ToString("F3", Loc.Culture)));
        data.Add((Loc.T("Velocità (km/h)"), row.HideSpeed ? "—" : row.Passage.Speed.SpeedKmh.ToString("F1", Loc.Culture)));
        data.Add((Loc.T("Incertezza"), row.UncertaintyText));
        data.Add((Loc.T("Rilevamento"), row.SourceText));
        y = Table(page, y, data, columns: 2);

        // Immagini: ingresso e uscita una sotto l'altra, alla dimensione che entra nella pagina.
        y += 10;
        double footerTop = PdfWriter.A4Height - Margin - 14;
        double captionH = 16, gap = 10;
        double available = footerTop - y - (2 * captionH) - gap - 6;
        foreach ((byte[] jpeg, string caption) in new[] { (entryJpeg, Loc.T("Ingresso")), (exitJpeg, Loc.T("Uscita")) })
        {
            (int pw, int ph, _) = PdfWriter.JpegSize(jpeg);
            double h = Math.Min(available / 2, ContentWidth * ph / pw);
            double w = h * pw / ph;
            page.Text(Margin, y + 11, Anonymized ? caption + " · " + Loc.T("veicoli pixellati") : caption, 9.5, bold: true, color: Muted);
            y += captionH;
            page.Jpeg(jpeg, Margin + ((ContentWidth - w) / 2), y, w, h);
            y += h + gap;
        }

        Footer(page, pageNumber);
    }

    public void Dispose() => _pdf.Dispose();

    private PdfPage NewPage()
    {
        PdfPage page = _pdf.NewPage();
        page.Text(Margin, Margin + 4, "SpeedVision", 10, bold: true, color: Accent);
        string right = _videoName;
        page.Text(PdfWriter.A4Width - Margin - PdfWriter.TextWidth(right, 9), Margin + 4, right, 9, color: Muted);
        page.Line(Margin, Margin + 12, PdfWriter.A4Width - Margin, Margin + 12, 0.6, Rule);
        return page;
    }

    private void Footer(PdfPage page, int number)
    {
        double y = PdfWriter.A4Height - Margin + 6;
        page.Line(Margin, y - 14, PdfWriter.A4Width - Margin, y - 14, 0.6, Rule);
        page.Text(Margin, y, Loc.T("Misure non omologate, non utilizzabili per sanzioni."), 8, color: Muted);
        string n = Loc.S($"Pagina {number} di {_totalPages}");
        page.Text(PdfWriter.A4Width - Margin - PdfWriter.TextWidth(n, 8), y, n, 8, color: Muted);
    }

    /// <summary>Tabella etichetta/valore su una o due colonne; restituisce la y sotto la tabella.</summary>
    private double Table(PdfPage page, double y, IReadOnlyList<(string Label, string Value)> rows, int columns)
    {
        double colW = ContentWidth / columns;
        double rowH = 22;
        int perColumn = (int)Math.Ceiling(rows.Count / (double)columns);
        for (int i = 0; i < perColumn; i++)
        {
            if (i % 2 == 0)
            {
                page.FillRect(Margin, y + (i * rowH), ContentWidth, rowH, Band);
            }
        }

        for (int i = 0; i < rows.Count; i++)
        {
            int col = i / perColumn, r = i % perColumn;
            double x = Margin + (col * colW) + 8, ry = y + (r * rowH) + 15;
            page.Text(x, ry, rows[i].Label, 9, color: Muted);
            double labelW = Math.Min(colW * 0.45, 150);
            string value = rows[i].Value;
            double maxW = colW - labelW - 16;
            while (value.Length > 4 && PdfWriter.TextWidth(value, 10, true) > maxW)
            {
                value = value.TrimEnd('…')[..^1] + "…";
            }

            page.Text(x + labelW, ry, value, 10, bold: true, color: Ink);
        }

        return y + (perColumn * rowH);
    }
}
