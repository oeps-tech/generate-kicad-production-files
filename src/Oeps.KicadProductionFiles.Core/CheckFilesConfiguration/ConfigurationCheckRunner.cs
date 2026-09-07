using Oeps.KicadProductionFiles.Core.Checks;

namespace Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

/// <summary>Checks for the Check files configuration button. Add each new check in its own source file here.</summary>
public sealed class ConfigurationCheckRunner
{
    private readonly CheckRunner _runner = new([
        new SymbolFieldsTableCheck(), new EditTabMetadataCheck(), new ExportConfigurationCheck(), new FieldOrderCheck(),
        new RevisionCheck(), new GerberPlotSettingsCheck()
    ]);

    public async Task<CheckReport> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        var report = await _runner.RunAsync(context, cancellationToken).ConfigureAwait(false);
        return report with { Title = "Check files configuration" };
    }
}
