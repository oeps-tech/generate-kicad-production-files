namespace Oeps.KicadProductionFiles.Core.Configuration;

public sealed class AppPaths
{
    public static string DefaultUserDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OEPS", "KicadProductionFiles");

    public AppPaths(string? userDataRoot = null) => UserDataRoot = Path.GetFullPath(userDataRoot ?? DefaultUserDataRoot);
    public string UserDataRoot { get; }
    public string CacheCsvFile => Path.Combine(UserDataRoot, "components.csv");
    public string SettingsFile => Path.Combine(UserDataRoot, "user-settings.json");
    public string ConfigurationFile => Path.Combine(UserDataRoot, "appsettings.json");
}
