using System.Text.Json.Nodes;

namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

/// <summary>Repairs the included column order without adding or enabling required fields.</summary>
public sealed class FieldOrderFix : IConfigurationFix
{
    public string CheckName => "Field order";
    public string Description => "Order the included columns as #, Qty, Reference, Value, Tolerance, Footprint, TempCo, Voltage, DNP, OEPS PN, MPN, LCSC (if present), OEPS Description. Clear Included for additional columns, retaining their settings. Missing or hidden required columns need the Symbol Fields Table fix first.";

    public void Apply(JsonObject project)
    {
        var fields = ConfigurationJson.Fields(ConfigurationJson.Bom(project, create: false), create: false);
        var existing = ConfigurationJson.ReadFields(fields);
        var ordered = new List<JsonObject>();
        foreach (var rule in ConfigurationJson.FieldOrder)
        {
            var matches = existing.Where(field => rule.Matches(field.Name)).ToArray();
            if (matches.Length == 0 && rule.Optional) continue;
            if (matches.Length == 0)
                throw new InvalidDataException($"'{rule.Label}' is missing. Apply the Symbol Fields Table fix before fixing field order.");
            if (matches.Length > 1)
                throw new InvalidDataException($"Multiple fields match '{rule.Label}': {string.Join(", ", matches.Select(field => field.Name))}. Choose the intended alias in KiCad before fixing field order.");
            var (field, name) = matches[0];
            if (!ConfigurationJson.Included(field, name))
                throw new InvalidDataException($"'{name}' is not included. Apply the Symbol Fields Table fix before fixing field order.");
            ordered.Add(field);
        }

        var additional = existing.Where(field => !ordered.Contains(field.Field)).ToArray();
        // Validate all existing flags before changing anything, including optional extra columns.
        var extraIncluded = additional.Where(field => ConfigurationJson.Included(field.Field, field.Name)).ToArray();
        foreach (var (field, _) in extraIncluded) field["show"] = false;

        // Detach the original objects and reuse them so labels and unknown properties are retained.
        fields.Clear();
        foreach (var field in ordered) fields.Add(field);
        foreach (var (field, _) in additional) fields.Add(field);
    }
}
