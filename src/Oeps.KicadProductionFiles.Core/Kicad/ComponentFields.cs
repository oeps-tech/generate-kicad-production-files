namespace Oeps.KicadProductionFiles.Core.Kicad;

/// <summary>One physical component, identified by its reference. Field keys are KiCad names, not CSV labels.</summary>
public sealed record ComponentFields(string Reference, IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string>? Issues = null);
