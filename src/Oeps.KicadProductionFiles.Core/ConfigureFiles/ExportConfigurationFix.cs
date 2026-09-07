using System.Text.Json.Nodes;

namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

/// <summary>Repairs the output filename and six BOM export format settings.</summary>
public sealed class ExportConfigurationFix : IConfigurationFix
{
    public string CheckName => "Export configuration";
    public string Description => "Set Output file to manufacturing/bom/${PROJECTNAME}.csv, use comma field and reference delimiters and a double quote string delimiter, leave the range delimiter empty, and clear Keep tabs and Keep line breaks.";

    public void Apply(JsonObject project)
    {
        var schematic = ConfigurationJson.Object(project, "schematic", "schematic");
        var format = ConfigurationJson.Object(schematic, "bom_fmt_settings", "schematic.bom_fmt_settings");
        ConfigurationJson.SetScalar(schematic, "bom_export_filename", JsonValue.Create("manufacturing/bom/${PROJECTNAME}.csv"));
        ConfigurationJson.SetScalar(format, "field_delimiter", JsonValue.Create(","));
        ConfigurationJson.SetScalar(format, "string_delimiter", JsonValue.Create("\""));
        ConfigurationJson.SetScalar(format, "ref_delimiter", JsonValue.Create(","));
        ConfigurationJson.SetScalar(format, "ref_range_delimiter", JsonValue.Create(""));
        ConfigurationJson.SetScalar(format, "keep_tabs", JsonValue.Create(false));
        ConfigurationJson.SetScalar(format, "keep_line_breaks", JsonValue.Create(false));
    }
}
