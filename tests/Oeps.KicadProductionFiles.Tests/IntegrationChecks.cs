using Oeps.KicadProductionFiles.Core.Configuration;
using Oeps.KicadProductionFiles.Core.Data;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.ConfigureFiles;

namespace Oeps.KicadProductionFiles.Tests;

internal static class IntegrationChecks
{
    public static async Task<int> VerifyDescriptionFixAsync(string cli, string projectDirectory, string databaseCsv)
    {
        var isolatedRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(projectDirectory).StartsWith(isolatedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fix integration harness only accepts isolated .local fixtures.");
        var database = CsvComponentParser.Parse(await File.ReadAllTextAsync(databaseCsv));
        var context = new CheckContext(cli, projectDirectory) { Database = database };
        var checks = new ConfigurationCheckRunner([new SchematicBomIdentifiersCheck()]);
        var runner = new ConfigurationFixRunner([new OepsDescriptionFix()], checks);
        var before = Directory.GetFiles(projectDirectory, "*.kicad_sch", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var declined = await runner.RunAsync(context, (_, _) => Task.FromResult(false));
        Assert.Equal(FixActionStatus.Skipped, declined.Actions.Single().Status);
        foreach (var (path, bytes) in before) Assert.True(bytes.SequenceEqual(File.ReadAllBytes(path)));
        Console.WriteLine("PASS declined description fix preserves all schematic bytes.");
        var result = await runner.RunAsync(context, (prompt, _) =>
        {
            Console.WriteLine(prompt.Description);
            return Task.FromResult(true);
        });
        foreach (var action in result.Actions) Console.WriteLine($"[{action.Status}] {action.Detail}");
        foreach (var check in result.Checks.Entries) Console.WriteLine($"[{check.Status}] {check.Name}\n{check.Detail}");
        Assert.Equal(FixActionStatus.Fixed, result.Actions.Single().Status);
        Assert.True(result.Checks.AllPassed);
        foreach (var (path, bytes) in before.Where(pair => !pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))))
            Assert.True(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, ".oeps-backups"), Path.GetFileName(path) + ".*.bak")
                .Any(backup => bytes.SequenceEqual(File.ReadAllBytes(backup))), "Original schematic backup was not found.");
        await runner.RunAsync(context, (_, _) => throw new Exception("A successful recheck must not prompt again."));
        Console.WriteLine("PASS real CLI description fix, original backups, post-fix checks and idempotency.");
        return 0;
    }

    public static async Task<int> VerifyBomIdentifiersAsync(string cli, string projectDirectory, string databaseCsv)
    {
        var database = CsvComponentParser.Parse(await File.ReadAllTextAsync(databaseCsv));
        var check = await new SchematicBomIdentifiersCheck().RunAsync(new(cli, projectDirectory) { Database = database });
        Console.WriteLine($"[{check.Status.ToString().ToUpperInvariant()}] {check.Name}");
        if (check.Status != CheckStatus.Passed) Console.WriteLine(check.Detail);
        return check.Status == CheckStatus.Passed ? 0 : 1;
    }

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
