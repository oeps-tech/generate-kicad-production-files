using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Data;
using Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

namespace Oeps.KicadProductionFiles.Core.CheckProductionFiles;

/// <summary>Checks every component exported from the schematic hierarchy against the database. No fix is offered.</summary>
public sealed class BomDatabaseIdentifiersCheck
{
    public const string CheckName = "BOM vs database: OEPS PN, MPN and OEPS Description";
    public string Name => CheckName;

    public CheckResult Run(BomData bom, IReadOnlyList<Component> database, CancellationToken cancellationToken = default,
        bool includeFieldIssues = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var problems = new List<string>();
        var fields = new BomIdentifiersCheck().Run(bom, cancellationToken);
        if (fields.Status != CheckStatus.Passed)
            problems.Add(includeFieldIssues ? fields.Detail : $"Some database comparisons could not be completed. See {BomIdentifiersCheck.CheckName}.");
        var pairings = database.GroupBy(component => component.OepsPn.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(component => component.Mpn.Trim()).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var descriptionsByPair = database.ToLookup(component => (component.OepsPn.Trim(), component.Mpn.Trim()));
        if (pairings.Count == 0) problems.Add("No component database is available. Click Update database and run the check again to validate the pairings.");
        foreach (var component in bom.Components.OrderBy(component => component.Reference, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identifiers = IdentifierFields.Read(component, includeDescription: true);
            var reference = IdentifierFields.Display(component.Reference);
            if (pairings.Count == 0 || identifiers.OepsPn is not { } pn) continue;
            if (!pairings.TryGetValue(pn, out var mpns))
                problems.Add($"{reference}: OEPS PN {IdentifierFields.Display(pn)} was not found in the database.");
            else if (identifiers.Mpn is { } mpn && !mpns.Contains(mpn))
                problems.Add($"{reference}: MPN {IdentifierFields.Display(mpn)} does not match OEPS PN {IdentifierFields.Display(pn)}. " +
                    "Database MPN(s): " + string.Join(", ", mpns.Order(StringComparer.Ordinal).Select(IdentifierFields.Display)) + ".");
            else if (identifiers.Mpn is { } matchedMpn)
            {
                var descriptions = descriptionsByPair[(pn, matchedMpn)].Select(row => row.Description?.Trim() ?? "")
                    .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                if (descriptions.Length == 0)
                    problems.Add($"{reference}: The database has no Description for OEPS PN {IdentifierFields.Display(pn)} / MPN {IdentifierFields.Display(matchedMpn)}; cannot validate OEPS Description.");
                else if (identifiers.Description is { } description && !descriptions.Contains(description, StringComparer.Ordinal))
                    problems.Add($"{reference}: OEPS Description {IdentifierFields.Display(description)} does not match the database for OEPS PN {IdentifierFields.Display(pn)} / MPN {IdentifierFields.Display(matchedMpn)}. " +
                        "Database Description(s): " + string.Join(", ", descriptions.Order(StringComparer.Ordinal).Select(IdentifierFields.Display)) + ".");
            }
        }
        return problems.Count == 0 ? new(Name, CheckStatus.Passed, "")
            : new(Name, CheckStatus.Failed, string.Join("\n", problems));
    }
}
