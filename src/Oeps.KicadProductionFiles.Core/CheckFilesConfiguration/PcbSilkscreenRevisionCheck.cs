using System.Text.RegularExpressions;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

/// <summary>Read-only check: placing revision text on the board requires a manual layout decision.</summary>
public sealed class PcbSilkscreenRevisionCheck : IFileCheck
{
    public string Name => "PCB silkscreen revision";

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = RevisionValue.Normalize(context.Revision);
            var board = await PcbPlotSettingsReader.ReadAsync(context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
            return Validate(board.Document, context.Revision);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        { return new(Name, CheckStatus.Failed, "Could not check the PCB silkscreen revision.\n" + ex.Message + "\nNo automatic fix is available."); }
    }

    public static CheckResult Validate(PcbPlotSettingsDocument board, string revision)
    {
        var expected = RevisionValue.Normalize(revision);
        var value = Regex.Escape(expected);
        var numeric = char.IsAsciiDigit(expected[0]);
        var prefixes = numeric ? "version|ver|v" : "revision|rev|r";
        // Match a whole revision inside a caption; 1.2 must not match 11.2, 1.20 or 1.2.3, and B must not match BB.
        var pattern = @"(?<![\p{L}\p{N}_])(?:(?:" + prefixes + @")\s*" + value
            + "|" + (numeric ? @"(?<!\.)" : "") + value + @")(?![\p{L}\p{N}_]|\.\d)";
        var matcher = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        foreach (var text in board.ReadSilkscreenTexts())
        {
            // Unresolved text variables are not evidence that the literal revision will be printed.
            var literal = Regex.Replace(text, @"\$\{[^}]*\}", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (matcher.IsMatch(literal)) return new("PCB silkscreen revision", CheckStatus.Passed, "");
        }
        return new("PCB silkscreen revision", CheckStatus.Failed,
            $"Revision '{expected}' was not found in visible text on F.SilkS or B.SilkS in the main .kicad_pcb.\n"
            + "Add or correct the silkscreen revision in KiCad, then save the board and check again. No automatic fix is available.");
    }
}
