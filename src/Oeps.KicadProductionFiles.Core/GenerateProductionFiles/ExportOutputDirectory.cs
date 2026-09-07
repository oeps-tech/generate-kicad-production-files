using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

/// <summary>Exports into an empty directory so stale files cannot count as newly generated output.</summary>
internal sealed class ExportOutputDirectory : IDisposable
{
    private readonly ProductionGenerationContext _context;
    private readonly string _destination;
    public string DirectoryPath { get; }

    public ExportOutputDirectory(ProductionGenerationContext context, string subdirectory)
    {
        _context = context;
        _destination = Path.Combine(context.ManufacturingDirectory, subdirectory);
        ManufacturingDirectory.EnsureSafePath(context.ProjectDirectory, _destination);
        DirectoryPath = Path.Combine(_destination, ".oeps-export-" + Guid.NewGuid().ToString("N"));
        ManufacturingDirectory.EnsureSafePath(context.ProjectDirectory, DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
    }

    public string[] GetFiles()
    {
        ManufacturingDirectory.EnsureSafePath(_context.ProjectDirectory, DirectoryPath);
        var files = Directory.GetFiles(DirectoryPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var file in files) ManufacturingDirectory.EnsureSafePath(_context.ProjectDirectory, file);
        return files;
    }

    public IReadOnlyList<string> Publish(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0) throw new IOException("KiCad CLI completed without producing the requested output files.");
        // Validate every destination before replacing any existing output.
        foreach (var file in files)
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), DirectoryPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The generated file is outside the temporary export directory.");
            ManufacturingDirectory.EnsureSafePath(_context.ProjectDirectory, file);
            ManufacturingDirectory.EnsureSafePath(_context.ProjectDirectory, Path.Combine(_destination, Path.GetFileName(file)));
            if (new FileInfo(file).Length == 0) throw new IOException("KiCad produced an empty file: " + Path.GetFileName(file));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var paths = new List<string>();
        foreach (var file in files)
        {
            var destination = Path.Combine(_destination, Path.GetFileName(file));
            ManufacturingDirectory.EnsureSafePath(_context.ProjectDirectory, destination);
            File.Move(file, destination, overwrite: true);
            paths.Add(Path.GetRelativePath(_context.ProjectDirectory, destination));
        }
        return paths.AsReadOnly();
    }

    public static async Task ValidateGerberAsync(string file, CancellationToken cancellationToken)
    {
        using var reader = File.OpenText(file);
        var coordinates = false;
        var units = false;
        string? last = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("%FS", StringComparison.Ordinal)) coordinates = true;
            if (line.StartsWith("%MO", StringComparison.Ordinal)) units = true;
            if (!string.IsNullOrWhiteSpace(line)) last = line.Trim();
        }
        if (!coordinates || !units || last != "M02*")
            throw new InvalidDataException("KiCad produced an incomplete or invalid Gerber file: " + Path.GetFileName(file));
    }

    public static async Task RequireOptionsAsync(ProductionGenerationContext context, ICliCommandRunner cli,
        string command, IReadOnlyList<string> options, CancellationToken cancellationToken)
    {
        var help = await cli.RunAsync(context.CliPath, ["pcb", "export", command, "--help"], context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        if (help.ExitCode != 0) throw new IOException("KiCad CLI cannot export " + command + ". " + Diagnostics(help));
        foreach (var option in options)
            if (!help.Output.Contains(option, StringComparison.Ordinal))
                throw new InvalidDataException("This KiCad CLI does not support the required " + command + " option " + option + ".");
    }

    public static string Diagnostics(CliCommandResult result) => $"Exit code {result.ExitCode}. " +
        (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);

    public void Dispose()
    {
        // Only files directly inside our own export directory are removed; never recurse through CLI output.
        foreach (var file in GetFiles()) File.Delete(file);
        ManufacturingDirectory.EnsureSafePath(_context.ProjectDirectory, DirectoryPath);
        Directory.Delete(DirectoryPath, recursive: false);
    }
}
