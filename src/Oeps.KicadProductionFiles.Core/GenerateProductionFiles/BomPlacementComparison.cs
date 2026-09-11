namespace Oeps.KicadProductionFiles.Core.GenerateProductionFiles;

public sealed record BomPlacementComparison(int BomCount, int PlacementCount,
    IReadOnlyList<string> OnlyInBom, IReadOnlyList<string> OnlyInPlacement)
{
    public bool CountsMatch => BomCount == PlacementCount;
    public bool Matches => CountsMatch && OnlyInBom.Count == 0 && OnlyInPlacement.Count == 0;
    public string CountMessage => $"BOM and placement component counts do not match.\n\nBOM: {BomCount}\nPlacement files: {PlacementCount}\n\nSee the report for the component differences.";

    public static BomPlacementComparison Compare(GeneratedProductionFile bom, GeneratedProductionFile placement)
    {
        if (bom.ComponentCount is not int bomCount || placement.ComponentCount is not int posCount
            || bom.References is null || placement.References is null) throw new InvalidDataException("Component comparison needs both generated CSV reference lists.");
        return new(bomCount, posCount, Difference(bom.References, placement.References), Difference(placement.References, bom.References));
    }
    // Compare occurrences as well as reference names, so duplicate designators are not silently discarded.
    private static IReadOnlyList<string> Difference(IReadOnlyList<string> source, IReadOnlyList<string> other)
    {
        var remaining = other.GroupBy(reference => reference, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var reference in source)
        {
            if (remaining.TryGetValue(reference, out var count) && count > 0) remaining[reference] = count - 1;
            else missing.Add(reference);
        }
        return missing.Order(StringComparer.Ordinal).ToArray();
    }
}
