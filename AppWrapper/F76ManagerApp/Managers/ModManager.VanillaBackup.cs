using System.Text.Json;
using System.Text.RegularExpressions;

namespace F76ManagerApp.Managers;

public partial class ModManager
{
    private static string VanillaBackupRoot =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups", "Vanilla", AppPaths.PlatformFolderName);

    private static string VanillaBackupManifestPath =>
        Path.Combine(VanillaBackupRoot, "manifest.json");

    private sealed class VanillaBackupEntry
    {
        public string RelativePath { get; set; } = "";
        public long Size { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string? BuildId { get; set; }
        public string BackedUpUtc { get; set; } = "";
    }

    private sealed class VanillaBackupManifest
    {
        public Dictionary<string, VanillaBackupEntry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private bool BackUpVanillaIfNeeded(string destPath, string relativeKey)
    {
        try
        {
            string rel = NormalizeVanillaRelativeKey(relativeKey, destPath);
            if (string.IsNullOrWhiteSpace(rel) || !IsVanillaFile(rel)) return false;
            if (string.IsNullOrWhiteSpace(destPath) || !File.Exists(destPath)) return false;

            string backupPath = GetVanillaBackupFilePath(rel);
            if (File.Exists(backupPath)) return false;

            string? dir = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.Copy(destPath, backupPath, overwrite: false);

            var info = new FileInfo(destPath);
            var manifest = LoadVanillaBackupManifest();
            manifest.Entries[rel] = new VanillaBackupEntry
            {
                RelativePath = rel,
                Size = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                BuildId = TryReadSteamBuildId(),
                BackedUpUtc = DateTime.UtcNow.ToString("o")
            };
            SaveVanillaBackupManifest(manifest);

            _logger($"[VANILLA] Backed up game file '{rel}' -> {backupPath}");
            return true;
        }
        catch (Exception ex)
        {
            _logger($"[VANILLA] Backup failed for '{relativeKey}': {ex.Message}");
            return false;
        }
    }

    private bool RestoreVanillaIfBackedUp(string destPath, string relativeKey)
    {
        try
        {
            string rel = NormalizeVanillaRelativeKey(relativeKey, destPath);
            if (string.IsNullOrWhiteSpace(rel) || !IsVanillaFile(rel)) return false;

            string backupPath = GetVanillaBackupFilePath(rel);
            if (!File.Exists(backupPath)) return false;

            var manifest = LoadVanillaBackupManifest();
            if (manifest.Entries.TryGetValue(rel, out var entry) && entry != null)
            {
                string? currentBuild = TryReadSteamBuildId();
                if (!string.IsNullOrEmpty(entry.BuildId) &&
                    !string.IsNullOrEmpty(currentBuild) &&
                    !string.Equals(entry.BuildId, currentBuild, StringComparison.Ordinal))
                {
                    _logger($"[VANILLA] Skipped restore of '{rel}': game build changed ({entry.BuildId} -> {currentBuild}). Verify game files in Steam.");
                    ReportStatus(
                        "warning",
                        $"Game updated since '{Path.GetFileName(rel)}' was backed up. Verify game files in Steam to restore it.",
                        "vanilla_backup_stale",
                        new object[] { Path.GetFileName(rel) });
                    return false;
                }
            }

            if (File.Exists(destPath))
                return false;

            string? dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(backupPath, destPath, overwrite: false);
            _logger($"[VANILLA] Restored game file '{rel}' from backup.");
            return true;
        }
        catch (Exception ex)
        {
            _logger($"[VANILLA] Restore failed for '{relativeKey}': {ex.Message}");
            return false;
        }
    }

    private static string NormalizeVanillaRelativeKey(string relativeKey, string destPath)
    {
        string rel = (relativeKey ?? "").Replace('\\', '/').Trim().TrimStart('/');
        if (rel.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
            rel = rel.Substring("Disabled/".Length);
        if (string.IsNullOrWhiteSpace(rel) && !string.IsNullOrWhiteSpace(destPath))
            rel = Path.GetFileName(destPath);
        return rel;
    }

    private static string GetVanillaBackupFilePath(string relativeKey)
    {
        string safe = relativeKey.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(VanillaBackupRoot, safe);
    }

    private VanillaBackupManifest LoadVanillaBackupManifest()
    {
        try
        {
            if (!File.Exists(VanillaBackupManifestPath))
                return new VanillaBackupManifest();
            string json = File.ReadAllText(VanillaBackupManifestPath);
            var loaded = JsonSerializer.Deserialize<VanillaBackupManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (loaded?.Entries == null)
                return new VanillaBackupManifest();
            loaded.Entries = new Dictionary<string, VanillaBackupEntry>(
                loaded.Entries, StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch (Exception ex)
        {
            _logger($"[VANILLA] Failed to read manifest: {ex.Message}");
            return new VanillaBackupManifest();
        }
    }

    private void SaveVanillaBackupManifest(VanillaBackupManifest manifest)
    {
        Directory.CreateDirectory(VanillaBackupRoot);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(VanillaBackupManifestPath, JsonSerializer.Serialize(manifest, opts));
    }

    private string? TryReadSteamBuildId()
    {
        try
        {
            if (AppPaths.IsXbox) return null;

            string root = AppPaths.GameInstallRoot;
            if (string.IsNullOrWhiteSpace(root)) return null;

            string? steamApps = FindSteamAppsDirectory(root);
            if (steamApps == null) return null;

            string appId = AppPaths.IsPts ? "1836200" : "1151340";
            string manifestPath = Path.Combine(steamApps, $"appmanifest_{appId}.acf");
            if (!File.Exists(manifestPath)) return null;

            string text = File.ReadAllText(manifestPath);
            var match = Regex.Match(text, "\"buildid\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex)
        {
            _logger($"[VANILLA] Could not read Steam build id: {ex.Message}");
            return null;
        }
    }

    private static string? FindSteamAppsDirectory(string gameInstallRoot)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(gameInstallRoot));
            while (dir != null)
            {
                if (dir.Name.Equals("common", StringComparison.OrdinalIgnoreCase) &&
                    dir.Parent != null &&
                    dir.Parent.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                    return dir.Parent.FullName;

                if (dir.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                    return dir.FullName;

                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }
}
