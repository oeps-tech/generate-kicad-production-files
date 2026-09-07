using System.Net;
using System.Text;
using Oeps.KicadProductionFiles.Core.Data;

namespace Oeps.KicadProductionFiles.Tests;

public static class DataTests
{
    private const string SpreadsheetUrl = "https://example.test/components.csv";
    private const string ValidCsv = "Description,OEPS_PN,MPN\r\nResistor,OEPS-0012,000042\r\n";

    [Test]
    public static void CsvPreservesPartNumbersAndHandlesQuotedFields()
    {
        var components = CsvComponentParser.Parse("\uFEFFDescription,MPN,OEPS PN,Manufacturer\r\n\"Line one\r\n\"\"quoted\"\" café\",\"MPN,42\",OEPS-0012,München\r\n");
        Assert.Equal(1, components.Count);
        Assert.Equal("OEPS-0012", components[0].OepsPn);
        Assert.Equal("MPN,42", components[0].Mpn);
        Assert.Equal("Line one\r\n\"quoted\" café", components[0].Description);
        Assert.Equal("München", components[0].Manufacturer);
        Assert.Equal("000042", CsvComponentParser.Parse(ValidCsv)[0].Mpn);
        var aliases = new ComponentHeaderAliases { OepsPn = ["Internal code"], Mpn = ["Supplier part"] };
        Assert.Equal("00123", CsvComponentParser.Parse("Supplier part,Internal code\n00123,OEPS-0012", aliases)[0].Mpn);
        Assert.Equal("000042", CsvComponentParser.Parse(ValidCsv, null)[0].Mpn);
        Assert.Equal("000042", CsvComponentParser.Parse(ValidCsv, new() { Description = null!, Manufacturer = null! })[0].Mpn);
    }

    [Test]
    public static void CsvKeepsAlternativeMpnsAndDeduplicatesOnlyExactPairs()
    {
        var components = CsvComponentParser.Parse("OEPS_PN,MPN\nOEPS101234,PART-A\nOEPS101234,PART-B\nOEPS101234,PART-A\n,\n\n");
        Assert.Equal(2, components.Count);
        Assert.Equal("PART-A", components[0].Mpn);
        Assert.Equal("PART-B", components[1].Mpn);
    }

    [Test]
    public static void CsvRejectsPartialOrAmbiguousDatabases()
    {
        foreach (var csv in new[]
        {
            "", "<!DOCTYPE html><html>Sign in</html>", "MPN,Description\npart,description",
            "OEPS_PN,MPN\n", "OEPS_PN,MPN\n,part", "OEPS_PN,MPN\nOEPS101234,",
            "OEPS_PN,MPN\nOEPS101234,\"unfinished", "OEPS_PN,MPN\nOEPS101234,\"part\"oops",
            "OEPS_PN,MPN\nOEPS101234,pa\"rt", "OEPS_PN,MPN\nOEPS101234,part,extra",
            "OEPS_PN,MPN,OEPS PN\nOEPS101234,part,OEPS101234",
            "OEPS_PN,MPN\nOEPS101234,\"part\nwith newline\"", "OEPS_PN,MPN\nOEPS101234,part\0"
        }) Assert.Throws<InvalidDataException>(() => CsvComponentParser.Parse(csv));
        var missingAliases = Assert.Throws<InvalidDataException>(() => CsvComponentParser.Parse(ValidCsv, new() { OepsPn = null! }));
        Assert.True(missingAliases.Message.Contains("No CSV header aliases", StringComparison.Ordinal));
    }

    [Test]
    public static async Task FailedRefreshPreservesCsvRecordsAndSuccessfulSyncTime()
    {
        using var folder = new DataTestFolder();
        using var handler = new SequenceHandler(Csv(ValidCsv), Csv("OEPS_PN,MPN\nOEPS101234,"),
            new(HttpStatusCode.OK) { Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html") },
            Csv("OEPS_PN,MPN"), new(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var time = new FakeTimeProvider();
        var repository = new ComponentRepository(client, SpreadsheetUrl, folder.CachePath, timeProvider: time);
        Assert.True(await repository.RefreshAsync());
        var original = await File.ReadAllBytesAsync(folder.CachePath);
        var syncedAt = repository.LastSuccessfulSyncUtc;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            Assert.False(await repository.RefreshAsync());
            Assert.Equal(1, repository.Components.Count);
            Assert.Equal("000042", repository.Components[0].Mpn);
            Assert.Equal(syncedAt, repository.LastSuccessfulSyncUtc);
            var persisted = await File.ReadAllBytesAsync(folder.CachePath);
            Assert.True(original.SequenceEqual(persisted));
            Assert.True(repository.LastError is not null);
            Assert.Equal(time.GetUtcNow() + ComponentRepository.RefreshInterval, repository.NextRefreshUtc);
            Assert.False(repository.IsRefreshing);
        }
        Assert.Equal(ValidCsv, await File.ReadAllTextAsync(folder.CachePath));
        Assert.Equal(1, Directory.GetFiles(folder.Path).Length);
    }

    [Test]
    public static async Task CachedDatabaseLoadsImmediatelyAndSurvivesOfflineStartup()
    {
        using var folder = new DataTestFolder();
        using var handler = new SequenceHandler(Csv(ValidCsv), new(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var time = new FakeTimeProvider();
        var online = new ComponentRepository(client, SpreadsheetUrl, folder.CachePath, timeProvider: time);
        Assert.False(online.LoadCache());
        Assert.True(await online.RefreshAsync());
        time.Advance(TimeSpan.FromHours(3));
        var offline = new ComponentRepository(client, SpreadsheetUrl, folder.CachePath, timeProvider: time);
        Assert.True(offline.LoadCache());
        Assert.Equal(online.LastSuccessfulSyncUtc, offline.LastSuccessfulSyncUtc);
        Assert.True(offline.IsRefreshDue);
        Assert.False(await offline.RefreshIfDueAsync());
        Assert.True(offline.HasData);
        Assert.False(offline.IsRefreshDue);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(offline.IsRefreshDue);
    }

    [Test]
    public static async Task AutomaticRefreshWaitsFiveMinutesAndManualRefreshBypassesInterval()
    {
        using var folder = new DataTestFolder();
        using var handler = new SequenceHandler(Csv(ValidCsv), Csv(ValidCsv), Csv(ValidCsv));
        using var client = new HttpClient(handler);
        var time = new FakeTimeProvider();
        var repository = new ComponentRepository(client, SpreadsheetUrl, folder.CachePath, timeProvider: time);
        Assert.True(await repository.RefreshIfDueAsync());
        var next = repository.NextRefreshUtc;
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(await repository.RefreshIfDueAsync());
        Assert.Equal(next, repository.NextRefreshUtc);
        Assert.Equal(1, handler.RequestCount);
        Assert.True(await repository.RefreshAsync());
        Assert.Equal(2, handler.RequestCount);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await repository.RefreshIfDueAsync());
        Assert.Equal(3, handler.RequestCount);
    }

    [Test]
    public static async Task OverlappingRefreshesDoNotDownloadTwice()
    {
        using var folder = new DataTestFolder();
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DeferredHandler(completion.Task);
        using var client = new HttpClient(handler);
        var repository = new ComponentRepository(client, SpreadsheetUrl, folder.CachePath);
        var first = repository.RefreshAsync();
        Assert.True(repository.IsRefreshing);
        Assert.False(await repository.RefreshAsync());
        Assert.False(await repository.RefreshIfDueAsync());
        Assert.Equal(1, handler.RequestCount);
        completion.SetResult(Csv(ValidCsv));
        Assert.True(await first);
        Assert.False(repository.IsRefreshing);
    }

    [Test]
    public static async Task CanceledRefreshDoesNotReplaceAValidDatabase()
    {
        using var folder = new DataTestFolder();
        await File.WriteAllTextAsync(folder.CachePath, ValidCsv);
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DeferredHandler(completion.Task);
        using var client = new HttpClient(handler);
        var repository = new ComponentRepository(client, SpreadsheetUrl, folder.CachePath);
        Assert.True(repository.LoadCache());
        using var cancellation = new CancellationTokenSource();
        var refresh = repository.RefreshAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.False(await refresh);
        Assert.True(repository.HasData);
        Assert.False(repository.IsRefreshing);
        Assert.Equal(ValidCsv, await File.ReadAllTextAsync(folder.CachePath));
        Assert.True(repository.LastError!.Contains("canceled", StringComparison.Ordinal));
    }

    [Test]
    public static async Task StalledResponseBodyTimesOutAndKeepsCacheAvailableForNextRefresh()
    {
        using var folder = new DataTestFolder();
        await File.WriteAllTextAsync(folder.CachePath, ValidCsv);
        using var stalledBody = new StalledReadStream();
        using var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stalledBody)
        }, Csv(ValidCsv));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var time = new FakeTimeProvider();
        var repository = new ComponentRepository(client, SpreadsheetUrl, folder.CachePath,
            timeProvider: time, refreshTimeout: TimeSpan.FromSeconds(1));
        Assert.True(repository.LoadCache());
        var syncedAt = repository.LastSuccessfulSyncUtc;
        var refresh = repository.RefreshAsync();
        await stalledBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(repository.IsRefreshing);
        Assert.True(repository.HasData);
        Assert.Equal("000042", repository.Components[0].Mpn);
        Assert.Equal(syncedAt, repository.LastSuccessfulSyncUtc);
        Assert.Equal(ValidCsv, await File.ReadAllTextAsync(folder.CachePath));
        Assert.Equal(time.GetUtcNow() + ComponentRepository.RefreshInterval, repository.NextRefreshUtc);
        Assert.True(repository.LastError!.Contains("timed out", StringComparison.Ordinal));
        Assert.True(await repository.RefreshAsync());
        Assert.True(repository.LastError is null);
    }

    [Test]
    public static async Task FailedInitialDownloadReportsMissingDataWithoutCreatingCache()
    {
        using var folder = new DataTestFolder();
        using var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);
        var repository = new ComponentRepository(client, SpreadsheetUrl, folder.CachePath);
        Assert.False(repository.LoadCache());
        Assert.False(await repository.RefreshAsync());
        Assert.False(repository.HasData);
        Assert.False(File.Exists(folder.CachePath));
        Assert.True(repository.LastError!.Contains("anonymously readable", StringComparison.Ordinal));
    }

    [Test]
    public static void InvalidCacheIsReportedWithoutCrashing()
    {
        using var folder = new DataTestFolder();
        using var client = new HttpClient();
        foreach (var content in new[] { "<html>Sign in</html>", "", "OEPS_PN,MPN\nOEPS000123," })
        {
            File.WriteAllText(folder.CachePath, content);
            var repository = new ComponentRepository(client, SpreadsheetUrl, folder.CachePath);
            Assert.False(repository.LoadCache());
            Assert.False(repository.HasData);
            Assert.True(repository.LastError is not null);
        }
    }

    [Test]
    public static async Task InvalidEncodingAndOversizedDownloadsNeverBecomeTheDatabase()
    {
        using var folder = new DataTestFolder();
        var malformed = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0xC3, 0x28])
        };
        var oversized = Csv(ValidCsv);
        oversized.Content.Headers.ContentLength = 21 * 1024 * 1024;
        using var handler = new SequenceHandler(malformed, oversized);
        using var client = new HttpClient(handler);
        var repository = new ComponentRepository(client, SpreadsheetUrl, folder.CachePath);
        Assert.False(await repository.RefreshAsync());
        Assert.False(await repository.RefreshAsync());
        Assert.False(repository.HasData);
        Assert.False(File.Exists(folder.CachePath));
        Assert.True(repository.LastError!.Contains("20 MiB", StringComparison.Ordinal));
    }

    private static HttpResponseMessage Csv(string csv) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(csv, Encoding.UTF8, "text/csv")
    };

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class DeferredHandler(Task<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return response.WaitAsync(cancellationToken);
        }
    }

    private sealed class StalledReadStream : MemoryStream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class DataTestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OepsKicadDataTests", Guid.NewGuid().ToString("N"));
        public string CachePath => System.IO.Path.Combine(Path, "components.csv");
        public DataTestFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
