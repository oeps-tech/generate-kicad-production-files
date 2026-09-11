using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.ConfigureFiles;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class PcbSilkscreenRevisionTests
{
    [Test]
    public static void AcceptsRequestedPrefixesBareValuesAndCaptionsOnEitherSide()
    {
        foreach (var (revision, texts) in new[]
        {
            ("ver1.2", new[] { "1.2", "v1.2", "ver1.2", "V1.2", "Ver1.2", "VER1.2", "version 1.2", "Version 1.2", "version1.2", "Version1.2." }),
            ("RevB", new[] { "B", "rB", "revB", "RB", "RevB", "REVB", "revisionB", "revision B", "Revision B", "RevisionB" })
        })
        foreach (var text in texts)
        foreach (var layer in new[] { "F.SilkS", "B.SilkS" })
        foreach (var caption in new[] { text, "OEPS board - " + text + " (2026)" })
        {
            var result = Check($"(gr_text \"{caption}\" (layer \"{layer}\"))", revision);
            Assert.Equal(CheckStatus.Passed, result.Status);
            Assert.Equal("", result.Detail);
        }
        Assert.Equal(CheckStatus.Passed, Check("(gr_text \"Version1.3.3.\" (layer \"F.SilkS\"))", "v1.3.3").Status);
    }

    [Test]
    public static void RejectsOtherLayersMetadataHiddenTextVariablesAndPartialVersions()
    {
        foreach (var text in new[] { "11.2", "1.20", "1.2.3", "2.1.2", "v11.2", "Version1.2.3", "v1.2b", "part_1.2", "${v1.2}" })
            Assert.Equal(CheckStatus.Failed, Check($"(gr_text \"{text}\" (layer \"F.SilkS\"))", "1.2").Status);
        foreach (var text in new[] { "BB", "revBB", "revision BA", "B1", "USB", "BRB", "${B}" })
            Assert.Equal(CheckStatus.Failed, Check($"(gr_text \"{text}\" (layer \"F.SilkS\"))", "B").Status);
        foreach (var fragment in new[]
        {
            "(gr_text \"RevB\" (layer \"F.Fab\"))", "(gr_text \"RevB\" (layer \"B.Cu\"))",
            "(gr_text \"RevB\")", "(title_block (rev \"B\"))", "(property \"Revision\" \"B\")",
            "(gr_text \"RevC\" (layer \"F.SilkS\")) (gr_text \"RevB\" (layer \"F.Fab\"))",
            "(footprint \"RevB\" (layer \"F.SilkS\"))",
            "(footprint \"X\" (fp_text user \"RevB\" (layer \"F.SilkS\") hide))",
            "(footprint \"X\" (property \"Note\" \"RevB\" (layer \"F.SilkS\") (hide yes)))",
            "(footprint \"X\" (fp_text user \"RevB\" (layer \"F.SilkS\") (effects (font (size 1 1)) hide)))"
        })
        {
            Assert.Equal(CheckStatus.Failed, Check(fragment, "B").Status);
        }
        Assert.Equal(CheckStatus.Failed, Check("(gr_text \"RevB (layer \\\"F.SilkS\\\")\" (layer \"F.Fab\"))", "B").Status);
    }

    [Test]
    public static void ReadsTextBoxesFootprintTextAndVisibleProperties()
    {
        foreach (var fragment in new[]
        {
            "(gr_text_box locked \"OEPS\\nRevision B\" (layer \"F.SilkS\"))",
            "(footprint \"X\" (fp_text user \"RevB\" (layer \"B.SilkS\")))",
            "(footprint \"X\" (fp_text_box locked \"RevB\" (layer \"F.SilkS\")))",
            "(footprint \"X\" (property \"Note\" \"Revision B\" (layer \"F.SilkS\") (hide no)))"
        }) Assert.Equal(CheckStatus.Passed, Check(fragment, "B").Status);
        Assert.Throws<InvalidDataException>(() => Check("(gr_text \"RevB\" (layer \"F.SilkS\")", "B"));
    }

    [Test]
    public static async Task MissingSilkscreenHasNoFixAndDoesNotWriteOrPrompt()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OepsSilkscreenTests"));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = new JsonObject();
            foreach (var fix in new IConfigurationFix[] { new SymbolFieldsTableFix(), new EditTabMetadataFix(), new ExportConfigurationFix(), new FieldOrderFix(), new RevisionFix("B") }) fix.Apply(project);
            await File.WriteAllTextAsync(Path.Combine(directory, "main.kicad_pro"), project.ToJsonString());
            await File.WriteAllTextAsync(Path.Combine(directory, "main.kicad_sch"), "(kicad_sch (title_block (rev \"B\")))");
            var board = Path.Combine(directory, "main.kicad_pcb");
            await File.WriteAllTextAsync(board, GerberPlotSettingsTests.GoodBoard.Replace("RevB", "RevC"));
            var before = await File.ReadAllBytesAsync(board);
            var context = new CheckContext("", directory, Revision: "B");
            var report = await ConfigurationTestChecks.CreateFixRunner().RunAsync(context, (_, _) => throw new Exception("No automatic fix may be offered."));
            Assert.Equal(0, report.Actions.Count);
            var failure = report.Checks.Entries.Single(entry => entry.Status == CheckStatus.Failed);
            Assert.Equal("PCB silkscreen revision", failure.Name);
            Assert.True(failure.Detail.Contains("No automatic fix"));
            var after = await File.ReadAllBytesAsync(board);
            Assert.True(before.SequenceEqual(after));
            Assert.False(Directory.Exists(Path.Combine(directory, ".oeps-backups")));
            project["board"]!["ipc2581"]!["sch_revision"] = "C";
            await File.WriteAllTextAsync(Path.Combine(directory, "main.kicad_pro"), project.ToJsonString());
            var prompts = 0;
            var mixed = await ConfigurationTestChecks.CreateFixRunner().RunAsync(context, (prompt, _) =>
            {
                Assert.Equal("Revision", prompt.Failure.Name);
                prompts++;
                return Task.FromResult(true);
            });
            Assert.Equal(1, prompts);
            Assert.Equal("PCB silkscreen revision", mixed.Checks.Entries.Single(entry => entry.Status == CheckStatus.Failed).Name);
            Assert.Equal(FixActionStatus.Fixed, mixed.Actions.Single().Status);
            var afterMetadataFix = await File.ReadAllBytesAsync(board);
            Assert.True(before.SequenceEqual(afterMetadataFix));
            File.Move(board, Path.Combine(directory, "other.kicad_pcb"));
            Assert.Equal(CheckStatus.Failed, (await new PcbSilkscreenRevisionCheck().RunAsync(context)).Status);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => new PcbSilkscreenRevisionCheck().RunAsync(context, cancelled.Token));
        }
        finally
        {
            if (!Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid test cleanup path.");
            Directory.Delete(directory, true);
        }
    }

    private static CheckResult Check(string fragment, string revision) =>
        PcbSilkscreenRevisionCheck.Validate(new PcbPlotSettingsDocument("(kicad_pcb " + fragment + ")"), revision);
}
