using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class SymbolFieldsTableCheckTests
{
    [Test]
    public static void RequiredColumnsPassWithoutOptionalLcsc()
    {
        var result = Validate(ValidFields());
        Assert.Equal(CheckStatus.Passed, result.Status);
    }

    [Test]
    public static void EverySpecifiedPartNumberAndTemperatureAliasIsAccepted()
    {
        foreach (var partNumber in new[] { "OEPS PN", "OEPSPN" })
        foreach (var temperature in new[] { "Temp. Co.", "TempCo", "Temp Co" })
        {
            var fields = ValidFields().Select(field => field.Name switch
            {
                "OEPS PN" => field with { Name = partNumber },
                "Temp. Co." => field with { Name = temperature },
                _ => field
            }).ToArray();
            Assert.Equal(CheckStatus.Passed, Validate(fields).Status);
        }
    }

    [Test]
    public static void CorrectAliasDoesNotHideOtherPresentAliasesNeedingCorrection()
    {
        var fields = ValidFields().Select(field => field.Name switch
        {
            "OEPS PN" => field with { Included = false, Grouped = false },
            "Temp. Co." => field with { Included = false, Grouped = false },
            _ => field
        }).Concat([
            new SymbolTableField("OEPSPN", true, true),
            new SymbolTableField("TempCo", false, true),
            new SymbolTableField("Temp Co", true, true)
        ]).ToArray();
        var result = Validate(fields);
        Assert.Equal(CheckStatus.Failed, result.Status);
        foreach (var name in new[] { "OEPS PN", "Temp. Co.", "TempCo" }) AssertMentions(result, name);
    }

    [Test]
    public static void AllPresentAliasesPassWhenEachIsIncludedAndGrouped()
    {
        var fields = ValidFields().Concat([
            new SymbolTableField("OEPSPN", true, true),
            new SymbolTableField("TempCo", true, true),
            new SymbolTableField("Temp Co", true, true)
        ]).ToArray();
        Assert.Equal(CheckStatus.Passed, Validate(fields).Status);
    }

    [Test]
    public static void IncorrectAliasesReportTheirActualNamesAndCannotCombineFlags()
    {
        var fields = ValidFields().Where(field => field.Name is not "OEPS PN" and not "Temp. Co.")
            .Concat([
                new SymbolTableField("OEPSPN", true, false),
                new SymbolTableField("TempCo", true, false),
                new SymbolTableField("Temp Co", false, true)
            ]).ToArray();
        var result = Validate(fields);
        Assert.Equal(CheckStatus.Failed, result.Status);
        foreach (var name in new[] { "OEPSPN", "TempCo", "Temp Co" }) AssertMentions(result, name);
    }

    [Test]
    public static void MissingColumnsAreReportedTogether()
    {
        var missing = new[] { "Value", "MPN", "OEPS Description", "${DNP}" };
        var result = Validate(ValidFields().Where(field => !missing.Contains(field.Name)).ToArray());
        Assert.Equal(CheckStatus.Failed, result.Status);
        foreach (var name in missing) AssertMentions(result, name);
    }

    [Test]
    public static void EveryRequiredColumnMustBeIncluded()
    {
        foreach (var required in ValidFields())
        {
            var fields = ValidFields().Select(field => field.Name == required.Name
                ? field with { Included = false } : field).ToArray();
            var result = Validate(fields);
            Assert.Equal(CheckStatus.Failed, result.Status);
            AssertMentions(result, required.Name);
        }
    }

    [Test]
    public static void EveryStarredColumnMustBeGrouped()
    {
        foreach (var required in ValidFields().Where(field => field.Grouped))
        {
            var fields = ValidFields().Select(field => field.Name == required.Name
                ? field with { Grouped = false } : field).ToArray();
            var result = Validate(fields);
            Assert.Equal(CheckStatus.Failed, result.Status);
            AssertMentions(result, required.Name);
        }
    }

    [Test]
    public static void UnstarredColumnsMayAlsoBeGroupedAndGlobalGroupingIsNotEnforced()
    {
        foreach (var globalGrouping in new bool?[] { true, false, null })
        {
            Assert.Equal(CheckStatus.Passed, Validate(ValidFields(), globalGrouping).Status);
            Assert.Equal(CheckStatus.Passed, Validate(ValidFields()
                .Select(field => field with { Grouped = true }).ToArray(), globalGrouping).Status);
        }
    }

    [Test]
    public static void OptionalLcscMustBeIncludedAndGroupedWhenPresent()
    {
        Assert.Equal(CheckStatus.Passed, Validate(ValidFields()
            .Append(new("LCSC", true, true)).ToArray()).Status);
        foreach (var flags in new[] { (Included: false, Grouped: false), (Included: true, Grouped: false), (Included: false, Grouped: true) })
        {
            var result = Validate(ValidFields().Append(new("LCSC", flags.Included, flags.Grouped)).ToArray());
            Assert.Equal(CheckStatus.Failed, result.Status);
            AssertMentions(result, "LCSC");
        }
    }

    [Test]
    public static void AdditionalColumnsAndColumnOrderDoNotAffectValidation()
    {
        var fields = ValidFields().Reverse().Concat([
            new SymbolTableField("Custom assembly note", false, false),
            new SymbolTableField("Datasheet", true, false),
            new SymbolTableField("DNP", false, false),
            new SymbolTableField("Item #", false, false)
        ]).ToArray();
        Assert.Equal(CheckStatus.Passed, Validate(fields).Status);
    }

    [Test]
    public static void DisplayLabelsAndPlainDnpCannotReplaceSpecialColumnNames()
    {
        foreach (var replacement in new[]
        {
            (Required: "${DNP}", Wrong: "DNP"),
            (Required: "${ITEM_NUMBER}", Wrong: "Item #"),
            (Required: "${ITEM_NUMBER}", Wrong: "Item Number"),
            (Required: "${ITEM_NUMBER}", Wrong: "${ITEM NUMBER}"),
            (Required: "${QUANTITY}", Wrong: "Qty")
        })
        {
            var fields = ValidFields().Select(field => field.Name == replacement.Required
                ? field with { Name = replacement.Wrong } : field).ToArray();
            var result = Validate(fields);
            Assert.Equal(CheckStatus.Failed, result.Status);
            AssertMentions(result, replacement.Required);
        }
    }

    [Test]
    public static void UnspecifiedCaseAndPunctuationVariantsDoNotSatisfyRequiredColumns()
    {
        foreach (var replacement in new[]
        {
            (Required: "MPN", Wrong: "mpn"),
            (Required: "OEPS PN", Wrong: "OEPS_PN"),
            (Required: "Temp. Co.", Wrong: "Temp.Co.")
        })
        {
            var fields = ValidFields().Select(field => field.Name == replacement.Required
                ? field with { Name = replacement.Wrong } : field).ToArray();
            var result = Validate(fields);
            Assert.Equal(CheckStatus.Failed, result.Status);
            AssertMentions(result, replacement.Required);
        }
    }

    private static CheckResult Validate(IReadOnlyList<SymbolTableField> fields, bool? groupSymbols = true) =>
        SymbolFieldsTableCheck.Validate(new SymbolFieldsTableSettings("board.kicad_pro", fields, groupSymbols));

    private static void AssertMentions(CheckResult result, string name) =>
        Assert.True(result.Detail.Contains(name, StringComparison.Ordinal),
            $"The report must identify '{name}'; actual report: {result.Detail}");

    private static SymbolTableField[] ValidFields() =>
    [
        new("Reference", true, false),
        new("Value", true, true),
        new("Footprint", true, true),
        new("${QUANTITY}", true, false),
        new("${ITEM_NUMBER}", true, false),
        new("MPN", true, true),
        new("OEPS Description", true, true),
        new("OEPS PN", true, true),
        new("Temp. Co.", true, true),
        new("Tolerance", true, true),
        new("Voltage", true, true),
        new("${DNP}", true, true)
    ];
}
