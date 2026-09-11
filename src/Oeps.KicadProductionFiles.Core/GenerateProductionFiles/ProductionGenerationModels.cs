using Oeps.KicadProductionFiles.Core.Kicad;
using Oeps.KicadProductionFiles.Core.Checks;

namespace Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

public sealed record ProductionGenerationContext(string CliPath, string ProjectDirectory, string BoardPath, string ManufacturingDirectory);
public sealed record ProductionGenerationOptions(bool DeleteExistingFiles = true, bool GenerateGerbers = true,
    bool GeneratePlacements = true, bool GenerateDrills = true, bool GenerateIpcD356 = true);
public sealed record BomData(IReadOnlyList<string> Fields, IReadOnlyList<ComponentFields> Components);
public sealed record GeneratedProductionFile(string Name, string RelativePath, int? ComponentCount = null)
{
    public IReadOnlyList<string> RelativePaths { get; init; } = [RelativePath];
    public IReadOnlyList<string>? References { get; init; }
    public BomData? Bom { get; init; }
}
public sealed record ProductionGenerationReport(IReadOnlyList<GeneratedProductionFile> Files, bool ManufacturingCleared, string? Error)
{
    public bool Success => Error is null;
    public BomPlacementComparison? Comparison { get; init; }
    public IReadOnlyList<CheckResult> Checks { get; init; } = [];
}

public interface IProductionFileGenerator
{
    string Name { get; }
    Task ValidateAsync(ProductionGenerationContext context, ICliCommandRunner cli, CancellationToken cancellationToken);
    Task<GeneratedProductionFile> GenerateAsync(ProductionGenerationContext context, ICliCommandRunner cli, CancellationToken cancellationToken);
}
