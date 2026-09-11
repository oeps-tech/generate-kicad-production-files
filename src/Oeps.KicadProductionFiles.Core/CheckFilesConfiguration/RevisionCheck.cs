using System.Text.Json;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

/// <summary>Checks both the project IPC-2581 revision and the main schematic title-block revision.</summary>
public sealed class RevisionCheck : IFileCheck
{
    private const string CheckName = "Revision";
    private const string RevisionPath = "board.ipc2581.sch_revision";
    public string Name => CheckName;

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = RevisionValue.Normalize(context.Revision);
            var (projectFile, project) = await SymbolFieldsTableReader.ReadProjectSettingsAsync(context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
            var problems = new List<string>();
            var projectResult = Validate(project, context.Revision);
            if (projectResult.Status != CheckStatus.Passed) problems.Add("Project (.kicad_pro): " + projectResult.Detail);
            try
            {
                var schematic = await SchematicRevisionReader.ReadAsync(projectFile, cancellationToken).ConfigureAwait(false);
                var schematicResult = ValidateSchematic(schematic.Document, context.Revision);
                if (schematicResult.Status != CheckStatus.Passed) problems.Add(schematicResult.Detail);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
            { problems.Add("Main schematic (.kicad_sch): " + ex.Message); }
            return new(Name, problems.Count == 0 ? CheckStatus.Passed : CheckStatus.Failed, string.Join("\n", problems));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(Name, CheckStatus.Failed, "Could not check the revision.\n" + ex.Message);
        }
    }

    public static CheckResult ValidateSchematic(SchematicRevisionDocument schematic, string revision)
    {
        try
        {
            var expected = RevisionValue.Normalize(revision);
            if (schematic.Revision is null)
                return new(CheckName, CheckStatus.Failed, $"Main schematic (.kicad_sch): title_block rev is missing; set it to '{expected}'.");
            string actual;
            try { actual = RevisionValue.Normalize(schematic.Revision); }
            catch (InvalidDataException)
            { return new(CheckName, CheckStatus.Failed, $"Main schematic (.kicad_sch): title_block rev is empty or invalid; set it to '{expected}'."); }
            return actual == expected ? new(CheckName, CheckStatus.Passed, "")
                : new(CheckName, CheckStatus.Failed, $"Main schematic (.kicad_sch): set title_block rev to '{expected}'; the saved revision is '{schematic.Revision}'.");
        }
        catch (InvalidDataException ex) { return new(CheckName, CheckStatus.Failed, ex.Message); }
    }

    public static CheckResult Validate(JsonElement project, string revision)
    {
        try
        {
            var expected = RevisionValue.Normalize(revision);
            var board = RequiredProperty(project, "board", "board");
            var ipc = RequiredProperty(board, "ipc2581", "board.ipc2581");
            var saved = RequiredProperty(ipc, "sch_revision", RevisionPath);
            if (saved.ValueKind != JsonValueKind.String)
                return new(CheckName, CheckStatus.Failed, $"Set {RevisionPath} to '{expected}'; the saved revision must be a string.");

            var savedText = saved.GetString()!;
            string actual;
            try { actual = RevisionValue.Normalize(savedText); }
            catch (InvalidDataException)
            {
                return new(CheckName, CheckStatus.Failed, $"Set {RevisionPath} to '{expected}'; the saved revision is empty or has no value after its revision prefix.");
            }

            return actual == expected
                ? new(CheckName, CheckStatus.Passed, "")
                : new(CheckName, CheckStatus.Failed, $"Set {RevisionPath} to '{expected}'; the saved revision is '{savedText}'.");
        }
        catch (InvalidDataException ex)
        {
            return new(CheckName, CheckStatus.Failed, ex.Message);
        }
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name, string location)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Cannot read {location}: its parent must be a JSON object.");
        var properties = parent.EnumerateObject().Where(property => property.Name == name).ToArray();
        if (properties.Length == 0)
            throw new InvalidDataException($"The project is missing saved setting {location}.");
        if (properties.Length > 1)
            throw new InvalidDataException($"The project contains duplicate setting {location}.");
        return properties[0].Value;
    }
}
