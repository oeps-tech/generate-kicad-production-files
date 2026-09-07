using System.Text;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.ConfigureFiles;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class GerberPlotSettingsTests
{
    public const string GoodBoard = """
        (kicad_pcb
          (version 20260206)
          (setup (aux_axis_origin 10 20)
            (pcbplotparams
              (layerselection 0x00000000_00000000_5555555f_575df5ff)
              (plot_on_all_layers_selection 0x00000000_00000000_00000000_00000000)
              (outputdirectory "manufacturing/gerber/") (outputformat 1) (svgprecision 4)
              (plotframeref no) (subtractmaskfromsilk yes)
              (hidednponfab no) (sketchdnponfab yes) (crossoutdnponfab yes)
              (sketchpadsonfab no) (plotpadnumbers no) (drillshape 0) (scaleselection 1)
              (useauxorigin yes) (mirror no) (psnegative no)
              (usegerberextensions no) (creategerberjobfile no)
              (usegerberattributes yes) (usegerberadvancedattributes yes) (disableapertmacros no)))
          (gr_text "Keep (useauxorigin no), \"quoted\", and \\ escaped" (at 10 20))
          (footprint "nested" (property "plotframeref" "yes") (setup (pcbplotparams (useauxorigin no))))
        )
        """;

    private static readonly string[] RequiredSettings =
    [
        "plotframeref no", "subtractmaskfromsilk yes", "hidednponfab no", "sketchdnponfab yes", "crossoutdnponfab yes",
        "sketchpadsonfab no", "plotpadnumbers no", "drillshape 0", "scaleselection 1", "useauxorigin yes",
        "mirror no", "psnegative no", "usegerberextensions no", "creategerberjobfile no", "usegerberattributes yes",
        "usegerberadvancedattributes yes", "disableapertmacros no", "outputdirectory \"manufacturing/gerber/\""
    ];

    [Test]
    public static void ScreenshotSettingsPassWithHeaderOnlyAndDefaultPrecision()
    {
        var result = Validate(GoodBoard);
        Assert.Equal("Gerber plot settings", result.Name);
        Assert.Equal(CheckStatus.Passed, result.Status);
        Assert.Equal("", result.Detail);
        Assert.Equal(GoodBoard, new GerberPlotSettingsFix().Apply(GoodBoard));
        Assert.Equal(CheckStatus.Passed, Validate(GoodBoard.Replace("(svgprecision 4)", "(svgprecision 4) (gerberprecision 6)")).Status);
    }

    [Test]
    public static void EveryMissingOrWrongRequiredOptionIsReportedAndRepaired()
    {
        foreach (var setting in RequiredSettings)
        foreach (var remove in new[] { false, true })
        {
            var key = setting.Split(' ')[0];
            var changed = GoodBoard.Replace("(" + setting + ")", remove ? "" : "(" + key + " wrong)");
            var result = Validate(changed);
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains(key));
            Assert.Equal(1, result.Detail.Split('\n').Length);
            var fixedText = new GerberPlotSettingsFix().Apply(changed);
            Assert.Equal(CheckStatus.Passed, Validate(fixedText).Status);
            if (!remove) Assert.Equal(GoodBoard, fixedText);
        }
    }

    [Test]
    public static void OutputDirectoryRequiresExactQuotedRelativePathAndFixPreservesOtherText()
    {
        const string correct = "(outputdirectory \"manufacturing/gerber/\")";
        foreach (var replacement in new[]
        {
            "(outputdirectory \"\")", "(outputdirectory \"old exports/gerber/\")",
            "(outputdirectory \"manufacturing/gerber\")", "(outputdirectory \"manufacturing\\\\gerber\\\\\")",
            "(outputdirectory \"C:/manufacturing/gerber/\")", "(outputdirectory manufacturing/gerber/)",
            "(outputdirectory)", "(outputdirectory \"old (folder) with \\\"quotes\\\"/\")"
        })
        {
            var changed = GoodBoard.Replace(correct, replacement);
            var result = Validate(changed);
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains(correct));
            Assert.Equal(GoodBoard, new GerberPlotSettingsFix().Apply(changed));
        }
    }

    [Test]
    public static void DuplicateOrStructuredOutputDirectoriesCannotBeFixedAutomatically()
    {
        const string correct = "(outputdirectory \"manufacturing/gerber/\")";
        foreach (var replacement in new[] { correct + " " + correct, "(outputdirectory (path \"old/\"))", "(outputdirectory \"old/\" \"other/\")" })
        {
            var changed = GoodBoard.Replace(correct, replacement);
            Assert.Equal(CheckStatus.Failed, Validate(changed).Status);
            Assert.Throws<InvalidDataException>(() => new GerberPlotSettingsFix().Apply(changed));
        }
    }

    [Test]
    public static void PrecisionFiveFailsAndIsFixedToSixWithoutConfusingSvgPrecision()
    {
        var changed = GoodBoard.Replace("(svgprecision 4)", "(svgprecision 4) (gerberprecision 5)");
        Assert.Equal(CheckStatus.Failed, Validate(changed).Status);
        var fixedText = new GerberPlotSettingsFix().Apply(changed);
        Assert.Equal(changed.Replace("(gerberprecision 5)", "(gerberprecision 6)"), fixedText);
    }

    [Test]
    public static void MissingContainersCanBeAddedAndAllProblemsAreReported()
    {
        foreach (var text in new[] { "(kicad_pcb)", "(kicad_pcb (setup))", "(kicad_pcb (setup (pcbplotparams)))" })
        {
            Assert.Equal(18, Validate(text).Detail.Split('\n').Length);
            var fixedText = new GerberPlotSettingsFix().Apply(text);
            Assert.Equal(CheckStatus.Passed, Validate(fixedText).Status);
            Assert.False(fixedText.Contains("gerberprecision"));
            Assert.Equal(fixedText, new GerberPlotSettingsFix().Apply(fixedText));
        }
    }

    [Test]
    public static void FixPreservesFormattingBomLayersAndUnrelatedPcbText()
    {
        var expected = "\uFEFF" + GoodBoard.ReplaceLineEndings("\r\n") + "\r\n";
        var changed = expected.Replace("(mirror no)", "(mirror yes)").Replace("(crossoutdnponfab yes)", "(crossoutdnponfab no)");
        Assert.Equal(expected, new GerberPlotSettingsFix().Apply(changed));
        // An application preference or unrelated plot format is outside this screenshot's PCB rule set.
        Assert.Equal(CheckStatus.Passed, Validate(GoodBoard.Replace("(svgprecision 4)", "(svgprecision 3) (check_zones_before_plotting no)")).Status);
    }

    [Test]
    public static void QuotedAndEmptyScalarValuesFailAndCanBeRepaired()
    {
        foreach (var replacement in new[] { "(mirror \"no\")", "(mirror)", "(mirror false)" })
        {
            var changed = GoodBoard.Replace("(mirror no)", replacement);
            Assert.Equal(CheckStatus.Failed, Validate(changed).Status);
            Assert.Equal(CheckStatus.Passed, Validate(new GerberPlotSettingsFix().Apply(changed)).Status);
        }
    }

    [Test]
    public static void DuplicateAndStructuredSettingsAreNotGuessedAt()
    {
        foreach (var replacement in new[] { "(mirror no) (mirror yes)", "(mirror no yes)", "(mirror (value no))" })
        {
            var changed = GoodBoard.Replace("(mirror no)", replacement);
            Assert.Equal(CheckStatus.Failed, Validate(changed).Status);
            Assert.Throws<InvalidDataException>(() => new GerberPlotSettingsFix().Apply(changed));
        }
        foreach (var text in new[]
        {
            "(kicad_pcb (setup) (setup))", "(kicad_pcb (setup (pcbplotparams) (pcbplotparams)))",
            "(kicad_pcb (setup no))", "(kicad_pcb (setup (pcbplotparams no)))", "(kicad_pcb", "(kicad_pcb))",
            "(kicad_pcb) (kicad_pcb)", "(kicad_pcb (gr_text \"bad))", "(kicad_sch)"
        }) Assert.Throws<InvalidDataException>(() => new GerberPlotSettingsFix().Apply(text));
    }

    [Test]
    public static async Task CheckReadsMatchingBoardWithoutChangingBytesOrDependingOnProjectJson()
    {
        using var folder = new Fixture();
        await File.WriteAllTextAsync(folder.Project, "{broken json");
        var before = await File.ReadAllBytesAsync(folder.Board);
        Assert.Equal(CheckStatus.Passed, (await new GerberPlotSettingsCheck().RunAsync(folder.Context)).Status);
        var after = await File.ReadAllBytesAsync(folder.Board);
        Assert.True(before.SequenceEqual(after));
        File.Move(folder.Board, Path.Combine(folder.DirectoryPath, "other.kicad_pcb"));
        var result = await new GerberPlotSettingsCheck().RunAsync(folder.Context);
        Assert.Equal(CheckStatus.Failed, result.Status);
        Assert.True(result.Detail.Contains("same name"));
    }

    [Test]
    public static async Task ConfirmationControlsBoardEditAndBackupAndLeavesProjectUntouched()
    {
        foreach (var approve in new[] { false, true })
        {
            using var folder = new Fixture();
            var wrong = GoodBoard.Replace("(useauxorigin yes)", "(useauxorigin no)");
            await File.WriteAllTextAsync(folder.Board, wrong, new UTF8Encoding(true));
            var before = await File.ReadAllBytesAsync(folder.Board);
            var project = await File.ReadAllBytesAsync(folder.Project);
            var confirmations = 0;
            var report = await new ConfigurationFixRunner([new GerberPlotSettingsFix()]).RunAsync(folder.Context, async (prompt, token) =>
            {
                confirmations++;
                Assert.Equal("Gerber plot settings", prompt.Failure.Name);
                var during = await File.ReadAllBytesAsync(folder.Board, token);
                Assert.True(before.SequenceEqual(during));
                Assert.Equal(0, folder.Backups.Length);
                return approve;
            });
            Assert.Equal(1, confirmations);
            Assert.Equal(approve ? FixActionStatus.Fixed : FixActionStatus.Skipped, report.Actions.Single().Status);
            Assert.Equal(approve ? CheckStatus.Passed : CheckStatus.Failed, report.Checks.Entries.Single(entry => entry.Name == "Gerber plot settings").Status);
            var after = await File.ReadAllBytesAsync(folder.Board);
            var projectAfter = await File.ReadAllBytesAsync(folder.Project);
            Assert.True(project.SequenceEqual(projectAfter));
            Assert.Equal(approve ? 1 : 0, folder.Backups.Length);
            if (approve)
            {
                Assert.True(Encoding.UTF8.GetBytes("\uFEFF" + GoodBoard).SequenceEqual(after));
                var backup = await File.ReadAllBytesAsync(folder.Backups.Single());
                Assert.True(before.SequenceEqual(backup));
                await new ConfigurationFixRunner([new GerberPlotSettingsFix()]).RunAsync(folder.Context, (_, _) => throw new Exception("Passing check must not prompt."));
            }
            else Assert.True(before.SequenceEqual(after));
        }
    }

    [Test]
    public static async Task ExternalEditsDuringConfirmationAreNotOverwritten()
    {
        using var folder = new Fixture();
        await File.WriteAllTextAsync(folder.Board, GoodBoard.Replace("(mirror no)", "(mirror yes)"));
        var external = await File.ReadAllTextAsync(folder.Board) + "\n ";
        var result = await new ConfigurationFixRunner([new GerberPlotSettingsFix()]).RunAsync(folder.Context, async (_, token) =>
        { await File.WriteAllTextAsync(folder.Board, external, token); return true; });
        Assert.Equal(FixActionStatus.Failed, result.Actions.Single().Status);
        Assert.True(result.Actions.Single().Detail.Contains("changed after"));
        Assert.Equal(external, await File.ReadAllTextAsync(folder.Board));
        Assert.Equal(0, folder.Backups.Length);
    }

    [Test]
    public static async Task CancellationAndUnreadableBoardsNeverWrite()
    {
        using var folder = new Fixture();
        await File.WriteAllTextAsync(folder.Board, GoodBoard.Replace("(mirror no)", "(mirror yes)"));
        var before = await File.ReadAllBytesAsync(folder.Board);
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ConfigurationFixRunner([new GerberPlotSettingsFix()]).RunAsync(folder.Context,
            (_, _) => { cancelled.Cancel(); return Task.FromResult(true); }, cancelled.Token));
        var after = await File.ReadAllBytesAsync(folder.Board);
        Assert.True(before.SequenceEqual(after));
        await File.WriteAllTextAsync(folder.Board, "(kicad_pcb");
        var result = await new ConfigurationFixRunner([new GerberPlotSettingsFix()]).RunAsync(folder.Context, (_, _) => throw new Exception("Malformed board must not prompt."));
        Assert.Equal(FixActionStatus.Failed, result.Actions.Single().Status);
        Assert.Equal(0, folder.Backups.Length);
    }

    private static CheckResult Validate(string text) => GerberPlotSettingsCheck.Validate(new PcbPlotSettingsDocument(text));

    private sealed class Fixture : IDisposable
    {
        private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OepsGerberSettingsTests"));
        public string DirectoryPath { get; } = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(DirectoryPath, "main.kicad_pro");
        public string Board => Path.ChangeExtension(Project, ".kicad_pcb");
        public CheckContext Context => new("", DirectoryPath, Revision: "B");
        public string[] Backups => Directory.Exists(Path.Combine(DirectoryPath, ".oeps-backups"))
            ? Directory.GetFiles(Path.Combine(DirectoryPath, ".oeps-backups")) : [];
        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Project, "{}");
            File.WriteAllText(Board, GoodBoard, new UTF8Encoding(true));
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(DirectoryPath).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cleanup must stay inside the test directory.");
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
