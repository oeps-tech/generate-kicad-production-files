namespace Oeps.KicadProductionFiles.Core.Checks;

public sealed class ComponentIdentifiersCheck : IFileCheck
{
    public string Name => "OEPS PN and MPN validation";
    public Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CheckResult(Name, CheckStatus.Pending,
            "Not implemented yet. Component field rules will be added to report missing identifiers and pairings that do not match the spreadsheet."));
    }
}
