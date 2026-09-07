using Oeps.KicadProductionFiles.Core.Configuration;
using Oeps.KicadProductionFiles.Core.Data;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

namespace Oeps.KicadProductionFiles.Tests;

internal static class IntegrationChecks
{
    public static async Task<int> VerifyConfigurationAsync(string projectDirectory, string revision = "")
    {
        var report = await new ConfigurationCheckRunner().RunAsync(new CheckContext("", projectDirectory, Revision: revision));
        Console.WriteLine(report.Summary);
        foreach (var result in report.Entries)
        {
            Console.WriteLine($"[{result.Status.ToString().ToUpperInvariant()}] {result.Name}");
            if (result.Status != CheckStatus.Passed) Console.WriteLine(result.Detail);
        }
        return report.HasFailures || report.HasPendingChecks ? 1 : 0;
    }

    public static async Task<int> VerifyLiveAsync(string dataDirectory)
    {
        var paths = new AppPaths(dataDirectory);
        var config = AppConfiguration.Load(AppContext.BaseDirectory, paths.UserDataRoot);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        var repository = new ComponentRepository(http, config.SpreadsheetCsvUrl, paths.CacheCsvFile, config.HeaderAliases);
        await repository.RefreshAsync();
        if (!repository.HasData || repository.LastError is not null)
        {
            Console.Error.WriteLine(repository.LastError ?? "No spreadsheet data received.");
            return 1;
        }
        Console.WriteLine($"PASS live spreadsheet: {repository.Components.Count} PN/MPN pairings cached at {paths.CacheCsvFile}");
        var offline = new ComponentRepository(http, config.SpreadsheetCsvUrl, paths.CacheCsvFile, config.HeaderAliases);
        offline.LoadCache();
        Assert.Equal(repository.Components.Count, offline.Components.Count);
        Console.WriteLine("PASS cache reload; automatic refresh interval is five minutes.");
        return 0;
    }
}
