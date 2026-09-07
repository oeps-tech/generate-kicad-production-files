using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Oeps.KicadProductionFiles.Core.Kicad;

public sealed record CliCommandResult(int ExitCode, string Output, string Error);

public interface ICliCommandRunner
{
    Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class CliCommandRunner : ICliCommandRunner
{
    public async Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new IOException("KiCad CLI could not start.");
            using var registration = timeout.Token.Register(() => Stop(process));
            var output = ReadAsync(process.StandardOutput, timeout.Token);
            var error = ReadAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("KiCad CLI did not finish within five minutes."); }
        catch (Win32Exception ex) { throw new IOException("KiCad CLI could not run: " + ex.Message, ex); }
        finally { Stop(process); }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
    }

    private static async Task<string> ReadAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            var retained = Math.Min(count, 16 * 1024 - text.Length);
            if (retained > 0) text.Append(buffer, 0, retained);
        }
        return text.ToString().Trim();
    }
}
