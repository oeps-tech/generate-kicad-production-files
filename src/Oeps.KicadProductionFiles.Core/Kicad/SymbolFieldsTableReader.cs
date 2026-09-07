using System.Text.Json;

namespace Oeps.KicadProductionFiles.Core.Kicad;

public sealed record SymbolTableField(string Name, bool Included, bool Grouped);

public sealed record SymbolFieldsTableSettings(string ProjectFilePath, IReadOnlyList<SymbolTableField> Fields, bool? GroupSymbols);

/// <summary>Reads the saved symbol-fields table settings without modifying the KiCad project.</summary>
public static class SymbolFieldsTableReader
{
    private const int MaximumProjectBytes = 20 * 1024 * 1024;

    public static async Task<SymbolFieldsTableSettings> ReadAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        var (projectFile, bom) = await ReadBomSettingsAsync(projectDirectory, cancellationToken).ConfigureAwait(false);
        return ParseTable(projectFile, bom, cancellationToken);
    }

    internal static SymbolFieldsTableSettings ParseTable(string projectFile, JsonElement bom, CancellationToken cancellationToken = default)
    {
        var orderedFields = RequiredProperty(bom, "fields_ordered", "schematic.bom_settings.fields_ordered");
        if (orderedFields.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Project setting schematic.bom_settings.fields_ordered must be an array.");

        List<SymbolTableField> fields = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        var index = 0;
        foreach (var field in orderedFields.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = $"schematic.bom_settings.fields_ordered[{index++}]";
            var nameValue = RequiredProperty(field, "name", location + ".name");
            if (nameValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameValue.GetString()))
                throw new InvalidDataException($"Project setting {location}.name must be a nonempty string.");
            var name = nameValue.GetString()!;
            if (!names.Add(name))
                throw new InvalidDataException($"The saved symbol-fields table contains more than one field named '{name}'.");
            fields.Add(new(name, OptionalBoolean(field, "show", location + ".show") ?? false,
                OptionalBoolean(field, "group_by", location + ".group_by") ?? false));
        }

        return new(projectFile, fields.AsReadOnly(), OptionalBoolean(bom, "group_symbols", "schematic.bom_settings.group_symbols"));
    }

    // Read each settings block independently so one broken configuration does not hide other results.
    internal static async Task<(string ProjectFilePath, JsonElement Bom)> ReadBomSettingsAsync(
        string projectDirectory, CancellationToken cancellationToken = default)
    {
        var (projectFile, schematic) = await ReadSchematicSettingsAsync(projectDirectory, cancellationToken).ConfigureAwait(false);
        var bom = RequiredProperty(schematic, "bom_settings", "schematic.bom_settings");
        if (bom.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Project setting schematic.bom_settings must be a JSON object.");
        return (projectFile, bom);
    }

    internal static async Task<(string ProjectFilePath, JsonElement Schematic)> ReadSchematicSettingsAsync(
        string projectDirectory, CancellationToken cancellationToken = default)
    {
        var (projectFile, project) = await ReadProjectSettingsAsync(projectDirectory, cancellationToken).ConfigureAwait(false);
        var schematic = RequiredProperty(project, "schematic", "schematic");
        if (schematic.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Project setting schematic must be a JSON object.");
        return (projectFile, schematic);
    }

    internal static async Task<(string ProjectFilePath, JsonElement Project)> ReadProjectSettingsAsync(
        string projectDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = projectDirectory?.Trim().Trim('"').Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("Choose the folder containing the KiCad project before checking symbol fields.");
        if (File.Exists(directory))
            throw new InvalidDataException("Select the KiCad project folder, rather than an individual file.");
        if (!Directory.Exists(directory))
            throw new InvalidDataException($"The KiCad project folder does not exist or cannot be accessed: {directory}");

        var projectFiles = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".kicad_pro", StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (projectFiles.Length == 0)
            throw new InvalidDataException("No .kicad_pro file was found directly in the selected folder. The saved symbol-fields table settings are stored in the project file.");
        if (projectFiles.Length > 1)
            throw new InvalidDataException("More than one .kicad_pro file was found directly in the selected folder. Select a folder with exactly one project so its symbol-fields settings are unambiguous.");

        var projectFile = Path.GetFullPath(projectFiles[0]);
        try
        {
            var bytes = await ReadProjectBytesAsync(projectFile, cancellationToken).ConfigureAwait(false);
            // Accept the UTF-8 BOM sometimes added by text editors without changing the project file.
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(offset));
            cancellationToken.ThrowIfCancellationRequested();
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The KiCad project must be a JSON object.");
            return (projectFile, document.RootElement.Clone());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The KiCad project '{Path.GetFileName(projectFile)}' contains invalid JSON: {exception.Message}", exception);
        }
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name, string location) =>
        OptionalProperty(parent, name, location) ?? throw new InvalidDataException($"The KiCad project is missing saved setting {location}.");

    private static JsonElement? OptionalProperty(JsonElement parent, string name, string location)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Cannot read project setting {location}: its parent must be a JSON object.");
        JsonElement? found = null;
        foreach (var property in parent.EnumerateObject())
        {
            if (property.Name != name) continue;
            if (found.HasValue)
                throw new InvalidDataException($"The KiCad project contains duplicate setting {location}.");
            found = property.Value;
        }
        return found;
    }

    private static bool? OptionalBoolean(JsonElement parent, string name, string location)
    {
        var value = OptionalProperty(parent, name, location);
        if (!value.HasValue) return null;
        return value.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Project setting {location} must be true or false.")
        };
    }

    private static async Task<byte[]> ReadProjectBytesAsync(string projectFile, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(projectFile, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumProjectBytes)
            throw new InvalidDataException("The KiCad project file exceeds the 20 MiB limit for reading symbol-fields settings.");
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int received;
        while ((received = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + received > MaximumProjectBytes)
                throw new InvalidDataException("The KiCad project file exceeds the 20 MiB limit for reading symbol-fields settings.");
            buffer.Write(chunk, 0, received);
        }
        return buffer.ToArray();
    }
}
