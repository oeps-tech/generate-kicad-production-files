using System.Text.Json.Nodes;

namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

internal static class ConfigurationJson
{
    // These names are KiCad field identifiers; the labels are only used for newly added columns.
    internal static readonly FieldRule[] FieldOrder =
    [
        new("${ITEM_NUMBER}", "#", ["${ITEM_NUMBER}"]),
        new("${QUANTITY}", "Qty", ["${QUANTITY}"]),
        new("Reference", "Reference", ["Reference"]),
        new("Value", "Value", ["Value"], Grouped: true),
        new("Tolerance", "Tolerance", ["Tolerance"], Grouped: true),
        new("Footprint", "Footprint", ["Footprint"], Grouped: true),
        new("TempCo", "TempCo", ["Temp. Co.", "TempCo", "Temp Co"], Grouped: true),
        new("Voltage", "Voltage", ["Voltage"], Grouped: true),
        new("${DNP}", "DNP", ["${DNP}"], Grouped: true),
        new("OEPS PN", "OEPS PN", ["OEPS PN", "OEPSPN"], Grouped: true),
        new("MPN", "MPN", ["MPN"], Grouped: true),
        new("LCSC", "LCSC", ["LCSC"], Grouped: true, Optional: true),
        new("OEPS Description", "OEPS Description", ["OEPS Description"], Grouped: true)
    ];

    internal static JsonObject Object(JsonObject parent, string name, string location, bool create = true)
    {
        if (!parent.TryGetPropertyValue(name, out var node))
        {
            if (!create) throw new InvalidDataException($"The project is missing {location}. Apply the Symbol Fields Table fix first.");
            var result = new JsonObject();
            parent.Add(name, result);
            return result;
        }
        return node as JsonObject ?? throw new InvalidDataException($"Project setting {location} must be a JSON object. Its existing value was not replaced.");
    }

    internal static JsonObject Bom(JsonObject project, bool create = true) =>
        Object(Object(project, "schematic", "schematic", create), "bom_settings", "schematic.bom_settings", create);

    internal static JsonArray Fields(JsonObject bom, bool create = true)
    {
        if (!bom.TryGetPropertyValue("fields_ordered", out var node))
        {
            if (!create) throw new InvalidDataException("The project is missing fields_ordered. Apply the Symbol Fields Table fix first.");
            var result = new JsonArray();
            bom.Add("fields_ordered", result);
            return result;
        }
        return node as JsonArray ?? throw new InvalidDataException("Project setting schematic.bom_settings.fields_ordered must be an array. Its existing value was not replaced.");
    }

    internal static IReadOnlyList<(JsonObject Field, string Name)> ReadFields(JsonArray fields)
    {
        var result = new List<(JsonObject Field, string Name)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < fields.Count; index++)
        {
            if (fields[index] is not JsonObject field || field["name"] is not JsonValue nameValue ||
                !nameValue.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException($"Field {index + 1} must be a JSON object with a nonempty name. Correct this entry before configuring fields.");
            if (!names.Add(name))
                throw new InvalidDataException($"More than one field is named '{name}'. Resolve the duplicate before configuring fields.");
            result.Add((field, name));
        }
        return result;
    }

    internal static bool Included(JsonObject field, string name)
    {
        if (!field.TryGetPropertyValue("show", out var value)) return false;
        if (value is JsonValue boolean && boolean.TryGetValue<bool>(out var included)) return included;
        throw new InvalidDataException($"The Included setting for '{name}' must be true or false. Apply the Symbol Fields Table fix first.");
    }

    internal static void SetScalar(JsonObject parent, string name, JsonValue value)
    {
        if (parent[name] is JsonObject or JsonArray)
            throw new InvalidDataException($"Setting '{name}' contains an unexpected object or array. Correct its structure before applying this fix.");
        parent[name] = value;
    }

    internal sealed record FieldRule(string Name, string Label, string[] Aliases, bool Grouped = false, bool Optional = false)
    {
        internal bool Matches(string name) => Aliases.Contains(name, StringComparer.Ordinal);
    }
}
