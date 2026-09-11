using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.ConfigureFiles;

namespace Oeps.KicadProductionFiles.Tests;

/// <summary>Runs metadata fix tests without an installed CLI or spreadsheet. CLI validation has its own tests.</summary>
internal static class ConfigurationTestChecks
{
    internal static ConfigurationFixRunner CreateFixRunner(IEnumerable<IConfigurationFix>? fixes = null) => new(fixes,
        new ConfigurationCheckRunner([
            new SymbolFieldsTableCheck(), new EditTabMetadataCheck(), new ExportConfigurationCheck(), new FieldOrderCheck(),
            new RevisionCheck(), new PcbSilkscreenRevisionCheck(), new GerberPlotSettingsCheck()
        ]));
}
