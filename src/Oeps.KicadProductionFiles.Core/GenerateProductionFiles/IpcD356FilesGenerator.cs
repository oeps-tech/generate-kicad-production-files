using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

public sealed class IpcD356FilesGenerator : IProductionFileGenerator
{
    public string Name => "IPC-D-356 netlist";

    public Task ValidateAsync(ProductionGenerationContext context, ICliCommandRunner cli, CancellationToken cancellationToken) =>
        ExportOutputDirectory.RequireOptionsAsync(context, cli, "ipcd356", ["--output"], cancellationToken);

    public async Task<GeneratedProductionFile> GenerateAsync(ProductionGenerationContext context, ICliCommandRunner cli,
        CancellationToken cancellationToken)
    {
        using var directory = new ExportOutputDirectory(context, "");
        var output = Path.Combine(directory.DirectoryPath, Path.GetFileNameWithoutExtension(context.BoardPath) + ".d356");
        var result = await cli.RunAsync(context.CliPath, ["pcb", "export", "ipcd356", "--output", output, context.BoardPath],
            context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("KiCad IPC-D-356 export failed. " + ExportOutputDirectory.Diagnostics(result));
        ManufacturingDirectory.EnsureSafePath(context.ProjectDirectory, output);
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
            throw new IOException("KiCad CLI completed without producing the IPC-D-356 netlist.");
        using (var reader = File.OpenText(output))
        {
            var code = false;
            var units = false;
            string? last = null;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("P  CODE ", StringComparison.Ordinal)) code = true;
                if (line.StartsWith("P  UNITS ", StringComparison.Ordinal)) units = true;
                if (!string.IsNullOrWhiteSpace(line)) last = line.Trim();
            }
            if (!code || !units || last != "999")
                throw new InvalidDataException("KiCad produced an incomplete or invalid IPC-D-356 netlist.");
        }
        var paths = directory.Publish([output], cancellationToken);
        return new(Name, paths[0]);
    }
}
