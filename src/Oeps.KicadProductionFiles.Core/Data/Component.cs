namespace Oeps.KicadProductionFiles.Core.Data;

/// <summary>A validated OEPS PN / manufacturer part number pairing from the component database.</summary>
public sealed record Component(string OepsPn, string Mpn, string? Manufacturer = null, string? Description = null);

public sealed class ComponentHeaderAliases
{
    public string[] OepsPn { get; set; } = ["OEPS_PN", "OEPS PN"];
    public string[] Mpn { get; set; } = ["MPN"];
    public string[] Manufacturer { get; set; } = ["Manufacturer"];
    public string[] Description { get; set; } = ["Description"];
}
