using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oeps.KicadProductionFiles.Core.Configuration;

public sealed class UserSettings
{
    public string KicadCliPath { get; set; } = "";
    public string ProjectDirectory { get; set; } = "";
    public string Revision { get; set; } = "";
    public int WindowWidth { get; set; } = 620;
    public int WindowHeight { get; set; } = 790;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }
    [JsonIgnore] public string? LoadError { get; private set; }

    public static UserSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), JsonStorage.Options) ?? new();
            settings.KicadCliPath ??= "";
            settings.ProjectDirectory ??= "";
            settings.Revision ??= "";
            settings.WindowWidth = Math.Clamp(settings.WindowWidth, 560, 3840);
            settings.WindowHeight = Math.Clamp(settings.WindowHeight, 500, 2160);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new() { LoadError = "Saved preferences could not be read: " + ex.Message };
        }
    }

    public void Save(string path) => JsonStorage.WriteAtomicAsync(path, this).GetAwaiter().GetResult();
}
