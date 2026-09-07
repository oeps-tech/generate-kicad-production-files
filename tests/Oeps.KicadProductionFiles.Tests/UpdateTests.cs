using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oeps.KicadProductionFiles.Core.Updates;

namespace Oeps.KicadProductionFiles.Tests;

public static class UpdateTests
{
    [Test]
    public static void SemanticVersionsFollowSemverPrecedence()
    {
        var ordered = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "1.10.0", "2.0.0" };
        for (var i = 1; i < ordered.Length; i++) Assert.True(SemanticVersion.Parse(ordered[i - 1]).CompareTo(SemanticVersion.Parse(ordered[i])) < 0);
        Assert.Equal(0, SemanticVersion.Parse("v1.2.3+first").CompareTo(SemanticVersion.Parse("1.2.3+second")));
        Assert.True(SemanticVersion.Parse("1.0.0-99999999999999999999999").CompareTo(SemanticVersion.Parse("1.0.0-100000000000000000000000")) < 0);
        foreach (var invalid in new[] { "1", "1.2", "1.2.3.4", "01.2.3", "1.2.3-01", "1.2.3-", "1.2.3+", "1.2.3\n", "../1.2.3" })
            Assert.False(SemanticVersion.TryParse(invalid, out _), invalid);
    }

    [Test]
    public static async Task StagingOnlyBecomesWorkingAfterReadinessAndRetainsPrevious()
    {
        using var temporary = new TemporaryDirectory();
        var store = new UpdateStore(Path.Combine(temporary.Path, "data"));
        var first = CreatePackage(temporary.Path, "1.0.0");
        var firstInstalled = await store.StagePackageAsync(first.Path, first.Hash, SemanticVersion.Parse("1.0.0"));
        Assert.True(store.LoadState().Current is null);
        store.MarkWorking(firstInstalled);
        Assert.Equal("1.0.0", store.LoadState().Current!.Version);
        var second = CreatePackage(temporary.Path, "1.1.0");
        var secondInstalled = await store.StagePackageAsync(second.Path, second.Hash, SemanticVersion.Parse("1.1.0"));
        // A startup crash before MarkWorking preserves the previous working pointer.
        Assert.Equal("1.0.0", store.LoadState().Current!.Version);
        store.MarkWorking(secondInstalled);
        Assert.Equal("1.1.0", store.LoadState().Current!.Version);
        Assert.Equal("1.0.0", store.LoadState().Previous!.Version);
        Assert.True(File.Exists(store.GetExecutablePath(firstInstalled)));
        store.MarkWorking(firstInstalled);
        Assert.Equal("1.0.0", store.LoadState().Current!.Version);
        Assert.True(store.LoadState().Previous is null);
    }

    [Test]
    public static async Task IncompleteCurrentAndCorruptStateRecoverWorkingBackup()
    {
        using var temporary = new TemporaryDirectory();
        var dataRoot = Path.Combine(temporary.Path, "data");
        var store = new UpdateStore(dataRoot);
        var first = CreatePackage(temporary.Path, "1.0.0");
        var second = CreatePackage(temporary.Path, "1.1.0");
        var firstInstalled = await store.StagePackageAsync(first.Path, first.Hash, SemanticVersion.Parse("1.0.0"));
        var secondInstalled = await store.StagePackageAsync(second.Path, second.Hash, SemanticVersion.Parse("1.1.0"));
        store.MarkWorking(firstInstalled);
        store.MarkWorking(secondInstalled);
        File.Delete(store.GetExecutablePath(secondInstalled));
        Assert.True(store.LoadState().Current is null);
        Assert.Equal("1.0.0", store.LoadState().Previous!.Version);
        File.WriteAllText(Path.Combine(dataRoot, "update-state.json"), "broken{");
        Assert.Equal("1.0.0", store.LoadState().Current!.Version);
    }

    [Test]
    public static async Task InvalidAndFuturePackagesDoNotChangeWorkingVersion()
    {
        using var temporary = new TemporaryDirectory();
        var store = new UpdateStore(Path.Combine(temporary.Path, "data"));
        var original = CreatePackage(temporary.Path, "1.0.0");
        var working = await store.StagePackageAsync(original.Path, original.Hash, SemanticVersion.Parse("1.0.0"));
        store.MarkWorking(working);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.StagePackageAsync(original.Path, new string('0', 64), SemanticVersion.Parse("1.0.0")));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.StagePackageAsync(original.Path, original.Hash, SemanticVersion.Parse("1.1.0")));
        var future = CreatePackage(temporary.Path, "2.0.0", runtimeMajor: 11);
        await Assert.ThrowsAsync<IncompatibleUpdateException>(() => store.StagePackageAsync(future.Path, future.Hash, SemanticVersion.Parse("2.0.0")));
        var incomplete = CreatePackage(temporary.Path, "1.2.0", omitExecutable: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.StagePackageAsync(incomplete.Path, incomplete.Hash, SemanticVersion.Parse("1.2.0")));
        Assert.Equal("1.0.0", store.LoadState().Current!.Version);
        Assert.Equal(0, Directory.GetFileSystemEntries(store.StagingDirectory).Length);
    }

    [Test]
    public static async Task ZipTraversalDuplicateAndSymlinkEntriesAreRejected()
    {
        using var temporary = new TemporaryDirectory();
        var store = new UpdateStore(Path.Combine(temporary.Path, "data"));
        foreach (var path in new[] { "../outside.txt", "app/../../outside.txt", "app\\..\\outside.txt", "/app/file.txt", "app/C:/file.txt", "app/file.txt:stream", "app/CON.txt", "app/dir./file.txt" })
        {
            var package = CreatePackage(temporary.Path, "1.0.0", extraPath: path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.StagePackageAsync(package.Path, package.Hash, SemanticVersion.Parse("1.0.0")));
        }
        var duplicate = CreatePackage(temporary.Path, "1.0.0", extraPath: "app/UPDATE-MANIFEST.json");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.StagePackageAsync(duplicate.Path, duplicate.Hash, SemanticVersion.Parse("1.0.0")));
        var symlink = CreatePackage(temporary.Path, "1.0.0", extraPath: "app/link", symlink: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.StagePackageAsync(symlink.Path, symlink.Hash, SemanticVersion.Parse("1.0.0")));
        Assert.False(File.Exists(Path.Combine(temporary.Path, "outside.txt")));
        Assert.Equal(0, Directory.GetFileSystemEntries(store.StagingDirectory).Length);
    }

    [Test]
    public static void ChecksumsRequireCorrectHashAndOptionalFilename()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdateStore.ParseChecksum(hash + "  app.zip\r\n", "app.zip"));
        Assert.Equal(hash, UpdateStore.ParseChecksum(hash, "app.zip"));
        Assert.Throws<InvalidDataException>(() => UpdateStore.ParseChecksum(hash + "  wrong.zip", "app.zip"));
        Assert.Throws<InvalidDataException>(() => UpdateStore.ParseChecksum(hash + "\n" + hash, "app.zip"));
        Assert.Throws<InvalidDataException>(() => UpdateStore.ParseChecksum(new string('x', 64), "app.zip"));
    }

    [Test]
    public static async Task ReleaseChecksUsePublishedStableAssetsAndHttps()
    {
        string releaseJson(bool draft, bool prerelease, string tag, string protocol = "https") => JsonSerializer.Serialize(new
        {
            draft, prerelease, tag_name = tag, published_at = "2026-09-01T12:00:00Z",
            assets = new[]
            {
                new { name = "Oeps.KicadProductionFiles-1.2.0-win-x64.zip", browser_download_url = protocol + "://example.com/Oeps.KicadProductionFiles-1.2.0-win-x64.zip" },
                new { name = "Oeps.KicadProductionFiles-1.2.0-win-x64.zip.sha256", browser_download_url = protocol + "://example.com/Oeps.KicadProductionFiles-1.2.0-win-x64.zip.sha256" }
            }
        });
        foreach (var json in new[] { releaseJson(true, false, "v1.2.0"), releaseJson(false, true, "v1.2.0"), releaseJson(false, false, "v1.2.0-rc.1") })
        {
            using var http = new HttpClient(new FixedResponse(json));
            Assert.True(await new GitHubReleaseClient(http, "oeps-tech", "generate-kicad-production-files", "Oeps.KicadProductionFiles").GetLatestReleaseAsync() is null);
        }
        using var stable = new HttpClient(new FixedResponse(releaseJson(false, false, "v1.2.0")));
        var release = await new GitHubReleaseClient(stable, "oeps-tech", "generate-kicad-production-files", "Oeps.KicadProductionFiles").GetLatestReleaseAsync();
        Assert.Equal("1.2.0", release!.VersionText);
        using var unsafeHttp = new HttpClient(new FixedResponse(releaseJson(false, false, "v1.2.0", "http")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseClient(unsafeHttp, "oeps-tech", "generate-kicad-production-files", "Oeps.KicadProductionFiles").GetLatestReleaseAsync());
    }

    private sealed class FixedResponse(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.github.com/repos/oeps-tech/generate-kicad-production-files/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.True(request.Headers.UserAgent.Count > 0);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json"), RequestMessage = request });
        }
    }

    [Test]
    public static async Task MalformedReleaseMetadataBecomesRecoverableError()
    {
        foreach (var json in new[] { "{}", "[]", "not json", "{\"draft\":\"invalid\"}" })
        {
            using var http = new HttpClient(new FixedResponse(json));
            await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseClient(http, "oeps-tech", "generate-kicad-production-files", "Oeps.KicadProductionFiles").GetLatestReleaseAsync());
        }
    }

    private static (string Path, string Hash) CreatePackage(string root, string version, int runtimeMajor = 10, bool omitExecutable = false, string? extraPath = null, bool symlink = false)
    {
        var path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "app/update-manifest.json", JsonSerializer.Serialize(new { version, runtimeMajor, architecture = "x64", executable = UpdateStore.ExecutableName }));
            if (!omitExecutable) AddEntry(archive, "app/" + UpdateStore.ExecutableName, "test executable fixture");
            AddEntry(archive, "app/Oeps.KicadProductionFiles.App.dll", "test DLL fixture");
            AddEntry(archive, "app/Oeps.KicadProductionFiles.Core.dll", "test core DLL fixture");
            AddEntry(archive, "app/Oeps.KicadProductionFiles.App.deps.json", "{}");
            AddEntry(archive, "app/appsettings.json", "{}");
            AddEntry(archive, "app/Oeps.KicadProductionFiles.App.runtimeconfig.json", JsonSerializer.Serialize(new
            {
                runtimeOptions = new { frameworks = new[] { new { name = "Microsoft.WindowsDesktop.App", version = runtimeMajor + ".0.0" } } }
            }));
            if (extraPath is not null)
            {
                var entry = AddEntry(archive, extraPath, "untrusted entry");
                if (symlink) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            }
        }
        return (path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static ZipArchiveEntry AddEntry(ZipArchive archive, string path, string contents)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(contents);
        return entry;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oeps-update-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (System.IO.Path.GetDirectoryName(resolved) != System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar)
                || !System.IO.Path.GetFileName(resolved).StartsWith("oeps-update-test-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(resolved, true);
        }
    }
}
