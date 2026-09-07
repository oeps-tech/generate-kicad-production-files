using System.Text;

namespace Oeps.KicadProductionFiles.Core.Data;

public static class CsvComponentParser
{
    public static IReadOnlyList<Component> Parse(string csv, ComponentHeaderAliases? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(csv);
        aliases ??= new();
        var input = csv.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (string.IsNullOrWhiteSpace(input)) throw new InvalidDataException("The spreadsheet download is empty.");
        if (input.StartsWith('<'))
            throw new InvalidDataException("The spreadsheet returned HTML or XML instead of CSV. Check anonymous view access and the export URL.");

        var records = ReadRecords(input);
        if (records.Count == 0) throw new InvalidDataException("The spreadsheet download has no header row.");
        var headers = records[0];
        var oepsColumn = FindColumn(headers, aliases.OepsPn, "OEPS PN / OEPS_PN", required: true);
        var mpnColumn = FindColumn(headers, aliases.Mpn, "MPN", required: true);
        var manufacturerColumn = FindColumn(headers, aliases.Manufacturer, "Manufacturer", required: false);
        var descriptionColumn = FindColumn(headers, aliases.Description, "Description", required: false);
        if (oepsColumn == mpnColumn) throw new InvalidDataException("OEPS PN and MPN must be separate CSV columns.");

        List<Component> components = [];
        HashSet<(string OepsPn, string Mpn)> seen = [];
        for (var index = 1; index < records.Count; index++)
        {
            var row = records[index];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (row.Count != headers.Count)
                throw new InvalidDataException($"CSV record {index + 1} has {row.Count} columns; expected {headers.Count}.");
            var oepsPn = row[oepsColumn].Trim();
            var mpn = row[mpnColumn].Trim();
            if (oepsPn.Length == 0 || mpn.Length == 0)
                throw new InvalidDataException($"CSV record {index + 1} must contain both OEPS PN and MPN. The previous database has been kept.");
            if (oepsPn.Any(char.IsControl) || mpn.Any(char.IsControl))
                throw new InvalidDataException($"CSV record {index + 1} contains control characters in an identifier.");
            if (seen.Add((oepsPn, mpn)))
            {
                string? Optional(int column) => column < 0 || string.IsNullOrWhiteSpace(row[column]) ? null : row[column].Trim();
                components.Add(new(oepsPn, mpn, Optional(manufacturerColumn), Optional(descriptionColumn)));
            }
        }
        if (components.Count == 0)
            throw new InvalidDataException("The spreadsheet contains no component pairings. The previous database has been kept.");
        return components.AsReadOnly();
    }

    private static int FindColumn(IReadOnlyList<string> headers, string[]? aliases, string label, bool required)
    {
        if (required && !(aliases ?? []).Any(alias => !string.IsNullOrWhiteSpace(alias)))
            throw new InvalidDataException($"No CSV header aliases are configured for {label}.");
        var matches = Enumerable.Range(0, headers.Count).Where(index =>
            (aliases ?? []).Any(alias => !string.IsNullOrWhiteSpace(alias)
                && string.Equals(headers[index].Trim(), alias.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"The CSV contains ambiguous duplicate {label} columns.");
        if (matches.Length == 0 && required) throw new InvalidDataException($"The CSV is missing the required {label} header.");
        return matches.Length == 0 ? -1 : matches[0];
    }

    internal static List<List<string>> ReadRecords(string input)
    {
        List<List<string>> records = [];
        List<string> row = [];
        StringBuilder field = new();
        var inQuotes = false;
        var afterQuote = false;
        var hasPendingField = false;

        void AddField()
        {
            row.Add(field.ToString());
            field.Clear();
            afterQuote = false;
            hasPendingField = false;
        }

        void AddRecord()
        {
            AddField();
            records.Add(row);
            row = [];
        }

        for (var index = 0; index < input.Length; index++)
        {
            var character = input[index];
            if (character == '\0') throw new InvalidDataException("The CSV contains a NUL byte.");
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < input.Length && input[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else { inQuotes = false; afterQuote = true; }
                }
                else field.Append(character);
                continue;
            }

            if (character == ',') { AddField(); hasPendingField = true; }
            else if (character is '\r' or '\n')
            {
                AddRecord();
                if (character == '\r' && index + 1 < input.Length && input[index + 1] == '\n') index++;
            }
            else if (character == '"' && field.Length == 0 && !afterQuote)
            {
                inQuotes = true;
                hasPendingField = true;
            }
            else if (afterQuote || character == '"')
                throw new InvalidDataException($"Malformed CSV quoting near character {index + 1}.");
            else { field.Append(character); hasPendingField = true; }
        }
        if (inQuotes) throw new InvalidDataException("The CSV ends inside a quoted field; the download may be incomplete.");
        if (hasPendingField || afterQuote || row.Count > 0 || field.Length > 0) AddRecord();
        return records;
    }
}
