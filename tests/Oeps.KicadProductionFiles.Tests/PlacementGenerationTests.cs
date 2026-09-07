using System.Diagnostics;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.GenerateProductionFiles;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class PlacementGenerationTests
{
    [Test]
    public static async Task PlacementArgumentsMatchRequestedSettingsAndPathsWithSpaces()
    {
        using var fixture = new Fixture();
        var cli = new FakeCli();
        var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false));
        Assert.True(report.Success, report.Error);
        Assert.True(report.ManufacturingCleared);
        Assert.Equal(2, cli.Calls.Count);
        var args = cli.Calls[1];
        var stagedOutput = args[Array.IndexOf(args, "--output") + 1];
        Assert.Equal(Path.GetFileName(fixture.Output), Path.GetFileName(stagedOutput));
        Assert.True(Path.GetDirectoryName(stagedOutput)!.StartsWith(Path.Combine(fixture.Manufacturing, "assembly", ".oeps-export-")));
        Assert.True(args.SequenceEqual(new[] { "pcb", "export", "pos", "--format", "csv", "--units", "mm", "--side", "both",
            "--use-drill-file-origin", "--output", stagedOutput, fixture.Board }));
        Assert.False(args.Any(arg => arg is "--variant" or "--smd-only" or "--exclude-fp-th" or "--exclude-dnp" or "--bottom-negate-x"));
        Assert.Equal("Placement files", report.Files.Single().Name);
        Assert.Equal<int?>(1, report.Files.Single().ComponentCount);
        Assert.Equal(Path.Combine("manufacturing", "assembly", "my board-pos.csv"), report.Files.Single().RelativePath);
        Assert.True(File.Exists(fixture.Output));
    }

    [Test]
    public static async Task ManufacturingIsClearedBeforeExportAndOtherFilesArePreserved()
    {
        using var fixture = new Fixture();
        var obsolete = fixture.AddObsolete();
        var outside = Path.Combine(fixture.DirectoryPath, "keep.txt");
        await File.WriteAllTextAsync(outside, "keep");
        var board = await File.ReadAllBytesAsync(fixture.Board);
        var cli = new FakeCli { BeforeExport = _ => Assert.False(File.Exists(obsolete)) };
        var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false));
        Assert.True(report.Success, report.Error);
        Assert.False(Directory.Exists(Path.GetDirectoryName(obsolete)));
        Assert.Equal("keep", await File.ReadAllTextAsync(outside));
        var after = await File.ReadAllBytesAsync(fixture.Board);
        Assert.True(board.SequenceEqual(after));
        Assert.Equal(1, Directory.GetFiles(fixture.Manufacturing, "*", SearchOption.AllDirectories).Length);
    }

    [Test]
    public static async Task InvalidInputAndUnsupportedCliPreserveExistingManufacturing()
    {
        using var fixture = new Fixture();
        var obsolete = fixture.AddObsolete();
        foreach (var context in new[] { fixture.Context with { KicadCliPath = "" }, fixture.Context with { ProjectDirectory = "" } })
        {
            var cli = new FakeCli();
            var report = await new ProductionGenerationRunner(cli).RunAsync(context, options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false));
            Assert.False(report.Success);
            Assert.False(report.ManufacturingCleared);
            Assert.True(File.Exists(obsolete));
            Assert.Equal(0, cli.Calls.Count);
        }
        foreach (var help in new[] { new CliCommandResult(1, "", "unknown export"), new CliCommandResult(0, "--format --units --side", "") })
        {
            var report = await new ProductionGenerationRunner(new FakeCli { Help = help }).RunAsync(fixture.Context, options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false));
            Assert.False(report.Success);
            Assert.False(report.ManufacturingCleared);
            Assert.True(File.Exists(obsolete));
        }
    }

    [Test]
    public static async Task MissingOrAmbiguousMainBoardDoesNotClearManufacturing()
    {
        using var fixture = new Fixture();
        var obsolete = fixture.AddObsolete();
        var alternative = Path.Combine(fixture.DirectoryPath, "other.kicad_pcb");
        File.Move(fixture.Board, alternative);
        var report = await new ProductionGenerationRunner(new FakeCli()).RunAsync(fixture.Context, options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false));
        Assert.False(report.Success);
        Assert.True(File.Exists(obsolete));
        File.Move(alternative, fixture.Board);
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, "other.kicad_pro"), "{}");
        report = await new ProductionGenerationRunner(new FakeCli()).RunAsync(fixture.Context, options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false));
        Assert.False(report.Success);
        Assert.True(File.Exists(obsolete));
    }

    [Test]
    public static async Task FailedMissingEmptyOrMalformedOutputNeverReportsSuccess()
    {
        foreach (var mode in new[] { "exit", "missing", "empty", "malformed" })
        {
            using var fixture = new Fixture();
            var obsolete = fixture.AddObsolete();
            var report = await new ProductionGenerationRunner(new FakeCli { Mode = mode }).RunAsync(fixture.Context, options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false));
            Assert.False(report.Success);
            Assert.True(report.ManufacturingCleared);
            Assert.True(report.Error!.Contains("Previous manufacturing files were cleared"));
            Assert.Equal(0, report.Files.Count);
            Assert.False(File.Exists(fixture.Output));
            Assert.False(File.Exists(obsolete));
        }
    }

    [Test]
    public static async Task CancellationBeforeCleanupPreservesFilesAndDuringExportRemovesPartialCsv()
    {
        using var fixture = new Fixture();
        var obsolete = fixture.AddObsolete();
        using var before = new CancellationTokenSource();
        before.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ProductionGenerationRunner(new FakeCli()).RunAsync(fixture.Context, options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false), cancellationToken: before.Token));
        Assert.True(File.Exists(obsolete));
        using var during = new CancellationTokenSource();
        var cli = new FakeCli { BeforeExport = output => { File.WriteAllText(output, "partial"); during.Cancel(); } };
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false), cancellationToken: during.Token));
        Assert.False(File.Exists(fixture.Output));
    }

    [Test]
    public static async Task LinkedManufacturingOrNestedDirectoryCannotRedirectCleanup()
    {
        foreach (var nested in new[] { false, true })
        {
            using var fixture = new Fixture();
            var protectedDirectory = Path.Combine(fixture.DirectoryPath, "protected");
            Directory.CreateDirectory(protectedDirectory);
            var protectedFile = Path.Combine(protectedDirectory, "keep.txt");
            await File.WriteAllTextAsync(protectedFile, "keep");
            string? obsolete = nested ? fixture.AddObsolete() : null;
            var link = nested ? Path.Combine(fixture.Manufacturing, "linked") : fixture.Manufacturing;
            var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, protectedDirectory }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            try
            {
                var report = await new ProductionGenerationRunner(new FakeCli()).RunAsync(fixture.Context, options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false));
                Assert.False(report.Success);
                Assert.True(File.Exists(protectedFile));
                if (obsolete is not null) Assert.True(File.Exists(obsolete));
                Assert.False(File.Exists(fixture.Output));
            }
            finally { Directory.Delete(link, recursive: false); }
        }
    }

    [Test]
    public static async Task CleanupFailureStopsGeneration()
    {
        using var fixture = new Fixture();
        var obsolete = fixture.AddObsolete();
        var cli = new FakeCli();
        File.SetAttributes(obsolete, FileAttributes.ReadOnly);
        try
        {
            var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false));
            Assert.False(report.Success);
            Assert.False(report.ManufacturingCleared);
            Assert.Equal(1, cli.Calls.Count);
            Assert.False(File.Exists(fixture.Output));
        }
        finally { File.SetAttributes(obsolete, FileAttributes.Normal); }
    }

    private sealed class FakeCli : ICliCommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public CliCommandResult Help { get; init; } = new(0, "--format --units --side --use-drill-file-origin", "");
        public string Mode { get; init; } = "success";
        public Action<string>? BeforeExport { get; init; }
        public async Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            if (arguments.Contains("--help")) return Help;
            var output = arguments[arguments.ToList().IndexOf("--output") + 1];
            BeforeExport?.Invoke(output);
            cancellationToken.ThrowIfCancellationRequested();
            if (Mode != "missing") await File.WriteAllTextAsync(output, Mode switch
            {
                "empty" => "", "malformed" => "not a CSV", "exit" => "partial",
                _ => "Ref,Val,Package,PosX,PosY,Rot,Side\n\"R1\",\"10k\",\"0402\",1,2,0,top\n"
            }, cancellationToken);
            return new(Mode == "exit" ? 1 : 0, "", Mode == "exit" ? "Board could not be loaded" : "");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OepsPlacementTests"));
        public string DirectoryPath { get; } = Path.Combine(Root, Guid.NewGuid().ToString("N"), "project with spaces");
        public string Board => Path.Combine(DirectoryPath, "my board.kicad_pcb");
        public string Manufacturing => Path.Combine(DirectoryPath, "manufacturing");
        public string Output => Path.Combine(Manufacturing, "assembly", "my board-pos.csv");
        public CheckContext Context => new(Path.Combine(DirectoryPath, "kicad-cli.exe"), DirectoryPath);
        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Board, "(kicad_pcb)");
            File.WriteAllText(Path.ChangeExtension(Board, ".kicad_pro"), "{}");
            File.WriteAllText(Context.KicadCliPath, "fake executable for injected runner");
        }
        public string AddObsolete()
        {
            var path = Path.Combine(Manufacturing, "old", "nested", "previous.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "old");
            return path;
        }
        public void Dispose()
        {
            var target = Path.GetFullPath(Path.GetDirectoryName(DirectoryPath)!);
            if (!target.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cleanup must stay inside the test directory.");
            Directory.Delete(target, recursive: true);
        }
    }
}
