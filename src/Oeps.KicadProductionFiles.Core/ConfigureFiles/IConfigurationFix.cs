using System.Text.Json.Nodes;

namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

/// <summary>A single configuration repair; the caller owns approval and saving the edited project.</summary>
public interface IConfigurationFix
{
    string CheckName { get; }
    string Description { get; }

    /// <summary>Changes an in-memory project. Discard the working copy if this throws.</summary>
    void Apply(JsonObject project);
}
