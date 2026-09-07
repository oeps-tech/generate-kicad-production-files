using System.Text.Json;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class FieldOrderCheckTests
{
    [Test]
    public static void CorrectOrderPassesWithHeaderOnlyWithOrWithoutLcsc()
    {
        foreach (var includeLcsc in new[] { true, false })
        {
            var result = Validate(ValidFields(includeLcsc));
            Assert.Equal(CheckStatus.Passed, result.Status);
            Assert.Equal("Field order", result.Name);
            Assert.Equal("", result.Detail);
        }
    }

    [Test]
    public static void EveryAcceptedPartNumberAndTemperatureAliasRetainsItsPosition()
    {
        foreach (var partNumber in new[] { "OEPS PN", "OEPSPN" })
        foreach (var temperature in new[] { "Temp. Co.", "TempCo", "Temp Co" })
        {
            var fields = ValidFields().Select(field => field.Name switch
            {
                "OEPS PN" => field with { Name = partNumber },
                "TempCo" => field with { Name = temperature },
                _ => field
            }).ToArray();
            Assert.Equal(CheckStatus.Passed, Validate(fields).Status);
        }
    }

    [Test]
    public static void SwappedAdjacentColumnsAndFirstAndLastColumnsFail()
    {
        for (var index = 0; index < ValidFields().Length - 1; index++)
        {
            var fields = ValidFields();
            (fields[index], fields[index + 1]) = (fields[index + 1], fields[index]);
            AssertSequenceFailure(Validate(fields));
        }
        var endSwap = ValidFields();
        (endSwap[0], endSwap[^1]) = (endSwap[^1], endSwap[0]);
        AssertSequenceFailure(Validate(endSwap));
    }

    [Test]
    public static void OptionalLcscMustBeBetweenMpnAndDescriptionWhenPresent()
    {
        foreach (var index in new[] { 0, 10, 12 })
        {
            var fields = ValidFields(includeLcsc: false).ToList();
            fields.Insert(index, new("LCSC", true, true));
            var result = Validate(fields);
            AssertSequenceFailure(result);
            Assert.True(result.Detail.Contains("LCSC"));
        }
    }

    [Test]
    public static void PresentButHiddenLcscDoesNotCountAsAnAbsentOptionalField()
    {
        var fields = ValidFields().Select(field => field.Name == "LCSC"
            ? field with { Included = false } : field).ToArray();
        var result = Validate(fields);
        AssertSequenceFailure(result);
        Assert.True(result.Detail.Contains("LCSC"));
    }

    [Test]
    public static void HiddenAdditionalFieldsAreIgnoredButIncludedAdditionalFieldsFail()
    {
        var fields = ValidFields().ToList();
        fields.Insert(0, new("Datasheet", false, false));
        fields.Insert(6, new("Assembly note", false, false));
        fields.Add(new("Description", false, false));
        Assert.Equal(CheckStatus.Passed, Validate(fields).Status);
        foreach (var name in new[] { "Datasheet", "Assembly note", "Description" })
        {
            var withExtraColumn = fields.Select(field => field.Name == name
                ? field with { Included = true } : field).ToArray();
            var result = Validate(withExtraColumn);
            AssertSequenceFailure(result);
            Assert.True(result.Detail.Contains(name));
        }
    }

    [Test]
    public static void MissingOrHiddenRequiredColumnsFailEvenWhenTheRestRemainOrdered()
    {
        foreach (var required in ValidFields(includeLcsc: false))
        {
            AssertSequenceFailure(Validate(ValidFields().Where(field => field.Name != required.Name).ToArray()));
            AssertSequenceFailure(Validate(ValidFields().Select(field => field.Name == required.Name
                ? field with { Included = false } : field).ToArray()));
        }
        AssertSequenceFailure(Validate([]));
    }

    [Test]
    public static void MultipleIncludedAliasesCannotOccupyOneColumn()
    {
        foreach (var (original, alias) in new[]
        {
            ("TempCo", "Temp Co"), ("TempCo", "Temp. Co."), ("OEPS PN", "OEPSPN")
        })
        {
            var fields = ValidFields().ToList();
            var index = fields.FindIndex(field => field.Name == original);
            fields.Insert(index + 1, new(alias, true, true));
            AssertSequenceFailure(Validate(fields));
        }
    }

    [Test]
    public static void LiteralDisplayLabelsDoNotReplaceComputedFields()
    {
        foreach (var (original, replacement) in new[]
        {
            ("${ITEM_NUMBER}", "#"), ("${ITEM_NUMBER}", "${ITEM NUMBER}"),
            ("${QUANTITY}", "Qty"), ("${DNP}", "DNP")
        })
        {
            var fields = ValidFields().Select(field => field.Name == original
                ? field with { Name = replacement } : field).ToArray();
            AssertSequenceFailure(Validate(fields));
        }
    }

    [Test]
    public static void GroupingFlagsDoNotChangeColumnOrderOrMutateTheInput()
    {
        var fields = ValidFields().Select(field => field with { Grouped = false }).ToArray();
        var before = fields.ToArray();
        foreach (var groupSymbols in new bool?[] { true, false, null })
            Assert.Equal(CheckStatus.Passed,
                FieldOrderCheck.Validate(new("board.kicad_pro", fields, groupSymbols)).Status);
        Assert.True(before.SequenceEqual(fields));
    }

    [Test]
    public static async Task SavedFieldOrderAndIncludedFlagsAreReadWithoutChangingTheProject()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OepsFieldOrderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = Path.Combine(directory, "board.kicad_pro");
            var check = new FieldOrderCheck();
            var fields = ValidFields().ToList();
            fields.Insert(3, new("Datasheet", false, false));
            foreach (var hideQuantity in new[] { false, true })
            {
                var json = JsonSerializer.Serialize(new
                {
                    schematic = new
                    {
                        bom_settings = new
                        {
                            fields_ordered = fields.Select(field => new
                            {
                                name = field.Name,
                                label = "Unrelated display label",
                                show = field.Included && !(hideQuantity && field.Name == "${QUANTITY}"),
                                group_by = field.Grouped
                            })
                        }
                    }
                });
                await File.WriteAllTextAsync(project, json);
                var before = await File.ReadAllBytesAsync(project);
                var result = await check.RunAsync(new("", directory));
                Assert.Equal(hideQuantity ? CheckStatus.Failed : CheckStatus.Passed, result.Status);
                var after = await File.ReadAllBytesAsync(project);
                Assert.True(before.SequenceEqual(after));
                Assert.False(result.Detail.Contains(project));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static async Task MissingProjectAndCancellationAreHandled()
    {
        var check = new FieldOrderCheck();
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new("", ""))).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => check.RunAsync(new("", ""), cancellation.Token));
    }

    private static CheckResult Validate(IReadOnlyList<SymbolTableField> fields) =>
        FieldOrderCheck.Validate(new("board.kicad_pro", fields, true));

    private static void AssertSequenceFailure(CheckResult result)
    {
        Assert.Equal(CheckStatus.Failed, result.Status);
        Assert.Equal("Field order", result.Name);
        Assert.True(result.Detail.Contains("Expected"), "The report must show the expected field sequence.");
        Assert.True(result.Detail.Contains("Actual"), "The report must show the actual field sequence.");
    }

    private static SymbolTableField[] ValidFields(bool includeLcsc = true)
    {
        string[] names =
        [
            "${ITEM_NUMBER}", "${QUANTITY}", "Reference", "Value", "Tolerance", "Footprint", "TempCo",
            "Voltage", "${DNP}", "OEPS PN", "MPN", "LCSC", "OEPS Description"
        ];
        return names.Where(name => includeLcsc || name != "LCSC")
            .Select(name => new SymbolTableField(name, true, true)).ToArray();
    }
}
