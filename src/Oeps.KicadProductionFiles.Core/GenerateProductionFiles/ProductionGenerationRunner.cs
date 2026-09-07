using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

public sealed class ProductionGenerationRunner(ICliCommandRunner? cli = null, IEnumerable<IProductionFileGenerator>? generators = null)
{
    private readonly ICliCommandRunner _cli = cli ?? new CliCommandRunner();
    private readonly IReadOnlyList<IProductionFileGenerator>? _generators = generators?.ToArray();

    public async Task<ProductionGenerationReport> RunAsync(CheckContext input, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, ProductionGenerationOptions? options = null)
    {
        var files = new List<GeneratedProductionFile>();
        var cleared = false;
        var cleanupStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            options ??= new();
            var selectedGenerators = _generators ?? SelectGenerators(options);
            if (selectedGenerators.Count == 0)
                throw new InvalidDataException("Select at least one file type to generate.");
            var context = Resolve(input);
            progress?.Report("Preparing production export…");
            foreach (var generator in selectedGenerators)
                await generator.ValidateAsync(context, _cli, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (options.DeleteExistingFiles)
            {
                progress?.Report("Clearing manufacturing…");
                cleanupStarted = true;
                ManufacturingDirectory.Clear(context.ProjectDirectory, cancellationToken);
                cleared = true;
            }
            foreach (var generator in selectedGenerators)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Generating " + generator.Name.ToLowerInvariant() + "…");
                files.Add(await generator.GenerateAsync(context, _cli, cancellationToken).ConfigureAwait(false));
            }
            return new(files.AsReadOnly(), cleared, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            var detail = ex.Message;
            if (cleared) detail += "\nPrevious manufacturing files were cleared before generation.";
            else if (cleanupStarted) detail += "\nManufacturing cleanup did not finish; check its contents before retrying.";
            else detail += "\nManufacturing was not cleared.";
            return new(files.AsReadOnly(), cleared, detail);
        }
    }

    private static IReadOnlyList<IProductionFileGenerator> SelectGenerators(ProductionGenerationOptions options)
    {
        var selected = new List<IProductionFileGenerator>();
        if (options.GenerateGerbers) selected.Add(new GerberFilesGenerator());
        if (options.GeneratePlacements) selected.Add(new PlacementFilesGenerator());
        if (options.GenerateDrills) selected.Add(new DrillFilesGenerator());
        if (options.GenerateIpcD356) selected.Add(new IpcD356FilesGenerator());
        return selected;
    }

    private static ProductionGenerationContext Resolve(CheckContext input)
    {
        var cli = input.KicadCliPath.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(cli) || !File.Exists(cli)
            || !Path.GetFileName(cli).Equals("kicad-cli.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose the full path to kicad-cli.exe before generating production files.");
        var selected = input.ProjectDirectory.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(selected) || !Directory.Exists(selected))
            throw new InvalidDataException("Choose an accessible KiCad project folder.");
        var project = Path.GetFullPath(selected);
        var projects = Directory.EnumerateFiles(project).Where(path =>
            Path.GetExtension(path).Equals(".kicad_pro", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (projects.Length != 1) throw new InvalidDataException("Select a folder containing exactly one .kicad_pro file.");
        var board = Path.ChangeExtension(projects[0], ".kicad_pcb");
        if (!File.Exists(board) || new FileInfo(board).Length == 0)
            throw new InvalidDataException("The main .kicad_pcb file with the same name as the .kicad_pro file is missing or empty.");
        var manufacturing = Path.Combine(project, "manufacturing");
        ManufacturingDirectory.EnsureSafePath(project, manufacturing);
        return new(cli, project, board, manufacturing);
    }
}
