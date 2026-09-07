using System.Text.Json;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

/// <summary>Exports the default variant with the board's saved plot options and included layers.</summary>
public sealed class GerberFilesGenerator : IProductionFileGenerator
{
    public string Name => "Gerber files";

    public async Task ValidateAsync(ProductionGenerationContext context, ICliCommandRunner cli, CancellationToken cancellationToken)
    {
        await ExportOutputDirectory.RequireOptionsAsync(context, cli, "gerbers",
            ["--board-plot-params", "--output", "--check-zones"], cancellationToken).ConfigureAwait(false);
        await PcbPlotSettingsReader.ReadAsync(context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GeneratedProductionFile> GenerateAsync(ProductionGenerationContext context, ICliCommandRunner cli,
        CancellationToken cancellationToken)
    {
        var board = await PcbPlotSettingsReader.ReadAsync(context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        using var output = new ExportOutputDirectory(context, "gerber");
        var result = await cli.RunAsync(context.CliPath,
            ["pcb", "export", "gerbers", "--board-plot-params", "--check-zones", "--output", output.DirectoryPath, context.BoardPath],
            context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("KiCad Gerber export failed. " + ExportOutputDirectory.Diagnostics(result));
        var generated = output.GetFiles();
        var gerbers = generated.Where(file => !Path.GetExtension(file).Equals(".gbrjob", StringComparison.OrdinalIgnoreCase)).ToList();
        if (gerbers.Count == 0) throw new IOException("KiCad CLI produced no Gerber layers. Check Include Layers in the board's Plot dialog.");
        foreach (var file in gerbers) await ExportOutputDirectory.ValidateGerberAsync(file, cancellationToken).ConfigureAwait(false);
        // KiCad 10 CLI creates a job file even when the saved creategerberjobfile flag is off.
        // Publish that auxiliary file only if the board explicitly requests it.
        if (board.Document.ReadValue("creategerberjobfile") == "yes")
        {
            foreach (var job in generated.Except(gerbers))
            {
                try { using var json = JsonDocument.Parse(await File.ReadAllTextAsync(job, cancellationToken).ConfigureAwait(false)); }
                catch (JsonException ex) { throw new InvalidDataException("KiCad produced an invalid Gerber job file.", ex); }
                gerbers.Add(job);
            }
        }
        var paths = output.Publish(gerbers, cancellationToken);
        return new(Name, paths[0]) { RelativePaths = paths };
    }
}
