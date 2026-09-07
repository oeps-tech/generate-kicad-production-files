namespace Oeps.KicadProductionFiles.Core.Kicad;

/// <summary>Accepts revision input such as B, RevB, or revB as the same revision.</summary>
public static class RevisionValue
{
    public static string Normalize(string? revision)
    {
        var normalized = revision?.Trim() ?? string.Empty;
        if (normalized.StartsWith("rev", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[3..].Trim();
        if (normalized.Length == 0)
            throw new InvalidDataException("Enter a Revision with a value after any optional 'Rev' prefix (for example B or RevB).");
        return normalized.ToUpperInvariant();
    }
}
