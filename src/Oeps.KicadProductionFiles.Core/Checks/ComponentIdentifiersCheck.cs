namespace Oeps.KicadProductionFiles.Core.Checks;

public sealed class ComponentIdentifiersCheck : IFileCheck
{
    public string Name => "OEPS PN and MPN validation";
    public Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CheckResult(Name, CheckStatus.Pending,
            "Generate production files to validate OEPS PN and MPN from the newly exported BOM against the database and layout."));
    }
}
