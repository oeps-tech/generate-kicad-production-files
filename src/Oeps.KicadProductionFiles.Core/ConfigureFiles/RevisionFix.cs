using System.Text.Json.Nodes;
using System.Text;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

/// <summary>Repairs the project and matching main schematic revisions under one confirmation.</summary>
public sealed class RevisionFix(string revision) : IConfigurationFix
{
    public string CheckName => "Revision";
    public string Description => $"Set the project revision (board.ipc2581.sch_revision) and the matching main schematic's title_block rev to '{RevisionValue.Normalize(revision)}'. Already matching values are kept.";

    internal async Task<IReadOnlyList<ConfigurationFileEdit>> PrepareAsync(ProjectConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var project = snapshot.CreateDocument();
        using var json = System.Text.Json.JsonDocument.Parse(project.ToJsonString());
        if (RevisionCheck.Validate(json.RootElement, revision).Status != CheckStatus.Passed) Apply(project);
        var schematic = await SchematicRevisionReader.ReadAsync(snapshot.ProjectFilePath, cancellationToken).ConfigureAwait(false);
        var text = schematic.Document.WithRevision(revision);
        var validation = RevisionCheck.ValidateSchematic(new SchematicRevisionDocument(text), revision);
        if (validation.Status != CheckStatus.Passed) throw new InvalidDataException(validation.Detail);
        return [ProjectConfigurationStore.PrepareEdit(snapshot, project),
            new(schematic.FilePath, schematic.OriginalBytes, Encoding.UTF8.GetBytes(text))];
    }

    public void Apply(JsonObject project)
    {
        // Validate the input before creating any missing containers.
        var expected = RevisionValue.Normalize(revision);
        var board = ConfigurationJson.Object(project, "board", "board");
        var ipc = ConfigurationJson.Object(board, "ipc2581", "board.ipc2581");
        ConfigurationJson.SetScalar(ipc, "sch_revision", JsonValue.Create(expected));
    }
}
