using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.ConfigureFiles;
using Oeps.KicadProductionFiles.Core.Data;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class SchematicBomIdentifiersCheckTests
{
    private const string Header = "Reference,OEPS PN,OEPSPN,MPN,OEPS Description\n";

    [Test]
    public static async Task ExplicitColumnsAndDefaultBomScopeIgnoreBrokenTableConfiguration()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Project, "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[],\"filter_string\":\"R*\"},\"bom_fmt_settings\":{\"field_delimiter\":\";\"}}}");
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "linked.kicad_sch"), "linked sheet fixture");
        var original = Snapshot(fixture.Directory);
        var cli = new FakeCli(Header + "C1,001,,CAP-1,Capacitor\nR1,,002,RES-2,Resistor\n");
        var result = await new SchematicBomIdentifiersCheck(cli).RunAsync(fixture.Context);
        Assert.Equal(CheckStatus.Passed, result.Status);
        Assert.Equal("", result.Detail);
        Assert.Equal(1, cli.Calls);
        Assert.Equal("Reference,OEPS PN,OEPSPN,MPN,OEPS Description", cli.Option("--fields"));
        Assert.Equal("Reference,OEPS PN,OEPSPN,MPN,OEPS Description", cli.Option("--labels"));
        foreach (var option in new[] { "--group-by", "--filter", "--ref-range-delimiter" }) Assert.Equal("", cli.Option(option));
        Assert.Equal(",", cli.Option("--field-delimiter"));
        Assert.Equal("\"", cli.Option("--string-delimiter"));
        Assert.True(cli.Arguments.Contains("--keep-tabs") && cli.Arguments.Contains("--keep-line-breaks"));
        foreach (var option in new[] { "--exclude-dnp", "--include-excluded-from-bom", "--preset", "--variant", "--sort-asc" })
            Assert.False(cli.Arguments.Contains(option));
        Assert.Equal(Path.ChangeExtension(fixture.Project, ".kicad_sch"), cli.Arguments[^1]);
        Assert.False(Path.GetFullPath(cli.Output).StartsWith(fixture.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.False(System.IO.Directory.Exists(Path.GetDirectoryName(cli.Output)));
        Assert.Equal(original, Snapshot(fixture.Directory));
    }

    [Test]
    public static async Task ReportsEachMissingUnknownAndMismatchedIdentifier()
    {
        using var fixture = new Fixture();
        var cli = new FakeCli(Header + "C1,,,,\nR1,999,,UNKNOWN,Unknown\nR2,002,,WRONG,Resistor\nC2,001,002,CAP-1,Capacitor\n");
        var result = await new SchematicBomIdentifiersCheck(cli).RunAsync(fixture.Context);
        Assert.Equal(CheckStatus.Failed, result.Status);
        foreach (var expected in new[] { "'C1': Missing OEPS PN", "'C1': Missing MPN", "'R1': OEPS PN '999' was not found", "'R2': MPN 'WRONG' does not match OEPS PN '002'", "RES-2", "'C2':" })
            Assert.True(result.Detail.Contains(expected), result.Detail);
        Assert.False(result.Detail.Contains("See BOM required fields: OEPS PN, MPN and OEPS Description"));
    }

    [Test]
    public static async Task MissingAndMismatchedDescriptionsAreReportedPerReference()
    {
        using var fixture = new Fixture();
        var cli = new FakeCli(Header + "C1,001,,CAP-1,\nR1,,002,RES-2,Wrong description\nC2,001,,CAP-1,capacitor\n");
        var result = await new SchematicBomIdentifiersCheck(cli).RunAsync(fixture.Context);
        Assert.Equal(CheckStatus.Failed, result.Status);
        Assert.True(result.Detail.Contains("'C1': Missing OEPS Description."), result.Detail);
        Assert.True(result.Detail.Contains("'R1': OEPS Description 'Wrong description' does not match"), result.Detail);
        Assert.True(result.Detail.Contains("Database Description(s): 'Resistor'"), result.Detail);
        Assert.True(result.Detail.Contains("'C2': OEPS Description 'capacitor' does not match"), result.Detail);
    }

    [Test]
    public static async Task DescriptionUsesMatchingPnMpnPairAndTrimsOnlyOuterWhitespace()
    {
        using var fixture = new Fixture();
        var context = fixture.Context with { Database = [
            new("001", "CAP-1", Description: "  Capacitor, 100 nF  "),
            new("001", "CAP-ALT", Description: "Alternative capacitor")
        ] };
        var good = await new SchematicBomIdentifiersCheck(new FakeCli(Header + "C1,001,,CAP-1,\" Capacitor, 100 nF \"\n")).RunAsync(context);
        Assert.Equal(CheckStatus.Passed, good.Status);
        Assert.Equal("", good.Detail);
        foreach (var description in new[] { "Alternative capacitor", "Capacitor,  100 nF" })
        {
            var bad = await new SchematicBomIdentifiersCheck(new FakeCli(Header + $"C1,001,,CAP-1,\"{description}\"\n")).RunAsync(context);
            Assert.Equal(CheckStatus.Failed, bad.Status);
            Assert.True(bad.Detail.Contains("Database Description(s): 'Capacitor, 100 nF'"), bad.Detail);
        }
    }

    [Test]
    public static async Task MissingDatabaseDescriptionCannotPass()
    {
        using var fixture = new Fixture();
        foreach (var description in new string?[] { null, "", "  " })
        {
            var result = await new SchematicBomIdentifiersCheck(new FakeCli(Header + "C1,001,,CAP-1,Capacitor\n"))
                .RunAsync(fixture.Context with { Database = [new("001", "CAP-1", Description: description)] });
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains("database has no Description"), result.Detail);
        }
    }

    [Test]
    public static async Task InvalidDescriptionsAndIdentifierFailuresAreReportedTogether()
    {
        using var fixture = new Fixture();
        foreach (var description in new[] { "${UNKNOWN}", "Cap\tacitor", "Cap\nacitor" })
        {
            var result = await new SchematicBomIdentifiersCheck(new FakeCli(Header + $"C1,001,,,\"{description}\"\n"))
                .RunAsync(fixture.Context);
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains("Missing MPN"), result.Detail);
            Assert.True(result.Detail.Contains("OEPS Description contains control characters or an unresolved text variable"), result.Detail);
        }
    }

    [Test]
    public static async Task DescriptionFixDoesNotEditMalformedSchematic()
    {
        using var fixture = new Fixture();
        var original = Snapshot(fixture.Directory);
        var check = new SchematicBomIdentifiersCheck(new FakeCli(Header + "C1,001,,CAP-1,Incorrect description\n"));
        var result = await new ConfigurationFixRunner(checks: new ConfigurationCheckRunner([check]))
            .RunAsync(fixture.Context, (_, _) => throw new Exception("Malformed schematics must not prompt for changes."));
        Assert.False(result.Checks.AllPassed);
        Assert.Equal(CheckStatus.Failed, result.Checks.Entries.Single().Status);
        Assert.Equal(FixActionStatus.Failed, result.Actions.Single().Status);
        Assert.Equal(original, Snapshot(fixture.Directory));
    }

    [Test]
    public static async Task MissingDatabaseIsFailureWithActionableMessage()
    {
        using var fixture = new Fixture();
        var result = await new SchematicBomIdentifiersCheck(new FakeCli(Header + "C1,001,,CAP-1,Capacitor\n"))
            .RunAsync(fixture.Context with { Database = [] });
        Assert.Equal(CheckStatus.Failed, result.Status);
        Assert.True(result.Detail.Contains("Update database"));
    }

    [Test]
    public static async Task PreservesLeadingZerosAndCaseAndAcceptsDatabaseAlternatives()
    {
        using var fixture = new Fixture();
        var context = fixture.Context with { Database = [new("001", "CAP-1", Description: "Capacitor"), new("001", "CAP-ALT", Description: "Capacitor")] };
        var good = await new SchematicBomIdentifiersCheck(new FakeCli(Header + "C1,001,,CAP-ALT,Capacitor\n")).RunAsync(context);
        Assert.Equal(CheckStatus.Passed, good.Status);
        var bad = await new SchematicBomIdentifiersCheck(new FakeCli(Header + "C1,1,,CAP-1,Capacitor\nC2,001,,cap-1,Capacitor\n")).RunAsync(context);
        Assert.Equal(CheckStatus.Failed, bad.Status);
        Assert.True(bad.Detail.Contains("'C1'") && bad.Detail.Contains("'C2'"));
    }

    [Test]
    public static async Task InvalidCsvAndDuplicateReferencesCannotPass()
    {
        using var fixture = new Fixture();
        foreach (var csv in new[] { "wrong,header\n", Header + "C1,001\n", Header + ",001,,CAP-1,Capacitor\n",
            Header + "C1,001,,CAP-1,Capacitor\nC1,001,,CAP-1,Capacitor\n", Header + "\"C1,C2\",001,,CAP-1,Capacitor\n",
            Header + "C1,001,,\"CAP\n-1\",Capacitor\n", Header + "C1,001,,\"unterminated" })
        {
            var cli = new FakeCli(csv);
            var result = await new SchematicBomIdentifiersCheck(cli).RunAsync(fixture.Context);
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.False(System.IO.Directory.Exists(Path.GetDirectoryName(cli.Output)));
        }
    }

    [Test]
    public static async Task ExportErrorsAndMissingOutputCannotPassAndCleanTemporaryFiles()
    {
        using var fixture = new Fixture();
        foreach (var cli in new[] { new FakeCli(Header) { ExitCode = 2 }, new FakeCli(null), new FakeCli("") })
        {
            var result = await new SchematicBomIdentifiersCheck(cli).RunAsync(fixture.Context);
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains("KiCad CLI"));
            Assert.False(System.IO.Directory.Exists(Path.GetDirectoryName(cli.Output)));
        }
    }

    [Test]
    public static async Task CancellationCleansPartialCsvAndPreservesProject()
    {
        using var fixture = new Fixture();
        var original = Snapshot(fixture.Directory);
        using var cancellation = new CancellationTokenSource();
        var cli = new FakeCli(Header) { OnExport = () => cancellation.Cancel() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => new SchematicBomIdentifiersCheck(cli).RunAsync(fixture.Context, cancellation.Token));
        Assert.False(System.IO.Directory.Exists(Path.GetDirectoryName(cli.Output)));
        Assert.Equal(original, Snapshot(fixture.Directory));
    }

    [Test]
    public static async Task DatabaseSnapshotDoesNotChangeDuringExport()
    {
        using var fixture = new Fixture();
        var database = fixture.Context.Database.ToList();
        var cli = new FakeCli(Header + "C1,001,,CAP-1,Capacitor\n") { OnExport = () => database.Clear() };
        var result = await new SchematicBomIdentifiersCheck(cli).RunAsync(fixture.Context with { Database = database });
        Assert.Equal(CheckStatus.Passed, result.Status);
    }

    [Test]
    public static async Task MissingCliOrMainSchematicIsReportedWithoutExport()
    {
        using var fixture = new Fixture();
        var cli = new FakeCli(Header);
        var check = new SchematicBomIdentifiersCheck(cli);
        var noCli = await check.RunAsync(fixture.Context with { KicadCliPath = "" });
        Assert.Equal(CheckStatus.Failed, noCli.Status);
        Assert.True(noCli.Detail.Contains("KiCad CLI"));
        File.Delete(Path.ChangeExtension(fixture.Project, ".kicad_sch"));
        var noSchematic = await check.RunAsync(fixture.Context);
        Assert.Equal(CheckStatus.Failed, noSchematic.Status);
        Assert.True(noSchematic.Detail.Contains("main .kicad_sch"));
        Assert.Equal(0, cli.Calls);
    }

    [Test]
    public static async Task DefaultRunnerIncludesBomCheckAndDoesNotPromptWhenUnavailable()
    {
        using var fixture = new Fixture();
        var context = fixture.Context with { KicadCliPath = "" };
        var original = Snapshot(fixture.Directory);
        var result = await new ConfigurationFixRunner().RunAsync(context, (_, _) => throw new Exception("No fix should be offered."));
        Assert.Equal(8, result.Checks.Entries.Count);
        Assert.Equal(1, result.Checks.Entries.Count(entry => entry.Status == CheckStatus.Failed));
        Assert.Equal(new SchematicBomIdentifiersCheck().Name, result.Checks.Entries.Single(entry => entry.Status == CheckStatus.Failed).Name);
        Assert.False(result.Actions.Any(action => action.Status == FixActionStatus.Fixed));
        Assert.Equal(original, Snapshot(fixture.Directory));
    }

    [Test]
    public static void OverallPassRequiresEveryCheckToPass()
    {
        Assert.True(new CheckReport([new("test", CheckStatus.Passed, "")]).AllPassed);
        Assert.False(new CheckReport([]).AllPassed);
        foreach (var status in new[] { CheckStatus.Failed, CheckStatus.Warning, CheckStatus.Pending })
            Assert.False(new CheckReport([new("first", CheckStatus.Passed, ""), new("last", status, "")]).AllPassed);
    }

    private static string Snapshot(string directory) => string.Join("\n", System.IO.Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))));

    private sealed class FakeCli(string? csv) : ICliCommandRunner
    {
        public int Calls { get; private set; }
        public int ExitCode { get; init; }
        public Action? OnExport { get; init; }
        public string[] Arguments { get; private set; } = [];
        public string Output => Option("--output");
        public string Option(string name) => Arguments[Array.IndexOf(Arguments, name) + 1];
        public async Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            Arguments = arguments.ToArray();
            Assert.True(Arguments.Take(3).SequenceEqual(new[] { "sch", "export", "bom" }));
            if (csv is not null) await File.WriteAllTextAsync(Output, csv, cancellationToken);
            OnExport?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return new(ExitCode, "", ExitCode == 0 ? "" : "Unsupported export option");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("Oeps-BomCheck-Tests-").FullName;
        public string Project => Path.Combine(Directory, "board.kicad_pro");
        public CheckContext Context => new(Path.Combine(Directory, "fake-cli.exe"), Directory, Revision: "B")
            { Database = [new("001", "CAP-1", Description: "Capacitor"), new("002", "RES-2", Description: "Resistor")] };
        public Fixture()
        {
            var project = new JsonObject();
            foreach (var fix in new IConfigurationFix[] { new SymbolFieldsTableFix(), new EditTabMetadataFix(), new ExportConfigurationFix(), new FieldOrderFix(), new RevisionFix("B") })
                fix.Apply(project);
            File.WriteAllText(Project, project.ToJsonString());
            File.WriteAllText(Path.ChangeExtension(Project, ".kicad_sch"), "(kicad_sch (title_block (rev \"B\")))");
            File.WriteAllText(Path.ChangeExtension(Project, ".kicad_pcb"), GerberPlotSettingsTests.GoodBoard);
            File.WriteAllText(Context.KicadCliPath, "fake executable");
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "manufacturing"));
            File.WriteAllText(Path.Combine(Directory, "manufacturing", "existing.csv"), "keep this production file");
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
