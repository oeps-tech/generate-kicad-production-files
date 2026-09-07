using System.Text.Json;
using Oeps.KicadProductionFiles.Core.Data;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Oeps.KicadProductionFiles.Core.Configuration;

public sealed class AppConfiguration
{
    public const string DefaultSpreadsheetCsvUrl = "https://docs.google.com/spreadsheets/d/1c0Wh_HY_bz6y2l1Jr0D6rPmvKgyRPSEPphRE5ZAqndk/gviz/tq?tqx=out:csv&sheet=components%20list&headers=0&tq=select%20A%2CB%2CD%20where%20B%20is%20not%20null%20and%20D%20is%20not%20null%20label%20A%20%27Description%27%2C%20B%20%27OEPS_PN%27%2C%20D%20%27MPN%27";

    public string SpreadsheetCsvUrl { get; set; } = DefaultSpreadsheetCsvUrl;
    public ComponentHeaderAliases HeaderAliases { get; set; } = new();
    public bool DryRun { get; set; } = true;
    public bool SampleMode { get; set; }
    public string GitHubOwner { get; set; } = "oeps-tech";
    public string GitHubRepository { get; set; } = "generate-kicad-production-files";
    public string PackagePrefix { get; set; } = "Oeps.KicadProductionFiles";

    /// <summary>User appsettings.json values override the packaged configuration.</summary>
    public static AppConfiguration Load(string baseDirectory, string userDataRoot)
    {
        JsonObject merged = new();
        var packagePath = Path.Combine(baseDirectory, "appsettings.json");
        var userPath = Path.Combine(userDataRoot, "appsettings.json");
        foreach (var path in new[] { packagePath, userPath }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var parsed = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }) as JsonObject ?? throw new InvalidDataException("Configuration must contain a JSON object.");
                Merge(merged, parsed);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Cannot read configuration '{path}': {ex.Message}", ex);
            }
        }

        var configuration = merged.Deserialize<AppConfiguration>(JsonStorage.Options) ?? new();
        configuration.HeaderAliases ??= new();
        return configuration;
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
        {
            var key = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, pair.Key, StringComparison.OrdinalIgnoreCase)) ?? pair.Key;
            if (pair.Value is JsonObject sourceChild && target[key] is JsonObject targetChild)
                Merge(targetChild, sourceChild);
            else
                target[key] = pair.Value?.DeepClone();
        }
    }
}

internal static class JsonStorage
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
