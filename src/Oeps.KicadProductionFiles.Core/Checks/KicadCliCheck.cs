using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.Checks;

public sealed class KicadCliCheck(ICliVersionProbe? versionProbe = null) : IFileCheck
{
    private readonly ICliVersionProbe _versionProbe = versionProbe ?? new CliVersionProbe();
    public string Name => "KiCad CLI";

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = CheckPaths.Clean(context.KicadCliPath);
        if (string.IsNullOrWhiteSpace(path))
            return new(Name, CheckStatus.Failed, "Choose the path to kicad-cli.exe.");
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            return new(Name, CheckStatus.Failed, "The KiCad CLI file does not exist. Choose the full path to kicad-cli.exe.");
        if (!string.Equals(Path.GetFileNameWithoutExtension(path), "kicad-cli", StringComparison.OrdinalIgnoreCase))
            return new(Name, CheckStatus.Failed, "Choose kicad-cli.exe from the KiCad installation's bin folder.");

        var result = await _versionProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        return new(Name, result.Success ? CheckStatus.Passed : CheckStatus.Failed, result.Detail);
    }
}
