using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.GenerateProductionFiles;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.CheckProductionFiles;

/// <summary>Compares PCB footprints, identifiers and descriptions with the freshly exported BOM, independently of the database.</summary>
public sealed class BomLayoutIdentifiersCheck
{
    public const string CheckName = "BOM vs layout: footprints, OEPS PN, MPN and OEPS Description";
    public string Name => CheckName;

    public async Task<CheckResult> RunAsync(BomData bom, string projectDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            var board = await PcbPlotSettingsReader.ReadAsync(projectDirectory, cancellationToken).ConfigureAwait(false);
            return Compare(bom, board.Document.ReadFootprintFields(), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        { return new(Name, CheckStatus.Failed, "Could not compare BOM and layout.\n" + ex.Message); }
    }

    public CheckResult Compare(BomData bom, IReadOnlyList<ComponentFields> footprints, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bomComplete = new BomIdentifiersCheck().Run(bom, cancellationToken).Status == CheckStatus.Passed;
        var problems = new List<string>();
        var byReference = footprints.ToLookup(footprint => footprint.Reference, StringComparer.Ordinal);
        foreach (var component in bom.Components.OrderBy(component => component.Reference, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = IdentifierFields.Display(component.Reference);
            var bomFields = IdentifierFields.Read(component, includeDescription: true);
            var matches = byReference[component.Reference].ToArray();
            if (matches.Length == 0) { problems.Add($"{reference}: component in the BOM is missing from the layout."); continue; }
            if (matches.Length != 1) { problems.Add($"{reference}: duplicate reference in the layout; cannot identify one footprint."); continue; }
            var layout = IdentifierFields.Read(matches[0], includeDescription: true);
            problems.AddRange(layout.Issues.Select(issue => $"{reference}: layout: {issue}"));
            void CompareField(string name, string? expected, string? actual)
            {
                if (expected is not null && actual is not null && !string.Equals(expected, actual, StringComparison.Ordinal))
                    problems.Add($"{reference}: {name} differs: BOM {IdentifierFields.Display(expected)}, layout {IdentifierFields.Display(actual)}.");
            }
            CompareField("OEPS PN", bomFields.OepsPn, layout.OepsPn);
            CompareField("MPN", bomFields.Mpn, layout.Mpn);
            CompareField("OEPS Description", bomFields.Description, layout.Description);
        }
        if (problems.Count == 0 && bomComplete) return new(Name, CheckStatus.Passed, "");
        var detail = problems.Count == 0 ? "" : "BOM and layout are not consistent.\n" + string.Join("\n", problems) + "\n";
        if (!bomComplete) detail += $"Some field comparisons could not be completed. See {BomIdentifiersCheck.CheckName}.";
        return new(Name, CheckStatus.Failed, detail.TrimEnd());
    }
}
