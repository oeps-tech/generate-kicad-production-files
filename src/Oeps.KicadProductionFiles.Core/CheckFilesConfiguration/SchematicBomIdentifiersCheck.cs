using Oeps.KicadProductionFiles.Core.CheckProductionFiles;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Data;
using Oeps.KicadProductionFiles.Core.GenerateProductionFiles;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

/// <summary>Checks identifiers and OEPS descriptions using a temporary CLI BOM of the saved schematic hierarchy.</summary>
public sealed class SchematicBomIdentifiersCheck(ICliCommandRunner? cli = null) : IFileCheck
{
    private readonly ICliCommandRunner _cli = cli ?? new CliCommandRunner();
    private static readonly string[] Fields = ["Reference", "OEPS PN", "OEPSPN", "MPN", "OEPS Description"];
    public string Name => "Schematic BOM OEPS PN / MPN / Description database validation";

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = context.Database.ToArray();
            var bom = await ExportBomAsync(context, cancellationToken).ConfigureAwait(false);
            return new BomDatabaseIdentifiersCheck().Run(bom, database, cancellationToken, includeFieldIssues: true) with { Name = Name };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        { return new(Name, CheckStatus.Failed, ex.Message); }
    }

    internal async Task<BomData> ExportBomAsync(CheckContext context, CancellationToken cancellationToken)
    {
        string? temporaryDirectory = null;
        string? output = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executable = CheckPaths.Clean(context.KicadCliPath);
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new InvalidDataException("Select an existing KiCad CLI executable to check the schematic BOM.");
            var (project, _) = await SymbolFieldsTableReader.ReadProjectSettingsAsync(context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
            var schematic = Path.ChangeExtension(project, ".kicad_sch");
            if (!File.Exists(schematic) || new FileInfo(schematic).Length == 0)
                throw new InvalidDataException("The main .kicad_sch with the same name as the .kicad_pro is missing or empty.");
            var workingDirectory = Path.GetDirectoryName(project)!;
            temporaryDirectory = Directory.CreateTempSubdirectory("Oeps-Kicad-BomCheck-").FullName;
            output = Path.Combine(temporaryDirectory, "identifiers.csv");
            // Explicit columns and no grouping/filtering: saved column visibility, labels and CSV formatting
            // cannot hide identifiers. KiCad follows linked sheets, includes DNP, and excludes non-BOM symbols.
            // Ascending is the default; explicit --sort-asc crashes KiCad 10.0.3 (bad_any_cast).
            var args = new List<string> { "sch", "export", "bom", "--output", output,
                "--fields", string.Join(',', Fields), "--labels", string.Join(',', Fields),
                "--group-by", "", "--sort-field", "Reference", "--filter", "",
                "--field-delimiter", ",", "--string-delimiter", "\"", "--ref-delimiter", ",", "--ref-range-delimiter", "",
                "--keep-tabs", "--keep-line-breaks", schematic };
            var result = await _cli.RunAsync(executable, args, workingDirectory, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new IOException("KiCad CLI could not export the schematic BOM for checking. " + ExportOutputDirectory.Diagnostics(result));
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new IOException("KiCad CLI completed without producing the BOM CSV for checking.");
            if (new FileInfo(output).Length > 20 * 1024 * 1024)
                throw new InvalidDataException("The temporary BOM CSV exceeds the supported size (20 MB).");
            var csv = await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false);
            return Parse(csv, cancellationToken);
        }
        finally
        {
            // Only remove the file and directory created by this invocation; never touch manufacturing.
            if (output is not null) File.Delete(output);
            if (temporaryDirectory is not null) Directory.Delete(temporaryDirectory, recursive: false);
        }
    }

    private static BomData Parse(string csv, CancellationToken token)
    {
        var records = CsvComponentParser.ReadRecords(csv.TrimStart('\uFEFF'));
        if (records.Count == 0 || !records[0].SequenceEqual(Fields))
            throw new InvalidDataException("The temporary BOM CSV does not contain the requested identifier columns.");
        var components = new List<ComponentFields>();
        foreach (var row in records.Skip(1))
        {
            token.ThrowIfCancellationRequested();
            if (row.Count != Fields.Length || string.IsNullOrWhiteSpace(row[0]) || row[0].Any(char.IsControl))
                throw new InvalidDataException("The temporary BOM CSV contains an incomplete row or invalid reference.");
            // --group-by is empty: each exported row represents one component, including multi-unit symbols.
            if (row[0].Contains(',')) throw new InvalidDataException("KiCad unexpectedly grouped references in the temporary BOM CSV.");
            components.Add(new(row[0].Trim(), Fields.Zip(row).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal)));
        }
        return new(Array.AsReadOnly(Fields), components.AsReadOnly());
    }
}
