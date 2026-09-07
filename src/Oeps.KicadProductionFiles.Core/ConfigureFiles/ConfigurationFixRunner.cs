using System.Text.Json;
using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

public sealed record ConfigurationFixPrompt(CheckResult Failure, string Description);
public enum FixActionStatus { Fixed, Skipped, Failed }
public sealed record FixActionResult(string CheckName, FixActionStatus Status, string Detail);
public sealed record ConfigurationFixReport(CheckReport Checks, IReadOnlyList<FixActionResult> Actions);

/// <summary>Prepares, confirms, and saves one failed configuration check at a time.</summary>
public sealed class ConfigurationFixRunner
{
    private readonly IReadOnlyList<IConfigurationFix>? _fixes;

    public ConfigurationFixRunner(IEnumerable<IConfigurationFix>? fixes = null) => _fixes = fixes?.ToArray();

    public async Task<ConfigurationFixReport> RunAsync(CheckContext context,
        Func<ConfigurationFixPrompt, CancellationToken, Task<bool>> confirmAsync,
        CancellationToken cancellationToken = default)
    {
        var actions = new List<FixActionResult>();
        var fixes = _fixes ??
            [new SymbolFieldsTableFix(), new EditTabMetadataFix(), new ExportConfigurationFix(), new FieldOrderFix(), new RevisionFix(context.Revision), new GerberPlotSettingsFix()];
        var checks = new ConfigurationCheckRunner();
        var report = await checks.RunAsync(context, cancellationToken);
        foreach (var fix in fixes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = report.Entries.SingleOrDefault(entry => entry.Name == fix.CheckName && entry.Status == CheckStatus.Failed);
            if (failure is null) continue;
            try
            {
                // Prepare an exact candidate before asking. No project or backup is written here.
                var edits = await Task.Run(async () =>
                {
                    if (fix is GerberPlotSettingsFix gerberFix)
                        return await gerberFix.PrepareAsync(context.ProjectDirectory, cancellationToken);
                    var snapshot = await ProjectConfigurationStore.LoadAsync(context.ProjectDirectory, cancellationToken);
                    if (fix is RevisionFix revisionFix)
                        return await revisionFix.PrepareAsync(snapshot, cancellationToken);
                    var document = snapshot.CreateDocument();
                    fix.Apply(document);
                    var validation = ValidateCandidate(fix.CheckName, document, context.Revision);
                    if (validation.Status != CheckStatus.Passed)
                        throw new InvalidDataException("The proposed fix could not satisfy this check: " + validation.Detail);
                    return (IReadOnlyList<ConfigurationFileEdit>)[ProjectConfigurationStore.PrepareEdit(snapshot, document)];
                }, cancellationToken);

                if (!await confirmAsync(new(failure, fix.Description), cancellationToken))
                {
                    actions.Add(new(fix.CheckName, FixActionStatus.Skipped, "Declined; no changes applied for this check."));
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var backups = await Task.Run(() => ConfigurationFileStore.SaveAsync(edits, cancellationToken), cancellationToken);
                    actions.Add(new(fix.CheckName, FixActionStatus.Fixed,
                        backups.Count == 0 ? "Already configured." : "Fix applied. Originals saved in .oeps-backups."));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException or JsonException or NotSupportedException)
            {
                actions.Add(new(fix.CheckName, FixActionStatus.Failed, "Could not apply this fix: " + ex.Message));
            }

            // Earlier changes can resolve another failure. Declined checks remain in the final report.
            report = await checks.RunAsync(context, cancellationToken);
        }
        report = await checks.RunAsync(context, cancellationToken);
        return new(report, actions.AsReadOnly());
    }

    private static CheckResult ValidateCandidate(string checkName, JsonObject document, string revision)
    {
        using var json = JsonDocument.Parse(document.ToJsonString());
        if (checkName == "Revision") return RevisionCheck.Validate(json.RootElement, revision);
        var schematic = json.RootElement.GetProperty("schematic");
        return checkName switch
        {
            "Symbol Fields Table" => SymbolFieldsTableCheck.Validate(SymbolFieldsTableReader.ParseTable("", schematic.GetProperty("bom_settings"))),
            "Edit Tab metadata" => EditTabMetadataCheck.Validate(schematic.GetProperty("bom_settings")),
            "Export configuration" => ExportConfigurationCheck.Validate(schematic),
            "Field order" => FieldOrderCheck.Validate(SymbolFieldsTableReader.ParseTable("", schematic.GetProperty("bom_settings"))),
            _ => throw new InvalidOperationException("No validation is registered for this fix.")
        };
    }
}
