using System.Text;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Export;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Measurement;
using TrafficSpeedAnalyzer.Core.Time;

namespace TrafficSpeedAnalyzer.Core.Tests;

public class CalibrationValidatorTests
{
    private static readonly LineSegment A = new(new Point2(200, 100), new Point2(220, 600));
    private static readonly LineSegment B = new(new Point2(700, 100), new Point2(720, 600));

    [Fact]
    public void GoodConfiguration_HasNoIssues()
    {
        Assert.Empty(CalibrationValidator.Validate(A, B, 20));
    }

    [Fact]
    public void MissingEverything_ReportsThreeErrors()
    {
        var issues = CalibrationValidator.Validate(null, null, null);
        Assert.Equal(3, issues.Count(i => i.Severity == IssueSeverity.Error));
        Assert.True(CalibrationValidator.HasErrors(issues));
    }

    [Fact]
    public void CrossingLines_IsAnError()
    {
        var crossing = new LineSegment(new Point2(100, 300), new Point2(400, 350));
        var issues = CalibrationValidator.Validate(A, crossing, 20);
        Assert.Contains(issues, i => i.Code == "LINES_CROSS" && i.Severity == IssueSeverity.Error);
    }

    [Theory]
    [InlineData(0, "DIST_INVALID", IssueSeverity.Error)]
    [InlineData(-3, "DIST_INVALID", IssueSeverity.Error)]
    [InlineData(1.5, "DIST_SHORT", IssueSeverity.Warning)]
    [InlineData(500, "DIST_LONG", IssueSeverity.Warning)]
    public void Distance_IsChecked(double meters, string code, IssueSeverity severity)
    {
        var issues = CalibrationValidator.Validate(A, B, meters);
        Assert.Contains(issues, i => i.Code == code && i.Severity == severity);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void Distance_FromTwoMeters_HasNoCloseLinesWarning(double meters)
    {
        Assert.DoesNotContain(CalibrationValidator.Validate(A, B, meters), i => i.Code == "DIST_SHORT");
    }

    [Fact]
    public void LinesCloseInTheImage_AreNotWarnedAbout()
    {
        // Linee a pochi pixel ma a più di 2 m: nessun avviso, conta solo la distanza in metri.
        var near = new LineSegment(new Point2(A.Start.X + 10, A.Start.Y), new Point2(A.End.X + 10, A.End.Y));
        Assert.Empty(CalibrationValidator.Validate(A, near, 5));
    }

    [Fact]
    public void NonParallelLines_AreAccepted()
    {
        // In prospettiva le linee a terra perpendicolari alla strada convergono verso il punto di fuga.
        var tilted = new LineSegment(new Point2(700, 100), new Point2(1100, 400));
        var issues = CalibrationValidator.Validate(A, tilted, 20);
        Assert.Empty(issues);
    }

    [Fact]
    public void Settings_ComputeSideAndScale()
    {
        var cal = new CalibrationSettings(
            new LineSegment(new Point2(200, 0), new Point2(200, 100)),
            new LineSegment(new Point2(600, 0), new Point2(600, 100)),
            20);
        Assert.Equal(400, cal.MeanPixelSeparation, 9);
        Assert.Equal(0.05, cal.ApproxMetersPerPixel, 9);
        Assert.Equal(cal.LineA.SideOf(new Point2(400, 50)), cal.SideTowardsOther(DetectionLine.A));
        Assert.Equal(cal.LineB.SideOf(new Point2(400, 50)), cal.SideTowardsOther(DetectionLine.B));
    }
}

public class TimeFormattingTests
{
    [Theory]
    [InlineData(0, "00:00.000")]
    [InlineData(12.3456, "00:12.346")]
    [InlineData(59.9996, "01:00.000")]
    [InlineData(3725.5, "1:02:05.500")]
    public void VideoTime(double seconds, string expected)
    {
        Assert.Equal(expected, TimeFormatting.FormatVideoTime(seconds));
    }

    [Theory]
    [InlineData(12.3456, "00:00:12.346")]
    [InlineData(3725.5, "01:02:05.500")]
    public void VideoTimeLong(double seconds, string expected)
    {
        Assert.Equal(expected, TimeFormatting.FormatVideoTimeLong(seconds));
    }

    [Fact]
    public void Clock_AddsVideoTimeToRecordingStart()
    {
        var start = new DateTimeOffset(2026, 10, 25, 22, 15, 0, TimeSpan.FromHours(1));
        Assert.Equal("22:15:03.412", TimeFormatting.FormatClock(start, 3.4124));
        Assert.Equal("22:16:00.000", TimeFormatting.FormatClock(start, 59.9999));
    }

    [Fact]
    public void Seconds_UseItalianDecimalComma()
    {
        Assert.Equal("1,437", TimeFormatting.FormatSeconds(1.4371));
    }
}

public class CsvExporterTests
{
    private static VehiclePassage Passage(int id, double tEntry, double tExit, double distance = 20, TravelDirection dir = TravelDirection.AToB)
    {
        var obs = new TrackObservation(0, tEntry, new Point2(0, 0));
        var entry = new CrossingEstimate(tEntry, CrossingMethod.Interpolated, 0.001, obs, 3, 1);
        var exit = new CrossingEstimate(tExit, CrossingMethod.Interpolated, 0.001, obs with { TimeSeconds = tExit }, 3, -1);
        return new VehiclePassage(id, dir, entry, exit, SpeedCalculator.Compute(distance, tEntry, tExit));
    }

    [Fact]
    public void Csv_UsesVideoTime_WhenRecordingStartUnknown()
    {
        string csv = CsvExporter.BuildCsv([Passage(1, 10.0, 11.44)], null);
        string expected =
            "Id;Verso;Orario ingresso;Orario uscita;Tempo impiegato (s);Velocità (km/h)\r\n" +
            "1;A → B;00:00:10.000;00:00:11.440;1,440;50,0\r\n";
        Assert.Equal(expected, csv);
    }

    [Fact]
    public void Csv_UsesClockTime_AndSortsById()
    {
        var start = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.FromHours(1));
        string csv = CsvExporter.BuildCsv([Passage(2, 65.25, 66.25, dir: TravelDirection.BToA), Passage(1, 3.5, 5.0)], start);
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("1;A → B;08:00:03.500;08:00:05.000;1,500;48,0", lines[1]);
        Assert.Equal("2;B → A;08:01:05.250;08:01:06.250;1,000;72,0", lines[2]);
    }

    [Fact]
    public void CsvBytes_StartWithUtf8Bom_AndKeepAccents()
    {
        byte[] bytes = CsvExporter.BuildCsvBytes([Passage(1, 0, 1)], null);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        string text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("Velocità", text);
    }

    [Theory]
    [InlineData("semplice", "semplice")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("di \"x\"", "\"di \"\"x\"\"\"")]
    public void Escape_QuotesWhenNeeded(string input, string expected)
    {
        Assert.Equal(expected, CsvExporter.Escape(input));
    }
}
