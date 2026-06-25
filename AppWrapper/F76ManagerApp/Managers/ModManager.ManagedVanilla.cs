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
}
