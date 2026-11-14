using System.Text.Json;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using Xunit;

namespace TrafficSpeedAnalyzer.Detection.Tests;

/// <summary>
/// Rete delle ruote incorporata nel programma. Il caso di prova è un pezzo di fotogramma di un video sintetico che la rete
/// non ha visto durante l'allenamento (Simulatore strada 3D, obiettivo ultragrandangolare 13 mm con distorsione), con i
/// punti di contatto veri delle ruote e l'uscita calcolata con la stessa rete in Python (onnxruntime, ritaglio con
/// cv2.warpAffine). Se si sostituisce wheelnet.onnx, il file .json va rigenerato.
/// </summary>
public sealed class WheelPointNetworkTests
{
    private sealed record Fixture(int Width, int Height, double[] Box, double[][] Expected, double[][] Truth);

    private static (byte[] Pixels, Fixture Data) Load()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Assets");
        var data = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(dir, "ruota_uu13_452.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return (File.ReadAllBytes(Path.Combine(dir, "ruota_uu13_452.bgr")), data);
    }

    [Fact]
    public void Locate_MatchesReferenceAndTruth()
    {
        (byte[] px, Fixture f) = Load();
        var box = new BoundingBox(f.Box[0], f.Box[1], f.Box[2], f.Box[3]);

        IReadOnlyList<WheelCandidate> wheels = WheelPointNetwork.Shared.Locate(px, f.Width, f.Height, f.Width * 3, [box])[0];

        Assert.Equal(2, wheels.Count);
        List<WheelCandidate> byX = [.. wheels.OrderBy(w => w.Contact.X)];
        for (int j = 0; j < 2; j++)
        {
            // Stesso ritaglio e stessa rete dell'allenamento: al più qualche decimo di pixel di differenza.
            Assert.InRange(byX[j].Contact.X, f.Expected[j][0] - 0.5, f.Expected[j][0] + 0.5);
            Assert.InRange(byX[j].Contact.Y, f.Expected[j][1] - 0.5, f.Expected[j][1] + 0.5);

            // E vicino al punto vero (auto lunga circa 450 pixel: 3 pixel sono meno di 3 cm).
            Assert.InRange(byX[j].Contact.X, f.Truth[j][0] - 3, f.Truth[j][0] + 3);
            Assert.InRange(byX[j].Contact.Y, f.Truth[j][1] - 3, f.Truth[j][1] + 3);
        }
    }

    [Fact]
    public void Calibration_UsesBothGroundContacts()
    {
        // Calibrazione con l'auto: dalle ruote trovate dalla rete ai due punti a terra per le linee A e B.
        (byte[] px, Fixture f) = Load();
        var box = new BoundingBox(f.Box[0], f.Box[1], f.Box[2], f.Box[3]);
        IReadOnlyList<WheelCandidate> wheels = WheelPointNetwork.Shared.Locate(px, f.Width, f.Height, f.Width * 3, [box])[0];

        Core.Calibration.WheelContacts c = Core.Calibration.VehicleWheels.FromCandidates(wheels, box)!.Value;

        Assert.InRange(c.Left.X, f.Truth[0][0] - 3, f.Truth[0][0] + 3);
        Assert.InRange(c.Right.X, f.Truth[1][0] - 3, f.Truth[1][0] + 3);
        Assert.InRange(c.Right.Y, f.Truth[1][1] - 3, f.Truth[1][1] + 3);
    }

    [Fact]
    public void Locate_SkipsBoxesTooSmall()
    {
        (byte[] px, Fixture f) = Load();
        var tiny = new BoundingBox(10, 10, 60, WheelPointNetwork.MinBoxHeight - 1);

        IReadOnlyList<WheelCandidate>[] result = WheelPointNetwork.Shared.Locate(px, f.Width, f.Height, f.Width * 3, [tiny]);

        Assert.Empty(result[0]);
    }

    [Fact]
    public void Decode_GivesGroundContactAndHidesInvisibleWheels()
    {
        var box = new BoundingBox(100, 50, 400, 200);
        BoundingBox crop = WheelPointNetwork.CropRect(box);

        // Ruota sinistra visibile al centro del ritaglio, destra invisibile (logit molto negativo).
        List<WheelCandidate> w = WheelPointNetwork.Decode([128f, 48f, 4f, 200f, 50f, -6f], box);

        WheelCandidate only = Assert.Single(w);
        Assert.Equal(crop.X + (crop.Width / 2), only.Contact.X, 6);
        Assert.Equal(crop.Y + (crop.Height / 2), only.Contact.Y, 6);
        Assert.Equal(only.Contact, only.GroundContact);
    }
}
