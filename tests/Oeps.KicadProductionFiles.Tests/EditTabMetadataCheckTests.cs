using System.Text.Json;
using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;

namespace Oeps.KicadProductionFiles.Tests;

public static class EditTabMetadataCheckTests
{
    private const string ValidMetadata = """
        {"group_symbols":true,"exclude_dnp":false,"include_excluded_from_bom":false,
         "sort_asc":true,"sort_field":"Reference"}
        """;

    [Test]
    public static void CorrectMetadataPassesUnderItsOwnHeader()
    {
        var result = Validate(ValidMetadata);
        Assert.Equal(CheckStatus.Passed, result.Status);
        Assert.Equal("Edit Tab metadata", result.Name);
        Assert.True(result.Detail.Contains("Everything is good."));
    }

    [Test]
    public static void EveryIncorrectBooleanReportsTheRequiredUiAction()
    {
        foreach (var (key, incorrect, action) in new[]
        {
            ("group_symbols", false, "Select Group symbols"),
            ("exclude_dnp", true, "Select Include 'DNP' Symbols"),
            ("include_excluded_from_bom", true, "Clear Include 'Exclude from BOM' Symbols"),
            ("sort_asc", false, "Select ascending sort order")
        })
        {
            var settings = JsonNode.Parse(ValidMetadata)!.AsObject();
            settings[key] = incorrect;
            var result = Validate(settings.ToJsonString());
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains(key));
            Assert.True(result.Detail.Contains(action));
            Assert.Equal(1, result.Detail.Split('\n').Length);
        }
    }

    [Test]
    public static void SortFieldMustBeExactlyReference()
    {
        foreach (var wrong in new[] { "Value", "reference", "Reference ", "" })
        {
            var settings = JsonNode.Parse(ValidMetadata)!.AsObject();
            settings["sort_field"] = wrong;
            var result = Validate(settings.ToJsonString());
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains("sort_field: \"Reference\""));
        }
    }

    [Test]
    public static void MissingSettingsAreAllReportedEvenWhenExpectedFalse()
    {
        var result = Validate("{}");
        Assert.Equal(CheckStatus.Failed, result.Status);
        Assert.Equal(5, result.Detail.Split('\n').Length);
        foreach (var key in JsonNode.Parse(ValidMetadata)!.AsObject().Select(property => property.Key))
            Assert.True(result.Detail.Contains(key));
        Assert.True(result.Detail.Contains("missing"));
    }

    [Test]
    public static void WrongJsonTypesAndDuplicateKeysDoNotPass()
    {
        foreach (var key in JsonNode.Parse(ValidMetadata)!.AsObject().Select(property => property.Key))
        foreach (var value in new[] { "null", "0", "[]", "{}", key == "sort_field" ? "true" : "\"true\"" })
        {
            var settings = JsonNode.Parse(ValidMetadata)!.AsObject();
            settings[key] = JsonNode.Parse(value);
            var result = Validate(settings.ToJsonString());
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains(key));
        }
        var duplicate = Validate(ValidMetadata.TrimEnd('}') + ",\"exclude_dnp\":false}");
        Assert.Equal(CheckStatus.Failed, duplicate.Status);
        Assert.True(duplicate.Detail.Contains("exclude_dnp"));
        Assert.True(duplicate.Detail.Contains("duplicated"));
    }

    [Test]
    public static void UnrelatedSettingsDoNotAffectMetadataValidation()
    {
        var settings = JsonNode.Parse(ValidMetadata)!.AsObject();
        settings["fields_ordered"] = "invalid column settings belong to the other check";
        settings["filter_string"] = "*";
        Assert.Equal(CheckStatus.Passed, Validate(settings.ToJsonString()).Status);
    }

    [Test]
    public static async Task RunnerReportsMetadataIndependentlyAndPreservesTheProject()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OepsEditTabTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = Path.Combine(directory, "board.kicad_pro");
            var json = "{\"schematic\":{\"bom_settings\":" + ValidMetadata + "}}";
            await File.WriteAllTextAsync(project, json);
            var before = await File.ReadAllBytesAsync(project);
            var report = await new ConfigurationCheckRunner().RunAsync(new("", directory));
            Assert.Equal(6, report.Entries.Count);
            Assert.Equal("Symbol Fields Table", report.Entries[0].Name);
            Assert.Equal(CheckStatus.Failed, report.Entries[0].Status);
            Assert.Equal("Edit Tab metadata", report.Entries[1].Name);
            Assert.Equal(CheckStatus.Passed, report.Entries[1].Status);
            var after = await File.ReadAllBytesAsync(project);
            Assert.True(before.SequenceEqual(after));
            Assert.False(report.Entries[1].Detail.Contains(project));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static async Task MissingProjectsAndCancellationAreHandled()
    {
        var check = new EditTabMetadataCheck();
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new("", ""))).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => check.RunAsync(new("", ""), cancellation.Token));
        foreach (var malformed in new[] { "null", "[]", "true", "42" })
            Assert.Equal(CheckStatus.Failed, Validate(malformed).Status);
    }

    private static CheckResult Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        return EditTabMetadataCheck.Validate(document.RootElement);
    }
}
