using System.Text;
using System.Text.Json.Nodes;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.CheckProductionFiles;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.ConfigureFiles;

/// <summary>Prepares description-only edits for BOM components with verified database PN/MPN pairs.</summary>
public sealed class OepsDescriptionFix(ICliCommandRunner? cli = null) : IConfigurationFix
{
    public string CheckName => new SchematicBomIdentifiersCheck().Name;
    public string Description => "Fill or update OEPS Description from the database only for matching OEPS PN/MPN pairs. Keep identifiers unchanged.";
    internal sealed record Plan(IReadOnlyList<ConfigurationFileEdit> Edits, bool HasChanges, string Summary);
    private sealed record Snapshot(string Path, byte[] Bytes, SchematicDescriptionDocument Document, List<string> InstancePaths);
    private sealed record Desired(string Pn, string Mpn, string Description);

    internal async Task<Plan> PrepareAsync(CheckContext context, CancellationToken token)
    {
        var database = context.Database.ToArray();
        var project = await ProjectConfigurationStore.LoadAsync(context.ProjectDirectory, token).ConfigureAwait(false);
        var rootDirectory = Path.GetDirectoryName(project.ProjectFilePath)!;
        var snapshots = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visits = 0;
        async Task Visit(string file, string? instancePath)
        {
            token.ThrowIfCancellationRequested();
            file = Path.GetFullPath(file);
            EnsureLocalPath(rootDirectory, file);
            if (!active.Add(file)) throw new InvalidDataException("The schematic hierarchy contains a cycle.");
            if (++visits > 2000) throw new InvalidDataException("The schematic hierarchy exceeds the supported size.");
            if (!snapshots.TryGetValue(file, out var snapshot))
            {
                if (new FileInfo(file).Length > 20 * 1024 * 1024) throw new InvalidDataException("A schematic exceeds the 20 MiB limit.");
                var bytes = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
                if (bytes.Length > 20 * 1024 * 1024) throw new InvalidDataException("A schematic exceeds the 20 MiB limit.");
                snapshot = new(file, bytes, new(new UTF8Encoding(false, true).GetString(bytes)), []);
                snapshots.Add(file, snapshot);
            }
            var path = instancePath ?? "/" + snapshot.Document.Uuid;
            snapshot.InstancePaths.Add(path);
            foreach (var sheet in snapshot.Document.Sheets)
            {
                var child = sheet.File.Replace("${KIPRJMOD}", rootDirectory, StringComparison.Ordinal);
                if (child.Contains("${", StringComparison.Ordinal)) throw new InvalidDataException("Resolve sheet filename variables in KiCad before applying the description fix.");
                await Visit(Path.Combine(Path.GetDirectoryName(file)!, child), path + "/" + sheet.Uuid).ConfigureAwait(false);
            }
            active.Remove(file);
        }
        await Visit(Path.ChangeExtension(project.ProjectFilePath, ".kicad_sch"), null).ConfigureAwait(false);
        // Export after capturing the hierarchy. All captured inputs are guarded again when saving approved edits.
        var bom = await new SchematicBomIdentifiersCheck(cli).ExportBomAsync(context, token).ConfigureAwait(false);
        var pairings = database.ToLookup(component => (component.OepsPn.Trim(), component.Mpn.Trim()));
        var desired = new Dictionary<string, Desired>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in bom.Components.GroupBy(component => component.Reference, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var component = group.First();
            var identifiers = IdentifierFields.Read(component);
            if (group.Count() != 1 || identifiers.Issues.Count != 0 || identifiers.OepsPn is not { } pn || identifiers.Mpn is not { } mpn)
            { skipped.Add($"{group.Key}: identifiers or reference are missing/ambiguous."); continue; }
            var matches = pairings[(pn, mpn)].ToArray();
            if (matches.Length == 0) { skipped.Add($"{group.Key}: OEPS PN/MPN do not match a database pair."); continue; }
            var descriptions = matches.Select(match => match.Description?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToArray();
            if (descriptions.Length != 1 || descriptions[0].Length == 0 || descriptions[0].Any(char.IsControl)
                || descriptions[0].Contains("${", StringComparison.Ordinal))
            { skipped.Add($"{group.Key}: the database description is missing, ambiguous or invalid."); continue; }
            desired.Add(group.Key, new(pn, mpn, descriptions[0]));
        }

        var candidates = snapshots.Values.SelectMany(snapshot => snapshot.Document.Symbols.Where(symbol => symbol.InBom)
            .Select(symbol => (Snapshot: snapshot, Symbol: symbol,
                References: snapshot.InstancePaths.Select(path => symbol.Instances.Where(instance => instance.Path == path)
                    .Select(instance => instance.Reference).Distinct(StringComparer.Ordinal).ToArray()).ToArray()))).ToArray();
        var ambiguous = candidates.SelectMany(candidate => candidate.References.SelectMany(refs => refs)
                .Select(reference => (Reference: reference, candidate.Symbol.Unit)))
            .GroupBy(item => item).Where(group => group.Count() > 1).Select(group => group.Key.Reference).ToHashSet(StringComparer.Ordinal);
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        var changes = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var changedReferences = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var refs = candidate.References.SelectMany(refs => refs).Distinct(StringComparer.Ordinal).ToArray();
            if (!refs.Any(desired.ContainsKey)) continue;
            if (candidate.References.Any(refs => refs.Length != 1) || refs.Any(reference => !desired.ContainsKey(reference) || ambiguous.Contains(reference)))
            { skipped.Add(string.Join(", ", refs) + ": cannot safely map all symbol instances to the exported BOM."); continue; }
            var values = refs.Select(reference => desired[reference]).Distinct().ToArray();
            var fields = IdentifierFields.Read(new(refs[0], candidate.Symbol.Fields));
            if (values.Length != 1 || fields.Issues.Count != 0 || fields.OepsPn != values[0].Pn || fields.Mpn != values[0].Mpn)
            { skipped.Add(string.Join(", ", refs) + ": saved identifiers do not unambiguously match the CLI BOM; correct them in KiCad first."); continue; }
            foreach (var reference in refs) mapped.Add(reference);
            var expected = values[0].Description;
            if (candidate.Symbol.Fields.GetValueOrDefault("OEPS Description", "").Trim() == expected) continue;
            if (!changes.TryGetValue(candidate.Snapshot.Path, out var symbols)) changes.Add(candidate.Snapshot.Path, symbols = []);
            symbols.Add(candidate.Symbol.Uuid, expected);
            foreach (var reference in refs) changedReferences[reference] = expected;
        }
        foreach (var reference in desired.Keys.Where(reference => !mapped.Contains(reference)))
            skipped.Add(reference + ": no unambiguous editable schematic symbol was found.");

        var edits = new List<ConfigurationFileEdit> { new(project.ProjectFilePath, project.OriginalBytes, project.OriginalBytes) };
        foreach (var snapshot in snapshots.Values)
        {
            var modified = changes.TryGetValue(snapshot.Path, out var symbols)
                ? Encoding.UTF8.GetBytes(snapshot.Document.WithDescriptions(symbols)) : snapshot.Bytes;
            edits.Add(new(snapshot.Path, snapshot.Bytes, modified));
        }
        var summary = new StringBuilder();
        summary.AppendLine(changes.Count == 0 ? "No descriptions can be updated safely."
            : $"Update OEPS Description for {changedReferences.Count} component(s) in {changes.Count} schematic file(s):");
        foreach (var group in changedReferences.GroupBy(pair => pair.Value).OrderBy(group => group.Key, StringComparer.Ordinal))
            summary.AppendLine(string.Join(", ", group.Select(pair => pair.Key).Order(StringComparer.Ordinal)) + ": " + group.Key);
        if (skipped.Count > 0) summary.AppendLine("Unchanged:").AppendLine(string.Join("\n", skipped.Order(StringComparer.Ordinal)));
        if (changes.Count > 0) summary.Append("OEPS PN and MPN stay unchanged. Originals will be backed up. Other failures may remain after this fix.");
        return new(edits, changes.Count > 0, summary.ToString().TrimEnd());
    }

    private static void EnsureLocalPath(string root, string file)
    {
        if (!file.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(file).Equals(".kicad_sch", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The description fix supports schematic sheets inside the selected project folder only.");
        for (var path = file; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The description fix cannot follow linked files or folders.");
    }

    void IConfigurationFix.Apply(JsonObject project) => throw new NotSupportedException("OEPS descriptions are stored in schematic files.");
}
