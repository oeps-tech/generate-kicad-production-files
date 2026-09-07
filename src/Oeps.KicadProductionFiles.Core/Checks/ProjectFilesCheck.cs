namespace Oeps.KicadProductionFiles.Core.Checks;

public sealed class ProjectFilesCheck : IFileCheck
{
    public string Name => "KiCad project files";

    public Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Check(context, cancellationToken));
    }

    private CheckResult Check(CheckContext context, CancellationToken cancellationToken)
    {
        var directory = CheckPaths.Clean(context.ProjectDirectory);
        if (string.IsNullOrWhiteSpace(directory))
            return new(Name, CheckStatus.Failed, "Choose the folder containing the main .kicad_sch schematic and .kicad_pcb board.");
        if (!Directory.Exists(directory))
            return new(Name, CheckStatus.Failed, "The KiCad project folder does not exist or is unavailable.");
        try
        {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).Equals(".kicad_sch", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(path).Equals(".kicad_pcb", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
            var schematics = files.Where(path => Path.GetExtension(path).Equals(".kicad_sch", StringComparison.OrdinalIgnoreCase)).ToArray();
            var boards = files.Where(path => Path.GetExtension(path).Equals(".kicad_pcb", StringComparison.OrdinalIgnoreCase)).ToArray();
            var missing = new List<string>();
            if (schematics.Length == 0) missing.Add("a .kicad_sch schematic");
            if (boards.Length == 0) missing.Add("a .kicad_pcb board");
            if (missing.Count > 0)
                return new(Name, CheckStatus.Failed, $"Missing {string.Join(" and ", missing)} in the selected folder. Select the folder containing the project files.");
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length == 0)
                    return new(Name, CheckStatus.Failed, $"{Path.GetFileName(file)} is empty.");
            }
            var detail = $"Found {schematics.Length} schematic(s) and {boards.Length} board(s): {string.Join(", ", files.Select(Path.GetFileName))}. Files are readable; contents have not been validated.";
            if (schematics.Length > 1 || boards.Length > 1)
                return new(Name, CheckStatus.Warning, detail + " Main schematic and board selection will be added with the production configuration rules.");
            return new(Name, CheckStatus.Passed, detail);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(Name, CheckStatus.Failed, $"The project files could not be read: {exception.Message}");
        }
    }
}
