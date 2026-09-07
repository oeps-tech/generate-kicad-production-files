namespace Oeps.KicadProductionFiles.Core.Checks;

public sealed class ComponentDatabaseCheck(TimeProvider? timeProvider = null) : IFileCheck
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    public string Name => "Component database";

    public Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.DatabaseComponentCount <= 0)
            return Task.FromResult(new CheckResult(Name, CheckStatus.Failed,
                "No component database is available. Click Update database to download the spreadsheet." + ErrorDetail(context)));
        var detail = $"{context.DatabaseComponentCount:N0} OEPS PN / MPN pairing(s) available.";
        if (context.DatabaseLastSuccessfulSyncUtc is { } syncedAt)
            detail += $" Last downloaded {syncedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}.";
        var stale = context.DatabaseLastSuccessfulSyncUtc is null ||
            _timeProvider.GetUtcNow() - context.DatabaseLastSuccessfulSyncUtc.Value >= TimeSpan.FromMinutes(5);
        if (stale || !string.IsNullOrWhiteSpace(context.DatabaseLastError))
            return Task.FromResult(new CheckResult(Name, CheckStatus.Warning,
                detail + " Using cached data; refresh to obtain the latest spreadsheet." + ErrorDetail(context)));
        return Task.FromResult(new CheckResult(Name, CheckStatus.Passed, detail));
    }

    private static string ErrorDetail(CheckContext context) =>
        string.IsNullOrWhiteSpace(context.DatabaseLastError) ? "" : $" Last download: {context.DatabaseLastError}";
}
