using System.Text;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.ConfigureFiles;
using Oeps.KicadProductionFiles.Core.Data;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class OepsDescriptionFixTests
{
    [Test]
    public static async Task ApprovedDescriptionFixUpdatesMainAndChildAndRechecks()
    {
        using var fixture = new Fixture();
        var original = fixture.Snapshot();
        var prompts = 0;
        var result = await fixture.Run((prompt, _) =>
        {
            prompts++;
            Assert.True(prompt.Description.Contains("C1") && prompt.Description.Contains("R1"));
            Assert.True(prompt.Description.Contains("Capacitor") && prompt.Description.Contains("Resistor"));
            Assert.Equal(original, fixture.Snapshot());
            return Task.FromResult(true);
        });
        Assert.Equal(1, prompts);
        Assert.Equal(FixActionStatus.Fixed, result.Actions.Single().Status);
        Assert.True(result.Checks.AllPassed, result.Checks.Entries.Single().Detail);
        Assert.True(File.ReadAllText(fixture.Main).Contains("(property \"OEPS Description\" \"Capacitor\""));
        Assert.True(File.ReadAllText(fixture.Child).Contains("(property \"OEPS Description\" \"Resistor\""));
        Assert.Equal(2, Directory.GetFiles(fixture.Directory, "*.bak", SearchOption.AllDirectories).Length);
        Assert.Equal("{}", File.ReadAllText(fixture.Project));
        Assert.Equal("manufacturing stays unchanged", File.ReadAllText(Path.Combine(fixture.Directory, "manufacturing", "keep.csv")));
        await fixture.Run((_, _) => throw new Exception("Passing descriptions should not prompt again."));
        Assert.Equal(2, Directory.GetFiles(fixture.Directory, "*.bak", SearchOption.AllDirectories).Length);
    }

    [Test]
    public static async Task DeclineAndCancellationLeaveAllFilesUntouched()
    {
        using var fixture = new Fixture();
        var original = fixture.Snapshot();
        var declined = await fixture.Run((_, _) => Task.FromResult(false));
        Assert.Equal(FixActionStatus.Skipped, declined.Actions.Single().Status);
        Assert.Equal(original, fixture.Snapshot());
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Run((_, _) =>
        { cancellation.Cancel(); return Task.FromResult(true); }, cancellation.Token));
        Assert.Equal(original, fixture.Snapshot());
        Assert.Equal(0, Directory.GetFiles(fixture.Directory, "*.bak", SearchOption.AllDirectories).Length);
    }

    [Test]
    public static async Task UnmatchedPairIsPreservedWhileEligibleDescriptionIsFixed()
    {
        using var fixture = new Fixture();
        fixture.Database = [new("001", "CAP", Description: "Capacitor")];
        var child = File.ReadAllText(fixture.Child);
        var result = await fixture.Run((prompt, _) =>
        {
            Assert.True(prompt.Description.Contains("R1: OEPS PN/MPN do not match"));
            return Task.FromResult(true);
        });
        Assert.Equal(FixActionStatus.Fixed, result.Actions.Single().Status);
        Assert.False(result.Checks.AllPassed);
        Assert.Equal(child, File.ReadAllText(fixture.Child));
        Assert.True(File.ReadAllText(fixture.Main).Contains("\"OEPS Description\" \"Capacitor\""));
    }

    [Test]
    public static async Task MissingConflictingAndInvalidDatabaseDescriptionsNeverPrompt()
    {
        using var fixture = new Fixture();
        foreach (var database in new Component[][] {
            [], [new("001", "CAP")], [new("001", "CAP", Description: "${UNKNOWN}")],
            [new("001", "CAP", Description: "Capacitor"), new("001", "CAP", Description: "Different")],
            [new("001", "OTHER", Description: "Capacitor")] })
        {
            fixture.Database = database;
            var original = fixture.Snapshot();
            var result = await fixture.Run((_, _) => throw new Exception("No eligible descriptions must not prompt."));
            Assert.Equal(FixActionStatus.Skipped, result.Actions.Single().Status);
            Assert.False(result.Checks.AllPassed);
            Assert.Equal(original, fixture.Snapshot());
        }
    }

    [Test]
    public static async Task EditingAnyInputDuringConfirmationPreventsAllWrites()
    {
        foreach (var target in new[] { "project", "child" })
        {
            using var fixture = new Fixture();
            var main = File.ReadAllBytes(fixture.Main);
            var result = await fixture.Run(async (_, token) =>
            {
                await File.AppendAllTextAsync(target == "project" ? fixture.Project : fixture.Child, "\n ", token);
                return true;
            });
            Assert.Equal(FixActionStatus.Failed, result.Actions.Single().Status);
            Assert.True(result.Actions.Single().Detail.Contains("changed after the fix was prepared"));
            Assert.True(main.SequenceEqual(File.ReadAllBytes(fixture.Main)));
            Assert.Equal(0, Directory.GetFiles(fixture.Directory, "*.bak", SearchOption.AllDirectories).Length);
        }
    }

    [Test]
    public static async Task ExistingDescriptionChangesOnlyItsValueIncludingEscapedText()
    {
        using var fixture = new Fixture();
        var before = File.ReadAllText(fixture.Main).Replace("(property \"MPN\" \"CAP\")", "(property \"MPN\" \"CAP\")\r\n(property \"OEPS Description\" \"Old\" (at 30 40 90) (effects (font (size 2 2))))");
        before = "\uFEFF" + before;
        File.WriteAllText(fixture.Main, before, new UTF8Encoding(false));
        fixture.Database = [new("001", "CAP", Description: "Capacitor \"special\" \\ part"), new("002", "RES", Description: "Resistor")];
        var result = await fixture.Run((_, _) => Task.FromResult(true));
        Assert.Equal(FixActionStatus.Fixed, result.Actions.Single().Status);
        var after = File.ReadAllBytes(fixture.Main);
        Assert.True(after.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal(before.Replace("\"Old\"", "\"Capacitor \\\"special\\\" \\\\ part\""), Encoding.UTF8.GetString(after));
    }

    [Test]
    public static async Task ReusedSheetsAndMultipleUnitsAreUpdatedWithoutDuplicateEdits()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Main, File.ReadAllText(fixture.Main).Replace("(uuid \"root\")", "(uuid \"root\") (sheet (uuid \"s2\") (property \"Sheetfile\" \"child.kicad_sch\"))")
            .Replace("(sheet (uuid \"s1\")", Symbol("main-unit2", "C1", "001", "CAP", "/root", 2) + " (sheet (uuid \"s1\")"));
        File.WriteAllText(fixture.Child, File.ReadAllText(fixture.Child).Replace("(reference \"R1\")))", "(reference \"R1\")) (path \"/root/s2\" (reference \"R2\")))"));
        fixture.ExtraCsv = "R2,002,,RES,\n";
        var result = await fixture.Run((_, _) => Task.FromResult(true));
        Assert.Equal(FixActionStatus.Fixed, result.Actions.Single().Status);
        Assert.Equal(2, File.ReadAllText(fixture.Main).Split("(property \"OEPS Description\"").Length - 1);
        Assert.Equal(1, File.ReadAllText(fixture.Child).Split("(property \"OEPS Description\"").Length - 1);
        Assert.Equal(2, Directory.GetFiles(fixture.Directory, "*.bak", SearchOption.AllDirectories).Length);
    }

    [Test]
    public static async Task DuplicateReferenceUnitOrConflictingSavedAliasesAreNotChanged()
    {
        using var fixture = new Fixture();
        var main = File.ReadAllText(fixture.Main);
        foreach (var changed in new[] {
            main.Replace("(sheet (uuid \"s1\")", Symbol("duplicate", "C1", "001", "CAP", "/root") + " (sheet (uuid \"s1\")"),
            main.Replace("(property \"MPN\" \"CAP\")", "(property \"MPN\" \"CAP\") (property \"OEPSPN\" \"999\")") })
        {
            File.WriteAllText(fixture.Main, changed);
            var result = await fixture.Run((_, _) => Task.FromResult(true));
            Assert.False(result.Checks.AllPassed);
            Assert.Equal(changed, File.ReadAllText(fixture.Main));
        }
    }

    [Test]
    public static async Task UnrelatedFilesLibrarySymbolsAndExcludedComponentsStayUnchanged()
    {
        using var fixture = new Fixture();
        var excluded = Symbol("excluded", "X1", "001", "CAP", "/root").Replace("(in_bom yes)", "(in_bom no)");
        var library = "(lib_symbols (symbol \"library\" (property \"OEPS Description\" \"library only\")))";
        File.WriteAllText(fixture.Main, File.ReadAllText(fixture.Main).Replace("(uuid \"root\")", "(uuid \"root\") " + excluded + library));
        var unrelated = Path.Combine(fixture.Directory, "unrelated.kicad_sch");
        File.WriteAllText(unrelated, "unrelated and deliberately not valid");
        await fixture.Run((_, _) => Task.FromResult(true));
        var main = File.ReadAllText(fixture.Main);
        Assert.True(main.Contains(excluded) && main.Contains(library));
        Assert.Equal("unrelated and deliberately not valid", File.ReadAllText(unrelated));
    }

    [Test]
    public static async Task UnsafeMissingOrCyclicSheetPathsFailBeforeConfirmation()
    {
        using var fixture = new Fixture();
        var main = File.ReadAllText(fixture.Main);
        foreach (var path in new[] { "../outside.kicad_sch", "missing.kicad_sch", "${UNKNOWN}/child.kicad_sch", "board.kicad_sch" })
        {
            File.WriteAllText(fixture.Main, main.Replace("child.kicad_sch", path));
            var original = fixture.Snapshot();
            var result = await fixture.Run((_, _) => throw new Exception("Unsafe hierarchy must not prompt."));
            Assert.Equal(FixActionStatus.Failed, result.Actions.Single().Status);
            Assert.Equal(original, fixture.Snapshot());
        }
    }

    private static string Symbol(string uuid, string reference, string pn, string mpn, string path, int unit = 1) =>
        $"(symbol (uuid \"{uuid}\") (at 10 20 0) (unit {unit}) (in_bom yes) " +
        $"(property \"Reference\" \"{reference}\") (property \"OEPS PN\" \"{pn}\") (property \"MPN\" \"{mpn}\") " +
        $"(instances (project \"board\" (path \"{path}\" (reference \"{reference}\")))))";

    private sealed class Fixture : IDisposable, ICliCommandRunner
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("Oeps-DescriptionFix-").FullName;
        public string Project => Path.Combine(Directory, "board.kicad_pro");
        public string Main => Path.Combine(Directory, "board.kicad_sch");
        public string Child => Path.Combine(Directory, "child.kicad_sch");
        public IReadOnlyList<Component> Database { get; set; } = [new("001", "CAP", Description: "Capacitor"), new("002", "RES", Description: "Resistor")];
        public string ExtraCsv { get; set; } = "";
        public Fixture()
        {
            File.WriteAllText(Project, "{}");
            File.WriteAllText(Main, "(kicad_sch (uuid \"root\") " + Symbol("main-symbol", "C1", "001", "CAP", "/root") + " (sheet (uuid \"s1\") (property \"Sheetfile\" \"child.kicad_sch\")))");
            File.WriteAllText(Child, "(kicad_sch (uuid \"child\") " + Symbol("child-symbol", "R1", "002", "RES", "/root/s1") + ")");
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "manufacturing"));
            File.WriteAllText(Path.Combine(Directory, "manufacturing", "keep.csv"), "manufacturing stays unchanged");
        }
        public Task<ConfigurationFixReport> Run(Func<ConfigurationFixPrompt, CancellationToken, Task<bool>> confirm, CancellationToken token = default) =>
            new ConfigurationFixRunner([new OepsDescriptionFix(this)], new ConfigurationCheckRunner([new SchematicBomIdentifiersCheck(this)]))
                .RunAsync(new(Project, Directory) { Database = Database }, confirm, token);
        public async Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> args, string workingDirectory, CancellationToken token)
        {
            var output = args.ToList().IndexOf("--output") + 1;
            string Description(string file, string expected) => File.ReadAllText(file).Contains("(property \"OEPS Description\" \"" + expected + "\"") ? expected : "";
            await File.WriteAllTextAsync(args[output], "Reference,OEPS PN,OEPSPN,MPN,OEPS Description\n" +
                "C1,001,,CAP," + Description(Main, "Capacitor") + "\nR1,002,,RES," + Description(Child, "Resistor") + "\n" + ExtraCsv, token);
            return new(0, "", "");
        }
        public string Snapshot() => string.Join("\n", System.IO.Directory.GetFiles(Directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Directory, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))));
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
