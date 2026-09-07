namespace Oeps.KicadProductionFiles.Core.Checks;

public enum CheckStatus { Passed, Warning, Failed, Pending }

public sealed record CheckContext(
    string KicadCliPath,
    string ProjectDirectory,
    int DatabaseComponentCount = 0,
    DateTimeOffset? DatabaseLastSuccessfulSyncUtc = null,
    string? DatabaseLastError = null,
    string Revision = "");

public sealed record CheckResult(string Name, CheckStatus Status, string Detail);

public sealed record CheckReport(IReadOnlyList<CheckResult> Entries)
{
    public string Title { get; init; } = "Setup checks";
    public bool HasFailures => Entries.Any(entry => entry.Status == CheckStatus.Failed);
    public bool HasPendingChecks => Entries.Any(entry => entry.Status == CheckStatus.Pending);
    public string Summary
    {
        get
        {
            var failures = Entries.Count(entry => entry.Status == CheckStatus.Failed);
            var warnings = Entries.Count(entry => entry.Status == CheckStatus.Warning);
            var pending = Entries.Count(entry => entry.Status == CheckStatus.Pending);
            if (Entries.Count > 0 && failures == 0 && warnings == 0 && pending == 0)
                return $"{Title}: everything is good.";
            return $"{Title}: {failures} failed, {warnings} warning(s). " +
                (pending > 0 ? $"{pending} production check(s) pending implementation; production files have not been validated." : "See each result for its scope.");
        }
    }
}

public interface IFileCheck
{
    string Name { get; }
    Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default);
}

internal static class CheckPaths
{
    public static string Clean(string value) => value.Trim().Trim('"');
}
