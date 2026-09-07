using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

public sealed class ProjectConfigurationSnapshot
{
    internal ProjectConfigurationSnapshot(string path, byte[] bytes, JsonObject document)
    { ProjectFilePath = path; OriginalBytes = bytes; _document = document; }

    private readonly JsonObject _document;
    internal byte[] OriginalBytes { get; }
    public string ProjectFilePath { get; }
    public JsonObject CreateDocument() => (JsonObject)_document.DeepClone();
}

/// <summary>Saves approved project edits with an original-file backup and a stale-file check.</summary>
public static class ProjectConfigurationStore
{
    private const int MaximumBytes = 20 * 1024 * 1024;

    public static async Task<ProjectConfigurationSnapshot> LoadAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = projectDirectory.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new InvalidDataException("Choose an accessible KiCad project folder before configuring files.");
        var files = Directory.EnumerateFiles(directory).Where(path =>
            Path.GetExtension(path).Equals(".kicad_pro", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (files.Length != 1)
            throw new InvalidDataException("Select a folder containing exactly one .kicad_pro file before configuring files.");
        return await LoadFileAsync(files[0], cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ProjectConfigurationSnapshot> LoadFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(filePath);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Select the original project file rather than a linked project file.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
        try
        {
            var offset = HasBom(bytes) ? 3 : 0;
            using var json = JsonDocument.Parse(bytes.AsMemory(offset));
            CheckDuplicateProperties(json.RootElement);
            var document = JsonNode.Parse(bytes.AsSpan(offset)) as JsonObject
                ?? throw new InvalidDataException("The configuration must contain a JSON object.");
            return new(path, bytes, document);
        }
        catch (JsonException ex) { throw new InvalidDataException("The configuration contains invalid JSON. Correct it before applying configuration fixes.", ex); }
    }

    public static async Task<string?> SaveAsync(ProjectConfigurationSnapshot snapshot, JsonObject modified,
        CancellationToken cancellationToken = default)
    {
        var backups = await ConfigurationFileStore.SaveAsync([PrepareEdit(snapshot, modified)], cancellationToken).ConfigureAwait(false);
        return backups.SingleOrDefault();
    }

    internal static ConfigurationFileEdit PrepareEdit(ProjectConfigurationSnapshot snapshot, JsonObject modified)
    {
        if (JsonNode.DeepEquals(snapshot.CreateDocument(), modified))
            return new(snapshot.ProjectFilePath, snapshot.OriginalBytes, snapshot.OriginalBytes);

        var originalText = Encoding.UTF8.GetString(snapshot.OriginalBytes);
        var newline = originalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var json = modified.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .ReplaceLineEndings(newline);
        if (originalText.EndsWith('\n')) json += newline;
        if (HasBom(snapshot.OriginalBytes)) json = "\uFEFF" + json;
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The configured project would exceed the 20 MiB size limit.");

        return new(snapshot.ProjectFilePath, snapshot.OriginalBytes, bytes);
    }

    private static bool HasBom(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static void CheckDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Duplicate JSON setting '{property.Name}' is ambiguous. Resolve it before applying automatic fixes.");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) CheckDuplicateProperties(child);
    }

    private static async Task<byte[]> ReadBoundedAsync(FileStream stream, CancellationToken cancellationToken)
    {
        if (stream.Length > MaximumBytes) throw new InvalidDataException("The project exceeds the 20 MiB size limit.");
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("The project exceeds the 20 MiB size limit.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
}
