using System.Text;
using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

public sealed class GerberPlotSettingsFix : IConfigurationFix
{
    public string CheckName => "Gerber plot settings";
    public string Description => "Correct the main PCB's saved General and Gerber plot options to match the screenshot. " +
        "Set the output directory to manufacturing/gerber/. Keep layer selections, layout and other settings. Zone-fill checking is an application preference and is not changed.";

    public string Apply(string pcbText)
    {
        var document = new PcbPlotSettingsDocument(pcbText);
        var changes = new Dictionary<string, string>();
        foreach (var rule in GerberPlotSettingsCheck.Rules)
        {
            try
            {
                var value = document.ReadValue(rule.Key, rule.Quoted);
                if (value == rule.Expected || (value is null && rule.AllowMissing)) continue;
            }
            catch (InvalidDataException) { /* WithValues rejects duplicates and structured values before editing. */ }
            changes.Add(rule.Key, rule.Expected);
        }
        var quotedKeys = GerberPlotSettingsCheck.Rules.Where(rule => rule.Quoted).Select(rule => rule.Key).ToHashSet(StringComparer.Ordinal);
        var modified = document.WithValues(changes, quotedKeys);
        var result = GerberPlotSettingsCheck.Validate(new PcbPlotSettingsDocument(modified));
        if (result.Status != CheckStatus.Passed) throw new InvalidDataException(result.Detail);
        return modified;
    }

    internal async Task<IReadOnlyList<ConfigurationFileEdit>> PrepareAsync(string projectDirectory, CancellationToken cancellationToken)
    {
        var snapshot = await PcbPlotSettingsReader.ReadAsync(projectDirectory, cancellationToken).ConfigureAwait(false);
        var modified = Apply(new UTF8Encoding(false, true).GetString(snapshot.OriginalBytes));
        return [new(snapshot.FilePath, snapshot.OriginalBytes, Encoding.UTF8.GetBytes(modified))];
    }

    void IConfigurationFix.Apply(JsonObject project) => throw new NotSupportedException("Gerber plot settings are stored in the .kicad_pcb file.");
}
