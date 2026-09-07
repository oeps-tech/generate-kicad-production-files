using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class CheckTests
{
    [Test]
    public static async Task CompleteSetupStillReportsProductionValidationPending()
    {
        using var folder = new CheckFolder();
        File.WriteAllText(Path.Combine(folder.Path, "project.kicad_sch"), "(kicad_sch)");
        File.WriteAllText(Path.Combine(folder.Path, "project.kicad_pcb"), "(kicad_pcb)");
        var cliPath = Path.Combine(folder.Path, "kicad-cli.exe");
        File.WriteAllText(cliPath, "test executable placeholder");
        var context = new CheckContext(cliPath, folder.Path, 42, DateTimeOffset.UtcNow);
        var runner = new CheckRunner([
            new KicadCliCheck(new FakeVersionProbe()), new ProjectFilesCheck(), new ComponentDatabaseCheck(),
            new BomPositionCountCheck(), new ComponentIdentifiersCheck()
        ]);
        var report = await runner.RunAsync(context);
        Assert.Equal(5, report.Entries.Count);
        Assert.False(report.HasFailures);
        Assert.True(report.HasPendingChecks);
        Assert.Equal(2, report.Entries.Count(entry => entry.Status == CheckStatus.Pending));
        Assert.True(report.Summary.Contains("have not been validated", StringComparison.Ordinal));
    }

    [Test]
    public static async Task MissingPathsAndDatabaseAreReportedIndependently()
    {
        var report = await new CheckRunner().RunAsync(new("", ""));
        Assert.Equal(3, report.Entries.Count(entry => entry.Status == CheckStatus.Failed));
        Assert.True(report.HasPendingChecks);
        Assert.True(report.Entries.Single(entry => entry.Name == "Component database").Detail.Contains("Update database", StringComparison.Ordinal));
    }

    [Test]
    public static async Task CliCheckRejectsMissingOrWrongFilesWithoutStartingProcess()
    {
        using var folder = new CheckFolder();
        var probe = new FakeVersionProbe();
        var check = new KicadCliCheck(probe);
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new("", folder.Path))).Status);
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new(Path.Combine(folder.Path, "kicad-cli.exe"), folder.Path))).Status);
        var wrongPath = Path.Combine(folder.Path, "cmd.exe");
        File.WriteAllText(wrongPath, "placeholder");
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new(wrongPath, folder.Path))).Status);
        Assert.Equal(0, probe.CallCount);
    }

    [Test]
    public static async Task CliCheckSurfacesVersionProbeFailureAndAcceptsQuotedPath()
    {
        using var folder = new CheckFolder();
        var cliPath = Path.Combine(folder.Path, "kicad-cli.exe");
        File.WriteAllText(cliPath, "placeholder");
        var probe = new FakeVersionProbe { Result = new(false, "CLI timed out") };
        var result = await new KicadCliCheck(probe).RunAsync(new($"\"{cliPath}\"", folder.Path));
        Assert.Equal(CheckStatus.Failed, result.Status);
        Assert.Equal("CLI timed out", result.Detail);
        Assert.Equal(cliPath, probe.LastPath);
    }

    [Test]
    public static async Task VersionProbeReportsUnlaunchableFile()
    {
        using var folder = new CheckFolder();
        var cliPath = Path.Combine(folder.Path, "kicad-cli.exe");
        File.WriteAllText(cliPath, "not an executable");
        var result = await new CliVersionProbe().ProbeAsync(cliPath);
        Assert.False(result.Success);
        Assert.True(result.Detail.Contains("could not run", StringComparison.Ordinal));
    }

    [Test]
    public static async Task ProjectCheckRequiresReadableNonemptySchematicAndBoardAtSelectedLevel()
    {
        using var folder = new CheckFolder();
        var nested = Directory.CreateDirectory(Path.Combine(folder.Path, "nested"));
        File.WriteAllText(Path.Combine(nested.FullName, "other.kicad_pcb"), "(kicad_pcb)");
        var schematic = Path.Combine(folder.Path, "project.kicad_sch");
        var board = Path.Combine(folder.Path, "project.kicad_pcb");
        File.WriteAllText(schematic, "(kicad_sch)");
        var check = new ProjectFilesCheck();
        var missing = await check.RunAsync(new("", folder.Path));
        Assert.Equal(CheckStatus.Failed, missing.Status);
        Assert.True(missing.Detail.Contains("Missing a .kicad_pcb", StringComparison.Ordinal));
        File.WriteAllText(board, "");
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new("", folder.Path))).Status);
        File.WriteAllText(board, "(kicad_pcb)");
        Assert.Equal(CheckStatus.Passed, (await check.RunAsync(new("", folder.Path))).Status);
        Assert.Equal("(kicad_sch)", File.ReadAllText(schematic));
        Assert.Equal("(kicad_pcb)", File.ReadAllText(board));
    }

    [Test]
    public static async Task MultipleProjectCandidatesRequireLaterSelection()
    {
        using var folder = new CheckFolder();
        File.WriteAllText(Path.Combine(folder.Path, "main.kicad_sch"), "(kicad_sch)");
        File.WriteAllText(Path.Combine(folder.Path, "subsheet.kicad_sch"), "(kicad_sch)");
        File.WriteAllText(Path.Combine(folder.Path, "main.kicad_pcb"), "(kicad_pcb)");
        var result = await new ProjectFilesCheck().RunAsync(new("", folder.Path));
        Assert.Equal(CheckStatus.Warning, result.Status);
        Assert.True(result.Detail.Contains("2 schematic(s)", StringComparison.Ordinal));
    }

    [Test]
    public static async Task DatabaseCheckDistinguishesFreshStaleAndFailedRefresh()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        var check = new ComponentDatabaseCheck(new FixedTimeProvider(now));
        Assert.Equal(CheckStatus.Passed, (await check.RunAsync(new("", "", 10, now.AddMinutes(-4)))).Status);
        Assert.Equal(CheckStatus.Warning, (await check.RunAsync(new("", "", 10, now.AddMinutes(-5)))).Status);
        Assert.Equal(CheckStatus.Warning, (await check.RunAsync(new("", "", 10, now, "Network unavailable"))).Status);
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new("", "", 0, null, "Network unavailable"))).Status);
    }

    [Test]
    public static async Task OneBrokenCheckDoesNotHideOtherResults()
    {
        var runner = new CheckRunner([new BrokenCheck(), new BomPositionCountCheck()]);
        var report = await runner.RunAsync(new("", ""));
        Assert.Equal(2, report.Entries.Count);
        Assert.Equal(CheckStatus.Failed, report.Entries[0].Status);
        Assert.Equal(CheckStatus.Pending, report.Entries[1].Status);
    }

    [Test]
    public static async Task CancelledChecksDoNotProduceMisleadingReport()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new CheckRunner().RunAsync(new("", ""), cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => new CliVersionProbe().ProbeAsync("missing.exe", cancellation.Token));
    }

    private sealed class FakeVersionProbe : ICliVersionProbe
    {
        public CliVersionProbeResult Result { get; init; } = new(true, "KiCad CLI is available: test version");
        public int CallCount { get; private set; }
        public string? LastPath { get; private set; }
        public Task<CliVersionProbeResult> ProbeAsync(string executablePath, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPath = executablePath;
            return Task.FromResult(Result);
        }
    }

    private sealed class BrokenCheck : IFileCheck
    {
        public string Name => "Broken test check";
        public Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default) => throw new IOException("Cannot read file");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CheckFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OepsProductionCheckTests", Guid.NewGuid().ToString("N"));
        public CheckFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
