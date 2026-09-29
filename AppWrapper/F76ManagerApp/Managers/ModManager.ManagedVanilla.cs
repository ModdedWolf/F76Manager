using System.Text.Json;

namespace F76ManagerApp.Managers;

public partial class ModManager
{
    private sealed class ManagedFileArtifact
    {
        public string Path { get; set; } = "";
        public bool HadPreviousFile { get; set; }
        public string PreviousContentBase64 { get; set; } = "";
        public string PreviousBackupPath { get; set; } = "";
    }

    private sealed class ManagedIniArtifact
    {
        public string FilePath { get; set; } = "";
        public string Section { get; set; } = "";
        public string Key { get; set; } = "";
        public bool HadValue { get; set; }
        public string PreviousValue { get; set; } = "";
    }

    private sealed class ManagedArtifactsManifest
    {
        public List<ManagedFileArtifact> Files { get; set; } = new();
        public List<ManagedIniArtifact> IniKeys { get; set; } = new();
    }

    private ManagedArtifactsManifest LoadManagedArtifacts()
    {
        try
        {
            if (File.Exists(AppPaths.ManagedArtifactsFile))
            {
                string json = File.ReadAllText(AppPaths.ManagedArtifactsFile);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<ManagedArtifactsManifest>(json, options) ?? new ManagedArtifactsManifest();
            }
        }
        catch (Exception ex)
        {
            _logger($"[MANAGED] Failed to load managed artifacts manifest: {ex.Message}");
        }

        return new ManagedArtifactsManifest();
    }

    private void SaveManagedArtifacts(ManagedArtifactsManifest manifest)
    {
        try
        {
            string dir = Path.GetDirectoryName(AppPaths.ManagedArtifactsFile) ?? AppPaths.SettingsFolder;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.ManagedArtifactsFile, json);
        }
        catch (Exception ex)
        {
            _logger($"[MANAGED] Failed to save managed artifacts manifest: {ex.Message}");
        }
    }

    private void TrackManagedFileArtifact(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string normalized = Path.GetFullPath(path);
        var manifest = LoadManagedArtifacts();
        bool alreadyTracked = manifest.Files.Any(f => string.Equals(Path.GetFullPath(f.Path), normalized, StringComparison.OrdinalIgnoreCase));
        if (!alreadyTracked)
        {
            var tracked = new ManagedFileArtifact { Path = normalized };
            if (File.Exists(normalized))
            {
                string backupsDir = Path.Combine(AppPaths.SettingsFolder, "managed-artifact-backups");
                if (!Directory.Exists(backupsDir)) Directory.CreateDirectory(backupsDir);

                string backupName = $"{Guid.NewGuid():N}.bak";
                string backupPath = Path.Combine(backupsDir, backupName);
                try
                {
                    File.Copy(normalized, backupPath, true);
                    tracked.HadPreviousFile = true;
                    tracked.PreviousBackupPath = backupPath;
                }
                catch
                {
                    byte[] bytes = File.ReadAllBytes(normalized);
                    if (bytes.Length <= 2 * 1024 * 1024)
                    {
                        tracked.HadPreviousFile = true;
                        tracked.PreviousContentBase64 = Convert.ToBase64String(bytes);
                    }
                }
            }

            manifest.Files.Add(tracked);
            SaveManagedArtifacts(manifest);
        }
    }

    private void TrackManagedIniSnapshot(string filePath, string section, string key)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        string normalized = Path.GetFullPath(filePath);
        var manifest = LoadManagedArtifacts();
        bool exists = manifest.IniKeys.Any(i =>
            string.Equals(Path.GetFullPath(i.FilePath), normalized, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.Section, section, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

        if (exists) return;

        string previousValue = _configManager.ReadIniValue(normalized, section, key);
        manifest.IniKeys.Add(new ManagedIniArtifact
        {
            FilePath = normalized,
            Section = section,
            Key = key,
            HadValue = previousValue != null,
            PreviousValue = previousValue ?? ""
        });
        SaveManagedArtifacts(manifest);
    }

    private void TrackArchiveKeySnapshots(string archiveKey)
    {
        TrackManagedIniSnapshot(AppPaths.CustomIniPath, "Archive", archiveKey);
    }

    private void TrackPluginsSnapshot()
    {
        TrackManagedFileArtifact(AppPaths.PluginsFilePath);
    }

    public bool HasPendingManagedArtifacts()
    {
        var manifest = LoadManagedArtifacts();
        return manifest.Files.Count > 0 || manifest.IniKeys.Count > 0;
    }

    public int CleanupManagedArtifacts()
    {
        var manifest = LoadManagedArtifacts();
        int processed = 0;

        foreach (var file in manifest.Files)
        {
            try
            {
                string normalizedPath = Path.GetFullPath(file.Path);
                string stagingRootBase = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Managed Staging"));
                if (normalizedPath.StartsWith(stagingRootBase, StringComparison.OrdinalIgnoreCase))
                {
                    _logger($"[MANAGED] Skipping cleanup of staging path: {normalizedPath}");
                    continue;
                }

                if (file.HadPreviousFile && !string.IsNullOrEmpty(file.PreviousContentBase64))
                {
                    string? dir = Path.GetDirectoryName(file.Path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    byte[] previous = Convert.FromBase64String(file.PreviousContentBase64);
                    File.WriteAllBytes(file.Path, previous);
                    processed++;
                    _logger($"[MANAGED] Restored artifact file: {file.Path}");
                }
                else if (file.HadPreviousFile && !string.IsNullOrEmpty(file.PreviousBackupPath) && File.Exists(file.PreviousBackupPath))
                {
                    string? dir = Path.GetDirectoryName(file.Path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.Copy(file.PreviousBackupPath, file.Path, true);
                    processed++;
                    _logger($"[MANAGED] Restored artifact file from backup: {file.Path}");
                }
                else if (file.HadPreviousFile)
                {
                    _logger($"[MANAGED] Skipped deleting '{file.Path}' because prior state snapshot is unavailable.");
                }
                else if (File.Exists(file.Path))
                {
                    File.Delete(file.Path);
                    processed++;
                    _logger($"[MANAGED] Removed artifact file: {file.Path}");
                }

                if (!string.IsNullOrEmpty(file.PreviousBackupPath) && File.Exists(file.PreviousBackupPath))
                {
                    File.Delete(file.PreviousBackupPath);
                }
            }
            catch (Exception ex)
            {
                _logger($"[MANAGED] Failed to remove artifact file '{file.Path}': {ex.Message}");
            }
        }

        foreach (var ini in manifest.IniKeys)
        {
            try
            {
                if (ini.HadValue)
                {
                    RestoreIniKeyInFile(ini.FilePath, ini.Section, ini.Key, ini.PreviousValue);
                }
                else
                {
                    _configManager.RemoveKey(ini.FilePath, ini.Section, ini.Key);
                }

                processed++;
                _logger($"[MANAGED] Restored INI key: [{ini.Section}] {ini.Key}");
            }
            catch (Exception ex)
            {
                _logger($"[MANAGED] Failed to restore INI key [{ini.Section}] {ini.Key}: {ex.Message}");
            }
        }

        SaveManagedArtifacts(new ManagedArtifactsManifest());
        return processed;
    }

    private void RestoreIniKeyInFile(string path, string section, string key, string value)
    {
        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            _configManager.UpdateIniKey(lines, section, key, value);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(path, lines);
        }
        catch (Exception ex)
        {
            _logger($"[MANAGED] Failed to write INI restore to {path}: {ex.Message}");
        }
    }

    private HashSet<string> CollectManagerOwnedLiveFiles()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadata = LoadMetadata();

        void TryAdd(string? relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return;
            string key = NormalizeMetadataKey(relative.Replace("\\", "/"));
            if (string.IsNullOrWhiteSpace(key)) return;
            if (key.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
                return;
            if (!key.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase) && IsVanillaFile(key)) return;
            keys.Add(key);
        }

        foreach (string modName in GetEnabledModNamesInLoadOrder())
        {
            string metaKey = modName;
            if (!metadata.ContainsKey(metaKey))
            {
                string fileName = Path.GetFileName(modName);
                if (metadata.ContainsKey(fileName)) metaKey = fileName;
            }

            if (!metadata.TryGetValue(metaKey, out var meta) || meta?.Files == null) continue;
            foreach (string file in meta.Files)
                TryAdd(file);
        }

        string dataRoot = AppPaths.DataPath;
        string stringsRoot = AppPaths.StringsPath;
        string gameRoot = AppPaths.GameInstallRoot;

        try
        {
            if (!string.IsNullOrWhiteSpace(dataRoot) && Directory.Exists(dataRoot))
            {
                foreach (var file in Directory.GetFiles(dataRoot, "*.*", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(file);
                    string ext = Path.GetExtension(fileName).ToLowerInvariant();
                    if (ext is not (".ba2" or ".esm" or ".esp")) continue;
                    TryAdd(fileName);
                }
            }

            if (!string.IsNullOrWhiteSpace(stringsRoot) && Directory.Exists(stringsRoot))
            {
                foreach (var file in Directory.GetFiles(stringsRoot, "*.*", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(file);
                    if (!TryParseStringModDiskName(fileName, out _, out _)) continue;
                    TryAdd($"Strings/{fileName}");
                }
            }

            if (!string.IsNullOrWhiteSpace(gameRoot) && Directory.Exists(gameRoot))
            {
                foreach (string name in GameRootInjectorBaseNames)
                {
                    string path = Path.Combine(gameRoot, name);
                    if (File.Exists(path))
                        TryAdd($"GameRoot/{name}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[MANAGED] Failed scanning live mod files: {ex.Message}");
        }

        return keys;
    }

    private static bool FilesMatchBySize(string a, string b)
    {
        try
        {
            return File.Exists(a) && File.Exists(b) &&
                   new FileInfo(a).Length == new FileInfo(b).Length;
        }
        catch
        {
            return false;
        }
    }

    public int MoveLiveModsIntoStaging()
    {
        if (!VirtualModMode) return 0;

        EnsureActiveDataFolders();
        int moved = 0;

        foreach (string relative in CollectManagerOwnedLiveFiles())
        {
            try
            {
                string livePath = GetManagedRuntimeDestinationPath(relative);
                string stagingPath = GetManagedRuntimeSourcePath(relative);
                if (string.IsNullOrEmpty(livePath) || string.IsNullOrEmpty(stagingPath)) continue;
                if (!File.Exists(livePath)) continue;

                string? stagingDir = Path.GetDirectoryName(stagingPath);
                if (!string.IsNullOrEmpty(stagingDir) && !Directory.Exists(stagingDir))
                    Directory.CreateDirectory(stagingDir);

                if (!FilesMatchBySize(livePath, stagingPath))
                    File.Copy(livePath, stagingPath, true);

                if (FilesMatchBySize(livePath, stagingPath))
                {
                    File.Delete(livePath);
                    moved++;
                    _logger($"[MANAGED] Moved live -> staging: {relative}");
                }
                else
                {
                    _logger($"[MANAGED] Skipped deleting live file after copy mismatch: {relative}");
                }
            }
            catch (Exception ex)
            {
                _logger($"[MANAGED] Failed moving live -> staging '{relative}': {ex.Message}");
            }
        }

        return moved;
    }

    private List<string> CollectAllStagedFiles()
    {
        var keys = new List<string>();

        string dataRoot = AppPaths.ManagedStagingDataPath;
        if (!string.IsNullOrWhiteSpace(dataRoot) && Directory.Exists(dataRoot))
        {
            foreach (string file in Directory.GetFiles(dataRoot, "*", SearchOption.AllDirectories))
            {
                string key = Path.GetRelativePath(dataRoot, file).Replace("\\", "/");
                if (key.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase))
                    continue;
                keys.Add(key);
            }
        }

        string gameRoot = AppPaths.ManagedStagingGameRootPath;
        if (!string.IsNullOrWhiteSpace(gameRoot) && Directory.Exists(gameRoot))
        {
            foreach (string file in Directory.GetFiles(gameRoot, "*", SearchOption.TopDirectoryOnly))
                keys.Add($"GameRoot/{Path.GetFileName(file)}");
        }

        return keys;
    }

    private static void RemoveEmptyDirectories(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        foreach (string dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
        }
    }

    public bool HasStagedFiles() => CollectAllStagedFiles().Count > 0;

    public int MoveStagedModsToLive()
    {
        if (VirtualModMode) return 0;

        EnsureActiveDataFolders();
        int moved = 0;

        foreach (string relative in CollectAllStagedFiles())
        {
            try
            {
                string stagingPath = GetManagedRuntimeSourcePath(relative);
                string livePath = GetManagedRuntimeDestinationPath(relative);
                if (string.IsNullOrEmpty(livePath) || string.IsNullOrEmpty(stagingPath)) continue;
                if (!File.Exists(stagingPath)) continue;

                bool needsCopy = NeedsRuntimeCopy(stagingPath, livePath);
                if (needsCopy && File.Exists(livePath) && !relative.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase) && IsVanillaFile(relative))
                {
                    _logger($"[MANAGED] Kept staged file; refusing to overwrite vanilla game file: {relative}");
                    continue;
                }

                string? liveDir = Path.GetDirectoryName(livePath);
                if (!string.IsNullOrEmpty(liveDir) && !Directory.Exists(liveDir))
                    Directory.CreateDirectory(liveDir);

                if (needsCopy)
                    File.Copy(stagingPath, livePath, true);

                if (!NeedsRuntimeCopy(stagingPath, livePath))
                {
                    File.Delete(stagingPath);
                    moved++;
                    _logger($"[MANAGED] Moved staging -> live: {relative}");
                }
                else
                {
                    _logger($"[MANAGED] Kept staged file after copy mismatch: {relative}");
                }
            }
            catch (Exception ex)
            {
                _logger($"[MANAGED] Failed moving staging -> live '{relative}': {ex.Message}");
            }
        }

        RemoveEmptyDirectories(AppPaths.ManagedStagingDataPath);
        RemoveEmptyDirectories(AppPaths.ManagedStagingGameRootPath);

        int left = CollectAllStagedFiles().Count;
        if (left > 0)
            _logger($"[MANAGED] {left} file(s) remain in staging after moving to live; see messages above.");

        return moved;
    }

    public void ClearLiveLoadState()
    {
        try
        {
            string archiveKey = DetectArchiveKey();
            _configManager.UpdateBothInis("Archive", archiveKey, "", onlyCustom: true);
            _logger($"[MANAGED] Cleared live Custom.ini archive key ({archiveKey}).");
        }
        catch (Exception ex)
        {
            _logger($"[MANAGED] Failed clearing live archive list: {ex.Message}");
        }

        try
        {
            string pluginsPath = AppPaths.PluginsFilePath;
            if (!string.IsNullOrWhiteSpace(pluginsPath))
            {
                string? dir = Path.GetDirectoryName(pluginsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(pluginsPath, "");
                _logger("[MANAGED] Cleared live plugins.txt.");
            }
        }
        catch (Exception ex)
        {
            _logger($"[MANAGED] Failed clearing plugins.txt: {ex.Message}");
        }
    }

    public void RedeployDirect()
    {
        if (VirtualModMode)
        {
            _logger("[MANAGED] RedeployDirect skipped: Virtual Mod Mode is still enabled.");
            return;
        }

        var order = GetEnabledModNamesInLoadOrder();
        _logger($"[MANAGED] Redeploying {order.Count} enabled mod(s) in direct mode.");
        UpdateModOrder(order, updateMetadataLoadOrder: false);
    }
}
