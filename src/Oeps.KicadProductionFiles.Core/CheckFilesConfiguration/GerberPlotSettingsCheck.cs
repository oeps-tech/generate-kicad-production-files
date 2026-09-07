using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

/// <summary>Checks the PCB-stored General and Gerber options in the supplied Plot dialog screenshot.</summary>
public sealed class GerberPlotSettingsCheck : IFileCheck
{
    public string Name => "Gerber plot settings";
    internal sealed record Rule(string Key, string Expected, string Label, bool AllowMissing = false, bool Quoted = false)
    {
        public string DisplayValue => Quoted ? $"\"{Expected}\"" : Expected;
    }
    internal static readonly Rule[] Rules =
    [
        new("plotframeref", "no", "Plot drawing sheet"),
        new("subtractmaskfromsilk", "yes", "Subtract soldermask from silkscreen"),
        new("hidednponfab", "no", "Hide DNP on fabrication layers"),
        new("sketchdnponfab", "yes", "Indicate DNP: cross-out (sketch)"),
        new("crossoutdnponfab", "yes", "Indicate DNP: cross-out"),
        new("sketchpadsonfab", "no", "Sketch pads on fabrication layers"),
        new("plotpadnumbers", "no", "Include pad numbers"),
        new("drillshape", "0", "Drill marks: None"),
        new("scaleselection", "1", "Scaling: 1:1"),
        new("useauxorigin", "yes", "Use drill/place file origin"),
        new("mirror", "no", "Mirrored plot"),
        new("psnegative", "no", "Negative plot"),
        new("usegerberextensions", "no", "Use Protel filename extensions"),
        new("creategerberjobfile", "no", "Generate Gerber job file"),
        new("gerberprecision", "6", "Coordinate format: 4.6, mm", AllowMissing: true),
        new("usegerberattributes", "yes", "Use extended X2 format"),
        new("usegerberadvancedattributes", "yes", "Include netlist attributes"),
        new("disableapertmacros", "no", "Disable aperture macros"),
        new("outputdirectory", "manufacturing/gerber/", "Output directory", Quoted: true)
    ];

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await PcbPlotSettingsReader.ReadAsync(context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
            return Validate(snapshot.Document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        { return new(Name, CheckStatus.Failed, "Could not check the PCB Gerber plot settings.\n" + ex.Message); }
    }

    public static CheckResult Validate(PcbPlotSettingsDocument document)
    {
        var problems = new List<string>();
        foreach (var rule in Rules)
        {
            try
            {
                var value = document.ReadValue(rule.Key, rule.Quoted);
                if (value == rule.Expected || (value is null && rule.AllowMissing)) continue;
                problems.Add($"• {rule.Label}: expected ({rule.Key} {rule.DisplayValue}); " +
                    (value is null ? "setting is missing." : $"saved value is '{value}'."));
            }
            catch (InvalidDataException ex) { problems.Add($"• {rule.Label}: {ex.Message} Expected ({rule.Key} {rule.DisplayValue})."); }
        }
        return new("Gerber plot settings", problems.Count == 0 ? CheckStatus.Passed : CheckStatus.Failed, string.Join("\n", problems));
    }
}
