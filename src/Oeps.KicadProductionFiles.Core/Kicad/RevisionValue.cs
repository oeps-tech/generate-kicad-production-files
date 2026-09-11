namespace Oeps.KicadProductionFiles.Core.Kicad;

/// <summary>Normalizes letter revisions and numeric versions without changing dotted version numbers.</summary>
public static class RevisionValue
{
    public static string Normalize(string? revision)
    {
        var normalized = revision?.Trim() ?? string.Empty;
        if (normalized.StartsWith("rev", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ver", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[3..].Trim();
        else if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = normalized[1..].TrimStart();
            // Keep alphabetic revisions such as V and VA intact.
            if (suffix.Length > 0 && char.IsAsciiDigit(suffix[0])) normalized = suffix;
        }
        if (normalized.Length == 0)
            throw new InvalidDataException("Enter a Revision with a value after any optional Rev, Ver or numeric v prefix (for example RevB, ver1.2 or v1.3.3).");
        return normalized.ToUpperInvariant();
    }
}
