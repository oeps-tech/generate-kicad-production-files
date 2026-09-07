using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.ConfigureFiles;

namespace Oeps.KicadProductionFiles.Tests;

public static class ConfigurationFixTests
{
    private static readonly string[] ExpectedNames =
    [
        "${ITEM_NUMBER}", "${QUANTITY}", "Reference", "Value", "Tolerance", "Footprint", "TempCo",
        "Voltage", "${DNP}", "OEPS PN", "MPN", "LCSC", "OEPS Description"
    ];

    [Test]
    public static async Task MissingSettingsBlocksCanBeCreatedByTheirOwnFixes()
    {
        foreach (var (fix, check) in FixesAndChecks())
        {
            if (fix is FieldOrderFix) continue;
            var project = new JsonObject { ["board"] = new JsonObject { ["keep"] = "unchanged" } };
            fix.Apply(project);
            Assert.Equal(check.Name, fix.CheckName);
            Assert.Equal(CheckStatus.Passed, (await CheckAsync(project, check)).Status);
            Assert.Equal("unchanged", project["board"]!["keep"]!.GetValue<string>());
        }
    }

    [Test]
    public static async Task SymbolFixAddsRequiredFieldsWithLabelsAndDoesNotInventLcsc()
    {
        var project = new JsonObject();
        new SymbolFieldsTableFix().Apply(project);
        var fields = Fields(project);
        Assert.Equal(12, fields.Count);
        Assert.False(fields.Any(field => Name(field!) == "LCSC"));
        foreach (var (name, label) in new[] { ("${ITEM_NUMBER}", "#"), ("${QUANTITY}", "Qty"), ("${DNP}", "DNP") })
            Assert.Equal(label, Field(project, name)["label"]!.GetValue<string>());
        Assert.False(Bom(project).ContainsKey("group_symbols"));
        Assert.Equal(CheckStatus.Passed, (await CheckAsync(project, new SymbolFieldsTableCheck())).Status);
    }

    [Test]
    public static async Task SymbolFixPreservesAliasesLabelsOrderAndUnrelatedSettings()
    {
        foreach (var partNumber in new[] { "OEPS PN", "OEPSPN" })
        foreach (var temperature in new[] { "Temp. Co.", "TempCo", "Temp Co" })
        foreach (var includeLcsc in new[] { true, false })
        {
            var project = Project(includeLcsc);
            Field(project, "OEPS PN")["name"] = partNumber;
            Field(project, "TempCo")["name"] = temperature;
            var fields = Fields(project);
            var reversed = fields.Reverse().ToArray();
            fields.Clear();
            foreach (var field in reversed)
            {
                fields.Add(field);
                field!["show"] = false;
                field["group_by"] = false;
            }
            Field(project, "Reference")["group_by"] = true;
            fields.Insert(1, new JsonObject { ["name"] = "Datasheet", ["show"] = false, ["custom"] = new JsonArray(1, 2) });
            Bom(project)["sort_field"] = "Value";
            var originalOrder = fields.Select(field => Name(field!)).ToArray();
            var originalLabels = fields.Select(field => field!["label"]?.ToJsonString()).ToArray();
            var extraBefore = fields[1]!.DeepClone();
            var unrelatedBefore = WithoutSymbolFlags(project);

            new SymbolFieldsTableFix().Apply(project);

            Assert.Equal(CheckStatus.Passed, (await CheckAsync(project, new SymbolFieldsTableCheck())).Status);
            Assert.True(originalOrder.SequenceEqual(fields.Select(field => Name(field!))));
            Assert.True(originalLabels.SequenceEqual(fields.Select(field => field!["label"]?.ToJsonString())));
            Assert.True(JsonNode.DeepEquals(extraBefore, fields[1]));
            Assert.True(JsonNode.DeepEquals(unrelatedBefore, WithoutSymbolFlags(project)));
            Assert.True(Field(project, "Reference")["group_by"]!.GetValue<bool>());
            Assert.Equal(CheckStatus.Failed, (await CheckAsync(project, new EditTabMetadataCheck())).Status);
            Assert.Equal(CheckStatus.Failed, (await CheckAsync(project, new FieldOrderCheck())).Status);
        }
    }

    [Test]
    public static async Task SymbolFixRepairsEveryMissingFieldAndRequiredFlag()
    {
        foreach (var name in ExpectedNames.Where(name => name != "LCSC"))
        {
            var project = Project();
            Fields(project).Remove(Field(project, name));
            new SymbolFieldsTableFix().Apply(project);
            Assert.Equal(CheckStatus.Passed, (await CheckAsync(project, new SymbolFieldsTableCheck())).Status);
        }
        foreach (var wrong in new JsonNode?[] { null, JsonValue.Create(false), JsonValue.Create("false"), JsonValue.Create(0) })
        {
            var project = Project();
            foreach (var field in Fields(project).OfType<JsonObject>())
            {
                field["show"] = wrong?.DeepClone();
                if (Name(field) is not ("${ITEM_NUMBER}" or "${QUANTITY}" or "Reference"))
                    field["group_by"] = wrong?.DeepClone();
            }
            new SymbolFieldsTableFix().Apply(project);
            Assert.Equal(CheckStatus.Passed, (await CheckAsync(project, new SymbolFieldsTableCheck())).Status);
        }
    }

    [Test]
    public static async Task MetadataFixRepairsFiveOptionsWithoutChangingOtherSettings()
    {
        string[] keys = ["group_symbols", "exclude_dnp", "include_excluded_from_bom", "sort_asc", "sort_field"];
        foreach (var missing in new[] { true, false })
        {
            var project = Project();
            foreach (var key in keys)
            {
                if (missing) Bom(project).Remove(key);
                else Bom(project)[key] = "incorrect";
            }
            var before = WithoutKeys(project, ["schematic", "bom_settings"], keys);
            new EditTabMetadataFix().Apply(project);
            Assert.Equal(CheckStatus.Passed, (await CheckAsync(project, new EditTabMetadataCheck())).Status);
            Assert.True(JsonNode.DeepEquals(before, WithoutKeys(project, ["schematic", "bom_settings"], keys)));
        }
    }

    [Test]
    public static async Task ExportFixRepairsSevenOptionsWithoutChangingOtherSettings()
    {
        string[] keys = ["field_delimiter", "string_delimiter", "ref_delimiter", "ref_range_delimiter", "keep_tabs", "keep_line_breaks"];
        foreach (var missing in new[] { true, false })
        {
            var project = Project();
            var schematic = project["schematic"]!.AsObject();
            var format = schematic["bom_fmt_settings"]!.AsObject();
            if (missing) schematic.Remove("bom_export_filename");
            else schematic["bom_export_filename"] = "wrong.csv";
            foreach (var key in keys)
            {
                if (missing) format.Remove(key);
                else format[key] = 23;
            }
            var before = WithoutKeys(project, ["schematic", "bom_fmt_settings"], keys);
            before["schematic"]!.AsObject().Remove("bom_export_filename");
            new ExportConfigurationFix().Apply(project);
            Assert.Equal(CheckStatus.Passed, (await CheckAsync(project, new ExportConfigurationCheck())).Status);
            var after = WithoutKeys(project, ["schematic", "bom_fmt_settings"], keys);
            after["schematic"]!.AsObject().Remove("bom_export_filename");
            Assert.True(JsonNode.DeepEquals(before, after));
        }
    }

    [Test]
    public static async Task OrderFixReusesExistingFieldsWithAliasesAndOptionalLcsc()
    {
        foreach (var includeLcsc in new[] { true, false })
        {
            var project = Project(includeLcsc);
            Field(project, "OEPS PN")["name"] = "OEPSPN";
            Field(project, "TempCo")["name"] = "Temp. Co.";
            var fields = Fields(project);
            var original = fields.Reverse().ToArray();
            fields.Clear();
            foreach (var field in original) fields.Add(field);
            fields.Insert(0, new JsonObject { ["name"] = "Datasheet", ["show"] = false, ["custom"] = "keep" });
            fields.Insert(3, new JsonObject { ["name"] = "Note", ["group_by"] = true, ["custom"] = new JsonArray(3, 4) });
            var objects = fields.ToDictionary(field => Name(field!), field => field!);
            var before = objects.ToDictionary(item => item.Key, item => item.Value.DeepClone());
            var unrelated = WithoutKeys(project, ["schematic", "bom_settings"], ["fields_ordered"]);

            new FieldOrderFix().Apply(project);

            Assert.Equal(CheckStatus.Passed, (await CheckAsync(project, new FieldOrderCheck())).Status);
            Assert.Equal(objects.Count, fields.Count);
            foreach (var field in fields)
            {
                Assert.True(ReferenceEquals(objects[Name(field!)], field));
                Assert.True(JsonNode.DeepEquals(before[Name(field!)], field));
            }
            Assert.Equal("Datasheet", Name(fields[^2]!));
            Assert.Equal("Note", Name(fields[^1]!));
            Assert.True(JsonNode.DeepEquals(unrelated, WithoutKeys(project, ["schematic", "bom_settings"], ["fields_ordered"])));
        }
    }

    [Test]
    public static async Task OrderFixExcludesExtraColumnsWhilePreservingTheirData()
    {
        var project = Project();
        var extra = new JsonObject
        {
            ["name"] = "Assembly note", ["label"] = "Special label", ["show"] = true,
            ["group_by"] = true, ["custom"] = new JsonObject { ["saved"] = 123 }
        };
        Fields(project).Insert(4, extra);
        var before = extra.DeepClone().AsObject();
        before["show"] = false;
        var fix = new FieldOrderFix();
        Assert.True(fix.Description.Contains("Clear Included for additional columns"));
        fix.Apply(project);
        Assert.True(JsonNode.DeepEquals(before, extra));
        Assert.Equal(CheckStatus.Passed, (await CheckAsync(project, new FieldOrderCheck())).Status);
        Assert.Equal(CheckStatus.Passed, (await CheckAsync(project, new SymbolFieldsTableCheck())).Status);
    }

    [Test]
    public static void OrderFixDoesNotApplyAnUnapprovedMissingOrHiddenFieldRepair()
    {
        foreach (var name in ExpectedNames)
        foreach (var hide in new[] { true, false })
        {
            if (name == "LCSC" && !hide) continue;
            var project = Project();
            if (hide) Field(project, name)["show"] = false;
            else Fields(project).Remove(Field(project, name));
            var before = project.DeepClone();
            var error = Assert.Throws<InvalidDataException>(() => new FieldOrderFix().Apply(project));
            Assert.True(error.Message.Contains("Symbol Fields Table fix"));
            Assert.True(JsonNode.DeepEquals(before, project));
        }
        var empty = new JsonObject();
        Assert.Throws<InvalidDataException>(() => new FieldOrderFix().Apply(empty));
        Assert.Equal(0, empty.Count);
    }

    [Test]
    public static void OrderFixReportsAmbiguousAliasesWithoutChangingTheProject()
    {
        foreach (var name in new[] { "OEPSPN", "Temp Co", "Temp. Co." })
        foreach (var included in new[] { true, false })
        {
            var project = Project();
            Fields(project).Add(new JsonObject { ["name"] = name, ["show"] = included, ["group_by"] = true });
            var before = project.DeepClone();
            var error = Assert.Throws<InvalidDataException>(() => new FieldOrderFix().Apply(project));
            Assert.True(error.Message.Contains("Choose the intended alias"));
            Assert.True(JsonNode.DeepEquals(before, project));
        }
    }

    [Test]
    public static void SymbolFixIncludesEveryPresentAliasWithoutDeletingColumns()
    {
        var project = Project();
        Fields(project).Add(new JsonObject { ["name"] = "OEPSPN", ["label"] = "Part number", ["show"] = false });
        new SymbolFieldsTableFix().Apply(project);
        Assert.True(Field(project, "OEPS PN")["show"]!.GetValue<bool>());
        Assert.True(Field(project, "OEPSPN")["show"]!.GetValue<bool>());
        Assert.Equal("Part number", Field(project, "OEPSPN")["label"]!.GetValue<string>());
        Assert.Equal(14, Fields(project).Count);
    }

    [Test]
    public static void UnexpectedSettingsStructuresAreNotReplaced()
    {
        foreach (var (fix, _) in FixesAndChecks())
        foreach (var malformed in new[] { "null", "[]", "23", "\"incorrect\"" })
        {
            var project = Project();
            project["schematic"] = JsonNode.Parse(malformed);
            var before = project.DeepClone();
            Assert.Throws<InvalidDataException>(() => fix.Apply(project));
            Assert.True(JsonNode.DeepEquals(before, project));
        }
        foreach (var fix in new IConfigurationFix[] { new SymbolFieldsTableFix(), new FieldOrderFix(), new EditTabMetadataFix() })
        {
            var project = Project();
            project["schematic"]!["bom_settings"] = new JsonArray("keep existing");
            var before = project.DeepClone();
            Assert.Throws<InvalidDataException>(() => fix.Apply(project));
            Assert.True(JsonNode.DeepEquals(before, project));
        }
        var exportProject = Project();
        exportProject["schematic"]!["bom_fmt_settings"] = new JsonArray("keep existing");
        var exportBefore = exportProject.DeepClone();
        Assert.Throws<InvalidDataException>(() => new ExportConfigurationFix().Apply(exportProject));
        Assert.True(JsonNode.DeepEquals(exportBefore, exportProject));
    }

    [Test]
    public static void InvalidOrDuplicateFieldEntriesAreNotDeletedByFixes()
    {
        foreach (var fix in new IConfigurationFix[] { new SymbolFieldsTableFix(), new FieldOrderFix() })
        foreach (var malformed in new[] { "null", "[]", "{}", "{\"name\":\"\"}", "{\"name\":\"Reference\"}" })
        {
            var project = Project();
            Fields(project).Add(JsonNode.Parse(malformed));
            var before = project.DeepClone();
            Assert.Throws<InvalidDataException>(() => fix.Apply(project));
            Assert.True(JsonNode.DeepEquals(before, project));
        }
    }

    [Test]
    public static async Task ApplyingAllFixesPassesAllChecksAndIsIdempotent()
    {
        var project = JsonNode.Parse("""
            {"board":{"keep":42},"schematic":{"bom_settings":{"filter_string":"*"},
            "bom_fmt_settings":{"custom":"keep"},"custom_setting":[1,2,3]}}
            """)!.AsObject();
        foreach (var (fix, _) in FixesAndChecks()) fix.Apply(project);
        foreach (var (_, check) in FixesAndChecks())
            Assert.Equal(CheckStatus.Passed, (await CheckAsync(project, check)).Status);
        var before = project.DeepClone();
        foreach (var (fix, _) in FixesAndChecks()) fix.Apply(project);
        Assert.True(JsonNode.DeepEquals(before, project));
    }

    private static (IConfigurationFix Fix, IFileCheck Check)[] FixesAndChecks() =>
    [
        (new SymbolFieldsTableFix(), new SymbolFieldsTableCheck()),
        (new EditTabMetadataFix(), new EditTabMetadataCheck()),
        (new ExportConfigurationFix(), new ExportConfigurationCheck()),
        (new FieldOrderFix(), new FieldOrderCheck())
    ];

    private static JsonObject Project(bool includeLcsc = true)
    {
        var project = JsonNode.Parse("""
            {"board":{"keep":[1,2,3]},"schematic":{
            "bom_export_filename":"manufacturing/bom/${PROJECTNAME}.csv",
            "bom_fmt_settings":{"field_delimiter":",","string_delimiter":"\"","ref_delimiter":",",
              "ref_range_delimiter":"","keep_tabs":false,"keep_line_breaks":false,"custom":42},
            "bom_settings":{"group_symbols":true,"exclude_dnp":false,"include_excluded_from_bom":false,
              "sort_asc":true,"sort_field":"Reference","filter_string":"*","fields_ordered":[]},
            "other":{"custom":"preserve"}}}
            """)!.AsObject();
        foreach (var name in ExpectedNames.Where(name => includeLcsc || name != "LCSC"))
            Fields(project).Add(new JsonObject
            {
                ["name"] = name, ["label"] = "Custom " + name, ["show"] = true, ["group_by"] = true,
                ["custom"] = new JsonObject { ["key"] = name }
            });
        return project;
    }

    private static JsonObject Bom(JsonObject project) => project["schematic"]!["bom_settings"]!.AsObject();
    private static JsonArray Fields(JsonObject project) => Bom(project)["fields_ordered"]!.AsArray();
    private static string Name(JsonNode field) => field["name"]!.GetValue<string>();
    private static JsonObject Field(JsonObject project, string name) => Fields(project).Single(field => Name(field!) == name)!.AsObject();

    private static JsonObject WithoutSymbolFlags(JsonObject project)
    {
        var copy = project.DeepClone().AsObject();
        foreach (var field in Fields(copy).OfType<JsonObject>())
        {
            field.Remove("show");
            field.Remove("group_by");
        }
        return copy;
    }

    private static JsonObject WithoutKeys(JsonObject project, string[] objectPath, string[] keys)
    {
        var copy = project.DeepClone().AsObject();
        JsonNode parent = copy;
        foreach (var segment in objectPath) parent = parent[segment]!;
        foreach (var key in keys) parent.AsObject().Remove(key);
        return copy;
    }

    private static async Task<CheckResult> CheckAsync(JsonObject project, IFileCheck check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "OepsConfigurationFixTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "board.kicad_pro"), project.ToJsonString());
            return await check.RunAsync(new("", directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
