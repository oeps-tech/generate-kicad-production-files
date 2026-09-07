using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

/// <summary>Exports Excellon drills and Gerber X2 maps with the requested fixed CLI settings.</summary>
public sealed class DrillFilesGenerator : IProductionFileGenerator
{
    public string Name => "Drill files";

    public Task ValidateAsync(ProductionGenerationContext context, ICliCommandRunner cli, CancellationToken cancellationToken) =>
        ExportOutputDirectory.RequireOptionsAsync(context, cli, "drill",
            ["--output", "--format", "--drill-origin", "--excellon-zeros-format", "--excellon-oval-format",
             "--excellon-units", "--excellon-separate-th", "--generate-map", "--map-format"], cancellationToken);

    public async Task<GeneratedProductionFile> GenerateAsync(ProductionGenerationContext context, ICliCommandRunner cli,
        CancellationToken cancellationToken)
    {
        using var output = new ExportOutputDirectory(context, "gerber");
        // Omit mirror, minimal-header and tenting switches. Route means alternate oval drill mode is off.
        var result = await cli.RunAsync(context.CliPath,
            ["pcb", "export", "drill", "--output", output.DirectoryPath, "--format", "excellon",
             "--drill-origin", "plot", "--excellon-zeros-format", "decimal", "--excellon-oval-format", "route",
             "--excellon-units", "in", "--excellon-separate-th", "--generate-map", "--map-format", "gerberx2", context.BoardPath],
            context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("KiCad drill export failed. " + ExportOutputDirectory.Diagnostics(result));
        var generated = output.GetFiles();
        var drills = generated.Where(file => Path.GetExtension(file).Equals(".drl", StringComparison.OrdinalIgnoreCase)).ToArray();
        var stem = Path.GetFileNameWithoutExtension(context.BoardPath);
        foreach (var type in new[] { "PTH", "NPTH" })
            if (!drills.Any(file => Path.GetFileName(file).Equals(stem + "-" + type + ".drl", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("KiCad CLI did not produce the separate " + type + " drill file.");
        var maps = new List<string>();
        foreach (var drill in drills)
        {
            await ValidateDrillAsync(drill, cancellationToken).ConfigureAwait(false);
            var map = Path.Combine(output.DirectoryPath, Path.GetFileNameWithoutExtension(drill) + "-drl_map.gbr");
            if (!generated.Contains(map, StringComparer.OrdinalIgnoreCase))
                throw new IOException("KiCad CLI did not produce the drill map for " + Path.GetFileName(drill) + ".");
            await ExportOutputDirectory.ValidateGerberAsync(map, cancellationToken).ConfigureAwait(false);
            maps.Add(map);
        }
        var paths = output.Publish(drills.Concat(maps).ToArray(), cancellationToken);
        return new(Name, paths[0]) { RelativePaths = paths };
    }

    private static async Task ValidateDrillAsync(string file, CancellationToken cancellationToken)
    {
        using var reader = File.OpenText(file);
        if (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) != "M48")
            throw new InvalidDataException("KiCad did not produce an Excellon header in " + Path.GetFileName(file) + ".");
        var inches = false;
        var decimalFormat = false;
        string? last = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("INCH", StringComparison.Ordinal)) inches = true;
            if (line.StartsWith("; FORMAT=", StringComparison.Ordinal) && line.Contains("decimal", StringComparison.OrdinalIgnoreCase)) decimalFormat = true;
            if (!string.IsNullOrWhiteSpace(line)) last = line.Trim();
        }
        if (!inches || !decimalFormat || last != "M30")
            throw new InvalidDataException("KiCad produced an incomplete drill file or incorrect units/zero format: " + Path.GetFileName(file));
    }
}
