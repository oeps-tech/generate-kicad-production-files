using System.Net;
using System.Text;

namespace Oeps.KicadProductionFiles.Core.Data;

/// <summary>Keeps the last valid spreadsheet CSV on disk and in memory across offline sessions.</summary>
public sealed class ComponentRepository
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private const int MaximumDownloadBytes = 20 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _httpClient;
    private readonly string _spreadsheetCsvUrl;
    private readonly ComponentHeaderAliases _aliases;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshTimeout;
    private int _refreshing;
    private RepositoryState _state;

    public ComponentRepository(HttpClient httpClient, string spreadsheetCsvUrl, string cacheCsvPath,
        ComponentHeaderAliases? aliases = null, TimeProvider? timeProvider = null, TimeSpan? refreshTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheCsvPath);
        _httpClient = httpClient;
        _spreadsheetCsvUrl = spreadsheetCsvUrl;
        CacheCsvPath = Path.GetFullPath(cacheCsvPath);
        _aliases = aliases ?? new();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshTimeout = refreshTimeout ?? TimeSpan.FromSeconds(40);
        if (_refreshTimeout <= TimeSpan.Zero || _refreshTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(refreshTimeout), "The refresh timeout must be positive and finite.");
        _state = new([], null, _timeProvider.GetUtcNow(), null);
    }

    public string CacheCsvPath { get; }
    public IReadOnlyList<Component> Components => Volatile.Read(ref _state).Components;
    public DateTimeOffset? LastSuccessfulSyncUtc => Volatile.Read(ref _state).LastSuccessfulSyncUtc;
    public DateTimeOffset NextRefreshUtc => Volatile.Read(ref _state).NextRefreshUtc;
    public string? LastError => Volatile.Read(ref _state).LastError;
    public bool HasData => Components.Count > 0;
    public bool IsRefreshing => Volatile.Read(ref _refreshing) != 0;
    public bool IsRefreshDue => !IsRefreshing && _timeProvider.GetUtcNow() >= NextRefreshUtc;

    /// <summary>Loads a validated local CSV at startup. Call before scheduling online refreshes.</summary>
    public bool LoadCache()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return false;
        try
        {
            if (!File.Exists(CacheCsvPath)) return false;
            if (new FileInfo(CacheCsvPath).Length > MaximumDownloadBytes)
                throw new InvalidDataException("The local CSV exceeds the 20 MiB limit.");
            var components = CsvComponentParser.Parse(File.ReadAllText(CacheCsvPath, StrictUtf8), _aliases);
            var syncedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(CacheCsvPath), TimeSpan.Zero);
            // An externally copied cache can have a future timestamp; never delay online refresh for it.
            var nextRefresh = syncedAt > _timeProvider.GetUtcNow() ? _timeProvider.GetUtcNow() : syncedAt + RefreshInterval;
            Volatile.Write(ref _state, new(components, syncedAt, nextRefresh, null));
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Volatile.Write(ref _state, _state with { LastError = $"Local database could not be loaded: {ex.Message}" });
            return false;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    /// <summary>For a manual Update database action; bypasses the five-minute schedule.</summary>
    public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) => RefreshCoreAsync(true, cancellationToken);

    /// <summary>For a timer or startup; requests only when the five-minute interval has elapsed.</summary>
    public Task<bool> RefreshIfDueAsync(CancellationToken cancellationToken = default) => RefreshCoreAsync(false, cancellationToken);

    private async Task<bool> RefreshCoreAsync(bool force, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return false;
        using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempted = false;
        try
        {
            if (!force && _timeProvider.GetUtcNow() < NextRefreshUtc) return false;
            attempted = true;
            // ResponseHeadersRead ends HttpClient.Timeout coverage at the headers, so body reads need our own deadline.
            refreshCancellation.CancelAfter(_refreshTimeout);
            var refreshToken = refreshCancellation.Token;
            var csv = await DownloadCsvAsync(refreshToken).ConfigureAwait(false);
            var components = await Task.Run(() => CsvComponentParser.Parse(csv, _aliases), refreshToken).ConfigureAwait(false);
            var syncedAt = _timeProvider.GetUtcNow();
            await WriteCacheAtomicAsync(csv, syncedAt, refreshToken).ConfigureAwait(false);
            Volatile.Write(ref _state, new(components, syncedAt, syncedAt + RefreshInterval, null));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _state, _state with { LastError = "Database refresh was canceled; the last valid copy has been kept." });
            return false;
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _state, _state with { LastError = "Database refresh timed out; the last valid copy has been kept." });
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
            or DecoderFallbackException)
        {
            Volatile.Write(ref _state, _state with { LastError = $"Database refresh failed: {ex.Message}" });
            return false;
        }
        finally
        {
            if (attempted)
                Volatile.Write(ref _state, _state with { NextRefreshUtc = _timeProvider.GetUtcNow() + RefreshInterval });
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private async Task<string> DownloadCsvAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_spreadsheetCsvUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Configure a publicly readable HTTPS spreadsheet CSV export URL.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("text/csv");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidDataException("The spreadsheet is not anonymously readable. Check its public view access.");
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme is { } scheme && scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The spreadsheet redirected to a non-HTTPS address.");
        if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml")
            throw new InvalidDataException("The spreadsheet returned HTML instead of CSV. Check public view access and the export URL.");
        if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
            throw new InvalidDataException("The spreadsheet download exceeds the 20 MiB limit.");
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int received;
        while ((received = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + received > MaximumDownloadBytes)
                throw new InvalidDataException("The spreadsheet download exceeds the 20 MiB limit.");
            buffer.Write(chunk, 0, received);
        }
        return StrictUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private async Task WriteCacheAtomicAsync(string csv, DateTimeOffset syncedAt, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(CacheCsvPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(CacheCsvPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(StrictUtf8.GetBytes(csv), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.SetLastWriteTimeUtc(temporaryPath, syncedAt.UtcDateTime);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(CacheCsvPath)) File.Replace(temporaryPath, CacheCsvPath, null);
            else File.Move(temporaryPath, CacheCsvPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed record RepositoryState(IReadOnlyList<Component> Components, DateTimeOffset? LastSuccessfulSyncUtc,
        DateTimeOffset NextRefreshUtc, string? LastError);
}
