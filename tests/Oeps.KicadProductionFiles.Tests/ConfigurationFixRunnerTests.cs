using System.Text;
using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.ConfigureFiles;

namespace Oeps.KicadProductionFiles.Tests;

public static class ConfigurationFixRunnerTests
{
    [Test]
    public static async Task ApprovedFixesAreSeparateAndVerifiedWithBackups()
    {
        using var folder = new FixFolder(BrokenProject());
        var prompts = new List<string>();
        var report = await ConfigurationTestChecks.CreateFixRunner().RunAsync(new("", folder.Directory, Revision: "revB"), async (prompt, token) =>
        {
            Assert.Equal(CheckStatus.Failed, prompt.Failure.Status);
            Assert.True(!string.IsNullOrWhiteSpace(prompt.Description));
            Assert.Equal(prompts.Count, folder.Backups.Length);
            if (prompts.Count == 0) Assert.Equal(folder.InitialText, await File.ReadAllTextAsync(folder.File, token));
            prompts.Add(prompt.Failure.Name);
            return true;
        });
        Assert.Equal(4, prompts.Count);
        Assert.Equal(4, prompts.Distinct().Count());
        Assert.Equal(4, report.Actions.Count(action => action.Status == FixActionStatus.Fixed));
        Assert.False(report.Checks.HasFailures);
        Assert.Equal(4, folder.Backups.Length);
        var fixedJson = JsonNode.Parse(await File.ReadAllTextAsync(folder.File))!;
        Assert.True(JsonNode.DeepEquals(BrokenProject()["board"], fixedJson["board"]));
    }

    [Test]
    public static async Task DecliningAllFixesMakesNoChangesOrBackups()
    {
        using var folder = new FixFolder(BrokenProject());
        var count = 0;
        var report = await ConfigurationTestChecks.CreateFixRunner().RunAsync(new("", folder.Directory, Revision: "revB"), (_, _) => { count++; return Task.FromResult(false); });
        // Field order cannot be prepared while MPN is still missing, so it is reported as blocked.
        Assert.Equal(3, count);
        Assert.Equal(3, report.Actions.Count(action => action.Status == FixActionStatus.Skipped));
        Assert.Equal(FixActionStatus.Failed, report.Actions.Single(action => action.CheckName == "Field order").Status);
        Assert.Equal(folder.InitialText, await File.ReadAllTextAsync(folder.File));
        Assert.Equal(0, folder.Backups.Length);
    }

    [Test]
    public static async Task DeclinedFieldFixIsNotAppliedByAnApprovedOrderFix()
    {
        using var folder = new FixFolder(BrokenProject());
        var report = await ConfigurationTestChecks.CreateFixRunner().RunAsync(new("", folder.Directory, Revision: "revB"), (prompt, _) =>
            Task.FromResult(prompt.Failure.Name != "Symbol Fields Table"));
        Assert.Equal(CheckStatus.Failed, report.Checks.Entries.Single(entry => entry.Name == "Symbol Fields Table").Status);
        Assert.Equal(CheckStatus.Passed, report.Checks.Entries.Single(entry => entry.Name == "Edit Tab metadata").Status);
        Assert.Equal(CheckStatus.Passed, report.Checks.Entries.Single(entry => entry.Name == "Export configuration").Status);
        Assert.Equal(CheckStatus.Failed, report.Checks.Entries.Single(entry => entry.Name == "Field order").Status);
        var initial = BrokenProject()["schematic"]!["bom_settings"]!["fields_ordered"];
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(folder.File))!["schematic"]!["bom_settings"]!["fields_ordered"];
        Assert.True(JsonNode.DeepEquals(initial, saved));
    }

    [Test]
    public static async Task AlreadyCorrectChecksAndResolvedFailuresDoNotPrompt()
    {
        using var good = new FixFolder(ConfiguredProject());
        var report = await ConfigurationTestChecks.CreateFixRunner().RunAsync(new("", good.Directory, Revision: "revB"), (_, _) => throw new Exception("Unexpected prompt."));
        Assert.Equal(0, report.Actions.Count);
        Assert.Equal(good.InitialText, await File.ReadAllTextAsync(good.File));
        Assert.Equal(0, good.Backups.Length);

        using var missing = new FixFolder(new JsonObject());
        var prompts = new List<string>();
        report = await ConfigurationTestChecks.CreateFixRunner().RunAsync(new("", missing.Directory, Revision: "revB"), (prompt, _) =>
        { prompts.Add(prompt.Failure.Name); return Task.FromResult(true); });
        Assert.False(report.Checks.HasFailures);
        Assert.False(prompts.Contains("Field order"));
    }

    [Test]
    public static async Task FileChangedWhilePromptIsOpenIsNotOverwritten()
    {
        var project = ConfiguredProject();
        project["schematic"]!["bom_settings"]!["group_symbols"] = false;
        using var folder = new FixFolder(project);
        var external = folder.InitialText + "\n ";
        var report = await ConfigurationTestChecks.CreateFixRunner([new EditTabMetadataFix()]).RunAsync(new("", folder.Directory, Revision: "revB"), async (_, token) =>
        { await File.WriteAllTextAsync(folder.File, external, token); return true; });
        Assert.Equal(FixActionStatus.Failed, report.Actions.Single().Status);
        Assert.True(report.Actions.Single().Detail.Contains("changed after the fix was prepared"));
        Assert.Equal(external, await File.ReadAllTextAsync(folder.File));
        Assert.Equal(0, folder.Backups.Length);
    }

    [Test]
    public static async Task CancellationAfterConfirmationDoesNotWrite()
    {
        using var folder = new FixFolder(BrokenProject());
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ConfigurationTestChecks.CreateFixRunner().RunAsync(new("", folder.Directory, Revision: "revB"), (_, _) =>
        { cancellation.Cancel(); return Task.FromResult(true); }, cancellation.Token));
        Assert.Equal(folder.InitialText, await File.ReadAllTextAsync(folder.File));
        Assert.Equal(0, folder.Backups.Length);
    }

    [Test]
    public static async Task StorePreservesBomNewlinesAndExactBackupAndRejectsAmbiguousJson()
    {
        using var folder = new FixFolder(ConfiguredProject());
        var text = folder.InitialText.ReplaceLineEndings("\r\n") + "\r\n";
        await File.WriteAllTextAsync(folder.File, text, new UTF8Encoding(true));
        var before = await File.ReadAllBytesAsync(folder.File);
        var snapshot = await ProjectConfigurationStore.LoadAsync(folder.Directory);
        var changed = snapshot.CreateDocument();
        changed["schematic"]!["bom_settings"]!["group_symbols"] = false;
        var backup = await ProjectConfigurationStore.SaveAsync(snapshot, changed);
        Assert.True(backup is not null);
        var backupBytes = await File.ReadAllBytesAsync(backup!);
        Assert.True(before.SequenceEqual(backupBytes));
        var after = await File.ReadAllBytesAsync(folder.File);
        Assert.True(after.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.True(Encoding.UTF8.GetString(after).Contains("\r\n"));
        snapshot = await ProjectConfigurationStore.LoadAsync(folder.Directory);
        Assert.Equal<string?>(null, await ProjectConfigurationStore.SaveAsync(snapshot, snapshot.CreateDocument()));
        Assert.Equal(1, folder.Backups.Length);
        foreach (var invalid in new[] { "{", "[]", "{\"schematic\":{},\"schematic\":{}}" })
        {
            await File.WriteAllTextAsync(folder.File, invalid);
            await Assert.ThrowsAsync<InvalidDataException>(() => ProjectConfigurationStore.LoadAsync(folder.Directory));
            Assert.Equal(invalid, await File.ReadAllTextAsync(folder.File));
        }
    }

    [Test]
    public static async Task UnreadableOrAmbiguousProjectNeverPromptsToReplaceIt()
    {
        using var folder = new FixFolder(ConfiguredProject());
        await File.WriteAllTextAsync(Path.Combine(folder.Directory, "another.kicad_pro"), "{}");
        var report = await ConfigurationTestChecks.CreateFixRunner().RunAsync(new("", folder.Directory, Revision: "revB"), (_, _) => throw new Exception("Unexpected prompt."));
        Assert.Equal(6, report.Actions.Count(action => action.Status == FixActionStatus.Failed));
        Assert.Equal(folder.InitialText, await File.ReadAllTextAsync(folder.File));
        Assert.Equal(0, folder.Backups.Length);
    }

    private static JsonObject ConfiguredProject()
    {
        var project = new JsonObject { ["board"] = new JsonObject { ["unrelated"] = "preserve me", ["track_width"] = 0.125 } };
        foreach (var fix in new IConfigurationFix[] { new SymbolFieldsTableFix(), new EditTabMetadataFix(), new ExportConfigurationFix(), new FieldOrderFix(), new RevisionFix("B") })
            fix.Apply(project);
        return project;
    }

    private static JsonObject BrokenProject()
    {
        var project = ConfiguredProject();
        var bom = project["schematic"]!["bom_settings"]!;
        var fields = bom["fields_ordered"]!.AsArray();
        var reversed = fields.Where(field => field!["name"]!.GetValue<string>() != "MPN").Reverse().Select(field => field!.DeepClone()).ToArray();
        bom["fields_ordered"] = new JsonArray(reversed);
        bom["group_symbols"] = false;
        project["schematic"]!["bom_export_filename"] = "wrong.csv";
        return project;
    }

    private sealed class FixFolder : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "OepsConfigureTests", Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Directory, "board.kicad_pro");
        public string InitialText { get; }
        public string[] Backups => System.IO.Directory.Exists(Path.Combine(Directory, ".oeps-backups"))
            ? System.IO.Directory.GetFiles(Path.Combine(Directory, ".oeps-backups")) : [];
        public FixFolder(JsonObject project)
        {
            System.IO.Directory.CreateDirectory(Directory);
            InitialText = project.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(File, InitialText);
            System.IO.File.WriteAllText(Path.ChangeExtension(File, ".kicad_sch"), "(kicad_sch (title_block (rev \"B\")))");
            System.IO.File.WriteAllText(Path.ChangeExtension(File, ".kicad_pcb"), GerberPlotSettingsTests.GoodBoard);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
