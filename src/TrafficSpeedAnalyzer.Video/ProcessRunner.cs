using System.Diagnostics;
using System.Text;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Video;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return psi;
    }

    /// <summary>Esegue un processo e ne raccoglie l'output testuale.</summary>
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        ProcessStartInfo psi = CreateStartInfo(exe, args);
        psi.StandardOutputEncoding = Encoding.UTF8;

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException(Loc.S($"Il processo {Path.GetFileName(exe)} non ha risposto entro {timeout.TotalSeconds:0} s."));
        }
    }

    /// <summary>
    /// Esegue un processo lungo passando ogni riga dell'output standard a <paramref name="onOutputLine"/>
    /// (per l'avanzamento). L'errore standard viene raccolto e restituito. L'annullamento termina il processo.
    /// </summary>
    public static async Task<ProcessResult> RunStreamingAsync(
        string exe,
        IEnumerable<string> args,
        string? workingDirectory,
        Action<string>? onOutputLine,
        CancellationToken ct)
    {
        ProcessStartInfo psi = CreateStartInfo(exe, args);
        psi.StandardOutputEncoding = Encoding.UTF8;
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                onOutputLine?.Invoke(line);
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, string.Empty, await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try
            {
                // Attende la chiusura, così i file che stava scrivendo si possono eliminare subito.
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // il processo è già terminato
        }
    }
}
