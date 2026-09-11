using Oeps.KicadProductionFiles.Core.Checks;

namespace Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

/// <summary>Checks for the Check files configuration button. Add each new check in its own source file here.</summary>
public sealed class ConfigurationCheckRunner
{
    private readonly CheckRunner _runner;

    public ConfigurationCheckRunner(IEnumerable<IFileCheck>? checks = null) => _runner = new(checks ?? [
        new SymbolFieldsTableCheck(), new EditTabMetadataCheck(), new ExportConfigurationCheck(), new FieldOrderCheck(),
        new RevisionCheck(), new PcbSilkscreenRevisionCheck(), new GerberPlotSettingsCheck(), new SchematicBomIdentifiersCheck()
    ]);

    public async Task<CheckReport> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        var report = await _runner.RunAsync(context with { Database = context.Database.ToArray() }, cancellationToken).ConfigureAwait(false);
        return report with { Title = "Check files configuration" };
    }
}
