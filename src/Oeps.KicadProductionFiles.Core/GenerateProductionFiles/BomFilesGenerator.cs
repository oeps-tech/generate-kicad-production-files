using System.Globalization;
using System.Text.Json;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Data;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

/// <summary>Exports the main schematic hierarchy using the saved Symbol Fields Table configuration.</summary>
public sealed class BomFilesGenerator : IProductionFileGenerator
{
    public string Name => "BOM";
    private sealed record Settings(string Schematic, string[] Fields, string[] Labels, string[] Groups,
        string SortField, string Filter, bool ExcludeDnp, bool IncludeExcluded);

    private static async Task<Settings> ReadSettingsAsync(ProductionGenerationContext context, CancellationToken token)
    {
        var (project, schematic) = await SymbolFieldsTableReader.ReadSchematicSettingsAsync(context.ProjectDirectory, token).ConfigureAwait(false);
        var format = ExportConfigurationCheck.Validate(schematic);
        if (format.Status != Checks.CheckStatus.Passed)
            throw new InvalidDataException("BOM export needs the configured CSV output and delimiters.\n" + format.Detail);
        var input = Path.ChangeExtension(project, ".kicad_sch");
        if (!File.Exists(input) || new FileInfo(input).Length == 0)
            throw new InvalidDataException("The main .kicad_sch with the same name as the .kicad_pro is missing or empty.");
        var bom = Required(schematic, "bom_settings");
        var table = SymbolFieldsTableReader.ParseTable(project, bom, token);
        var included = table.Fields.Where(field => field.Included).ToArray();
        if (!included.Any(field => field.Name == "Reference") || !included.Any(field => field.Name == "${QUANTITY}"))
            throw new InvalidDataException("Include Reference and ${QUANTITY} in the Symbol Fields Table before exporting the BOM.");
        var fields = Required(bom, "fields_ordered").EnumerateArray().ToDictionary(field => Required(field, "name").GetString()!, StringComparer.Ordinal);
        var labels = included.Select(field => fields[field.Name].TryGetProperty("label", out var label)
            ? label.ValueKind == JsonValueKind.String ? label.GetString()! : throw new InvalidDataException("BOM labels must be strings.")
            : field.Name switch { "${ITEM_NUMBER}" => "#", "${QUANTITY}" => "Qty", "${DNP}" => "DNP", _ => field.Name }).ToArray();
        if (included.Select(field => field.Name).Concat(labels).Any(value => value.Contains(',') || value.Any(char.IsControl)))
            throw new InvalidDataException("KiCad CLI cannot represent commas or control characters in BOM field names or labels.");
        var groups = Boolean(bom, "group_symbols") ? table.Fields.Where(field => field.Grouped).Select(field => field.Name).ToArray() : [];
        if (groups.Any(value => value.Contains(',') || value.Any(char.IsControl))) throw new InvalidDataException("Unsupported BOM grouping field name.");
        var filter = bom.TryGetProperty("filter_string", out var savedFilter) ? savedFilter.GetString() ?? "" : "";
        if (!Boolean(bom, "sort_asc"))
            throw new InvalidDataException("Set the Symbol Fields Table sort to ascending before exporting the BOM. Use Configure files to fix Edit Tab metadata.");
        return new(input, included.Select(field => field.Name).ToArray(), labels, groups,
            Required(bom, "sort_field").GetString()!, filter,
            Boolean(bom, "exclude_dnp"), Boolean(bom, "include_excluded_from_bom"));
    }

    public async Task ValidateAsync(ProductionGenerationContext context, ICliCommandRunner cli, CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(context, cancellationToken).ConfigureAwait(false);
        var help = await cli.RunAsync(context.CliPath, ["sch", "export", "bom", "--help"], context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        if (help.ExitCode != 0) throw new IOException("KiCad CLI cannot export a BOM. " + ExportOutputDirectory.Diagnostics(help));
        foreach (var option in new[] { "--output", "--fields", "--labels", "--group-by", "--sort-field", "--filter",
            "--exclude-dnp", "--field-delimiter", "--string-delimiter", "--ref-delimiter", "--ref-range-delimiter" })
            if (!help.Output.Contains(option, StringComparison.Ordinal)) throw new InvalidDataException("KiCad CLI does not support the required BOM option " + option + ".");
        if (settings.IncludeExcluded && (!help.Output.Contains("--include-excluded-from-bom", StringComparison.Ordinal)
            || help.Output.Contains("Has no effect", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("This KiCad CLI cannot include symbols excluded from BOM. Clear that saved option before exporting.");
    }

    public async Task<GeneratedProductionFile> GenerateAsync(ProductionGenerationContext context, ICliCommandRunner cli, CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(context, cancellationToken).ConfigureAwait(false);
        using var directory = new ExportOutputDirectory(context, "bom");
        var output = Path.Combine(directory.DirectoryPath, Path.GetFileNameWithoutExtension(settings.Schematic) + ".csv");
        var args = new List<string> { "sch", "export", "bom", "--output", output,
            "--fields", string.Join(',', settings.Fields), "--labels", string.Join(',', settings.Labels),
            "--group-by", string.Join(',', settings.Groups), "--sort-field", settings.SortField, "--filter", settings.Filter,
            "--field-delimiter", ",", "--string-delimiter", "\"", "--ref-delimiter", ",", "--ref-range-delimiter", "" };
        // Ascending is the CLI default. Passing --sort-asc explicitly crashes KiCad 10.0.3 (bad_any_cast).
        if (settings.ExcludeDnp) args.Add("--exclude-dnp");
        if (settings.IncludeExcluded) args.Add("--include-excluded-from-bom");
        args.Add(settings.Schematic);
        var result = await cli.RunAsync(context.CliPath, args, context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("KiCad BOM export failed. " + ExportOutputDirectory.Diagnostics(result));
        ManufacturingDirectory.EnsureSafePath(context.ProjectDirectory, output);
        if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new IOException("KiCad CLI completed without producing the BOM CSV.");
        var records = CsvComponentParser.ReadRecords((await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false)).TrimStart('\uFEFF'));
        if (records.Count == 0 || !records[0].SequenceEqual(settings.Labels)) throw new InvalidDataException("BOM CSV headers do not match the saved field labels.");
        var qtyColumn = Array.IndexOf(settings.Fields, "${QUANTITY}");
        var refColumn = Array.IndexOf(settings.Fields, "Reference");
        var references = new List<string>();
        var components = new List<ComponentFields>();
        long total = 0;
        foreach (var row in records.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Count != settings.Fields.Length) throw new InvalidDataException("BOM CSV contains an incomplete row.");
            if (!int.TryParse(row[qtyColumn], NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
                throw new InvalidDataException("BOM CSV contains an invalid quantity.");
            var refs = row[refColumn].Split(',', StringSplitOptions.TrimEntries);
            if (refs.Length != quantity || refs.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("BOM quantity does not match the references in a row.");
            references.AddRange(refs);
            // Use field identities rather than user-editable CSV labels; expand grouped references once.
            var fields = settings.Fields.Zip(row).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal).AsReadOnly();
            components.AddRange(refs.Select(reference => new ComponentFields(reference, fields)));
            total += quantity;
            if (total > int.MaxValue) throw new InvalidDataException("BOM component count exceeds the supported limit.");
        }
        var paths = directory.Publish([output], cancellationToken);
        return new(Name, paths[0], (int)total)
        {
            References = references.AsReadOnly(),
            Bom = new(Array.AsReadOnly(settings.Fields), components.AsReadOnly())
        };
    }

    private static JsonElement Required(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Malformed BOM settings.");
        var values = parent.EnumerateObject().Where(property => property.Name == name).ToArray();
        if (values.Length != 1) throw new InvalidDataException("Missing or duplicate BOM setting: " + name);
        return values[0].Value;
    }
    private static bool Boolean(JsonElement parent, string name) => Required(parent, name).ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        _ => throw new InvalidDataException("BOM setting must be true or false: " + name)
    };
}
