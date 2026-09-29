namespace F76ManagerApp.Managers;

public partial class ModManager
{
    private static readonly HashSet<string> ModsBackupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ba2", ".esm", ".esp", ".strings", ".dlstrings", ".ilstrings", ".dll"
    };

    public List<(string SourcePath, string EntryName)> CollectModsBackupEntries()
    {
        var entries = new List<(string SourcePath, string EntryName)>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? sourcePath, string? entryName)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(entryName)) return;
            if (!File.Exists(sourcePath)) return;

            string full;
            try { full = Path.GetFullPath(sourcePath); }
            catch { return; }

            if (!seenSources.Add(full)) return;

            string ext = Path.GetExtension(full);
            if (!ModsBackupExtensions.Contains(ext)) return;

            entries.Add((full, entryName.Replace('\\', '/')));
        }

        var metadata = LoadMetadata();
        foreach (var kvp in metadata)
        {
            if (AppPaths.IsProtectedCoreIniListKey(kvp.Key)) continue;

            var fileKeys = kvp.Value.Files ?? new List<string>();
            if (fileKeys.Count == 0)
            {
                string key = (kvp.Key ?? "").Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(key) && !key.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase))
                    fileKeys = new List<string> { key };
            }

            foreach (string rel in fileKeys)
            {
                string normalized = (rel ?? "").Replace('\\', '/').Trim();
                if (string.IsNullOrWhiteSpace(normalized)) continue;
                if (normalized.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase)) continue;

                string? entryName = MapRelativeKeyToArchiveEntry(normalized);
                if (entryName == null) continue;

                string fullPath = GetFullPath(normalized);
                TryAdd(fullPath, entryName);
            }
        }

        AppendDirectoryModFiles(AppPaths.BundlesPath, "Bundles", TryAdd, recursive: true);
        AppendDirectoryModFiles(AppPaths.DisabledModsPath, "Disabled Mods", TryAdd, recursive: true);

        return entries;
    }

    public List<(string SourcePath, string EntryName)> CollectModsBackupEntriesForListKeys(IEnumerable<string> listKeys)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in listKeys ?? Array.Empty<string>())
        {
            string key = (raw ?? "").Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;
            wanted.Add(key);
            if (key.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
                wanted.Add(key.Substring("Disabled/".Length));
            else if (!key.Contains('/'))
                wanted.Add("Disabled/" + key);
        }

        if (wanted.Count == 0)
            return new List<(string, string)>();

        var entries = new List<(string SourcePath, string EntryName)>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? sourcePath, string? entryName)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(entryName)) return;
            if (!File.Exists(sourcePath)) return;

            string full;
            try { full = Path.GetFullPath(sourcePath); }
            catch { return; }

            if (!seenSources.Add(full)) return;

            string ext = Path.GetExtension(full);
            if (!ModsBackupExtensions.Contains(ext)) return;

            entries.Add((full, entryName.Replace('\\', '/')));
        }

        bool MatchesWanted(string metaKey)
        {
            string k = (metaKey ?? "").Replace('\\', '/');
            if (wanted.Contains(k)) return true;
            if (k.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase) &&
                wanted.Contains(k.Substring("Disabled/".Length)))
                return true;
            if (!k.Contains('/') && wanted.Contains("Disabled/" + k))
                return true;
            if (k.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase))
            {
                if (wanted.Contains(k)) return true;
            }
            return false;
        }

        var metadata = LoadMetadata();
        foreach (var kvp in metadata)
        {
            if (AppPaths.IsProtectedCoreIniListKey(kvp.Key)) continue;
            if (!MatchesWanted(kvp.Key)) continue;

            var fileKeys = kvp.Value.Files ?? new List<string>();
            if (fileKeys.Count == 0)
            {
                string key = (kvp.Key ?? "").Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(key) && !key.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase))
                    fileKeys = new List<string> { key };
            }

            foreach (string rel in fileKeys)
            {
                string normalized = (rel ?? "").Replace('\\', '/').Trim();
                if (string.IsNullOrWhiteSpace(normalized)) continue;
                if (normalized.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase)) continue;

                string? entryName = MapRelativeKeyToArchiveEntry(normalized);
                if (entryName == null) continue;

                string fullPath = GetFullPath(normalized);
                TryAdd(fullPath, entryName);
            }
        }

        foreach (var raw in wanted.ToList())
        {
            string? full = ResolveListKeyToFullPath(raw);
            if (string.IsNullOrWhiteSpace(full) || !File.Exists(full)) continue;
            string? entry = MapRelativeKeyToArchiveEntry(raw.Replace('\\', '/'));
            if (entry != null) TryAdd(full, entry);
        }

        return entries;
    }

    public Dictionary<string, ModMetadata> GetMetadataSubsetForListKeys(IEnumerable<string> listKeys)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in listKeys ?? Array.Empty<string>())
        {
            string key = (raw ?? "").Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;
            wanted.Add(key);
            if (key.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
                wanted.Add(key.Substring("Disabled/".Length));
            else if (!key.Contains('/'))
                wanted.Add("Disabled/" + key);
        }

        var result = new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
        var metadata = LoadMetadata();
        foreach (var kvp in metadata)
        {
            string k = (kvp.Key ?? "").Replace('\\', '/');
            bool match = wanted.Contains(k)
                || (k.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase) && wanted.Contains(k.Substring("Disabled/".Length)))
                || (!k.Contains('/') && wanted.Contains("Disabled/" + k));
            if (!match) continue;
            result[kvp.Key] = kvp.Value ?? new ModMetadata();
        }
        return result;
    }

    public void MergeMetadataSubset(Dictionary<string, ModMetadata> incoming)
    {
        if (incoming == null || incoming.Count == 0) return;
        var metadata = LoadMetadata();
        bool changed = false;
        foreach (var kvp in incoming)
        {
            string key = ResolveMetadataKey(metadata, NormalizeMetadataKey(kvp.Key));
            if (string.IsNullOrEmpty(key)) key = NormalizeMetadataKey(kvp.Key);
            if (string.IsNullOrEmpty(key)) continue;

            if (metadata.TryGetValue(key, out var existing) && existing != null)
                metadata[key] = MergeMetadataEntries(existing, kvp.Value);
            else
                metadata[key] = MergeMetadataEntries(new ModMetadata(), kvp.Value);
            changed = true;
        }
        if (changed) SaveMetadata(metadata);
    }

    private static string? MapRelativeKeyToArchiveEntry(string normalizedKey)
    {
        if (normalizedKey.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase))
            return normalizedKey;

        if (normalizedKey.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(normalizedKey.Substring("Disabled/GameRoot/".Length));
            return string.IsNullOrEmpty(fn) ? null : $"Disabled Mods/{fn}";
        }

        if (normalizedKey.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(normalizedKey.Substring("GameRoot/".Length));
            return string.IsNullOrEmpty(fn) ? null : $"GameInstall/{fn}";
        }

        if (normalizedKey.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
            return $"Disabled Mods/{normalizedKey.Substring("Disabled/".Length)}";

        if (normalizedKey.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase))
            return $"Data/{normalizedKey}";

        return $"Data/{normalizedKey}";
    }

    private static void AppendDirectoryModFiles(
        string? directory,
        string archiveRoot,
        Action<string?, string?> tryAdd,
        bool recursive)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.*", searchOption);
        }
        catch
        {
            return;
        }

        foreach (string filePath in files)
        {
            if (!ModsBackupExtensions.Contains(Path.GetExtension(filePath))) continue;

            string relative = Path.GetRelativePath(directory, filePath).Replace('\\', '/');
            tryAdd(filePath, $"{archiveRoot}/{relative}");
        }
    }
}
