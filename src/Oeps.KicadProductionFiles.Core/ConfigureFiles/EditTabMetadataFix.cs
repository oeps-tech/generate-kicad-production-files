using System.Text.Json.Nodes;

namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

/// <summary>Repairs the five Edit tab metadata options.</summary>
public sealed class EditTabMetadataFix : IConfigurationFix
{
    public string CheckName => "Edit Tab metadata";
    public string Description => "Select Group symbols and Include 'DNP' Symbols, clear Include 'Exclude from BOM' Symbols, and sort by Reference in ascending order.";

    public void Apply(JsonObject project)
    {
        var bom = ConfigurationJson.Bom(project);
        ConfigurationJson.SetScalar(bom, "group_symbols", JsonValue.Create(true));
        ConfigurationJson.SetScalar(bom, "exclude_dnp", JsonValue.Create(false));
        ConfigurationJson.SetScalar(bom, "include_excluded_from_bom", JsonValue.Create(false));
        ConfigurationJson.SetScalar(bom, "sort_asc", JsonValue.Create(true));
        ConfigurationJson.SetScalar(bom, "sort_field", JsonValue.Create("Reference"));
    }
}
