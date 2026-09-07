using System.Text.Json;
using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;

namespace Oeps.KicadProductionFiles.Tests;

public static class ExportConfigurationCheckTests
{
    private const string ValidConfiguration = """
        {
          "bom_export_filename":"manufacturing/bom/${PROJECTNAME}.csv",
          "bom_fmt_settings":{
            "field_delimiter":",", "string_delimiter":"\"", "ref_delimiter":",",
            "ref_range_delimiter":"", "keep_tabs":false, "keep_line_breaks":false
          }
        }
        """;

    [Test]
    public static void CorrectExportSettingsPassWithHeaderOnly()
    {
        var result = Validate(ValidConfiguration);
        Assert.Equal(CheckStatus.Passed, result.Status);
        Assert.Equal("Export configuration", result.Name);
        Assert.Equal("", result.Detail);
    }

    [Test]
    public static void OutputFilenameMustKeepExactRelativePathAndProjectVariable()
    {
        foreach (var wrong in new[] { "${PROJECTNAME}.csv", "manufacturing/${PROJECTNAME}.csv",
            "manufacturing/bom/board.csv", "manufacturing/bom/${PROJECTNAME}.csv ", "" })
        {
            var settings = JsonNode.Parse(ValidConfiguration)!;
            settings["bom_export_filename"] = wrong;
            var result = Validate(settings.ToJsonString());
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains("bom_export_filename"));
            Assert.True(result.Detail.Contains("manufacturing/bom/${PROJECTNAME}.csv"));
            Assert.Equal(1, result.Detail.Split('\n').Length);
        }
    }

    [Test]
    public static void WrongDelimitersAndKeepOptionsEachIdentifyTheCorrection()
    {
        foreach (var (key, wrong) in new[]
        {
            ("field_delimiter", "\";\""), ("string_delimiter", "\"'\""),
            ("ref_delimiter", "\";\""), ("ref_range_delimiter", "\"-\""),
            ("ref_range_delimiter", "\" \""), ("keep_tabs", "true"), ("keep_line_breaks", "true")
        })
        {
            var settings = JsonNode.Parse(ValidConfiguration)!;
            settings["bom_fmt_settings"]![key] = JsonNode.Parse(wrong);
            var result = Validate(settings.ToJsonString());
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains(key));
            Assert.True(result.Detail.Contains("incorrect"));
            Assert.Equal(1, result.Detail.Split('\n').Length);
        }
    }

    [Test]
    public static void MissingFieldsAreReportedTogetherIncludingEmptyAndFalseValues()
    {
        var result = Validate("""{"bom_fmt_settings":{}}""");
        Assert.Equal(CheckStatus.Failed, result.Status);
        Assert.Equal(7, result.Detail.Split('\n').Length);
        foreach (var key in new[] { "bom_export_filename", "field_delimiter", "string_delimiter", "ref_delimiter",
            "ref_range_delimiter", "keep_tabs", "keep_line_breaks" })
            Assert.True(result.Detail.Contains(key));
        Assert.True(result.Detail.Split('\n').All(line => line.Contains("missing")));
    }

    [Test]
    public static void MissingMalformedOrDuplicateFormatBlockStillReportsOutputProblems()
    {
        foreach (var malformed in new[] { "{}", """{"bom_fmt_settings":null}""",
            """{"bom_fmt_settings":[]}""", """{"bom_fmt_settings":"CSV"}""",
            """{"bom_fmt_settings":{},"bom_fmt_settings":{}}""" })
        {
            var result = Validate(malformed);
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains("bom_fmt_settings"));
            Assert.True(result.Detail.Contains("bom_export_filename"));
        }
    }

    [Test]
    public static void IncorrectJsonTypesAndDuplicateRulesCannotPass()
    {
        foreach (var key in new[] { "bom_export_filename", "field_delimiter", "string_delimiter", "ref_delimiter",
            "ref_range_delimiter", "keep_tabs", "keep_line_breaks" })
        foreach (var value in new[] { "null", "0", "[]", "{}", "\"false\"" })
        {
            var settings = JsonNode.Parse(ValidConfiguration)!;
            var parent = key == "bom_export_filename" ? settings : settings["bom_fmt_settings"]!;
            parent[key] = JsonNode.Parse(value);
            var result = Validate(settings.ToJsonString());
            Assert.Equal(CheckStatus.Failed, result.Status);
            Assert.True(result.Detail.Contains(key));
        }
        var duplicateField = Validate(ValidConfiguration.Replace("\"keep_tabs\":false", "\"keep_tabs\":true,\"keep_tabs\":false"));
        Assert.Equal(CheckStatus.Failed, duplicateField.Status);
        Assert.True(duplicateField.Detail.Contains("duplicated"));
        var duplicatePath = Validate(ValidConfiguration.TrimEnd('}') + ",\"bom_export_filename\":\"manufacturing/bom/${PROJECTNAME}.csv\"}");
        Assert.Equal(CheckStatus.Failed, duplicatePath.Status);
        Assert.True(duplicatePath.Detail.Contains("duplicated"));
    }

    [Test]
    public static async Task ExportCheckRunsWithoutBomSettingsAndDoesNotModifyTheProject()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OepsExportSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = Path.Combine(directory, "board.kicad_pro");
            await File.WriteAllTextAsync(project, "{\"schematic\":" + ValidConfiguration + "}");
            var before = await File.ReadAllBytesAsync(project);
            var report = await new ConfigurationCheckRunner().RunAsync(new("", directory));
            Assert.Equal(6, report.Entries.Count);
            Assert.Equal(CheckStatus.Failed, report.Entries[0].Status);
            Assert.Equal(CheckStatus.Failed, report.Entries[1].Status);
            Assert.Equal("Export configuration", report.Entries[2].Name);
            Assert.Equal(CheckStatus.Passed, report.Entries[2].Status);
            var after = await File.ReadAllBytesAsync(project);
            Assert.True(before.SequenceEqual(after));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static async Task InvalidInputAndCancellationAreHandled()
    {
        foreach (var malformed in new[] { "null", "[]", "42" })
            Assert.Equal(CheckStatus.Failed, Validate(malformed).Status);
        var check = new ExportConfigurationCheck();
        Assert.Equal(CheckStatus.Failed, (await check.RunAsync(new("", ""))).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => check.RunAsync(new("", ""), cancellation.Token));
    }

    private static CheckResult Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ExportConfigurationCheck.Validate(document.RootElement);
    }
}
