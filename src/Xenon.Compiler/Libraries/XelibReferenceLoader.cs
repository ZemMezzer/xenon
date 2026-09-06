using System.Collections.Immutable;

namespace Xenon.Compiler.Libraries;

public static class XelibReferenceLoader
{
    public static ImmutableArray<LibraryCompilationReference> LoadFiles(IEnumerable<string> paths,
        bool metadataOnly = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] orderedPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (orderedPaths.Length == 0) return [];

        var metadata = orderedPaths.Select(path => (Path: path, Metadata: XelibMetadataReader.ReadFile(path))).ToArray();
        string[] duplicateIdentities = metadata.GroupBy(item => item.Metadata.Manifest.ContentIdentity,
                StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicateIdentities.Length != 0)
            metadata = metadata.GroupBy(item => item.Metadata.Manifest.ContentIdentity, StringComparer.Ordinal)
                .Select(group => group.First()).ToArray();
        foreach (IGrouping<(string Name, string? Version), (string Path, XelibMetadata Metadata)> group in
            metadata.GroupBy(item => (item.Metadata.Manifest.Name, item.Metadata.Manifest.Version)))
            if (group.Select(item => item.Metadata.Manifest.ContentIdentity).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new XelibFormatException(XelibErrorCode.DuplicateLibraryIdentity,
                    $"multiple incompatible XELIB artifacts declare '{group.Key.Name}' version '{group.Key.Version}'");

        var byIdentity = metadata.ToDictionary(item => item.Metadata.Manifest.ContentIdentity,
            StringComparer.Ordinal);
        var loaded = new Dictionary<string, LibraryCompilationReference>(StringComparer.Ordinal);
        var visiting = new List<string>();
        LibraryCompilationReference Load(string identity)
        {
            if (loaded.TryGetValue(identity, out LibraryCompilationReference? existing)) return existing;
            int cycle = visiting.IndexOf(identity);
            if (cycle >= 0)
                throw new XelibFormatException(XelibErrorCode.DependencyCycle,
                    $"XELIB dependency cycle: {string.Join(" -> ", visiting.Skip(cycle).Append(identity))}");
            if (!byIdentity.TryGetValue(identity, out var item))
                throw new XelibFormatException(XelibErrorCode.DependencyMissing,
                    $"required XELIB dependency '{identity}' was not explicitly supplied");
            visiting.Add(identity);
            LibraryCompilationReference[] dependencies = item.Metadata.Dependencies
                .Select(dependency => Load(dependency.ContentIdentity)).ToArray();
            LibraryCompilationReference created = XelibReader.ReadFile(item.Path, dependencies, metadataOnly);
            visiting.RemoveAt(visiting.Count - 1);
            loaded.Add(identity, created);
            return created;
        }
        return metadata.Select(item => Load(item.Metadata.Manifest.ContentIdentity)).Distinct().ToImmutableArray();
    }
}
