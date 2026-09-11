using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

namespace Oeps.KicadProductionFiles.Core.CheckProductionFiles;

/// <summary>Checks that every BOM component has usable OEPS PN, MPN and OEPS Description fields.</summary>
public sealed class BomIdentifiersCheck
{
    public const string CheckName = "BOM required fields: OEPS PN, MPN and OEPS Description";
    public string Name => CheckName;

    public CheckResult Run(BomData bom, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var problems = IdentifierFields.BomIssues(bom);
        foreach (var component in bom.Components.OrderBy(component => component.Reference, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            problems.AddRange(IdentifierFields.Read(component, includeDescription: true).Issues
                .Select(issue => $"{IdentifierFields.Display(component.Reference)}: {issue}"));
        }
        return problems.Count == 0 ? new(Name, CheckStatus.Passed, "")
            : new(Name, CheckStatus.Failed, string.Join("\n", problems));
    }
}
