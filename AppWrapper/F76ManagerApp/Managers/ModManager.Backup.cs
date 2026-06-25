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
