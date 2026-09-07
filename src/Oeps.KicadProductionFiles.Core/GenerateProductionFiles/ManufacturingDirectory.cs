namespace Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

/// <summary>Clears only the selected project's manufacturing directory without following links.</summary>
internal static class ManufacturingDirectory
{
    internal static void EnsureSafePath(string projectDirectory, string target)
    {
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        var manufacturing = Path.Combine(project, "manufacturing");
        var resolved = Path.GetFullPath(target);
        if (!resolved.Equals(manufacturing, StringComparison.OrdinalIgnoreCase)
            && !resolved.StartsWith(manufacturing + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The output path must stay inside the selected project's manufacturing folder.");
        // Inspect existing ancestors too: a junction must not redirect output or cleanup elsewhere.
        for (var current = resolved; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Manufacturing cleanup cannot follow linked files or directories.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void Clear(string projectDirectory, CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(Path.Combine(projectDirectory, "manufacturing"));
        EnsureSafePath(projectDirectory, target);
        if (File.Exists(target)) throw new InvalidDataException("manufacturing exists as a file rather than a folder.");
        if (!Directory.Exists(target)) { Directory.CreateDirectory(target); return; }
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(target);
        // Validate the complete tree before deleting anything; do not use recursive deletion through unknown entries.
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePath(projectDirectory, directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                EnsureSafePath(projectDirectory, entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0) { directories.Add(entry); pending.Push(entry); }
                else files.Add(entry);
            }
        }
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePath(projectDirectory, file);
            File.Delete(file);
        }
        foreach (var directory in directories.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePath(projectDirectory, directory);
            Directory.Delete(directory, recursive: false);
        }
    }
}
