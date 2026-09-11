using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.GenerateProductionFiles;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Tests;

public static class ProductionGenerationTests
{
    [Test]
    public static void DefaultsSelectCleanupAndAllExports()
    {
        var options = new ProductionGenerationOptions();
        Assert.True(options.DeleteExistingFiles && options.GenerateGerbers && options.GeneratePlacements && options.GenerateDrills && options.GenerateIpcD356);
    }

    [Test]
    public static async Task EverySelectionAlwaysGeneratesBomFirstThenOnlySelectedExports()
    {
        for (var flags = 0; flags < 16; flags++)
        {
            using var fixture = new Fixture();
            var keep = fixture.WriteExisting("keep.txt", "old");
            var cli = new FakeCli();
            var options = new ProductionGenerationOptions(GenerateGerbers: (flags & 1) != 0,
                GeneratePlacements: (flags & 2) != 0, GenerateDrills: (flags & 4) != 0, GenerateIpcD356: (flags & 8) != 0);
            var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: options);
            Assert.True(report.Success, report.Error);
            Assert.True(report.ManufacturingCleared);
            Assert.False(File.Exists(keep));
            var expected = new List<string> { "bom" };
            if (options.GenerateGerbers) expected.Add("gerbers");
            if (options.GeneratePlacements) expected.Add("pos");
            if (options.GenerateDrills) expected.Add("drill");
            if (options.GenerateIpcD356) expected.Add("ipcd356");
            Assert.True(expected.SequenceEqual(cli.Calls.Where(args => args.Contains("--help")).Select(args => args[2])));
            Assert.True(expected.SequenceEqual(cli.Calls.Where(args => !args.Contains("--help")).Select(args => args[2])));
            Assert.True(cli.Calls.Take(expected.Count).All(args => args.Contains("--help")));
            Assert.Equal(expected.Count, report.Files.Count);
            Assert.Equal(options.GeneratePlacements, report.Comparison is not null);
            if (report.Comparison is { } comparison) Assert.True(comparison.Matches);
            foreach (var file in report.Files.SelectMany(group => group.RelativePaths))
                Assert.True(File.Exists(Path.Combine(fixture.DirectoryPath, file)));
        }
    }

    [Test]
    public static async Task CleanupDisabledKeepsUnrelatedFilesAndReplacesSelectedOutputs()
    {
        using var fixture = new Fixture();
        var unrelated = fixture.WriteExisting("other/nested/keep.txt", "preserve");
        var stale = fixture.WriteExisting("gerber/old-layer.gbr", "old layer");
        var matching = fixture.WriteExisting("gerber/my board-F_Cu.gbr", "old output");
        var boardBefore = await File.ReadAllBytesAsync(fixture.Board);
        var report = await new ProductionGenerationRunner(new FakeCli()).RunAsync(fixture.Context,
            options: new(DeleteExistingFiles: false));
        Assert.True(report.Success, report.Error);
        Assert.False(report.ManufacturingCleared);
        Assert.Equal("preserve", await File.ReadAllTextAsync(unrelated));
        Assert.Equal("old layer", await File.ReadAllTextAsync(stale));
        Assert.Equal(Gerber, await File.ReadAllTextAsync(matching));
        Assert.False(report.Files.SelectMany(file => file.RelativePaths).Any(path => path.Contains("old-layer")));
        var boardAfter = await File.ReadAllBytesAsync(fixture.Board);
        Assert.True(boardBefore.SequenceEqual(boardAfter));
        Assert.Equal(0, Directory.GetDirectories(fixture.Manufacturing, ".oeps-export-*", SearchOption.AllDirectories).Length);
    }

    [Test]
    public static async Task UnsupportedSelectedExportStopsBeforeCleanupOrAnyExport()
    {
        using var fixture = new Fixture();
        var keep = fixture.WriteExisting("keep.txt", "keep");
        var cli = new FakeCli { Unsupported = "drill" };
        var result = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context);
        Assert.False(result.Success);
        Assert.False(result.ManufacturingCleared);
        Assert.True(File.Exists(keep));
        Assert.True(cli.Calls.All(args => args.Contains("--help")));
        var placementOnly = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context,
            options: new(GenerateGerbers: false, GenerateDrills: false, GenerateIpcD356: false));
        Assert.True(placementOnly.Success, placementOnly.Error);
    }

    [Test]
    public static async Task GerberExportUsesBoardPlotOptionsAndDiscardsUnrequestedJobFile()
    {
        foreach (var includeJob in new[] { false, true })
        {
            using var fixture = new Fixture();
            if (includeJob) await File.WriteAllTextAsync(fixture.Board, GerberPlotSettingsTests.GoodBoard.Replace("(creategerberjobfile no)", "(creategerberjobfile yes)"));
            var cli = new FakeCli();
            var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: Only("gerbers"));
            Assert.True(report.Success, report.Error);
            var args = cli.Calls.Single(args => args[2] == "gerbers" && !args.Contains("--help"));
            Assert.True(args.SequenceEqual(new[] { "pcb", "export", "gerbers", "--board-plot-params", "--check-zones", "--output", Value(args, "--output"), fixture.Board }));
            Assert.Equal(includeJob ? 3 : 2, report.Files.Single(file => file.Name == "Gerber files").RelativePaths.Count);
            Assert.Equal(includeJob, File.Exists(Path.Combine(fixture.Manufacturing, "gerber", "my board-job.gbrjob")));
        }
    }

    [Test]
    public static async Task DrillArgumentsMatchScreenshotAndMapsAreReported()
    {
        using var fixture = new Fixture();
        var cli = new FakeCli();
        var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: Only("drill"));
        Assert.True(report.Success, report.Error);
        var args = cli.Calls.Single(args => args[2] == "drill" && !args.Contains("--help"));
        Assert.True(args.SequenceEqual(new[] { "pcb", "export", "drill", "--output", Value(args, "--output"),
            "--format", "excellon", "--drill-origin", "plot", "--excellon-zeros-format", "decimal", "--excellon-oval-format", "route",
            "--excellon-units", "in", "--excellon-separate-th", "--generate-map", "--map-format", "gerberx2", fixture.Board }));
        Assert.False(args.Any(arg => arg is "--excellon-mirror-y" or "--excellon-min-header" or "--generate-tenting"));
        Assert.Equal(4, report.Files.Single(file => file.Name == "Drill files").RelativePaths.Count);
        Assert.True(report.Files.Single(file => file.Name == "Drill files").RelativePaths.All(path => path.StartsWith(Path.Combine("manufacturing", "gerber"))));
    }

    [Test]
    public static async Task FailedEmptyMissingAndInvalidExportsCannotReplacePreviousFiles()
    {
        foreach (var command in new[] { "gerbers", "pos", "drill", "ipcd356" })
        foreach (var mode in new[] { "exit", "missing", "empty", "malformed" })
        {
            using var fixture = new Fixture();
            var previous = fixture.WriteExisting(command switch
            { "pos" => "assembly/my board-pos.csv", "gerbers" => "gerber/my board-F_Cu.gbr", "ipcd356" => "my board.d356", _ => "gerber/my board-PTH.drl" }, "previous");
            var report = await new ProductionGenerationRunner(new FakeCli { Mode = mode }).RunAsync(fixture.Context,
                options: Only(command) with { DeleteExistingFiles = false });
            Assert.False(report.Success, command + " " + mode);
            Assert.Equal("BOM", report.Files.Single().Name);
            Assert.False(report.ManufacturingCleared);
            Assert.Equal("previous", await File.ReadAllTextAsync(previous));
            Assert.Equal(2, Directory.GetFiles(fixture.Manufacturing, "*", SearchOption.AllDirectories).Length);
        }
    }

    [Test]
    public static async Task MissingDrillMapsAndWrongUnitsOrZeroFormatAreRejected()
    {
        foreach (var mode in new[] { "missing-map", "missing-npth", "metric", "nondecimal" })
        {
            using var fixture = new Fixture();
            var report = await new ProductionGenerationRunner(new FakeCli { Mode = mode }).RunAsync(fixture.Context, options: Only("drill"));
            Assert.False(report.Success, mode);
            Assert.Equal("BOM", report.Files.Single().Name);
            Assert.Equal(1, Directory.GetFiles(fixture.Manufacturing, "*", SearchOption.AllDirectories).Length);
        }
    }

    [Test]
    public static async Task CancellationDuringExportRemovesTemporaryOutputAndKeepsExistingFiles()
    {
        foreach (var command in new[] { "gerbers", "pos", "drill", "ipcd356" })
        {
            using var fixture = new Fixture();
            var keep = fixture.WriteExisting("keep.txt", "keep");
            using var cancellation = new CancellationTokenSource();
            var cli = new FakeCli { AfterWrite = () => cancellation.Cancel() };
            await Assert.ThrowsAsync<OperationCanceledException>(() => new ProductionGenerationRunner(cli).RunAsync(fixture.Context,
                cancellationToken: cancellation.Token, options: Only(command) with { DeleteExistingFiles = false }));
            Assert.Equal("keep", await File.ReadAllTextAsync(keep));
            Assert.Equal(2, Directory.GetFiles(fixture.Manufacturing, "*", SearchOption.AllDirectories).Length);
        }
    }

    [Test]
    public static async Task MalformedBoardStopsGerbersBeforeCleanup()
    {
        using var fixture = new Fixture();
        var keep = fixture.WriteExisting("keep.txt", "keep");
        await File.WriteAllTextAsync(fixture.Board, "(kicad_pcb");
        var result = await new ProductionGenerationRunner(new FakeCli()).RunAsync(fixture.Context, options: Only("gerbers"));
        Assert.False(result.Success);
        Assert.False(result.ManufacturingCleared);
        Assert.True(File.Exists(keep));
    }

    [Test]
    public static async Task IpcD356UsesItsOwnCommandAndManufacturingRoot()
    {
        using var fixture = new Fixture();
        var cli = new FakeCli();
        var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context, options: Only("ipcd356"));
        Assert.True(report.Success, report.Error);
        var args = cli.Calls.Single(args => args[2] == "ipcd356" && !args.Contains("--help"));
        Assert.True(args.SequenceEqual(new[] { "pcb", "export", "ipcd356", "--output", Value(args, "--output"), fixture.Board }));
        Assert.Equal("my board.d356", Path.GetFileName(Value(args, "--output")));
        Assert.Equal(Path.Combine("manufacturing", "my board.d356"), report.Files.Single(file => file.Name == "IPC-D-356 netlist").RelativePath);
        Assert.True(File.Exists(Path.Combine(fixture.Manufacturing, "my board.d356")));
    }

    [Test]
    public static async Task UnsupportedIpcD356StopsBeforeCleanup()
    {
        using var fixture = new Fixture();
        var keep = fixture.WriteExisting("keep.txt", "keep");
        var cli = new FakeCli { Unsupported = "ipcd356" };
        var report = await new ProductionGenerationRunner(cli).RunAsync(fixture.Context);
        Assert.False(report.Success);
        Assert.False(report.ManufacturingCleared);
        Assert.True(cli.Calls.All(args => args.Contains("--help")));
        Assert.Equal("keep", await File.ReadAllTextAsync(keep));
    }

    private static ProductionGenerationOptions Only(string command) => new(GenerateGerbers: command == "gerbers",
        GeneratePlacements: command == "pos", GenerateDrills: command == "drill", GenerateIpcD356: command == "ipcd356");
    private static string Value(string[] args, string flag) => args[Array.IndexOf(args, flag) + 1];
    private const string Gerber = "G04 test*\n%FSLAX46Y46*%\n%MOMM*%\nM02*\n";
    private const string Drill = "M48\n; FORMAT={-:-/ absolute / inch / decimal}\nINCH\n%\nG90\nM30\n";

    private sealed class FakeCli : ICliCommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public string? Unsupported { get; init; }
        public string Mode { get; init; } = "success";
        public Action? AfterWrite { get; init; }
        public async Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToArray();
            Calls.Add(args);
            var command = args[2];
            if (args.Contains("--help")) return new(0, command == Unsupported ? "" :
                command == "bom" ? BomGenerationTests.Help :
                "--format --units --side --use-drill-file-origin --output --board-plot-params --check-zones --drill-origin " +
                "--excellon-zeros-format --excellon-oval-format --excellon-units --excellon-separate-th --generate-map --map-format", "");
            var output = Value(args, "--output");
            var stem = Path.GetFileNameWithoutExtension(args[^1]);
            var files = new Dictionary<string, string>();
            if (command == "bom")
            {
                await File.WriteAllTextAsync(output, "Refs,Qty,Value\nR1,1,10k\n", cancellationToken);
                return new(0, "", "");
            }
            if (command == "ipcd356") files[output] = "P  CODE 00\nP  UNITS CUST 0\n999\n";
            if (command == "pos") files[output] = "Ref,Val,Package,PosX,PosY,Rot,Side\nR1,10k,0402,1,2,0,top\n";
            if (command == "gerbers")
            {
                files[Path.Combine(output, stem + "-F_Cu.gbr")] = Gerber;
                files[Path.Combine(output, stem + "-B_Cu.gbr")] = Gerber;
                files[Path.Combine(output, stem + "-job.gbrjob")] = "{}";
            }
            if (command == "drill")
            {
                foreach (var type in new[] { "PTH", "NPTH" })
                {
                    if (Mode != "missing-npth" || type != "NPTH")
                        files[Path.Combine(output, stem + "-" + type + ".drl")] = Mode == "metric" ? Drill.Replace("INCH", "METRIC") :
                            Mode == "nondecimal" ? Drill.Replace("decimal", "suppressleading") : Drill;
                    if (Mode != "missing-map") files[Path.Combine(output, stem + "-" + type + "-drl_map.gbr")] = Gerber;
                }
            }
            if (Mode != "missing") foreach (var (file, content) in files)
                await File.WriteAllTextAsync(file, Mode == "empty" ? "" : Mode is "malformed" or "exit" ? "partial" : content, cancellationToken);
            AfterWrite?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return new(Mode == "exit" ? 1 : 0, "", Mode == "exit" ? "export failed" : "");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OepsProductionGenerationTests"));
        public string DirectoryPath { get; } = Path.Combine(Root, Guid.NewGuid().ToString("N"), "board with spaces");
        public string Board => Path.Combine(DirectoryPath, "my board.kicad_pcb");
        public string Manufacturing => Path.Combine(DirectoryPath, "manufacturing");
        public CheckContext Context => new(Path.Combine(DirectoryPath, "kicad-cli.exe"), DirectoryPath);
        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Board, GerberPlotSettingsTests.GoodBoard);
            File.WriteAllText(Path.ChangeExtension(Board, ".kicad_pro"), BomGenerationTests.ProjectJson);
            File.WriteAllText(Path.ChangeExtension(Board, ".kicad_sch"), "(kicad_sch)");
            File.WriteAllText(Context.KicadCliPath, "fake CLI for tests");
        }
        public string WriteExisting(string relative, string text)
        {
            var path = Path.Combine(Manufacturing, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }
        public void Dispose()
        {
            var target = Path.GetFullPath(Path.GetDirectoryName(DirectoryPath)!);
            if (!target.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cleanup must stay inside the fixture root.");
            Directory.Delete(target, recursive: true);
        }
    }
}
