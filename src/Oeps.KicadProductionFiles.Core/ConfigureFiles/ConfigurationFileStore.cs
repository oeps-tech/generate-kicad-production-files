namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

internal sealed record ConfigurationFileEdit(string Path, byte[] Original, byte[] Modified);

/// <summary>Preflights all files before an approved edit, backing up each changed file.</summary>
internal static class ConfigurationFileStore
{
    public static async Task<IReadOnlyList<string>> SaveAsync(IReadOnlyList<ConfigurationFileEdit> edits,
        CancellationToken cancellationToken = default)
    {
        var locks = new List<FileStream>();
        var staged = new List<(ConfigurationFileEdit Edit, string Temp, string Backup)>();
        var saved = new List<(ConfigurationFileEdit Edit, string Backup)>();
        try
        {
            // Hold all read handles before comparing. A stale schematic must not allow the project half to be saved.
            foreach (var edit in edits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(edit.Path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Cannot configure a linked project or schematic file.");
                var stream = new FileStream(edit.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                locks.Add(stream);
                if (stream.Length != edit.Original.Length) throw Changed();
                var current = new byte[edit.Original.Length];
                await stream.ReadExactlyAsync(current, cancellationToken).ConfigureAwait(false);
                if (!current.SequenceEqual(edit.Original)) throw Changed();
            }
            foreach (var edit in edits.Where(edit => !edit.Original.SequenceEqual(edit.Modified)))
            {
                if (edit.Modified.Length > 20 * 1024 * 1024) throw new InvalidDataException("The configured file exceeds the 20 MiB size limit.");
                var directory = Path.GetDirectoryName(edit.Path)!;
                var temp = Path.Combine(directory, $".{Path.GetFileName(edit.Path)}.{Guid.NewGuid():N}.tmp");
                var backupDirectory = Path.Combine(directory, ".oeps-backups");
                var backup = Path.Combine(backupDirectory, $"{Path.GetFileName(edit.Path)}.{Guid.NewGuid():N}.bak");
                staged.Add((edit, temp, backup));
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await output.WriteAsync(edit.Modified, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                Directory.CreateDirectory(backupDirectory);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Finish this short commit without cancellation between files. Each replacement is atomic.
            foreach (var item in staged)
            {
                File.Replace(item.Temp, item.Edit.Path, item.Backup);
                saved.Add((item.Edit, item.Backup));
            }
            return saved.Select(item => item.Backup).ToArray();
        }
        catch (Exception error)
        {
            var recoveryErrors = new List<string>();
            foreach (var item in saved.AsEnumerable().Reverse())
            {
                try
                {
                    // Avoid overwriting an external edit during recovery. Keep the original backup available.
                    if (!File.ReadAllBytes(item.Edit.Path).SequenceEqual(item.Edit.Modified)) throw Changed();
                    var restore = item.Backup + ".restore";
                    try { File.Copy(item.Backup, restore); File.Replace(restore, item.Edit.Path, null); }
                    finally { if (File.Exists(restore)) File.Delete(restore); }
                }
                catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException)
                { recoveryErrors.Add(Path.GetFileName(item.Edit.Path)); }
            }
            if (recoveryErrors.Count != 0)
                throw new IOException("The fix could not finish and automatic recovery failed for " + string.Join(", ", recoveryErrors)
                    + ". Restore those files from .oeps-backups before continuing.", error);
            throw;
        }
        finally
        {
            foreach (var stream in locks) await stream.DisposeAsync().ConfigureAwait(false);
            foreach (var item in staged) if (File.Exists(item.Temp)) File.Delete(item.Temp);
        }
    }

    private static IOException Changed() => new("A file changed after the fix was prepared. No fix was saved. Run Configure files again to review the current settings.");
}
