namespace Oeps.KicadProductionFiles.Core.Checks;

public sealed class BomPositionCountCheck : IFileCheck
{
    public string Name => "BOM and position component counts";
    public Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CheckResult(Name, CheckStatus.Pending,
            "Not implemented yet. BOM and position file formats, component quantity rules and exclusions will be configured in the next stage."));
    }
}
