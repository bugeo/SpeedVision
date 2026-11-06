using System.Security.Cryptography;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Detection;

/// <summary>Modello di rilevamento disponibile.</summary>
/// <param name="NameKey">Nome in italiano (chiave della traduzione).</param>
/// <param name="DescriptionKey">Descrizione in italiano (chiave della traduzione).</param>
public sealed record ModelInfo(
    string Id,
    string NameKey,
    string DescriptionKey,
    string FileName,
    string Url,
    string Sha256,
    long SizeBytes,
    int InputSize)
{
    public string DisplayName => Loc.T(NameKey);

    public string Description => Loc.T(DescriptionKey);

    public string SizeText => $"{SizeBytes / 1024.0 / 1024.0:0.#} MB";

    public override string ToString() => DisplayName;
}

/// <summary>
/// Modelli YOLOX di Megvii (licenza Apache-2.0), addestrati su COCO, nella versione ONNX
/// pubblicata ufficialmente. Le impronte SHA-256 garantiscono che il file scaricato sia quello atteso.
/// </summary>
public static class ModelCatalog
{
    private const string Base = "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/";

    public static ModelInfo Nano { get; } = new(
        "yolox_nano", "Veloce (YOLOX-Nano)",
        "Molto leggero. Adatto a PC senza scheda grafica; perde più spesso i veicoli piccoli o lontani.",
        "yolox_nano.onnx", Base + "yolox_nano.onnx",
        "c789161ed43c8269fcd4e67c67eeeb4e80c622da2eb296a20bc6007bd18a0b7d", 3_659_407, 416);

    public static ModelInfo Tiny { get; } = new(
        "yolox_tiny", "Bilanciato (YOLOX-Tiny)",
        "Buon compromesso tra velocità e precisione.",
        "yolox_tiny.onnx", Base + "yolox_tiny.onnx",
        "427cc366d34e27ff7a03e2899b5e3671425c262ea2291f88bb942bc1cc70b0f7", 20_219_662, 416);

    public static ModelInfo Small { get; } = new(
        "yolox_s", "Preciso (YOLOX-S)",
        "Il più affidabile. Consigliato, soprattutto con scheda grafica.",
        "yolox_s.onnx", Base + "yolox_s.onnx",
        "c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063", 35_858_002, 640);

    public static IReadOnlyList<ModelInfo> All { get; } = [Small, Tiny, Nano];

    public static ModelInfo Default => Small;

    public static ModelInfo? Find(string? id) => All.FirstOrDefault(m => m.Id == id);
}

/// <summary>Scarica e verifica i modelli, conservandoli in una cartella locale.</summary>
public sealed class ModelManager(string modelsDirectory)
{
    private static readonly HttpClient Http = CreateClient();

    public string ModelsDirectory { get; } = modelsDirectory;

    /// <summary>Percorso del modello se presente (cartella dei modelli o cartella "models" accanto all'app).</summary>
    public string? FindLocal(ModelInfo model)
    {
        string[] candidates =
        [
            Path.Combine(ModelsDirectory, model.FileName),
            Path.Combine(AppContext.BaseDirectory, "models", model.FileName),
        ];
        return candidates.FirstOrDefault(p => File.Exists(p) && new FileInfo(p).Length == model.SizeBytes);
    }

    public bool IsAvailable(ModelInfo model) => FindLocal(model) is not null;

    /// <summary>Restituisce il percorso del modello, scaricandolo se necessario.</summary>
    public async Task<string> EnsureAsync(ModelInfo model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (FindLocal(model) is { } existing)
        {
            return existing;
        }

        Directory.CreateDirectory(ModelsDirectory);
        string target = Path.Combine(ModelsDirectory, model.FileName);
        string part = target + ".part";

        using (HttpResponseMessage response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? model.SizeBytes;
            await using Stream src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report(total > 0 ? Math.Min(1, done / (double)total) : 0);
                }
            }
        }

        string hash = await ComputeSha256Async(part, ct).ConfigureAwait(false);
        if (!string.Equals(hash, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new InvalidDataException(Loc.T("Il file scaricato non corrisponde al modello atteso (impronta SHA-256 diversa)."));
        }

        File.Move(part, target, overwrite: true);
        progress?.Report(1);
        return target;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using FileStream fs = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TrafficSpeedAnalyzer/0.2");
        return client;
    }
}
