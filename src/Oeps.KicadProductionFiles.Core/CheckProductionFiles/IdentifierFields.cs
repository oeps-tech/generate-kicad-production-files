using Oeps.KicadProductionFiles.Core.GenerateProductionFiles;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.CheckProductionFiles;

internal sealed record IdentifierFields(string? OepsPn, string? Mpn, string? Description, IReadOnlyList<string> Issues)
{
    internal static IdentifierFields Read(ComponentFields component, bool includeDescription = false)
    {
        var issues = new List<string>(component.Issues ?? []);
        string? ReadField(string label, params string[] names)
        {
            var values = names.Where(component.Fields.ContainsKey).Select(name => component.Fields[name].Trim())
                .Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length == 0) { issues.Add($"Missing {label}."); return null; }
            if (values.Length > 1)
            { issues.Add($"Conflicting {label} aliases: " + string.Join(", ", values.Select(Display)) + "."); return null; }
            if (values[0].Any(char.IsControl) || values[0].Contains("${", StringComparison.Ordinal))
            { issues.Add($"{label} contains control characters or an unresolved text variable: {Display(values[0])}."); return null; }
            return values[0];
        }
        var pn = ReadField("OEPS PN", "OEPS PN", "OEPSPN");
        var mpn = ReadField("MPN", "MPN");
        var description = includeDescription ? ReadField("OEPS Description", "OEPS Description") : null;
        return new(pn, mpn, description, issues.AsReadOnly());
    }

    internal static List<string> BomIssues(BomData bom)
    {
        var issues = new List<string>();
        if (!bom.Fields.Any(field => field is "OEPS PN" or "OEPSPN")) issues.Add("The generated BOM has no included OEPS PN / OEPSPN column.");
        if (!bom.Fields.Contains("MPN")) issues.Add("The generated BOM has no included MPN column.");
        if (!bom.Fields.Contains("OEPS Description")) issues.Add("The generated BOM has no included OEPS Description column.");
        foreach (var group in bom.Components.GroupBy(component => component.Reference, StringComparer.Ordinal).Where(group => group.Count() > 1))
            issues.Add($"{Display(group.Key)}: duplicate reference in the BOM; cannot identify one physical component.");
        return issues;
    }

    internal static string Display(string value) => "'" + value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "'";
}
