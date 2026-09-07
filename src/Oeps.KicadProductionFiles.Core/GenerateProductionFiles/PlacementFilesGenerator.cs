using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

/// <summary>Exports the default variant to one CSV containing front and back placements.</summary>
public sealed class PlacementFilesGenerator : IProductionFileGenerator
{
    public string Name => "Placement files";

    public async Task ValidateAsync(ProductionGenerationContext context, ICliCommandRunner cli, CancellationToken cancellationToken)
    {
        var help = await cli.RunAsync(context.CliPath, ["pcb", "export", "pos", "--help"], context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        if (help.ExitCode != 0) throw new IOException("KiCad CLI cannot export placement files. " + Diagnostics(help));
        foreach (var option in new[] { "--format", "--units", "--side", "--use-drill-file-origin" })
            if (!help.Output.Contains(option, StringComparison.Ordinal))
                throw new InvalidDataException("This KiCad CLI does not support the required placement option " + option + ".");
    }

    public async Task<GeneratedProductionFile> GenerateAsync(ProductionGenerationContext context, ICliCommandRunner cli,
        CancellationToken cancellationToken)
    {
        using var directory = new ExportOutputDirectory(context, "assembly");
        var output = Path.Combine(directory.DirectoryPath, Path.GetFileNameWithoutExtension(context.BoardPath) + "-pos.csv");
        // Omit variant and all exclusion/negative-X switches: default variant, all requested footprint types.
        var result = await cli.RunAsync(context.CliPath,
            ["pcb", "export", "pos", "--format", "csv", "--units", "mm", "--side", "both",
             "--use-drill-file-origin", "--output", output, context.BoardPath],
            context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("KiCad placement export failed. " + Diagnostics(result));
        ManufacturingDirectory.EnsureSafePath(context.ProjectDirectory, output);
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
            throw new IOException("KiCad CLI completed without producing the placement CSV.");
        var componentCount = 0;
        using (var reader = File.OpenText(output))
        {
            if (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) != "Ref,Val,Package,PosX,PosY,Rot,Side")
                throw new InvalidDataException("KiCad CLI did not produce the expected placement CSV format.");
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
                componentCount++;
        }
        var paths = directory.Publish([output], cancellationToken);
        return new(Name, paths[0], componentCount);
    }

    private static string Diagnostics(CliCommandResult result) => $"Exit code {result.ExitCode}. " +
        (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
}
