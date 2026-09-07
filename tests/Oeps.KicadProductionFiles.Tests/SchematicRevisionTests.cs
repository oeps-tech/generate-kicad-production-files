using System.Text;
using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.ConfigureFiles;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class SchematicRevisionTests
{
    [Test]
    public static void OnlyRootTitleRevisionIsReadAndOnlyItsTokenChanges()
    {
        const string text = """
            (kicad_sch
              (version 20260306)
              (title_block (title "A title with (rev \"X\") and \\ slash")
                (rev "C") (company "Open Ephys") (comment 1 "Keep ) and ("))
              (lib_symbols (symbol "nested" (title_block (rev "D"))))
              (text "(rev \"E\")"))
            """;
        var document = new SchematicRevisionDocument(text);
        Assert.Equal("C", document.Revision);
        var fixedText = document.WithRevision("revB");
        Assert.Equal(text.Replace("(rev \"C\")", "(rev \"B\")"), fixedText);
        Assert.Equal("B", new SchematicRevisionDocument(fixedText).Revision);
        Assert.Equal(fixedText, new SchematicRevisionDocument(fixedText).WithRevision("RevB"));
    }

    [Test]
    public static void EquivalentSchematicRevisionsPassWithoutRewritingText()
    {
        foreach (var saved in new[] { "B", "RevB", "revB", " b " })
        {
            var text = "(kicad_sch (title_block (rev \"" + saved + "\")))";
            var document = new SchematicRevisionDocument(text);
            Assert.Equal(CheckStatus.Passed, RevisionCheck.ValidateSchematic(document, " revB ").Status);
            Assert.Equal(text, document.WithRevision("B"));
        }
    }

    [Test]
    public static void MissingTitleOrRevisionCanBeInsertedWithoutLosingOtherContent()
    {
        foreach (var text in new[]
        {
            "(kicad_sch)", "(kicad_sch (version 20260306) (lib_symbols))",
            "(kicad_sch (title_block (title \"Keep\") (comment 1 \"author\")))",
            "(kicad_sch (title_block))"
        })
        {
            var document = new SchematicRevisionDocument(text);
            Assert.Equal(CheckStatus.Failed, RevisionCheck.ValidateSchematic(document, "B").Status);
            var fixedText = document.WithRevision("RevB");
            Assert.Equal("B", new SchematicRevisionDocument(fixedText).Revision);
            Assert.True(fixedText.Contains("(rev \"B\")"));
            if (text.Contains("Keep")) Assert.True(fixedText.Contains("(title \"Keep\") (comment 1 \"author\")"));
            if (text.Contains("version")) Assert.True(fixedText.IndexOf("(version", StringComparison.Ordinal) < fixedText.IndexOf("(title_block", StringComparison.Ordinal));
        }
    }

    [Test]
    public static void MalformedOrAmbiguousSchematicsAreRejected()
    {
        foreach (var text in new[]
        {
            "", "(other)", "(kicad_sch", "(kicad_sch))", "(kicad_sch) (kicad_sch)",
            "(kicad_sch (title_block (rev \"B)))", "(kicad_sch (title_block (rev B)))",
            "(kicad_sch (title_block (rev \"B\" \"C\")))", "(kicad_sch (title_block (rev)))",
            "(kicad_sch (title_block (rev (value \"B\"))))",
            "(kicad_sch (title_block (rev \"B\") (rev \"B\")))",
            "(kicad_sch (title_block) (title_block))", "(kicad_sch (title_block \"bad\"))"
        }) Assert.Throws<InvalidDataException>(() => new SchematicRevisionDocument(text));
    }

    [Test]
    public static async Task CombinedCheckRequiresBothValuesAndReportsBothMismatches()
    {
        using var folder = new Fixture("A", "C");
        var result = await new RevisionCheck().RunAsync(folder.Context);
        Assert.Equal(CheckStatus.Failed, result.Status);
        Assert.True(result.Detail.Contains(".kicad_pro"));
        Assert.True(result.Detail.Contains(".kicad_sch"));
        Assert.True(result.Detail.Contains("'A'"));
        Assert.True(result.Detail.Contains("'C'"));
        await File.WriteAllTextAsync(folder.Schematic, "(kicad_sch (title_block (rev \"revB\")))");
        result = await new RevisionCheck().RunAsync(folder.Context);
        Assert.Equal(CheckStatus.Failed, result.Status);
        Assert.False(result.Detail.Contains(".kicad_sch"));
    }

    [Test]
    public static async Task OnlySameNamedMainSchematicIsSelected()
    {
        using var folder = new Fixture("B", "B");
        File.Move(folder.Schematic, Path.Combine(folder.DirectoryPath, "other.kicad_sch"));
        var result = await new RevisionCheck().RunAsync(folder.Context);
        Assert.Equal(CheckStatus.Failed, result.Status);
        Assert.True(result.Detail.Contains("same name"));
        var report = await new ConfigurationFixRunner([new RevisionFix("B")]).RunAsync(folder.Context,
            (_, _) => throw new Exception("A missing main schematic must not prompt to modify another file."));
        Assert.Equal(FixActionStatus.Failed, report.Actions.Single().Status);
        Assert.Equal(0, folder.Backups.Length);
    }

    [Test]
    public static async Task OneConfirmationFixesBothFilesWithExactBackups()
    {
        using var folder = new Fixture("A", "C");
        var beforeProject = await File.ReadAllBytesAsync(folder.Project);
        var beforeSchematic = await File.ReadAllBytesAsync(folder.Schematic);
        var prompts = 0;
        var result = await new ConfigurationFixRunner().RunAsync(folder.Context, async (prompt, token) =>
        {
            prompts++;
            Assert.Equal("Revision", prompt.Failure.Name);
            Assert.True(Enumerable.SequenceEqual(beforeProject, await File.ReadAllBytesAsync(folder.Project, token)));
            Assert.True(Enumerable.SequenceEqual(beforeSchematic, await File.ReadAllBytesAsync(folder.Schematic, token)));
            return true;
        });
        Assert.Equal(1, prompts);
        Assert.False(result.Checks.HasFailures);
        Assert.Equal(FixActionStatus.Fixed, result.Actions.Single().Status);
        Assert.Equal(2, folder.Backups.Length);
        Assert.True(Enumerable.SequenceEqual(beforeProject, await File.ReadAllBytesAsync(folder.Backups.Single(path => path.Contains(".kicad_pro.")))));
        Assert.True(Enumerable.SequenceEqual(beforeSchematic, await File.ReadAllBytesAsync(folder.Backups.Single(path => path.Contains(".kicad_sch.")))));
        var afterSchematic = await File.ReadAllBytesAsync(folder.Schematic);
        Assert.True(afterSchematic.SequenceEqual(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(beforeSchematic).Replace("(rev \"C\")", "(rev \"B\")"))));
        await new ConfigurationFixRunner().RunAsync(folder.Context, (_, _) => throw new Exception("Already matching revisions must not prompt."));
        Assert.Equal(2, folder.Backups.Length);
    }

    [Test]
    public static async Task DecliningBothFileFixLeavesBothFilesAndBackupsUntouched()
    {
        using var folder = new Fixture("A", "C");
        var beforeProject = await File.ReadAllBytesAsync(folder.Project);
        var beforeSchematic = await File.ReadAllBytesAsync(folder.Schematic);
        var result = await new ConfigurationFixRunner().RunAsync(folder.Context, (_, _) => Task.FromResult(false));
        Assert.Equal(FixActionStatus.Skipped, result.Actions.Single().Status);
        Assert.True(Enumerable.SequenceEqual(beforeProject, await File.ReadAllBytesAsync(folder.Project)));
        Assert.True(Enumerable.SequenceEqual(beforeSchematic, await File.ReadAllBytesAsync(folder.Schematic)));
        Assert.Equal(0, folder.Backups.Length);
    }

    [Test]
    public static async Task SchematicOnlyMismatchKeepsMatchingProjectBytes()
    {
        using var folder = new Fixture("RevB", "C");
        var before = await File.ReadAllBytesAsync(folder.Project);
        var result = await new ConfigurationFixRunner().RunAsync(folder.Context, (_, _) => Task.FromResult(true));
        Assert.False(result.Checks.HasFailures);
        Assert.True(Enumerable.SequenceEqual(before, await File.ReadAllBytesAsync(folder.Project)));
        Assert.Equal(1, folder.Backups.Length);
        Assert.True(folder.Backups[0].Contains(".kicad_sch."));
    }

    [Test]
    public static async Task AFileEditedDuringConfirmationPreventsBothWrites()
    {
        foreach (var editSchematic in new[] { false, true })
        {
            using var folder = new Fixture("A", "C");
            var beforeProject = await File.ReadAllBytesAsync(folder.Project);
            var beforeSchematic = await File.ReadAllBytesAsync(folder.Schematic);
            var target = editSchematic ? folder.Schematic : folder.Project;
            var external = (await File.ReadAllBytesAsync(target)).Concat(new byte[] { 32 }).ToArray();
            var result = await new ConfigurationFixRunner().RunAsync(folder.Context, async (_, token) =>
            { await File.WriteAllBytesAsync(target, external, token); return true; });
            Assert.Equal(FixActionStatus.Failed, result.Actions.Single().Status);
            Assert.True(result.Actions.Single().Detail.Contains("changed after"));
            Assert.True(Enumerable.SequenceEqual((editSchematic ? beforeProject : external), await File.ReadAllBytesAsync(folder.Project)));
            Assert.True(Enumerable.SequenceEqual((editSchematic ? external : beforeSchematic), await File.ReadAllBytesAsync(folder.Schematic)));
            Assert.Equal(0, folder.Backups.Length);
        }
    }

    [Test]
    public static async Task CancellationAtConfirmationDoesNotSaveEitherFile()
    {
        using var folder = new Fixture("A", "C");
        var beforeProject = await File.ReadAllBytesAsync(folder.Project);
        var beforeSchematic = await File.ReadAllBytesAsync(folder.Schematic);
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ConfigurationFixRunner().RunAsync(folder.Context,
            (_, _) => { cancelled.Cancel(); return Task.FromResult(true); }, cancelled.Token));
        Assert.True(Enumerable.SequenceEqual(beforeProject, await File.ReadAllBytesAsync(folder.Project)));
        Assert.True(Enumerable.SequenceEqual(beforeSchematic, await File.ReadAllBytesAsync(folder.Schematic)));
        Assert.Equal(0, folder.Backups.Length);
    }

    [Test]
    public static async Task MalformedSchematicPreventsSavingAnOtherwiseFixableProject()
    {
        using var folder = new Fixture("A", "C");
        var before = await File.ReadAllBytesAsync(folder.Project);
        await File.WriteAllTextAsync(folder.Schematic, "(kicad_sch (title_block (rev \"unterminated)");
        var result = await new ConfigurationFixRunner().RunAsync(folder.Context, (_, _) => throw new Exception("Invalid schematic must not prompt."));
        Assert.Equal(FixActionStatus.Failed, result.Actions.Single().Status);
        Assert.True(Enumerable.SequenceEqual(before, await File.ReadAllBytesAsync(folder.Project)));
        Assert.Equal(0, folder.Backups.Length);
    }

    [Test]
    public static async Task FailureReplacingSecondFileRestoresFirstFile()
    {
        using var folder = new Fixture("A", "C");
        var beforeProject = await File.ReadAllBytesAsync(folder.Project);
        var beforeSchematic = await File.ReadAllBytesAsync(folder.Schematic);
        File.SetAttributes(folder.Schematic, FileAttributes.ReadOnly);
        try
        {
            var result = await new ConfigurationFixRunner().RunAsync(folder.Context, (_, _) => Task.FromResult(true));
            Assert.Equal(FixActionStatus.Failed, result.Actions.Single().Status);
            Assert.True(Enumerable.SequenceEqual(beforeProject, await File.ReadAllBytesAsync(folder.Project)));
            Assert.True(Enumerable.SequenceEqual(beforeSchematic, await File.ReadAllBytesAsync(folder.Schematic)));
        }
        finally { File.SetAttributes(folder.Schematic, FileAttributes.Normal); }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OepsSchematicRevisionTests"));
        public string DirectoryPath { get; } = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(DirectoryPath, "main.kicad_pro");
        public string Schematic => Path.ChangeExtension(Project, ".kicad_sch");
        public CheckContext Context => new("", DirectoryPath, Revision: "revB");
        public string[] Backups => Directory.Exists(Path.Combine(DirectoryPath, ".oeps-backups"))
            ? Directory.GetFiles(Path.Combine(DirectoryPath, ".oeps-backups")) : [];
        public Fixture(string projectRevision, string schematicRevision)
        {
            Directory.CreateDirectory(DirectoryPath);
            var project = new JsonObject();
            foreach (var fix in new IConfigurationFix[] { new SymbolFieldsTableFix(), new EditTabMetadataFix(), new ExportConfigurationFix(), new FieldOrderFix() })
                fix.Apply(project);
            project["board"] = new JsonObject { ["ipc2581"] = new JsonObject { ["sch_revision"] = projectRevision } };
            File.WriteAllText(Project, project.ToJsonString());
            File.WriteAllText(Path.ChangeExtension(Project, ".kicad_pcb"), GerberPlotSettingsTests.GoodBoard);
            File.WriteAllText(Schematic, "(kicad_sch\r\n\t(title_block\r\n\t\t(title \"Keep\")\r\n\t\t(rev \"" + schematicRevision
                + "\")\r\n\t\t(company \"Open Ephys, Inc\")\r\n\t)\r\n)\r\n", new UTF8Encoding(true));
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(DirectoryPath).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cleanup must stay inside the test directory.");
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
