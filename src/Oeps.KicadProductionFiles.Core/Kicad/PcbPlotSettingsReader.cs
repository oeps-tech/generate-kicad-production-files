using System.Text;

namespace Oeps.KicadProductionFiles.Core.Kicad;

public sealed record PcbPlotSettingsSnapshot(string FilePath, byte[] OriginalBytes, PcbPlotSettingsDocument Document);

public static class PcbPlotSettingsReader
{
    public static async Task<PcbPlotSettingsSnapshot> ReadAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = projectDirectory.Trim().Trim('"').Trim();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new InvalidDataException("Choose an accessible KiCad project folder.");
        var projects = Directory.EnumerateFiles(directory).Where(path =>
            Path.GetExtension(path).Equals(".kicad_pro", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (projects.Length != 1) throw new InvalidDataException("Select a folder containing exactly one .kicad_pro file.");
        var path = Path.GetFullPath(Path.ChangeExtension(projects[0], ".kicad_pcb"));
        if (!File.Exists(path)) throw new InvalidDataException("The main .kicad_pcb file with the same name as the .kicad_pro file is missing.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Select the original PCB rather than a linked file.");
        const int maximumBytes = 20 * 1024 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new InvalidDataException("The PCB exceeds the 20 MiB size limit.");
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > maximumBytes) throw new InvalidDataException("The PCB exceeds the 20 MiB size limit.");
            buffer.Write(chunk, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = buffer.ToArray();
        try { return new(path, bytes, new PcbPlotSettingsDocument(new UTF8Encoding(false, true).GetString(bytes))); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("The PCB is not valid UTF-8.", ex); }
    }
}
