using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.ConfigureFiles;

namespace Oeps.KicadProductionFiles.Tests;

public static class RevisionCheckTests
{
    [Test]
    public static void RevisionComparisonAcceptsCaseWhitespaceAndOptionalRevPrefix()
    {
        string[] forms = ["B", "b", "revB", "RevB", "REVB", " B ", " rev b ", "\treVb\r\n"];
        foreach (var expected in forms)
        foreach (var saved in forms)
        {
            var result = Validate(Project(saved), expected);
            Assert.Equal(CheckStatus.Passed, result.Status);
            Assert.Equal(new RevisionCheck().Name, result.Name);
            Assert.Equal("", result.Detail);
        }
    }

    [Test]
    public static void RevisionMismatchIdentifiesTheExpectedAndSavedValues()
    {
        var result = Validate(Project("RevC"), "revB");
        Assert.Equal(CheckStatus.Failed, result.Status);
        Assert.True(result.Detail.Contains("B", StringComparison.Ordinal));
        Assert.True(result.Detail.Contains("C", StringComparison.Ordinal));
        Assert.True(result.Detail.Contains("sch_revision", StringComparison.Ordinal));
        Assert.Equal(CheckStatus.Failed, Validate(Project("revBB"), "revB").Status);
        Assert.Equal(CheckStatus.Failed, Validate(Project("revrevB"), "revB").Status);
    }

    [Test]
    public static void EmptyAndPrefixOnlyExpectedOrSavedRevisionsNeverPass()
    {
        foreach (var invalid in new[] { "", " ", "\t\r\n", "rev", "REV", " Rev \t" })
        {
            Assert.Equal(CheckStatus.Failed, Validate(Project("B"), invalid).Status);
            Assert.Equal(CheckStatus.Failed, Validate(Project(invalid), "B").Status);
            Assert.Equal(CheckStatus.Failed, Validate(Project(invalid), invalid).Status);
        }
    }

    [Test]
    public static void RevisionRequiresTheExactRootBoardIpc2581Path()
    {
        foreach (var json in new[]
        {
            "{}", "{\"board\":{}}", "{\"board\":{\"ipc2581\":{}}}",
            "{\"sch_revision\":\"B\"}",
            "{\"board\":{\"sch_revision\":\"B\"}}",
            "{\"schematic\":{\"board\":{\"ipc2581\":{\"sch_revision\":\"B\"}}}}",
            "{\"Board\":{\"ipc2581\":{\"sch_revision\":\"B\"}}}",
            "{\"board\":{\"IPC2581\":{\"sch_revision\":\"B\"}}}",
            "{\"board\":{\"ipc2581\":{\"SCH_REVISION\":\"B\"}}}"
        })
        {
            var result = Validate(json, "revB");
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(!string.IsNullOrWhiteSpace(result.Detail));
        }
    }

    [Test]
    public static void WrongTypesAtAnyRevisionPathLevelDoNotPass()
    {
        foreach (var wrong in new[] { "null", "[]", "true", "42", "\"B\"" })
        {
            Assert.Equal(CheckStatus.Failed, Validate(wrong, "B").Status);
            Assert.Equal(CheckStatus.Failed, Validate("{\"board\":" + wrong + "}", "B").Status);
            Assert.Equal(CheckStatus.Failed, Validate("{\"board\":{\"ipc2581\":" + wrong + "}}", "B").Status);
        }
        foreach (var wrong in new[] { "null", "[]", "{}", "true", "42" })
            Assert.Equal(CheckStatus.Failed,
                Validate("{\"board\":{\"ipc2581\":{\"sch_revision\":" + wrong + "}}}", "B").Status);
    }

    [Test]
    public static void DuplicateSettingsAlongRevisionPathAreRejectedEvenWhenEqual()
    {
        foreach (var json in new[]
        {
            "{\"board\":{\"ipc2581\":{\"sch_revision\":\"B\"}},\"board\":{\"ipc2581\":{\"sch_revision\":\"B\"}}}",
            "{\"board\":{\"ipc2581\":{\"sch_revision\":\"B\"},\"ipc2581\":{\"sch_revision\":\"B\"}}}",
            "{\"board\":{\"ipc2581\":{\"sch_revision\":\"B\",\"sch_revision\":\"B\"}}}",
            "{\"board\":null,\"board\":{\"ipc2581\":{\"sch_revision\":\"B\"}}}",
            "{\"board\":{\"ipc2581\":{\"sch_revision\":\"C\",\"sch_revision\":\"B\"}}}"
        })
        {
            var result = Validate(json, "B");
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains("duplicat", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Test]
    public static void UnrelatedRevisionKeysAndMalformedSchematicDoNotOverrideRootBoard()
    {
        var json = """
            {"board":{"ipc2581":{"sch_revision":"B","bom_rev":"wrong","schRevision":"wrong"}},
             "schematic":{"board":{"ipc2581":{"sch_revision":"wrong"}},"bom_settings":42},
             "sch_revision":"wrong","unrelated":{"sch_revision":"X","sch_revision":"Y"}}
            """;
        Assert.Equal(CheckStatus.Passed, Validate(json, "revB").Status);
        foreach (var schematic in new[] { "null", "[]", "42", "\"not settings\"" })
            Assert.Equal(CheckStatus.Passed,
                Validate("{\"board\":{\"ipc2581\":{\"sch_revision\":\"B\"}},\"schematic\":" + schematic + "}", "B").Status);
    }

    [Test]
    public static async Task RevisionRunReadsBoardWithoutDependingOnSchematicAndPreservesBytes()
    {
        using var folder = new RevisionFolder();
        const string json = "{\r\n  \"schematic\": [\"malformed settings\"],\r\n  \"board\": {\"ipc2581\": {\"sch_revision\": \"RevB\"}}\r\n}\r\n";
        await File.WriteAllTextAsync(folder.ProjectFile, json, new UTF8Encoding(true));
        var before = await File.ReadAllBytesAsync(folder.ProjectFile);
        var check = new RevisionCheck();
        var good = await check.RunAsync(new("", folder.DirectoryPath, Revision: "revB"));
        Assert.Equal(CheckStatus.Passed, good.Status);
        Assert.Equal("", good.Detail);
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new("", folder.DirectoryPath, Revision: "revC"))).Status);
        var after = await File.ReadAllBytesAsync(folder.ProjectFile);
        Assert.True(before.SequenceEqual(after));
        Assert.Equal(0, folder.Backups.Length);
    }

    [Test]
    public static async Task RevisionRunHandlesMissingAmbiguousMalformedAndCancelledProjects()
    {
        var check = new RevisionCheck();
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new("", "", Revision: "B"))).Status);
        using var folder = new RevisionFolder();
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new("", folder.DirectoryPath, Revision: "B"))).Status);
        await File.WriteAllTextAsync(folder.ProjectFile, "{");
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new("", folder.DirectoryPath, Revision: "B"))).Status);
        await File.WriteAllTextAsync(folder.ProjectFile, Project("B").ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(folder.DirectoryPath, "another.kicad_pro"), Project("B").ToJsonString());
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new("", folder.DirectoryPath, Revision: "B"))).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => check.RunAsync(new("", folder.DirectoryPath, Revision: "B"), cancellation.Token));
    }

    [Test]
    public static void RevisionFixCreatesMissingSettingsAndWritesOnlyNormalizedSuffix()
    {
        foreach (var json in new[] { "{}", "{\"board\":{}}", "{\"board\":{\"ipc2581\":{}}}" })
        foreach (var expected in new[] { "B", "b", "revB", "RevB", " rev b " })
        {
            var project = JsonNode.Parse(json)!.AsObject();
            var fix = new RevisionFix(expected);
            fix.Apply(project);
            Assert.Equal(new RevisionCheck().Name, fix.CheckName);
            Assert.Equal("B", project["board"]!["ipc2581"]!["sch_revision"]!.GetValue<string>());
            Assert.False(project.ContainsKey("schematic"));
            Assert.Equal(CheckStatus.Passed, Validate(project, expected).Status);
            var beforeSecondApply = project.DeepClone();
            fix.Apply(project);
            Assert.True(JsonNode.DeepEquals(beforeSecondApply, project));
        }
    }

    [Test]
    public static void RevisionFixPreservesAllOtherProjectMetadata()
    {
        var project = JsonNode.Parse("""
            {"board":{"ipc2581":{"sch_revision":"C","bom_rev":"keep","extra":[1,2]},
             "design_settings":{"track_width":0.125},"other":"keep"},
             "schematic":{"board":{"ipc2581":{"sch_revision":"leave nested value"}},"bom_settings":["keep"]},
             "meta":{"filename":"example.kicad_pro"},"text_variables":{"REVISION":"leave variable"}}
            """)!.AsObject();
        var expected = project.DeepClone().AsObject();
        expected["board"]!["ipc2581"]!["sch_revision"] = "B";
        new RevisionFix("RevB").Apply(project);
        Assert.True(JsonNode.DeepEquals(expected, project));
    }

    [Test]
    public static void RevisionFixRejectsInvalidExpectedValueBeforeChangingProject()
    {
        foreach (var invalid in new[] { "", " ", "\t\r\n", "rev", " REV " })
        {
            var project = new JsonObject { ["keep"] = new JsonArray(1, 2) };
            var before = project.DeepClone();
            Assert.Throws<InvalidDataException>(() => new RevisionFix(invalid).Apply(project));
            Assert.True(JsonNode.DeepEquals(before, project));
        }
    }

    [Test]
    public static void RevisionFixDoesNotReplaceMalformedContainersOrStructuredRevision()
    {
        foreach (var wrong in new[] { "null", "[]", "42", "true", "\"keep\"" })
        foreach (var json in new[] { "{\"board\":" + wrong + "}", "{\"board\":{\"ipc2581\":" + wrong + "}}" })
        {
            var project = JsonNode.Parse(json)!.AsObject();
            var before = project.DeepClone();
            Assert.Throws<InvalidDataException>(() => new RevisionFix("B").Apply(project));
            Assert.True(JsonNode.DeepEquals(before, project));
        }
        foreach (var wrong in new[] { "{}", "[\"keep\"]" })
        {
            var project = JsonNode.Parse("{\"board\":{\"ipc2581\":{\"sch_revision\":" + wrong + "}}}")!.AsObject();
            var before = project.DeepClone();
            Assert.Throws<InvalidDataException>(() => new RevisionFix("B").Apply(project));
            Assert.True(JsonNode.DeepEquals(before, project));
        }
    }

    [Test]
    public static void RevisionFixCanRepairIncorrectScalarRevisionValues()
    {
        foreach (var wrong in new[] { "null", "42", "true", "\"\"", "\"rev\"" })
        {
            var project = JsonNode.Parse("{\"board\":{\"ipc2581\":{\"sch_revision\":" + wrong + "}}}")!.AsObject();
            new RevisionFix("RevB").Apply(project);
            Assert.Equal("B", project["board"]!["ipc2581"]!["sch_revision"]!.GetValue<string>());
        }
    }

    [Test]
    public static async Task RevisionFixRunnerPromptsBeforeWritingAndHonorsDecline()
    {
        foreach (var approve in new[] { false, true })
        {
            using var folder = new RevisionFolder();
            var project = Project("C");
            foreach (var fix in new IConfigurationFix[] { new SymbolFieldsTableFix(), new EditTabMetadataFix(), new ExportConfigurationFix(), new FieldOrderFix() })
                fix.Apply(project);
            await File.WriteAllTextAsync(folder.ProjectFile, project.ToJsonString());
            var before = await File.ReadAllBytesAsync(folder.ProjectFile);
            var count = 0;
            var report = await new ConfigurationFixRunner([new RevisionFix("revB")]).RunAsync(
                new("", folder.DirectoryPath, Revision: "revB"), async (prompt, cancellationToken) =>
                {
                    count++;
                    Assert.Equal(new RevisionCheck().Name, prompt.Failure.Name);
                    Assert.Equal(CheckStatus.Failed, prompt.Failure.Status);
                    var duringPrompt = await File.ReadAllBytesAsync(folder.ProjectFile, cancellationToken);
                    Assert.True(before.SequenceEqual(duringPrompt));
                    Assert.Equal(0, folder.Backups.Length);
                    return approve;
                });
            Assert.Equal(1, count);
            var action = report.Actions.Single();
            Assert.Equal(approve ? FixActionStatus.Fixed : FixActionStatus.Skipped, action.Status);
            Assert.Equal(approve ? CheckStatus.Passed : CheckStatus.Failed,
                report.Checks.Entries.Single(entry => entry.Name == new RevisionCheck().Name).Status);
            var after = await File.ReadAllBytesAsync(folder.ProjectFile);
            Assert.Equal(approve ? 1 : 0, folder.Backups.Length);
            if (approve)
            {
                var backup = await File.ReadAllBytesAsync(folder.Backups.Single());
                Assert.True(before.SequenceEqual(backup));
                var updated = JsonNode.Parse(after)!.AsObject();
                project["board"]!["ipc2581"]!["sch_revision"] = "B";
                Assert.True(JsonNode.DeepEquals(project, updated));
            }
            else Assert.True(before.SequenceEqual(after));
        }
    }

    private static JsonObject Project(string revision) => new()
    {
        ["board"] = new JsonObject { ["ipc2581"] = new JsonObject { ["sch_revision"] = revision } }
    };

    private static CheckResult Validate(JsonObject project, string expected) => Validate(project.ToJsonString(), expected);

    private static CheckResult Validate(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        return RevisionCheck.Validate(document.RootElement, expected);
    }

    private sealed class RevisionFolder : IDisposable
    {
        private static readonly string TestRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OepsRevisionTests"));
        public string DirectoryPath { get; } = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        public string ProjectFile => Path.Combine(DirectoryPath, "board.kicad_pro");
        public string[] Backups => Directory.Exists(Path.Combine(DirectoryPath, ".oeps-backups"))
            ? Directory.GetFiles(Path.Combine(DirectoryPath, ".oeps-backups")) : [];
        public RevisionFolder()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.ChangeExtension(ProjectFile, ".kicad_sch"), "(kicad_sch (title_block (rev \"B\")))");
            File.WriteAllText(Path.ChangeExtension(ProjectFile, ".kicad_pcb"), GerberPlotSettingsTests.GoodBoard);
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(DirectoryPath);
            if (!resolved.StartsWith(TestRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup path is outside its temporary folder.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
