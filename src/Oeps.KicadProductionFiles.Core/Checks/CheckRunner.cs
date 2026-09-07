namespace Oeps.KicadProductionFiles.Core.Checks;

public sealed class CheckRunner
{
    private readonly IReadOnlyList<IFileCheck> _checks;

    public CheckRunner(IEnumerable<IFileCheck>? checks = null)
    {
        _checks = checks?.ToArray() ??
        [
            new KicadCliCheck(),
            new ProjectFilesCheck(),
            new ComponentDatabaseCheck(),
            new BomPositionCountCheck(),
            new ComponentIdentifiersCheck()
        ];
    }

    public Task<CheckReport> RunAsync(CheckContext context, CancellationToken cancellationToken = default) =>
        // File metadata and process startup can block, especially on network paths.
        // Keep those synchronous portions of each check away from the GUI thread.
        Task.Run(() => RunChecksAsync(context, cancellationToken), cancellationToken);

    private async Task<CheckReport> RunChecksAsync(CheckContext context, CancellationToken cancellationToken)
    {
        var entries = new List<CheckResult>();
        foreach (var check in _checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                entries.Add(await check.RunAsync(context, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                entries.Add(new(check.Name, CheckStatus.Failed, $"Check could not complete: {exception.Message}"));
            }
        }
        return new(entries.AsReadOnly());
    }
}
