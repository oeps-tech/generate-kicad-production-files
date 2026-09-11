using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.CheckProductionFiles;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Data;
using Oeps.KicadProductionFiles.Core.GenerateProductionFiles;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class BomIdentifierChecksTests
{
    private static readonly Component[] Database = [new("0012", "000123", Description: "Part description"), new("0012", "ALTERNATE", Description: "Part description"), new("0099", "OTHER", Description: "Part description")];
    private static ComponentFields Part(string reference, string? pn = "0012", string? mpn = "000123", string alias = "OEPS PN", string? description = "Part description")
    {
        var fields = new Dictionary<string, string>();
        if (pn is not null) fields[alias] = pn;
        if (mpn is not null) fields["MPN"] = mpn;
        if (description is not null) fields["OEPS Description"] = description;
        return new(reference, fields);
    }
    private static BomData Bom(params ComponentFields[] components) => new(["OEPS PN", "MPN", "OEPS Description"], components);
    private static void Failed(CheckResult result, params string[] details)
    {
        Assert.Equal(CheckStatus.Failed, result.Status);
        foreach (var detail in details) Assert.True(result.Detail.Contains(detail, StringComparison.Ordinal), result.Detail);
    }

    [Test]
    public static void RequiredDescriptionColumnAndValuesCannotBeMissingOrInvalid()
    {
        Failed(new BomIdentifiersCheck().Run(new(["OEPS PN", "MPN"], [Part("R1")])), "no included OEPS Description column");
        foreach (var description in new string?[] { null, "", "  " })
            Failed(new BomIdentifiersCheck().Run(Bom(Part("C1", description: description))), "'C1': Missing OEPS Description.");
        foreach (var description in new[] { "${UNKNOWN}", "Part\ndescription", "Part\tdescription" })
            Failed(new BomIdentifiersCheck().Run(Bom(Part("C1", description: description))), "OEPS Description contains control characters or an unresolved text variable");
        Assert.Equal(CheckStatus.Passed, new BomIdentifiersCheck().Run(Bom(Part("C1", description: "  Part description  "))).Status);
    }

    [Test]
    public static void DatabaseDescriptionMustMatchTheSamePnMpnPair()
    {
        var database = new Component[] {
            new("0012", "000123", Description: "Capacitor, 100 nF"),
            new("0012", "ALTERNATE", Description: "Alternative capacitor")
        };
        var good = Bom(Part("C1", description: " Capacitor, 100 nF "));
        Assert.Equal(CheckStatus.Passed, new BomDatabaseIdentifiersCheck().Run(good, database).Status);
        foreach (var description in new[] { "Alternative capacitor", "capacitor, 100 nF", "Capacitor,  100 nF" })
            Failed(new BomDatabaseIdentifiersCheck().Run(Bom(Part("C1", description: description)), database),
                "'C1': OEPS Description", "does not match the database", "Database Description(s): 'Capacitor, 100 nF'");
        Failed(new BomDatabaseIdentifiersCheck().Run(good, [new("0012", "000123")]), "database has no Description");
    }

    [Test]
    public static void MissingBomDescriptionFailsDependentChecksWithoutDuplicatingItsMissingFieldMessage()
    {
        var bom = Bom(Part("C1", description: null));
        Failed(new BomIdentifiersCheck().Run(bom), "'C1': Missing OEPS Description.");
        var database = new BomDatabaseIdentifiersCheck().Run(bom, Database);
        var layout = new BomLayoutIdentifiersCheck().Compare(bom, [Part("C1")]);
        foreach (var result in new[] { database, layout })
        {
            Failed(result, "See " + BomIdentifiersCheck.CheckName);
            Assert.False(result.Detail.Contains("Missing OEPS Description"));
        }
        Assert.False(layout.Detail.Contains("are not consistent"));
        Failed(new BomLayoutIdentifiersCheck().Compare(bom, [Part("C1", description: null)]), "'C1': layout: Missing OEPS Description.");
    }

    [Test]
    public static void LayoutDescriptionIsComparedToBomWithoutConsultingDatabase()
    {
        var bom = Bom(Part("R1", "UNLISTED", "UNLISTED", description: "Custom part"));
        Assert.Equal(CheckStatus.Passed, new BomLayoutIdentifiersCheck().Compare(bom,
            [Part("R1", "UNLISTED", "UNLISTED", description: " Custom part ")]).Status);
        var mismatch = new BomLayoutIdentifiersCheck().Compare(bom, [Part("R1", "UNLISTED", "UNLISTED", description: "Different part")]);
        Failed(mismatch, "BOM and layout are not consistent", "'R1': OEPS Description differs: BOM 'Custom part', layout 'Different part'");
        Assert.False(mismatch.Detail.Contains("schematic", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public static void PcbReaderRequiresActualOepsDescriptionAndRejectsDuplicateOrMalformedValues()
    {
        foreach (var properties in new[] {
            "(property \"Description\" \"Part description\")",
            "(property \"OEPS Description\" 123)",
            "(property \"OEPS Description\" \"Part description\") (property \"OEPS Description\" \"Other\")",
            "(property \"OEPS Description\" \"${UNKNOWN}\")",
            "(property \"OEPS Description\" \"Part\\tdescription\")" })
        {
            var footprints = new PcbPlotSettingsDocument("(kicad_pcb (footprint \"test\" " +
                "(property \"Reference\" \"C1\") (property \"OEPS PN\" \"0012\") (property \"MPN\" \"000123\") " + properties + "))").ReadFootprintFields();
            Failed(new BomLayoutIdentifiersCheck().Compare(Bom(Part("C1")), footprints), "OEPS Description");
        }
        var valid = new PcbPlotSettingsDocument("""
            (kicad_pcb (footprint "test"
              (property "Reference" "C1") (property "OEPS PN" "0012") (property "MPN" "000123")
              (property "OEPS Description" "Part \"quoted\", µF" (hide yes))
              (pad "1" smd rect (property "OEPS Description" "decoy"))))
            """).ReadFootprintFields();
        Assert.Equal(CheckStatus.Passed, new BomLayoutIdentifiersCheck().Compare(Bom(Part("C1", description: "Part \"quoted\", µF")), valid).Status);
    }

    [Test]
    public static async Task GeneratedBomDescriptionFailuresDoNotStopOtherExports()
    {
        using var fixture = new Fixture();
        var before = fixture.InputBytes();
        var cli = new FakeCli { BomDescription = "Wrong description" };
        var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context,
            options: Fixture.BomOnly with { GeneratePlacements = true }, database: Database);
        Assert.True(report.Success, report.Error);
        Assert.Equal(CheckStatus.Passed, report.Checks[0].Status);
        Failed(report.Checks[1], "OEPS Description", "Database Description(s): 'Part description'");
        Failed(report.Checks[2], "OEPS Description differs: BOM 'Wrong description', layout 'Part description'");
        Assert.Equal(BomIdentifiersCheck.CheckName, report.Checks[0].Name);
        Assert.Equal(BomDatabaseIdentifiersCheck.CheckName, report.Checks[1].Name);
        Assert.Equal(BomLayoutIdentifiersCheck.CheckName, report.Checks[2].Name);
        Assert.True(cli.Exported.SequenceEqual(new[] { "bom", "pos" }));
        fixture.AssertUnchanged(before);
    }

    [Test]
    public static void DatabaseAcceptsAlternateMpnsAliasesAndOuterWhitespaceWithoutLosingLeadingZeros()
    {
        var bom = new BomData(["OEPSPN", "MPN", "OEPS Description"], [Part("R1", " 0012 ", " 000123 ", "OEPSPN"), Part("R2", "0012", "ALTERNATE", "OEPSPN")]);
        var result = new BomDatabaseIdentifiersCheck().Run(bom, Database);
        Assert.Equal(CheckStatus.Passed, result.Status);
        Assert.Equal("", result.Detail);
        var fields = new BomIdentifiersCheck().Run(bom);
        Assert.Equal(CheckStatus.Passed, fields.Status);
        Assert.Equal("", fields.Detail);
    }

    [Test]
    public static void DatabaseReportsMissingUnknownAndIncorrectPairsForEveryReference()
    {
        var bom = Bom(Part("R1", null, null), Part("R2", "unknown"), Part("C1", "0012", "OTHER"), Part("U1", "0012", ""));
        Failed(new BomIdentifiersCheck().Run(bom), "'R1': Missing OEPS PN.", "'R1': Missing MPN.", "'U1': Missing MPN.");
        var pairs = new BomDatabaseIdentifiersCheck().Run(bom, Database);
        Failed(pairs, "See BOM required fields: OEPS PN, MPN and OEPS Description",
            "'R2': OEPS PN 'unknown' was not found", "'C1': MPN 'OTHER' does not match OEPS PN '0012'",
            "Database MPN(s): '000123', 'ALTERNATE'");
        Assert.False(pairs.Detail.Contains("Missing MPN"));
    }

    [Test]
    public static void DatabaseUsesExactPartNumbersAndDoesNotCoerceNumbersOrCase()
    {
        foreach (var part in new[] { Part("R1", "12"), Part("R1", "0012", "123"), Part("R1", "0012", "alternate") })
            Assert.Equal(CheckStatus.Failed, new BomDatabaseIdentifiersCheck().Run(Bom(part), Database).Status);
    }

    [Test]
    public static void MissingDatabaseAndMissingIdentifiersHaveSeparateResults()
    {
        var bom = Bom(Part("C1", null, ""));
        Failed(new BomDatabaseIdentifiersCheck().Run(bom, []), "No component database is available", "Click Update database",
            "See BOM required fields: OEPS PN, MPN and OEPS Description");
        Failed(new BomIdentifiersCheck().Run(bom), "'C1': Missing OEPS PN.", "'C1': Missing MPN.");
    }

    [Test]
    public static void ConflictingAliasesMissingColumnsAndDuplicateBomReferencesCannotPass()
    {
        var conflict = new ComponentFields("R1", new Dictionary<string, string> { ["OEPS PN"] = "0012", ["OEPSPN"] = "0099", ["MPN"] = "000123" });
        Failed(new BomIdentifiersCheck().Run(Bom(conflict)), "Conflicting OEPS PN aliases");
        Failed(new BomIdentifiersCheck().Run(new([], [])), "no included OEPS PN / OEPSPN column", "no included MPN column");
        Failed(new BomIdentifiersCheck().Run(Bom(Part("R1"), Part("R1"))), "duplicate reference in the BOM");
        var identical = conflict with { Fields = new Dictionary<string, string> { ["OEPS PN"] = "0012", ["OEPSPN"] = "0012", ["MPN"] = "000123", ["OEPS Description"] = "Part description" } };
        Assert.Equal(CheckStatus.Passed, new BomDatabaseIdentifiersCheck().Run(Bom(identical), Database).Status);
    }

    [Test]
    public static void UnresolvedVariablesAndControlCharactersAreReportedAsInvalidIdentifiers()
    {
        foreach (var mpn in new[] { "${VALUE}", "abc\n[ PASSED ]", "abc\tdef" })
        {
            var part = Part("R1", "0012", mpn);
            Failed(new BomIdentifiersCheck().Run(Bom(part)), "MPN contains control characters or an unresolved text variable");
            Failed(new BomLayoutIdentifiersCheck().Compare(Bom(part), [part]), "MPN contains control characters or an unresolved text variable");
        }
    }

    [Test]
    public static void LayoutComparisonIsIndependentOfDatabaseAndIgnoresFootprintsOutsideBom()
    {
        var bom = Bom(Part("R1", "NOT-IN-DATABASE", "unlisted"));
        var result = new BomLayoutIdentifiersCheck().Compare(bom,
            [Part("R1", " NOT-IN-DATABASE ", "unlisted", "OEPSPN"), Part("H1", null, null)]);
        Assert.Equal(CheckStatus.Passed, result.Status);
        Assert.Equal("", result.Detail);
        Assert.Equal(CheckStatus.Failed, new BomDatabaseIdentifiersCheck().Run(bom, Database).Status);
    }

    [Test]
    public static void LayoutReportsBothIdentifierDifferencesAndMissingAndDuplicateFootprints()
    {
        var bom = Bom(Part("R1"), Part("C1"), Part("U1"), Part("R2"));
        var board = new[] { Part("R1", "0099", "OTHER"), Part("U1"), Part("U1"), Part("R2", null, "") };
        Failed(new BomLayoutIdentifiersCheck().Compare(bom, board), "BOM and layout are not consistent.",
            "'R1': OEPS PN differs: BOM '0012', layout '0099'", "'R1': MPN differs: BOM '000123', layout 'OTHER'",
            "'C1': component in the BOM is missing from the layout", "'U1': duplicate reference in the layout",
            "'R2': layout: Missing OEPS PN.", "'R2': layout: Missing MPN.");
    }

    [Test]
    public static void LayoutDoesNotAcceptTwoMissingValuesOrAmbiguousAliasesAsAMatch()
    {
        var empty = Part("R1", null, null);
        var layout = new BomLayoutIdentifiersCheck().Compare(Bom(empty), [empty]);
        Failed(layout, "layout: Missing OEPS PN", "layout: Missing MPN", "See BOM required fields: OEPS PN, MPN and OEPS Description");
        Assert.False(layout.Detail.Contains("BOM: Missing"));
        var conflicting = new ComponentFields("R1", new Dictionary<string, string> { ["OEPS PN"] = "0012", ["OEPSPN"] = "0099", ["MPN"] = "000123" });
        Failed(new BomLayoutIdentifiersCheck().Compare(Bom(Part("R1")), [conflicting]), "layout: Conflicting OEPS PN aliases");
        Failed(new BomLayoutIdentifiersCheck().Compare(new([], []), []), "See BOM required fields: OEPS PN, MPN and OEPS Description");
    }

    [Test]
    public static void MissingBomMpnIsReportedOnlyByBomFieldsWhileLayoutComparisonRemainsIncomplete()
    {
        var bom = Bom(Part("C1", mpn: ""), Part("C11", mpn: null));
        Failed(new BomIdentifiersCheck().Run(bom), "'C1': Missing MPN.", "'C11': Missing MPN.");
        var layout = new BomLayoutIdentifiersCheck().Compare(bom, [Part("C1"), Part("C11")]);
        Failed(layout, "Some field comparisons could not be completed", "See BOM required fields: OEPS PN, MPN and OEPS Description");
        Assert.False(layout.Detail.Contains("Missing MPN"));
        Assert.False(layout.Detail.Contains("are not consistent"));
    }

    [Test]
    public static void IncompleteBomStillAllowsLayoutPresenceAndAvailableIdentifiersToBeChecked()
    {
        var bom = Bom(Part("C1", mpn: null), Part("C11", mpn: null), Part("R1"));
        var layout = new BomLayoutIdentifiersCheck().Compare(bom, [Part("C1", "0099", ""), Part("C11"), Part("C11")]);
        Failed(layout, "'C1': layout: Missing MPN.", "'C1': OEPS PN differs", "'C11': duplicate reference in the layout",
            "'R1': component in the BOM is missing from the layout", "See BOM required fields: OEPS PN, MPN and OEPS Description");
        Assert.False(layout.Detail.Contains("BOM: Missing"));
    }

    [Test]
    public static void PcbReaderUsesFootprintPropertiesIncludingHiddenValuesAndLegacyReferences()
    {
        var text = """
            (kicad_pcb
              (gr_text "(property \"OEPS PN\" \"decoy\")" (layer "F.SilkS"))
              (footprint "part (library)"
                (property "Reference" "R1" (at 0 0) (hide yes))
                (property "OEPSPN" "0012" (layer "F.Fab") (hide yes))
                (property "MPN" "A\"B\\C-µ" (layer "B.Fab") (hide yes))
                (property "OEPS Description" "Part description" (layer "F.Fab") (hide yes))
                (property ki_fp_filters "R_*")
                (pad "1" smd rect (property "MPN" "decoy")))
              (footprint "legacy"
                (fp_text reference "C1" (at 0 0) (layer "F.SilkS") hide)
                (property "OEPS PN" "0099") (property "MPN" "OTHER") (property "OEPS Description" "Part description")))
            """;
        var fields = new PcbPlotSettingsDocument(text).ReadFootprintFields();
        Assert.Equal(2, fields.Count);
        Assert.Equal("A\"B\\C-µ", fields[0].Fields["MPN"]);
        Assert.Equal("C1", fields[1].Reference);
        var bom = Bom(Part("R1", "0012", "A\"B\\C-µ"), Part("C1", "0099", "OTHER"));
        Assert.Equal(CheckStatus.Passed, new BomLayoutIdentifiersCheck().Compare(bom, fields).Status);
    }

    [Test]
    public static void DuplicateMalformedPropertiesAndDisagreeingReferencesCannotBeSilentlyChosen()
    {
        foreach (var extra in new[] { "(property \"MPN\" \"different\")", "(property \"MPN\" 123)", "(fp_text reference \"C1\")" })
        {
            var fields = new PcbPlotSettingsDocument("(kicad_pcb (footprint \"test\" (property \"Reference\" \"R1\") " +
                "(property \"OEPS PN\" \"0012\") (property \"MPN\" \"000123\") " + extra + "))").ReadFootprintFields();
            Assert.Equal(CheckStatus.Failed, new BomLayoutIdentifiersCheck().Compare(Bom(Part("R1")), fields).Status);
        }
    }

    [Test]
    public static async Task MissingOrMalformedPcbFailsWithoutAFalseConsistencyPass()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Board, "(kicad_pcb");
        Failed(await new BomLayoutIdentifiersCheck().RunAsync(Bom(Part("R1")), fixture.Root), "Could not compare BOM and layout");
        File.Move(fixture.Board, Path.Combine(fixture.Root, "different.kicad_pcb"));
        Failed(await new BomLayoutIdentifiersCheck().RunAsync(Bom(Part("R1")), fixture.Root), "main .kicad_pcb");
    }

    [Test]
    public static async Task GenerationExpandsGroupedBomFieldsByIdentityAndRunsBothChecksReadOnly()
    {
        using var fixture = new Fixture();
        var before = fixture.InputBytes();
        var cli = new FakeCli();
        var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: Fixture.BomOnly, database: Database);
        Assert.True(report.Success, report.Error);
        Assert.Equal(2, report.Files.Single().Bom!.Components.Count);
        Assert.True(report.Files.Single().Bom!.Components.All(part => part.Fields["OEPSPN"] == "0012" && part.Fields["MPN"] == "000123"));
        Assert.Equal(3, report.Checks.Count);
        Assert.Equal("BOM required fields: OEPS PN, MPN and OEPS Description", report.Checks[0].Name);
        Assert.True(report.Checks.All(check => check.Status == CheckStatus.Passed && check.Detail == ""));
        fixture.AssertUnchanged(before);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".oeps-backups")));
    }

    [Test]
    public static async Task FailedIdentifierChecksAreIndependentAndDoNotStopRemainingExportsOrModifyInputs()
    {
        using var fixture = new Fixture();
        var before = fixture.InputBytes();
        var cli = new FakeCli();
        var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context,
            options: Fixture.BomOnly with { GeneratePlacements = true }, database: []);
        Assert.True(report.Success, report.Error);
        Assert.True(report.Comparison!.Matches);
        Assert.Equal(CheckStatus.Passed, report.Checks[0].Status);
        Assert.Equal(CheckStatus.Failed, report.Checks[1].Status);
        Assert.Equal(CheckStatus.Passed, report.Checks[2].Status);
        Assert.True(cli.Exported.SequenceEqual(new[] { "bom", "pos" }));
        fixture.AssertUnchanged(before);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".oeps-backups")));
    }

    [Test]
    public static async Task DatabaseSnapshotIsStableAndCheckResultsSurviveALaterExportFailure()
    {
        using var fixture = new Fixture();
        var database = Database.ToList();
        var cli = new FakeCli { DuringBom = () => database.Clear(), FailPlacement = true };
        var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context,
            options: Fixture.BomOnly with { GeneratePlacements = true }, database: database);
        Assert.False(report.Success);
        Assert.Equal("BOM", report.Files.Single().Name);
        Assert.Equal(3, report.Checks.Count);
        Assert.True(report.Checks.All(check => check.Status == CheckStatus.Passed));
        Assert.Equal(0, database.Count);
    }

    [Test]
    public static async Task IdentifierChecksRespectCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new BomIdentifiersCheck().Run(Bom(Part("R1")), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => new BomDatabaseIdentifiersCheck().Run(Bom(Part("R1")), Database, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => new BomLayoutIdentifiersCheck().RunAsync(Bom(Part("R1")), "unused", cancellation.Token));
    }

    private sealed class FakeCli : ICliCommandRunner
    {
        public List<string> Exported { get; } = [];
        public Action? DuringBom { get; init; }
        public bool FailPlacement { get; init; }
        public string BomDescription { get; init; } = "Part description";
        public async Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken = default)
        {
            if (arguments.Contains("--help")) return new(0, arguments[2] == "bom" ? BomGenerationTests.Help : "--format --units --side --use-drill-file-origin", "");
            var command = arguments[2];
            Exported.Add(command);
            if (command == "bom") DuringBom?.Invoke();
            if (command == "pos" && FailPlacement) return new(1, "", "placement test failure");
            var output = arguments[arguments.ToList().IndexOf("--output") + 1];
            var csv = command == "bom" ? "Designators,Count,Stock number,Manufacturer number,Part details\n\"R1,C1\",2,0012,000123," + BomDescription + "\n"
                : "Ref,Val,Package,PosX,PosY,Rot,Side\nR1,1k,0402,1,2,0,top\nC1,1uF,0402,1,2,0,top\n";
            await File.WriteAllTextAsync(output, csv, cancellationToken);
            return new(0, "", "");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OepsBomIdentifierChecks"));
        internal static readonly ProductionGenerationOptions BomOnly = new(GenerateGerbers: false, GeneratePlacements: false, GenerateDrills: false, GenerateIpcD356: false);
        internal string Root { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        internal string Board => Path.Combine(Root, "main.kicad_pcb");
        internal CheckContext Context => new(Path.Combine(Root, "kicad-cli.exe"), Root);
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            var project = JsonNode.Parse(BomGenerationTests.ProjectJson)!;
            project["schematic"]!["bom_settings"]!["fields_ordered"] = JsonNode.Parse("""
                [{"name":"Reference","label":"Designators","show":true,"group_by":false},
                 {"name":"${QUANTITY}","label":"Count","show":true,"group_by":false},
                 {"name":"OEPSPN","label":"Stock number","show":true,"group_by":true},
                 {"name":"MPN","label":"Manufacturer number","show":true,"group_by":true},
                 {"name":"OEPS Description","label":"Part details","show":true,"group_by":true}]
                """);
            File.WriteAllText(Path.ChangeExtension(Board, ".kicad_pro"), project.ToJsonString());
            File.WriteAllText(Path.ChangeExtension(Board, ".kicad_sch"), "(kicad_sch)");
            File.WriteAllText(Board, "(kicad_pcb " + string.Concat(new[] { "R1", "C1" }.Select(reference =>
                "(footprint \"test\" (property \"Reference\" \"" + reference + "\") (property \"OEPS PN\" \"0012\") (property \"MPN\" \"000123\") (property \"OEPS Description\" \"Part description\"))")) + ")");
            File.WriteAllText(Context.KicadCliPath, "fake CLI");
        }
        internal Dictionary<string, byte[]> InputBytes() => Directory.GetFiles(Root).ToDictionary(file => file, File.ReadAllBytes);
        internal void AssertUnchanged(Dictionary<string, byte[]> before)
        {
            foreach (var (path, content) in before) Assert.True(content.SequenceEqual(File.ReadAllBytes(path)), "Input changed: " + path);
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup must stay in its test directory.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
