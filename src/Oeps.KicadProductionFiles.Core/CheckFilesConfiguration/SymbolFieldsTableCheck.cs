using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

/// <summary>Checks the saved Symbol Fields Table columns, inclusion and grouping.</summary>
public sealed class SymbolFieldsTableCheck : IFileCheck
{
    private const string CheckName = "Symbol Fields Table";
    public string Name => CheckName;

    private static readonly FieldRule[] Rules =
    [
        new(["Reference"]),
        new(["Value"], Grouped: true),
        new(["Footprint"], Grouped: true),
        new(["${QUANTITY}"]),
        // KiCad saves the displayed Item Number field with an underscore.
        new(["${ITEM_NUMBER}"]),
        new(["LCSC"], Grouped: true, Optional: true),
        new(["MPN"], Grouped: true),
        new(["OEPS Description"], Grouped: true),
        new(["OEPS PN", "OEPSPN"], Grouped: true),
        new(["Temp. Co.", "TempCo", "Temp Co"], Grouped: true),
        new(["Tolerance"], Grouped: true),
        new(["Voltage"], Grouped: true),
        new(["${DNP}"], Grouped: true)
    ];

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await SymbolFieldsTableReader.ReadAsync(context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
            return Validate(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(Name, CheckStatus.Failed, "Could not check the Symbol Fields Table.\n" + ex.Message);
        }
    }

    public static CheckResult Validate(SymbolFieldsTableSettings settings)
    {
        var problems = new List<string>();
        foreach (var rule in Rules)
        {
            var matches = settings.Fields.Where(field => rule.Names.Contains(field.Name, StringComparer.Ordinal)).ToArray();
            if (matches.Length == 0)
            {
                if (!rule.Optional)
                    problems.Add($"{string.Join(" or ", rule.Names)}: missing field. Add it and enable Included{(rule.Grouped ? " and Group By" : "")}.");
                continue;
            }

            // Any named alternative is accepted, but KiCad stores each present field independently.
            foreach (var field in matches)
            {
                var missing = new List<string>();
                if (!field.Included) missing.Add("not included (enable Included)");
                if (rule.Grouped && !field.Grouped) missing.Add("not grouped (enable Group By)");
                if (missing.Count > 0) problems.Add($"{field.Name}: {string.Join("; ", missing)}.");
            }
        }

        if (problems.Count > 0)
            return new(CheckName, CheckStatus.Failed,
                string.Join("\n", problems.Select(problem => "• " + problem)));

        var lcsc = settings.Fields.Any(field => field.Name == "LCSC")
            ? "LCSC is present, included and grouped."
            : "LCSC is absent, which is allowed.";
        return new(CheckName, CheckStatus.Passed,
            "Everything is good. All required fields exist and are included, and all fields requiring grouping are grouped.\n" + lcsc);
    }

    private sealed record FieldRule(string[] Names, bool Grouped = false, bool Optional = false);
}
