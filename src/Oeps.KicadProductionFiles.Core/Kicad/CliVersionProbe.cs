using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Oeps.KicadProductionFiles.Core.Kicad;

public sealed record CliVersionProbeResult(bool Success, string Detail);

public interface ICliVersionProbe
{
    Task<CliVersionProbeResult> ProbeAsync(string executablePath, CancellationToken cancellationToken = default);
}

public sealed class CliVersionProbe : ICliVersionProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public async Task<CliVersionProbeResult> ProbeAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        var started = false;
        try
        {
            started = process.Start();
            if (!started) return new(false, "KiCad CLI could not start.");
            // Terminate immediately when the form closes or the timeout expires,
            // even if a redirected stream is still waiting for the child process.
            using var killOnCancellation = timeout.Token.Register(() => TryTerminate(process));
            var stdout = ReadOutputAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadOutputAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).ConfigureAwait(false);
            var version = (await stdout.ConfigureAwait(false)).Trim();
            var error = (await stderr.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
                return new(false, $"KiCad CLI --version returned exit code {process.ExitCode}. {error}".Trim());
            if (string.IsNullOrWhiteSpace(version))
                return new(false, "KiCad CLI returned no version information.");
            return new(true, $"KiCad CLI is available: {version}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "KiCad CLI did not finish its version check within 8 seconds.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return new(false, $"KiCad CLI could not run: {exception.Message}");
        }
        finally
        {
            if (started) TryTerminate(process);
        }
    }

    private static void TryTerminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException) { }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        // Drain both streams while keeping only a small amount of diagnostic text.
        var output = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            var retained = Math.Min(count, 2048 - output.Length);
            if (retained > 0) output.Append(buffer, 0, retained);
        }
        return output.ToString();
    }
}
