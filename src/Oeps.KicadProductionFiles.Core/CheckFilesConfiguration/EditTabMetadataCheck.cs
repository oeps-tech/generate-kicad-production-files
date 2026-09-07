using System.Text.Json;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

/// <summary>Checks the Symbol Fields Table Edit tab's grouping, inclusion and sorting options.</summary>
public sealed class EditTabMetadataCheck : IFileCheck
{
    private const string CheckName = "Edit Tab metadata";
    public string Name => CheckName;

    private static readonly MetadataRule[] Rules =
    [
        new("group_symbols", JsonValueKind.True, "true", "Select Group symbols"),
        new("exclude_dnp", JsonValueKind.False, "false", "Select Include 'DNP' Symbols"),
        new("include_excluded_from_bom", JsonValueKind.False, "false", "Clear Include 'Exclude from BOM' Symbols"),
        new("sort_asc", JsonValueKind.True, "true", "Select ascending sort order"),
        new("sort_field", JsonValueKind.String, "Reference", "Set the sort field to Reference")
    ];

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, bom) = await SymbolFieldsTableReader.ReadBomSettingsAsync(context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
            return Validate(bom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(Name, CheckStatus.Failed, "Could not check the Edit Tab metadata.\n" + ex.Message);
        }
    }

    public static CheckResult Validate(JsonElement bomSettings)
    {
        if (bomSettings.ValueKind != JsonValueKind.Object)
            return new(CheckName, CheckStatus.Failed, "The saved bom_settings must be a JSON object.");

        var problems = new List<string>();
        foreach (var rule in Rules)
        {
            var properties = bomSettings.EnumerateObject().Where(property => property.Name == rule.Key).ToArray();
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
            {
                var expected = rule.Kind == JsonValueKind.String ? $"\"{rule.Expected}\"" : rule.Expected;
                problems.Add($"• {rule.Instruction} ({rule.Key}: {expected}; saved setting is {issue}).");
            }
        }

        return problems.Count == 0
            ? new(CheckName, CheckStatus.Passed,
                "Everything is good. Group symbols is selected; DNP symbols are included; symbols marked 'Exclude from BOM' are excluded; sorting is by Reference in ascending order.")
            : new(CheckName, CheckStatus.Failed, string.Join("\n", problems));
    }

    private sealed record MetadataRule(string Key, JsonValueKind Kind, string Expected, string Instruction);
}
