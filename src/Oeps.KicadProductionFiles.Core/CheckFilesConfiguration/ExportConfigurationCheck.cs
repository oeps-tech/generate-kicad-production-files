using System.Text.Json;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

/// <summary>Checks the saved BOM output filename and export format options.</summary>
public sealed class ExportConfigurationCheck : IFileCheck
{
    private const string CheckName = "Export configuration";
    public string Name => CheckName;

    private static readonly ExportRule OutputRule = new("bom_export_filename", JsonValueKind.String,
        "manufacturing/bom/${PROJECTNAME}.csv", "Set Output file to manufacturing/bom/${PROJECTNAME}.csv");

    private static readonly ExportRule[] FormatRules =
    [
        new("field_delimiter", JsonValueKind.String, ",", "Set Field delimiter to a comma (,)"),
        new("string_delimiter", JsonValueKind.String, "\"", "Set String delimiter to a double quote (\")"),
        new("ref_delimiter", JsonValueKind.String, ",", "Set Reference delimiter to a comma (,)"),
        new("ref_range_delimiter", JsonValueKind.String, "", "Leave Range delimiter empty"),
        new("keep_tabs", JsonValueKind.False, "false", "Clear Keep tabs"),
        new("keep_line_breaks", JsonValueKind.False, "false", "Clear Keep line breaks")
    ];

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, schematic) = await SymbolFieldsTableReader.ReadSchematicSettingsAsync(context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
            return Validate(schematic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(Name, CheckStatus.Failed, "Could not check the export configuration.\n" + ex.Message);
        }
    }

    public static CheckResult Validate(JsonElement schematicSettings)
    {
        if (schematicSettings.ValueKind != JsonValueKind.Object)
            return new(CheckName, CheckStatus.Failed, "The saved schematic settings must be a JSON object.");

        var problems = new List<string>();
        CheckSetting(schematicSettings, OutputRule, problems);

        var formats = schematicSettings.EnumerateObject().Where(property => property.Name == "bom_fmt_settings").ToArray();
        if (formats.Length == 0) problems.Add("• bom_fmt_settings: missing export format settings.");
        else if (formats.Length > 1) problems.Add("• bom_fmt_settings: duplicated export format settings.");
        else if (formats[0].Value.ValueKind != JsonValueKind.Object)
            problems.Add("• bom_fmt_settings: export format settings must be a JSON object.");
        else
            foreach (var rule in FormatRules) CheckSetting(formats[0].Value, rule, problems);

        return problems.Count == 0
            ? new(CheckName, CheckStatus.Passed, "")
            : new(CheckName, CheckStatus.Failed, string.Join("\n", problems));
    }

    private static void CheckSetting(JsonElement parent, ExportRule rule, List<string> problems)
    {
        var properties = parent.EnumerateObject().Where(property => property.Name == rule.Key).ToArray();
        string? issue;
        if (properties.Length == 0) issue = "missing";
        else if (properties.Length > 1) issue = "duplicated";
        else
        {
            var value = properties[0].Value;
            var correct = value.ValueKind == rule.Kind &&
                (rule.Kind != JsonValueKind.String || value.GetString() == rule.Expected);
            issue = correct ? null : "incorrect";
        }

        if (issue is not null)
            problems.Add($"• {rule.Instruction} ({rule.Key}; saved setting is {issue}).");
    }

    private sealed record ExportRule(string Key, JsonValueKind Kind, string Expected, string Instruction);
}
