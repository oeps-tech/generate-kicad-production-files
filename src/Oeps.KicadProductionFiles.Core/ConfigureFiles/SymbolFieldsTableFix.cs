using System.Text.Json.Nodes;

namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

/// <summary>Repairs required columns and their Included/Group By settings.</summary>
public sealed class SymbolFieldsTableFix : IConfigurationFix
{
    public string CheckName => "Symbol Fields Table";
    public string Description => "Add missing required fields, enable Included for required fields, and enable Group By where required. Keep existing field names and labels. LCSC is configured only when already present.";

    public void Apply(JsonObject project)
    {
        var fields = ConfigurationJson.Fields(ConfigurationJson.Bom(project));
        var existing = ConfigurationJson.ReadFields(fields);
        foreach (var rule in ConfigurationJson.FieldOrder)
        {
            var matches = existing.Where(field => rule.Matches(field.Name)).ToArray();
            if (matches.Length == 0)
            {
                if (!rule.Optional)
                    fields.Add(new JsonObject
                    {
                        ["name"] = rule.Name,
                        ["label"] = rule.Label,
                        ["show"] = true,
                        ["group_by"] = rule.Grouped
                    });
                continue;
            }

            foreach (var (field, _) in matches)
            {
                ConfigurationJson.SetScalar(field, "show", JsonValue.Create(true));
                if (rule.Grouped) ConfigurationJson.SetScalar(field, "group_by", JsonValue.Create(true));
            }
        }
    }
}
