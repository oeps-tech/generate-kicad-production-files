using System.Text;

namespace Oeps.KicadProductionFiles.Core.Kicad;

public sealed record SchematicRevisionSnapshot(string FilePath, byte[] OriginalBytes, SchematicRevisionDocument Document);

public static class SchematicRevisionReader
{
    internal const int MaximumBytes = 20 * 1024 * 1024;

    public static async Task<SchematicRevisionSnapshot> ReadAsync(string projectFile, CancellationToken cancellationToken = default)
    {
        var path = Path.ChangeExtension(projectFile, ".kicad_sch");
        if (!File.Exists(path))
            throw new InvalidDataException("The main .kicad_sch file with the same name as the .kicad_pro file is missing.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Select the original main schematic rather than a linked schematic file.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("The main schematic exceeds the 20 MiB size limit.");
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("The main schematic exceeds the 20 MiB size limit.");
            buffer.Write(chunk, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = buffer.ToArray();
        try { return new(path, bytes, new SchematicRevisionDocument(new UTF8Encoding(false, true).GetString(bytes))); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("The main schematic is not valid UTF-8.", ex); }
    }
}
