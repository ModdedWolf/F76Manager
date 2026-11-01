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
        var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            string normalizedEntry = entryName.Replace('\\', '/');
            if (!seenEntries.Add(normalizedEntry)) return;

            entries.Add((full, normalizedEntry));
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

        foreach (var bundleDir in AppPaths.CreationBundleDirectories())
            AppendDirectoryModFiles(bundleDir, "Bundles", TryAdd, recursive: true);
        AppendDirectoryModFiles(AppPaths.DisabledModsPath, "Disabled Mods", TryAdd, recursive: true);

        return entries;
    }

    private static HashSet<string> BuildWantedListKeys(IEnumerable<string> listKeys)
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
        return wanted;
    }

    /// <summary>
    /// Metadata keys for the wanted list keys, resolved the same way the mod list resolves them.
    /// </summary>
    private HashSet<string> ResolveExportMetadataKeys(Dictionary<string, ModMetadata> metadata, HashSet<string> wanted)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in metadata.Keys)
        {
            string n = (k ?? "").Replace('\\', '/');
            if (wanted.Contains(n)
                || (n.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase) && wanted.Contains(n.Substring("Disabled/".Length)))
                || (!n.Contains('/') && wanted.Contains("Disabled/" + n)))
                keys.Add(k);
        }
        foreach (var w in wanted)
        {
            string resolved = ResolveMetadataKey(metadata, w);
            if (!string.IsNullOrEmpty(resolved)) keys.Add(resolved);
        }
        keys.RemoveWhere(k => AppPaths.IsProtectedCoreIniListKey(k));
        return keys;
    }

    public List<(string SourcePath, string EntryName)> CollectModsBackupEntriesForListKeys(IEnumerable<string> listKeys)
    {
        var wanted = BuildWantedListKeys(listKeys);
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

            if (IsVanillaFile(Path.GetFileName(full)))
            {
                _logger($"[PRESETS] Not packing vanilla game file '{Path.GetFileName(full)}'.");
                return;
            }

            if (!seenSources.Add(full)) return;
            entries.Add((full, entryName.Replace('\\', '/')));
        }

        void TryAddListFile(string normalized)
        {
            string? entryName = MapRelativeKeyToArchiveEntry(normalized);
            if (entryName == null) return;

            string fullPath = GetFullPath(normalized);
            if (!File.Exists(fullPath) && IsPlainDataRelativeKey(normalized))
            {
                string disabledPath = Path.Combine(AppPaths.DisabledModsPath, normalized.Replace('/', '\\'));
                if (File.Exists(disabledPath))
                {
                    fullPath = disabledPath;
                    entryName = $"Disabled Mods/{normalized}";
                }
            }
            TryAdd(fullPath, entryName);
        }

        var metadata = LoadMetadata();
        foreach (var metaKey in ResolveExportMetadataKeys(metadata, wanted))
        {
            if (!metadata.TryGetValue(metaKey, out var meta) || meta == null) continue;

            var fileKeys = meta.Files ?? new List<string>();
            if (fileKeys.Count == 0)
            {
                string key = (metaKey ?? "").Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(key) && !key.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase))
                    fileKeys = new List<string> { key };
            }

            foreach (string rel in fileKeys)
            {
                string normalized = (rel ?? "").Replace('\\', '/').Trim();
                if (string.IsNullOrWhiteSpace(normalized)) continue;
                if (normalized.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase)) continue;
                TryAddListFile(normalized);
            }
        }

        foreach (var raw in wanted.ToList())
        {
            string normalized = raw.Replace('\\', '/');
            if (normalized.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase))
                continue;
            TryAddListFile(normalized);
        }

        return entries;
    }

    private static bool IsPlainDataRelativeKey(string normalizedKey) =>
        !normalizedKey.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase) &&
        !normalizedKey.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase) &&
        !normalizedKey.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase) &&
        !normalizedKey.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase) &&
        !normalizedKey.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase) &&
        !normalizedKey.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase);

    public Dictionary<string, ModMetadata> GetMetadataSubsetForListKeys(IEnumerable<string> listKeys)
    {
        var wanted = BuildWantedListKeys(listKeys);
        var result = new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
        var metadata = LoadMetadata();
        foreach (var key in ResolveExportMetadataKeys(metadata, wanted))
        {
            if (!metadata.TryGetValue(key, out var source) || source == null) continue;

            var copy = MergeMetadataEntries(new ModMetadata(), source);
            copy = MergeMissingFieldsFromSiblings(metadata, copy, key, Path.GetFileName(key));
            foreach (var file in (copy.Files ?? new List<string>()).ToList())
            {
                if (copy.NexusModId.HasValue) break;
                string fileName = Path.GetFileName((file ?? "").Replace('\\', '/'));
                if (!string.IsNullOrWhiteSpace(fileName))
                    copy = MergeMissingFieldsFromSiblings(metadata, copy, key, fileName);
            }
            result[key] = copy;
        }
        return result;
    }

    /// <param name="preferIncoming">
    /// Incoming details and Nexus linkage replace existing values; existing entries keep their
    /// enabled state and load order because those describe where the files are on this machine.
    /// </param>
    public void MergeMetadataSubset(Dictionary<string, ModMetadata> incoming, bool preferIncoming = false)
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
                metadata[key] = preferIncoming
                    ? OverlayMetadataEntry(existing, kvp.Value)
                    : MergeMetadataEntries(existing, kvp.Value);
            else
                metadata[key] = MergeMetadataEntries(new ModMetadata(), kvp.Value);
            changed = true;
        }
        if (changed) SaveMetadata(metadata);
    }

    private ModMetadata OverlayMetadataEntry(ModMetadata existing, ModMetadata incoming)
    {
        var result = MergeMetadataEntries(existing, incoming);
        if (incoming == null) return result;

        if (!string.IsNullOrWhiteSpace(incoming.Name) && incoming.Name != "Unknown Mod") result.Name = incoming.Name;
        if (!string.IsNullOrWhiteSpace(incoming.Author) && incoming.Author != "Unknown") result.Author = incoming.Author;
        if (!string.IsNullOrWhiteSpace(incoming.Version) && incoming.Version != "1.0") result.Version = incoming.Version;
        if (!string.IsNullOrWhiteSpace(incoming.URL)) result.URL = incoming.URL;
        if (!string.IsNullOrWhiteSpace(incoming.Category) && incoming.Category != "General") result.Category = incoming.Category;
        if (!string.IsNullOrWhiteSpace(incoming.Details)) result.Details = incoming.Details;

        if (incoming.NexusModId.HasValue)
        {
            result.NexusModId = incoming.NexusModId;
            result.NexusFileId = incoming.NexusFileId;
            result.NexusFileVersion = incoming.NexusFileVersion ?? "";
            result.NexusFileUploaded = incoming.NexusFileUploaded;
        }

        result.IsEnabled = existing.IsEnabled;
        result.IsBundle = existing.IsBundle;
        result.IsLoose = existing.IsLoose;
        if (existing.LoadOrder != UnassignedLoadOrder) result.LoadOrder = existing.LoadOrder;
        return result;
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
