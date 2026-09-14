using Oeps.KicadProductionFiles.Core.CheckProductionFiles;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.GenerateProductionFiles;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

public sealed class SchematicBomDuplicatesCheck(ICliCommandRunner? cli = null) : IFileCheck
{
    public string Name => "Schematic BOM duplicate OEPS PN / MPN";

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Use exactly the same exporter and parser as the preceding database check.
            var bom = await new SchematicBomIdentifiersCheck(cli).ExportBomAsync(context, cancellationToken, useSavedGrouping: true).ConfigureAwait(false);
            return Validate(bom, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        { return new(Name, CheckStatus.Failed, ex.Message); }
    }

    public static CheckResult Validate(BomData bom, CancellationToken cancellationToken = default)
    {
        var rows = bom.Components.Select(component => (component.Reference, Fields: IdentifierFields.Read(component))).ToArray();
        var problems = new List<string>();
        void Check(string label, Func<IdentifierFields, string?> select)
        {
            foreach (var group in rows.Where(row => select(row.Fields) is not null)
                .GroupBy(row => select(row.Fields)!, StringComparer.Ordinal).Where(group => group.Count() > 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                problems.Add($"Duplicate {label} {IdentifierFields.Display(group.Key)} - {group.Count()} BOM rows:\n" +
                    string.Join("\n", group.Select((row, index) => $"Row {index + 1}: {row.Reference}")));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        Check("OEPS PN", fields => fields.OepsPn);
        Check("MPN", fields => fields.Mpn);
        return new("Schematic BOM duplicate OEPS PN / MPN", problems.Count == 0 ? CheckStatus.Passed : CheckStatus.Failed,
            string.Join("\n\n", problems));
    }
}
