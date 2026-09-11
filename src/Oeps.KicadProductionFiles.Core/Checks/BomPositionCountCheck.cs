namespace Oeps.KicadProductionFiles.Core.Checks;

public sealed class BomPositionCountCheck : IFileCheck
{
    public string Name => "BOM and position component counts";
    public Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CheckResult(Name, CheckStatus.Pending,
            "Generate production files with placement export selected to compare the newly generated BOM and placement component counts and references."));
    }
}
