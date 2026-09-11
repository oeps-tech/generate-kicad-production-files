using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.GenerateProductionFiles;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class BomGenerationTests
{
    internal const string Help = "--output --fields --labels --group-by --sort-field --sort-asc --filter --exclude-dnp " +
        "--field-delimiter --string-delimiter --ref-delimiter --ref-range-delimiter --include-excluded-from-bom Deprecated. Has no effect.";
    internal const string ProjectJson = """
        {"schematic": {
          "bom_export_filename": "manufacturing/bom/${PROJECTNAME}.csv",
          "bom_fmt_settings": {"field_delimiter": ",", "string_delimiter": "\"", "ref_delimiter": ",",
            "ref_range_delimiter": "", "keep_tabs": false, "keep_line_breaks": false},
          "bom_settings": {"group_symbols": true, "exclude_dnp": false, "include_excluded_from_bom": false,
            "sort_asc": true, "sort_field": "Reference", "filter_string": "",
            "fields_ordered": [
              {"name": "Reference", "label": "Refs", "show": true, "group_by": false},
              {"name": "${QUANTITY}", "label": "Qty", "show": true, "group_by": false},
              {"name": "Value", "label": "Value", "show": true, "group_by": true},
              {"name": "MPN", "label": "MPN", "show": false, "group_by": true}]}
        }}
        """;
    private static ProductionGenerationOptions Options(bool placement = true) => new(GenerateGerbers: false,
        GeneratePlacements: placement, GenerateDrills: false, GenerateIpcD356: false);

    [Test]
    public static async Task BomUsesSavedFieldOrderLabelsGroupingAndMainSchematicAndSumsGroupedQuantity()
    {
        using var fixture = new Fixture();
        var cli = new FakeCli();
        var before = File.ReadAllText(fixture.Project);
        var result = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: Options());
        Assert.True(result.Success, result.Error);
        Assert.True(result.Comparison!.Matches);
        Assert.Equal(3, result.Comparison.BomCount); // Two BOM rows, three components.
        var bom = result.Files.First();
        Assert.Equal("BOM", bom.Name);
        Assert.Equal(Path.Combine("manufacturing", "bom", "main board.csv"), bom.RelativePath);
        Assert.True(bom.References!.SequenceEqual(new[] { "R1", "R2", "C1" }));
        var args = cli.Calls.Single(call => call[2] == "bom" && !call.Contains("--help"));
        Assert.Equal(Path.ChangeExtension(fixture.Project, ".kicad_sch"), args[^1]);
        Assert.Equal("Reference,${QUANTITY},Value", Value(args, "--fields"));
        Assert.Equal("Refs,Qty,Value", Value(args, "--labels"));
        Assert.Equal("Value,MPN", Value(args, "--group-by")); // Hidden fields still affect grouping.
        Assert.Equal("Reference", Value(args, "--sort-field"));
        Assert.False(args.Contains("--sort-asc")); // KiCad's ascending default avoids its boolean argument crash.
        Assert.Equal("", Value(args, "--filter"));
        Assert.Equal(",", Value(args, "--field-delimiter"));
        Assert.Equal("\"", Value(args, "--string-delimiter"));
        Assert.Equal(",", Value(args, "--ref-delimiter"));
        Assert.Equal("", Value(args, "--ref-range-delimiter"));
        Assert.False(args.Any(arg => arg is "--exclude-dnp" or "--include-excluded-from-bom" or "--keep-tabs" or "--keep-line-breaks" or "--variant"));
        Assert.Equal(before, File.ReadAllText(fixture.Project));
    }

    [Test]
    public static async Task GroupingOffFilterAndDnpSelectionArePassedExplicitly()
    {
        using var fixture = new Fixture();
        fixture.Edit(bom => { bom["group_symbols"] = false; bom["exclude_dnp"] = true; bom["filter_string"] = "R*"; });
        var cli = new FakeCli();
        var result = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: Options(false));
        Assert.True(result.Success, result.Error);
        var args = cli.Calls.Single(call => !call.Contains("--help"));
        Assert.Equal("", Value(args, "--group-by"));
        Assert.False(args.Contains("--sort-asc"));
        Assert.Equal("R*", Value(args, "--filter"));
        Assert.True(args.Contains("--exclude-dnp"));
    }

    [Test]
    public static async Task DifferentCountsReportBothReferenceDifferencesAndNotificationCounts()
    {
        using var fixture = new Fixture();
        var cli = new FakeCli { PlacementCsv = Positions("R2", "U1") };
        var result = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: Options());
        Assert.True(result.Success, result.Error);
        var comparison = result.Comparison!;
        Assert.False(comparison.CountsMatch);
        Assert.Equal(3, comparison.BomCount);
        Assert.Equal(2, comparison.PlacementCount);
        Assert.True(comparison.OnlyInBom.SequenceEqual(new[] { "C1", "R1" }));
        Assert.True(comparison.OnlyInPlacement.SequenceEqual(new[] { "U1" }));
        Assert.True(comparison.CountMessage.Contains("BOM: 3") && comparison.CountMessage.Contains("Placement files: 2"));
    }

    [Test]
    public static async Task EqualCountsStillDetectDifferentReferencesAndDuplicateOccurrences()
    {
        foreach (var refs in new[] { new[] { "R1", "R2", "U1" }, new[] { "R1", "R2", "R2" } })
        {
            using var fixture = new Fixture();
            var result = await new ProductionGenerationRunner(new FakeCli { PlacementCsv = Positions(refs) })
                .RunAsync(fixture.Context, options: Options());
            Assert.True(result.Success, result.Error);
            Assert.True(result.Comparison!.CountsMatch);
            Assert.False(result.Comparison.Matches);
            Assert.Equal("C1", result.Comparison.OnlyInBom.Single());
            Assert.Equal(refs[^1], result.Comparison.OnlyInPlacement.Single());
        }
    }

    [Test]
    public static async Task UnselectedPlacementDoesNotReadOrCompareAnOldCsv()
    {
        using var fixture = new Fixture();
        var old = fixture.Existing("assembly/main board-pos.csv", "old invalid placement CSV");
        var result = await new ProductionGenerationRunner(new FakeCli()).RunAsync(fixture.Context,
            options: Options(false) with { DeleteExistingFiles = false });
        Assert.True(result.Success, result.Error);
        Assert.Equal("BOM", result.Files.Single().Name);
        Assert.True(result.Comparison is null);
        Assert.Equal("old invalid placement CSV", File.ReadAllText(old));
    }

    [Test]
    public static async Task InvalidBomOutputStopsRemainingExportsAndPreservesExistingOutputWithoutCleanup()
    {
        foreach (var csv in new string?[] { null, "", "wrong,header\n", "Refs,Qty,Value\nR1,2,10k\n",
            "Refs,Qty,Value\nR1,-1,10k\n", "Refs,Qty,Value\nR1,invalid,10k\n", "Refs,Qty,Value\nR1,1\n",
            "Refs,Qty,Value\n\"R1,\",2,10k\n", "Refs,Qty,Value\n\"unclosed" })
        {
            using var fixture = new Fixture();
            var old = fixture.Existing("bom/main board.csv", "previous BOM");
            var cli = new FakeCli { BomCsv = csv };
            var result = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context,
                options: Options() with { DeleteExistingFiles = false });
            Assert.False(result.Success, csv ?? "missing");
            Assert.Equal(0, result.Files.Count);
            Assert.True(result.Comparison is null);
            Assert.Equal("previous BOM", File.ReadAllText(old));
            Assert.True(cli.Calls.Where(call => !call.Contains("--help")).All(call => call[2] == "bom"));
            Assert.Equal(0, Directory.GetDirectories(fixture.Manufacturing, ".oeps-export-*", SearchOption.AllDirectories).Length);
        }
    }

    [Test]
    public static async Task FailedBomCommandCannotPublishPartialOutputOrStartPlacement()
    {
        using var fixture = new Fixture();
        var cli = new FakeCli { BomExitCode = 1 };
        var result = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: Options());
        Assert.False(result.Success);
        Assert.True(result.Error!.Contains("BOM export failed"));
        Assert.Equal(0, result.Files.Count);
        Assert.False(File.Exists(Path.Combine(fixture.Manufacturing, "bom", "main board.csv")));
        Assert.True(cli.Calls.Where(call => !call.Contains("--help")).All(call => call[2] == "bom"));
    }

    [Test]
    public static async Task InvalidSavedSettingsMissingSchematicOrUnsupportedCliFailBeforeCleanup()
    {
        foreach (var issue in new[] { "settings", "schematic", "cli", "excluded", "quantity", "labels", "descending" })
        {
            using var fixture = new Fixture();
            var keep = fixture.Existing("keep.txt", "keep");
            if (issue == "settings") File.WriteAllText(fixture.Project, "{}");
            if (issue == "schematic") File.Move(Path.ChangeExtension(fixture.Project, ".kicad_sch"), Path.Combine(fixture.Root, "other.kicad_sch"));
            if (issue == "excluded") fixture.Edit(bom => bom["include_excluded_from_bom"] = true);
            if (issue == "descending") fixture.Edit(bom => bom["sort_asc"] = false);
            if (issue == "quantity") fixture.Edit(bom => bom["fields_ordered"]![1]!["show"] = false);
            if (issue == "labels") fixture.Edit(bom => bom["fields_ordered"]![0]!["label"] = "Ref,des");
            var cli = new FakeCli { BomHelp = issue == "cli" ? "--output" : Help };
            var result = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: Options());
            Assert.False(result.Success, issue);
            Assert.False(result.ManufacturingCleared);
            Assert.Equal("keep", File.ReadAllText(keep));
            Assert.True(cli.Calls.All(call => call.Contains("--help")));
        }
    }

    [Test]
    public static async Task CsvEscapingAndMultilineValuesDoNotInflatePlacementCounts()
    {
        using var fixture = new Fixture();
        var cli = new FakeCli { BomCsv = "\uFEFFRefs,Qty,Value\r\n\"R1, R2\",2,\"10k, precision \"\"special\"\"\"\r\nC1,1,µF\r\n",
            PlacementCsv = "Ref,Val,Package,PosX,PosY,Rot,Side\r\nR1,\"value\r\nnext line\",0402,1,2,0,top\r\nR2,10k,0402,1,2,0,top\r\nC1,µF,0402,1,2,0,top\r\n" };
        var result = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: Options());
        Assert.True(result.Success, result.Error);
        Assert.True(result.Comparison!.Matches);
        Assert.Equal(3, result.Comparison.PlacementCount);
    }

    [Test]
    public static async Task EmptyBOMAndPlacementExportHaveZeroComponentsAndMatch()
    {
        using var fixture = new Fixture();
        var result = await new ProductionGenerationRunner(new FakeCli { BomCsv = "Refs,Qty,Value\n", PlacementCsv = Positions() })
            .RunAsync(fixture.Context, options: Options());
        Assert.True(result.Success, result.Error);
        Assert.True(result.Comparison!.Matches);
        Assert.Equal(0, result.Comparison.BomCount);
    }

    [Test]
    public static async Task ComparisonIsPreservedWhenALaterExportFails()
    {
        using var fixture = new Fixture();
        var result = await new ProductionGenerationRunner(new FakeCli { PlacementCsv = Positions("U1") })
            .RunAsync(fixture.Context, options: Options() with { GenerateIpcD356 = true });
        Assert.False(result.Success);
        Assert.Equal(2, result.Files.Count);
        Assert.False(result.Comparison!.CountsMatch);
        Assert.Equal(3, result.Comparison.BomCount);
        Assert.Equal(1, result.Comparison.PlacementCount);
    }

    private static string Value(string[] args, string flag) => args[Array.IndexOf(args, flag) + 1];
    private static string Positions(params string[] references) => "Ref,Val,Package,PosX,PosY,Rot,Side\n" +
        string.Concat(references.Select(reference => reference + ",10k,0402,1,2,0,top\n"));

    private sealed class FakeCli : ICliCommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public string BomHelp { get; init; } = Help;
        public int BomExitCode { get; init; }
        public string? BomCsv { get; init; } = "Refs,Qty,Value\n\"R1,R2\",2,10k\nC1,1,1uF\n";
        public string PlacementCsv { get; init; } = Positions("C1", "R2", "R1");
        public async Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToArray();
            Calls.Add(args);
            if (args.Contains("--help")) return new(0, args[2] == "bom" ? BomHelp : "--output --format --units --side --use-drill-file-origin", "");
            if (args[2] == "ipcd356") return new(1, "", "test failure after comparison");
            var csv = args[2] == "bom" ? BomCsv : PlacementCsv;
            if (csv is not null) await File.WriteAllTextAsync(Value(args, "--output"), csv, cancellationToken);
            return new(args[2] == "bom" ? BomExitCode : 0, "", "");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OepsBomGenerationTests"));
        public string Root { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(Root, "main board.kicad_pro");
        public string Manufacturing => Path.Combine(Root, "manufacturing");
        public CheckContext Context => new(Path.Combine(Root, "kicad-cli.exe"), Root);
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Project, ProjectJson);
            File.WriteAllText(Path.ChangeExtension(Project, ".kicad_sch"), "(kicad_sch)");
            File.WriteAllText(Path.ChangeExtension(Project, ".kicad_pcb"), "(kicad_pcb)");
            File.WriteAllText(Context.KicadCliPath, "test CLI");
        }
        public void Edit(Action<JsonNode> edit)
        {
            var root = JsonNode.Parse(File.ReadAllText(Project))!;
            edit(root["schematic"]!["bom_settings"]!);
            File.WriteAllText(Project, root.ToJsonString());
        }
        public string Existing(string relative, string text)
        {
            var path = Path.Combine(Manufacturing, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup must stay in its test directory.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
