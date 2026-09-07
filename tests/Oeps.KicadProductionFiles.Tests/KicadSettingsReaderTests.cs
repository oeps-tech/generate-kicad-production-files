using System.Text;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class KicadSettingsReaderTests
{
    [Test]
    public static async Task ReadsSavedTableByFieldNameAndPreservesProjectBytes()
    {
        using var folder = new SettingsTestFolder();
        var project = folder.WriteProject("""
            {"schematic":{"bom_settings":{"group_symbols":true,"fields_ordered":[
              {"name":"${QUANTITY}","label":"Qty","show":true,"group_by":false},
              {"name":"OEPS PN","label":"Custom column title","show":true,"group_by":true},
              {"name":"Datasheet","label":"OEPS PN","show":false,"group_by":false}
            ]}}}
            """);
        var before = await File.ReadAllBytesAsync(project);
        var modifiedAt = File.GetLastWriteTimeUtc(project);
        var settings = await SymbolFieldsTableReader.ReadAsync(folder.Path);
        Assert.Equal(project, settings.ProjectFilePath);
        Assert.Equal<bool?>(true, settings.GroupSymbols);
        Assert.Equal(3, settings.Fields.Count);
        Assert.Equal(new SymbolTableField("${QUANTITY}", true, false), settings.Fields[0]);
        Assert.Equal(new SymbolTableField("OEPS PN", true, true), settings.Fields[1]);
        Assert.Equal(new SymbolTableField("Datasheet", false, false), settings.Fields[2]);
        var after = await File.ReadAllBytesAsync(project);
        Assert.True(before.SequenceEqual(after));
        Assert.Equal(modifiedAt, File.GetLastWriteTimeUtc(project));
    }

    [Test]
    public static async Task MissingFlagsAreFalseAndMissingGroupSymbolsIsUnknown()
    {
        using var folder = new SettingsTestFolder();
        folder.WriteProject("""
            {"schematic":{"bom_settings":{"fields_ordered":[
              {"name":"OEPS PN"},
              {"name":"MPN","show":true},
              {"name":"Value","group_by":true}
            ]}}}
            """);
        var settings = await SymbolFieldsTableReader.ReadAsync(folder.Path);
        Assert.Equal<bool?>(null, settings.GroupSymbols);
        Assert.Equal(new SymbolTableField("OEPS PN", false, false), settings.Fields[0]);
        Assert.Equal(new SymbolTableField("MPN", true, false), settings.Fields[1]);
        Assert.Equal(new SymbolTableField("Value", false, true), settings.Fields[2]);
    }

    [Test]
    public static async Task QuotedDirectoryIsAcceptedAndNestedProjectsAreIgnored()
    {
        using var folder = new SettingsTestFolder();
        var project = folder.WriteProject("""{"schematic":{"bom_settings":{"fields_ordered":[]}}}""", "main.KICAD_PRO");
        var nested = System.IO.Path.Combine(folder.Path, "subsheet");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(System.IO.Path.Combine(nested, "child.kicad_pro"), "{}");
        var settings = await SymbolFieldsTableReader.ReadAsync($"  \"{folder.Path}\"  ");
        Assert.Equal(project, settings.ProjectFilePath);
        Assert.Equal(0, settings.Fields.Count);
    }

    [Test]
    public static async Task MissingDirectoriesProjectsAndMultipleProjectsAreExplicitErrors()
    {
        using var folder = new SettingsTestFolder();
        var missingDirectory = await Assert.ThrowsAsync<InvalidDataException>(() => SymbolFieldsTableReader.ReadAsync(System.IO.Path.Combine(folder.Path, "missing")));
        Assert.True(missingDirectory.Message.Contains("folder does not exist", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => SymbolFieldsTableReader.ReadAsync("  \"\" "));
        var nested = System.IO.Path.Combine(folder.Path, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(System.IO.Path.Combine(nested, "child.kicad_pro"), "{}");
        var missingProject = await Assert.ThrowsAsync<InvalidDataException>(() => SymbolFieldsTableReader.ReadAsync(folder.Path));
        Assert.True(missingProject.Message.Contains("No .kicad_pro", StringComparison.Ordinal));
        var project = folder.WriteProject("{}", "first.kicad_pro");
        var selectedFile = await Assert.ThrowsAsync<InvalidDataException>(() => SymbolFieldsTableReader.ReadAsync(project));
        Assert.True(selectedFile.Message.Contains("project folder", StringComparison.Ordinal));
        folder.WriteProject("{}", "second.kicad_pro");
        var multiple = await Assert.ThrowsAsync<InvalidDataException>(() => SymbolFieldsTableReader.ReadAsync(folder.Path));
        Assert.True(multiple.Message.Contains("More than one .kicad_pro", StringComparison.Ordinal));
    }

    [Test]
    public static async Task MalformedOrMissingSavedSettingsNeverProduceAUsableTable()
    {
        using var folder = new SettingsTestFolder();
        foreach (var content in new[]
        {
            "", "{", "[]", "{}", "{\"schematic\":null}", "{\"schematic\":{}}",
            "{\"schematic\":{\"bom_settings\":{}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":null}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":{}}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[{}]}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[null]}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[{\"name\":null}]}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[{\"name\":\" \"}]}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[{\"name\":42}]}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[{\"name\":\"MPN\",\"show\":\"true\"}]}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[{\"name\":\"MPN\",\"group_by\":1}]}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[{\"name\":\"MPN\",\"show\":null}]}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[],\"group_symbols\":\"true\"}}}"
        })
        {
            folder.WriteProject(content);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => SymbolFieldsTableReader.ReadAsync(folder.Path));
            Assert.True(!string.IsNullOrWhiteSpace(error.Message));
        }
    }

    [Test]
    public static async Task DuplicateFieldNamesOrSettingsAreRejectedAsAmbiguous()
    {
        using var folder = new SettingsTestFolder();
        foreach (var content in new[]
        {
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[{\"name\":\"MPN\"},{\"name\":\"MPN\"}]}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[{\"name\":\"MPN\",\"show\":false,\"show\":true}]}}}",
            "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[],\"fields_ordered\":[]}}}"
        })
        {
            folder.WriteProject(content);
            await Assert.ThrowsAsync<InvalidDataException>(() => SymbolFieldsTableReader.ReadAsync(folder.Path));
        }
    }

    [Test]
    public static async Task OversizedProjectsAreRejectedAndCancellationIsPropagated()
    {
        using var folder = new SettingsTestFolder();
        var project = folder.WriteProject("{}");
        using (var stream = new FileStream(project, FileMode.Open, FileAccess.Write)) stream.SetLength(20 * 1024 * 1024 + 1);
        var sizeError = await Assert.ThrowsAsync<InvalidDataException>(() => SymbolFieldsTableReader.ReadAsync(folder.Path));
        Assert.True(sizeError.Message.Contains("20 MiB", StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => SymbolFieldsTableReader.ReadAsync(folder.Path, cancellation.Token));
    }

    [Test]
    public static async Task Utf8BomIsAcceptedWithoutBeingRemovedFromFile()
    {
        using var folder = new SettingsTestFolder();
        var project = folder.WriteProject("{}");
        await File.WriteAllTextAsync(project, "{\"schematic\":{\"bom_settings\":{\"fields_ordered\":[]}}}", new UTF8Encoding(true));
        var before = await File.ReadAllBytesAsync(project);
        var settings = await SymbolFieldsTableReader.ReadAsync(folder.Path);
        Assert.Equal(0, settings.Fields.Count);
        var after = await File.ReadAllBytesAsync(project);
        Assert.True(before.SequenceEqual(after));
    }

    private sealed class SettingsTestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OepsKicadSettingsTests", Guid.NewGuid().ToString("N"));
        public SettingsTestFolder() => Directory.CreateDirectory(Path);
        public string WriteProject(string content, string filename = "board.kicad_pro")
        {
            var file = System.IO.Path.Combine(Path, filename);
            File.WriteAllText(file, content);
            return file;
        }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
