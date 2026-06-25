using System.IO.Compression;
using System.Text.Json;
using F76ManagerApp.Managers;

namespace F76ManagerApp;

public partial class Form1
{
    private sealed class BackupEntryDto
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Label { get; set; } = "";
        public string Path { get; set; } = "";
        public string Created { get; set; } = "";
        public int FileCount { get; set; }
    }

    private string GetBackupsRoot() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");

    private List<BackupEntryDto> EnumerateBackups()
    {
        var entries = new List<BackupEntryDto>();
        string root = GetBackupsRoot();
        if (!Directory.Exists(root)) return entries;

        foreach (string iniFile in Directory.GetFiles(root, "*.ini"))
        {
            var fi = new FileInfo(iniFile);
            entries.Add(new BackupEntryDto
            {
                Id = $"ini:{fi.Name}",
                Type = "ini",
                Label = fi.Name,
                Path = iniFile,
                Created = fi.LastWriteTime.ToString("u"),
                FileCount = 1
            });
        }

        foreach (string zip in Directory.GetFiles(root, "ModsBackup_*.zip"))
        {
            var fi = new FileInfo(zip);
            int count = 0;
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                count = archive.Entries.Count;
            }
            catch { }

            entries.Add(new BackupEntryDto
            {
                Id = $"mods:{fi.Name}",
                Type = "mods",
                Label = fi.Name.StartsWith("ModsBackup_PreRestore_", StringComparison.OrdinalIgnoreCase)
                    ? FormatPreRestoreLabel(fi.Name)
                    : fi.Name,
                Path = zip,
                Created = fi.LastWriteTime.ToString("u"),
                FileCount = count
            });
        }

        foreach (string dir in Directory.GetDirectories(root, "PreRestore_INI_*"))
        {
            var di = new DirectoryInfo(dir);
            int count = Directory.GetFiles(dir).Length;
            entries.Add(new BackupEntryDto
            {
                Id = $"ini:{di.Name}",
                Type = "ini",
                Label = FormatPreRestoreLabel(di.Name),
                Path = dir,
                Created = di.LastWriteTime.ToString("u"),
                FileCount = count
            });
        }

        string configsRoot = Path.Combine(root, "Configs");
        if (Directory.Exists(configsRoot))
        {
            foreach (string platformDir in Directory.GetDirectories(configsRoot))
            {
                foreach (string configDir in Directory.GetDirectories(platformDir, "ConfigsBackup_*"))
                {
                    var di = new DirectoryInfo(configDir);
                    int count = Directory.GetFiles(configDir).Count(f =>
                        !Path.GetFileName(f).Equals("README.txt", StringComparison.OrdinalIgnoreCase));
                    entries.Add(new BackupEntryDto
                    {
                        Id = $"configs:{Path.GetFileName(platformDir)}:{di.Name}",
                        Type = "configs",
                        Label = di.Name.StartsWith("ConfigsBackup_PreRestore_", StringComparison.OrdinalIgnoreCase)
                            ? $"{Path.GetFileName(platformDir)}/{FormatPreRestoreLabel(di.Name)}"
                            : $"{Path.GetFileName(platformDir)}/{di.Name}",
                        Path = configDir,
                        Created = di.LastWriteTime.ToString("u"),
                        FileCount = count
                    });
                }
            }
        }

        return entries.OrderByDescending(e => e.Created).ToList();
    }

    private static string FormatPreRestoreLabel(string name)
    {
        if (name.StartsWith("PreRestore_INI_", StringComparison.OrdinalIgnoreCase))
            return $"Pre-restore · INI · {name["PreRestore_INI_".Length..]}";
        if (name.StartsWith("ModsBackup_PreRestore_", StringComparison.OrdinalIgnoreCase))
            return $"Pre-restore · Mods · {name["ModsBackup_PreRestore_".Length..]}";
        if (name.StartsWith("ConfigsBackup_PreRestore_", StringComparison.OrdinalIgnoreCase))
            return $"Pre-restore · Configs · {name["ConfigsBackup_PreRestore_".Length..]}";
        return $"Pre-restore · {name}";
    }

    private BackupEntryDto? FindBackupById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return EnumerateBackups().FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private void HandleListBackups()
    {
        var backups = EnumerateBackups();
        SendMessageToWeb(JsonSerializer.Serialize(new { type = "BACKUPS_LIST", backups }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private void HandlePreviewBackup(JsonElement root)
    {
        if (!TryGetString(root, "id", out var id, required: true)) return;
        var entry = FindBackupById(id);
        if (entry == null)
        {
            SendMessageToWeb(JsonSerializer.Serialize(new { type = "BACKUP_PREVIEW", id, error = "not_found" }));
            return;
        }

        var files = new List<string>();
        try
        {
            if (entry.Type == "ini")
            {
                if (Directory.Exists(entry.Path))
                    files.AddRange(Directory.GetFiles(entry.Path).Select(Path.GetFileName).Where(f => f != null).Cast<string>());
                else
                    files.Add(Path.GetFileName(entry.Path));
            }
            else if (entry.Type == "mods")
            {
                using var archive = ZipFile.OpenRead(entry.Path);
                files.AddRange(archive.Entries.Select(e => e.FullName).Take(200));
            }
            else if (entry.Type == "configs")
            {
                files.AddRange(Directory.GetFiles(entry.Path).Select(Path.GetFileName).Where(f => f != null).Cast<string>());
            }
        }
        catch (Exception ex)
        {
            SendMessageToWeb(JsonSerializer.Serialize(new { type = "BACKUP_PREVIEW", id, error = ex.Message }));
            return;
        }

        SendMessageToWeb(JsonSerializer.Serialize(new { type = "BACKUP_PREVIEW", id, files }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private bool TryDeleteBackupEntry(BackupEntryDto entry, out string? error)
    {
        error = null;
        try
        {
            if (Directory.Exists(entry.Path))
            {
                Directory.Delete(entry.Path, recursive: true);
            }
            else if (File.Exists(entry.Path))
            {
                File.Delete(entry.Path);
            }
            else
            {
                error = entry.Type == "configs" ? "Backup folder not found." : "Backup file not found.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void HandleDeleteBackup(JsonElement root)
    {
        if (!TryGetString(root, "id", out var id, required: true)) return;
        var entry = FindBackupById(id);
        if (entry == null)
        {
            SendStatusMessage("error", "Backup not found.", "backup_delete_not_found");
            return;
        }

        if (!TryDeleteBackupEntry(entry, out var error))
        {
            SendStatusMessage("error", error ?? "Could not delete backup.", "backup_delete_failed");
            return;
        }

        SendStatusMessage("success", "Backup deleted.", "backup_delete_success");
        HandleListBackups();
    }

    private void HandleDeleteAllBackups()
    {
        var backups = EnumerateBackups();
        if (backups.Count == 0)
        {
            SendStatusMessage("info", "No backups to delete.", "delete_all_backups_none");
            return;
        }

        int deleted = 0;
        int failed = 0;
        string? lastError = null;

        foreach (var entry in backups)
        {
            if (TryDeleteBackupEntry(entry, out var error))
                deleted++;
            else
            {
                failed++;
                lastError = error;
            }
        }

        HandleListBackups();

        if (deleted == 0)
        {
            SendStatusMessage("error", lastError ?? "Could not delete backups.", "delete_all_backups_failed", new object[] { lastError ?? "" });
            return;
        }

        if (failed > 0)
            SendStatusMessage("warning", $"Deleted {deleted} backup(s). {failed} could not be removed.", "delete_all_backups_partial", new object[] { deleted, failed });
        else
            SendStatusMessage("success", $"Deleted {deleted} backup(s).", "delete_all_backups_success", new object[] { deleted });
    }

    private void HandleRestoreBackup(JsonElement root)
    {
        if (!TryGetString(root, "id", out var id, required: true)) return;
        var entry = FindBackupById(id);
        if (entry == null)
        {
            SendStatusMessage("error", "Backup not found.", "backup_restore_not_found");
            return;
        }

        try
        {
            string? snapshotLabel = IsPreRestoreBackup(entry) ? null : CreatePreRestoreSnapshot(entry);
            if (entry.Type == "ini")
            {
                RestoreIniBackup(entry.Path);
            }
            else if (entry.Type == "mods")
            {
                RestoreModsBackup(entry.Path);
            }
            else if (entry.Type == "configs")
            {
                RestoreConfigsBackup(entry.Path);
            }
            else
            {
                SendStatusMessage("error", "Unknown backup type.", "backup_restore_failed");
                return;
            }

            RefreshConflictCount();
            SendDataToWeb();
            if (!string.IsNullOrWhiteSpace(snapshotLabel))
                SendStatusMessage("success", $"Backup restored. Pre-restore snapshot saved as {snapshotLabel}.", "backup_restore_success_snapshot", new object[] { snapshotLabel });
            else
                SendStatusMessage("success", "Backup restored successfully.", "backup_restore_success");
            HandleListBackups();
            LogActivity($"[BACKUP] Restored {entry.Type} backup: {entry.Label}");
        }
        catch (Exception ex)
        {
            LogError($"[BACKUP] Restore failed: {ex.Message}");
            SendStatusMessage("error", $"Restore failed: {ex.Message}", "backup_restore_failed");
        }
    }

    private static bool IsPreRestoreBackup(BackupEntryDto entry)
    {
        string name = Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.StartsWith("PreRestore_INI_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ModsBackup_PreRestore_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ConfigsBackup_PreRestore_", StringComparison.OrdinalIgnoreCase);
    }

    private string? CreatePreRestoreSnapshot(BackupEntryDto entry)
    {
        try
        {
            SyncAppPaths();
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return entry.Type switch
            {
                "ini" => CreatePreRestoreIniSnapshot(ts),
                "mods" => CreatePreRestoreModsSnapshot(ts),
                "configs" => CreatePreRestoreConfigsSnapshot(ts),
                _ => null
            };
        }
        catch (Exception ex)
        {
            LogError($"[BACKUP] Pre-restore snapshot failed: {ex.Message}");
            return null;
        }
    }

    private string? CreatePreRestoreIniSnapshot(string ts)
    {
        string root = GetBackupsRoot();
        Directory.CreateDirectory(root);
        string preDir = Path.Combine(root, $"PreRestore_INI_{ts}");
        Directory.CreateDirectory(preDir);

        bool any = false;
        if (File.Exists(AppPaths.CustomIniPath))
        {
            File.Copy(AppPaths.CustomIniPath, Path.Combine(preDir, Path.GetFileName(AppPaths.CustomIniPath)), true);
            any = true;
        }
        if (File.Exists(AppPaths.PrefsIniPath))
        {
            File.Copy(AppPaths.PrefsIniPath, Path.Combine(preDir, Path.GetFileName(AppPaths.PrefsIniPath)), true);
            any = true;
        }

        return any ? FormatPreRestoreLabel($"PreRestore_INI_{ts}") : null;
    }

    private string? CreatePreRestoreModsSnapshot(string ts)
    {
        string root = GetBackupsRoot();
        Directory.CreateDirectory(root);
        string zipPath = Path.Combine(root, $"ModsBackup_PreRestore_{ts}.zip");
        int backedUp = WriteModsBackupZip(zipPath);
        if (backedUp <= 0)
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            return null;
        }

        return FormatPreRestoreLabel($"ModsBackup_PreRestore_{ts}.zip");
    }

    private int WriteModsBackupZip(string zipPath)
    {
        int backedUpCount = 0;
        int skippedCount = 0;
        var modEntries = _modManager.CollectModsBackupEntries();
        var addedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (sourcePath, entryName) in modEntries)
            {
                if (!addedEntryNames.Add(entryName))
                {
                    skippedCount++;
                    continue;
                }

                try
                {
                    archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
                    backedUpCount++;
                }
                catch (Exception fileEx)
                {
                    skippedCount++;
                    LogError($"[BACKUP] Pre-restore skipped '{sourcePath}': {fileEx.Message}");
                }
            }

            backedUpCount += AddOptionalFileToArchive(archive, AppPaths.ModsMetadataFile, "Settings/mods.json");
            backedUpCount += AddOptionalFileToArchive(archive, AppPaths.ProfilesFile, "Profiles/profiles.json");
        }

        if (skippedCount > 0)
            LogActivity($"[BACKUP] Pre-restore mods snapshot completed with {skippedCount} skipped file(s).");

        return backedUpCount;
    }

    private string? CreatePreRestoreConfigsSnapshot(string ts)
    {
        string platformLabel = _platformManager.GetPlatformLabel();
        if (string.IsNullOrWhiteSpace(platformLabel)) platformLabel = "Unknown";

        string destDir = Path.Combine(GetBackupsRoot(), "Configs", platformLabel, $"ConfigsBackup_PreRestore_{ts}");
        int backedUp = WriteConfigsBackupToDirectory(destDir);
        if (backedUp <= 0)
        {
            try { if (Directory.Exists(destDir)) Directory.Delete(destDir, true); } catch { }
            return null;
        }

        string manifest = $"Platform: {platformLabel}{Environment.NewLine}Created (local): {DateTime.Now:u}{Environment.NewLine}Pre-restore snapshot{Environment.NewLine}";
        File.WriteAllText(Path.Combine(destDir, "README.txt"), manifest);

        return FormatPreRestoreLabel($"ConfigsBackup_PreRestore_{ts}");
    }

    private int WriteConfigsBackupToDirectory(string destDir)
    {
        Directory.CreateDirectory(destDir);
        var mods = SafeGetRealMods();
        int backedUp = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in mods)
        {
            string originalName = (string)((dynamic)m).originalName;
            if (string.IsNullOrWhiteSpace(originalName)) continue;

            string lower = originalName.ToLowerInvariant();
            if (!lower.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
                !lower.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                !lower.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                continue;

            string src = _modManager.ResolveListKeyToFullPath(originalName);
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) continue;

            string baseName = SanitizeConfigBackupEntryName(originalName);
            string ext = Path.GetExtension(baseName);
            string stem = string.IsNullOrEmpty(ext) ? baseName : baseName[..^ext.Length];
            string destFile = baseName;
            int n = 1;
            while (usedNames.Contains(destFile))
            {
                destFile = string.IsNullOrEmpty(ext) ? $"{stem}_{n}" : $"{stem}_{n}{ext}";
                n++;
            }
            usedNames.Add(destFile);

            File.Copy(src, Path.Combine(destDir, destFile), overwrite: false);
            backedUp++;
        }

        return backedUp;
    }

    private void RestoreIniBackup(string backupPath)
    {
        if (Directory.Exists(backupPath))
        {
            bool any = false;
            foreach (string iniFile in Directory.GetFiles(backupPath, "*.ini"))
            {
                string name = Path.GetFileName(iniFile);
                if (name.Contains("Custom", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(AppPaths.CustomIniPath))
                {
                    File.Copy(iniFile, AppPaths.CustomIniPath, true);
                    any = true;
                }
                else if (name.Contains("Prefs", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(AppPaths.PrefsIniPath))
                {
                    File.Copy(iniFile, AppPaths.PrefsIniPath, true);
                    any = true;
                }
            }

            if (!any)
                throw new InvalidOperationException("No INI files found in backup folder.");
            return;
        }

        string fileName = Path.GetFileName(backupPath);
        if (fileName.Contains("Custom", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(AppPaths.CustomIniPath))
            File.Copy(backupPath, AppPaths.CustomIniPath, true);
        else if (fileName.Contains("Prefs", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(AppPaths.PrefsIniPath))
            File.Copy(backupPath, AppPaths.PrefsIniPath, true);
        else
            throw new InvalidOperationException($"Cannot map INI backup file: {fileName}");
    }

    private void RestoreModsBackup(string zipPath)
    {
        string extractRoot = Path.Combine(Path.GetTempPath(), "F76ManagerRestore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractRoot);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true);

            string dataSrc = Path.Combine(extractRoot, "Data");
            if (Directory.Exists(dataSrc))
            {
                string dataDest = virtualModMode ? AppPaths.ManagedStagingDataPath : AppPaths.DataPath;
                CopyDirectoryContents(dataSrc, dataDest);
            }

            string stringsSrc = Path.Combine(extractRoot, "Data", "Strings");
            if (Directory.Exists(stringsSrc))
            {
                string stringsDest = virtualModMode ? AppPaths.ManagedStagingStringsPath : AppPaths.StringsPath;
                CopyDirectoryContents(stringsSrc, stringsDest);
            }

            string bundlesSrc = Path.Combine(extractRoot, "Bundles");
            if (Directory.Exists(bundlesSrc))
                CopyDirectoryContents(bundlesSrc, AppPaths.BundlesPath);

            string disabledSrc = Path.Combine(extractRoot, "Disabled Mods");
            if (Directory.Exists(disabledSrc))
                CopyDirectoryContents(disabledSrc, AppPaths.DisabledModsPath);

            string settingsMeta = Path.Combine(extractRoot, "Settings", "mods.json");
            if (File.Exists(settingsMeta))
                File.Copy(settingsMeta, AppPaths.ModsMetadataFile, true);

            string profilesFile = Path.Combine(extractRoot, "Profiles", "profiles.json");
            if (File.Exists(profilesFile))
                File.Copy(profilesFile, AppPaths.ProfilesFile, true);
        }
        finally
        {
            try { if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true); } catch { }
        }
    }

    private void RestoreConfigsBackup(string backupDir)
    {
        var mods = SafeGetRealMods();
        int restored = 0;

        foreach (string backupFile in Directory.GetFiles(backupDir))
        {
            if (Path.GetFileName(backupFile).Equals("README.txt", StringComparison.OrdinalIgnoreCase))
                continue;

            string backupName = Path.GetFileName(backupFile);
            foreach (dynamic mod in mods)
            {
                string originalName = mod.originalName;
                if (string.IsNullOrWhiteSpace(originalName)) continue;
                string sanitized = SanitizeConfigBackupEntryName(originalName);
                if (!string.Equals(sanitized, backupName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string dest = _modManager.ResolveListKeyToFullPath(originalName);
                if (string.IsNullOrWhiteSpace(dest)) continue;
                string? destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);
                File.Copy(backupFile, dest, true);
                restored++;
                break;
            }
        }

        if (restored == 0)
            throw new InvalidOperationException("No matching config files found for this backup.");
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        if (!Directory.Exists(sourceDir)) return;
        if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }
    }

    private void PersistImportedCollection(NexusManager.CollectionImportResult collection, string collectionLabel)
    {
        _importedCollection = new ImportedCollectionRecord
        {
            Slug = collection.Slug,
            Name = collectionLabel,
            Revision = collection.RevisionNumber,
            Mods = collection.Mods.Select(m => new ImportedCollectionModEntry
            {
                ModId = m.ModId,
                FileId = m.FileId,
                FileName = m.FileName,
                FileVersion = m.FileVersion
            }).ToList()
        };
        SaveSettings();
    }

    private void HandleCheckCollectionRevision()
    {
        if (_importedCollection == null || string.IsNullOrWhiteSpace(_importedCollection.Slug))
        {
            SendMessageToWeb(JsonSerializer.Serialize(new { type = "COLLECTION_REVISION_RESULT", hasUpdate = false }));
            return;
        }

        if (_nexusManager == null || !nexusLoggedIn)
        {
            SendMessageToWeb(JsonSerializer.Serialize(new { type = "COLLECTION_REVISION_RESULT", hasUpdate = false, error = "nexus_not_connected" }));
            return;
        }

        string slug = _importedCollection.Slug;
        int localRevision = _importedCollection.Revision;

        Task.Run(async () =>
        {
            try
            {
                var latest = await _nexusManager.FetchCollectionModsAsync(slug, null);
                if (!string.IsNullOrWhiteSpace(latest.Error))
                {
                    this.Invoke(() => SendMessageToWeb(JsonSerializer.Serialize(new { type = "COLLECTION_REVISION_RESULT", hasUpdate = false, error = latest.Error })));
                    return;
                }

                bool hasUpdate = latest.RevisionNumber > localRevision;
                var diff = hasUpdate ? BuildCollectionDiff(_importedCollection!, latest) : null;

                this.Invoke(() => SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "COLLECTION_REVISION_RESULT",
                    hasUpdate,
                    localRevision,
                    latestRevision = latest.RevisionNumber,
                    collectionName = _importedCollection?.Name ?? latest.CollectionName,
                    slug,
                    diff
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
            }
            catch (Exception ex)
            {
                this.Invoke(() => SendMessageToWeb(JsonSerializer.Serialize(new { type = "COLLECTION_REVISION_RESULT", hasUpdate = false, error = ex.Message })));
            }
        });
    }

    private static object BuildCollectionDiff(ImportedCollectionRecord local, NexusManager.CollectionImportResult latest)
    {
        var localByFile = local.Mods.ToDictionary(m => m.FileId);
        var latestByFile = latest.Mods.ToDictionary(m => m.FileId);

        var added = latest.Mods
            .Where(m => !localByFile.ContainsKey(m.FileId))
            .Select(m => new { modId = m.ModId, fileId = m.FileId, fileName = m.FileName, fileVersion = m.FileVersion })
            .ToList();

        var removed = local.Mods
            .Where(m => !latestByFile.ContainsKey(m.FileId))
            .Select(m => new { modId = m.ModId, fileId = m.FileId, fileName = m.FileName, fileVersion = m.FileVersion })
            .ToList();

        var updated = latest.Mods
            .Where(m => localByFile.TryGetValue(m.FileId, out var old) &&
                        (!string.Equals(old.FileVersion, m.FileVersion, StringComparison.OrdinalIgnoreCase) ||
                         old.ModId != m.ModId))
            .Select(m => new
            {
                modId = m.ModId,
                fileId = m.FileId,
                fileName = m.FileName,
                fileVersion = m.FileVersion,
                previousVersion = localByFile[m.FileId].FileVersion
            })
            .ToList();

        return new { added, removed, updated };
    }

    private void HandleApplyCollectionRevision(JsonElement root)
    {
        if (_importedCollection == null || string.IsNullOrWhiteSpace(_importedCollection.Slug))
            return;

        int? revision = null;
        if (root.TryGetProperty("revision", out var revProp) && revProp.ValueKind == JsonValueKind.Number)
            revision = revProp.GetInt32();

        _ = Task.Run(async () => await HandleCollectionImportAsync(_importedCollection.Slug, revision));
    }

    private void HandleBulkUpdateMods()
    {
        if (_nexusManager == null || !nexusLoggedIn)
        {
            SendMessageToWeb(JsonSerializer.Serialize(new { type = "BULK_UPDATE_COMPLETE", updated = 0, failed = 0, skipped = 0, error = "nexus_not_connected" }));
            return;
        }

        if (_bulkUpdateInProgress || _bulkUpdatePreflightInProgress)
        {
            SendStatusMessage("warning", "Bulk update already in progress.", "bulk_update_in_progress");
            return;
        }

        _bulkUpdatePreflightInProgress = true;

        Task.Run(async () =>
        {
            try
            {
                var updates = await CollectModUpdatesAsync();
                foreach (var u in updates)
                {
                    _modUpdateCache[u.OriginalName] = new CachedModUpdate
                    {
                        HasUpdate = u.HasUpdate,
                        LatestFileId = u.LatestFileId,
                        LatestVersion = u.LatestVersion,
                        LatestFileName = u.LatestFileName,
                        LatestUploaded = u.LatestUploaded
                    };
                }

                this.Invoke(StartBulkUpdateFromCache);
            }
            catch (Exception ex)
            {
                LogError($"[BULK] Preflight check failed: {ex.Message}");
                this.Invoke(() =>
                {
                    _bulkUpdatePreflightInProgress = false;
                    SendMessageToWeb(JsonSerializer.Serialize(new { type = "BULK_UPDATE_COMPLETE", updated = 0, failed = 0, error = ex.Message }));
                });
            }
        });
    }

    private sealed class ModUpdateCheckResult
    {
        public string OriginalName { get; set; } = "";
        public bool HasUpdate { get; set; }
        public long? LatestFileId { get; set; }
        public string LatestVersion { get; set; } = "";
        public string LatestFileName { get; set; } = "";
        public long? LatestUploaded { get; set; }
    }

    private async Task<List<ModUpdateCheckResult>> CollectModUpdatesAsync()
    {
        var updates = new List<ModUpdateCheckResult>();
        var mods = SafeGetRealMods();

        foreach (dynamic mod in mods)
        {
            try
            {
                string originalName = mod.originalName;
                long? nexusModId = mod.nexusModId;
                if (!nexusModId.HasValue || nexusModId.Value <= 0) continue;

                long? installedFileId = mod.nexusFileId;
                string installedVersion = mod.nexusFileVersion ?? mod.version ?? "";
                long? installedUploaded = mod.nexusFileUploaded;

                var check = await _nexusManager!.CheckModUpdateAsync(
                    originalName,
                    nexusModId.Value,
                    installedFileId,
                    installedVersion,
                    installedUploaded);

                if (!string.IsNullOrEmpty(check.Error)) continue;

                updates.Add(new ModUpdateCheckResult
                {
                    OriginalName = originalName,
                    HasUpdate = check.HasUpdate,
                    LatestFileId = check.LatestFileId,
                    LatestVersion = check.LatestVersion ?? "",
                    LatestFileName = check.LatestFileName ?? "",
                    LatestUploaded = check.LatestUploaded
                });
            }
            catch (Exception ex)
            {
                LogError($"[BULK] Update check failed: {ex.Message}");
            }
        }

        return updates;
    }

    private void StartBulkUpdateFromCache()
    {
        _bulkUpdatePreflightInProgress = false;
        _bulkUpdateQueue.Clear();
        foreach (dynamic mod in SafeGetRealMods())
        {
            string originalName = mod.originalName;
            if (!_modUpdateCache.TryGetValue(originalName, out var cached) || !cached.HasUpdate)
                continue;

            long modId = mod.nexusModId ?? 0;
            long fileId = cached.LatestFileId ?? mod.nexusFileId ?? 0;
            if (modId <= 0 || fileId <= 0) continue;

            _bulkUpdateQueue.Enqueue(new BulkUpdateQueueItem
            {
                OriginalName = originalName,
                ModId = modId,
                FileId = fileId,
                FileName = cached.LatestFileName ?? Path.GetFileName(originalName),
                FileVersion = cached.LatestVersion ?? "",
                FileUploaded = cached.LatestUploaded
            });
        }

        if (_bulkUpdateQueue.Count == 0)
        {
            SendMessageToWeb(JsonSerializer.Serialize(new { type = "BULK_UPDATE_COMPLETE", updated = 0, failed = 0, skipped = 0 }));
            SendStatusMessage("info", "No mod updates available.", "bulk_update_none");
            return;
        }

        _bulkUpdateInProgress = true;
        _bulkUpdateSuppressReplaceConfirm = true;
        _bulkUpdateTotal = _bulkUpdateQueue.Count;
        _bulkUpdateCompleted = 0;
        _bulkUpdateFailed = 0;

        SendMessageToWeb(JsonSerializer.Serialize(new
        {
            type = "BULK_UPDATE_STARTED",
            total = _bulkUpdateTotal
        }));

        ProcessNextBulkUpdate();
    }

    private void ProcessNextBulkUpdate()
    {
        if (!_bulkUpdateInProgress)
            return;

        if (_bulkUpdateQueue.Count == 0)
        {
            FinishBulkUpdate();
            return;
        }

        var item = _bulkUpdateQueue.Dequeue();
        SendMessageToWeb(JsonSerializer.Serialize(new
        {
            type = "BULK_UPDATE_PROGRESS",
            current = _bulkUpdateCompleted + _bulkUpdateFailed + 1,
            total = _bulkUpdateTotal,
            modName = Path.GetFileName(item.OriginalName)
        }));

        var existingMeta = _modManager.GetMetadataForMod(item.OriginalName);
        _pendingNexusImport = BuildPendingNexusImport(
            item.ModId,
            item.FileId,
            item.FileName,
            item.FileVersion,
            item.FileUploaded,
            existingMeta?.Name ?? "",
            existingMeta?.Author ?? "",
            existingMeta?.Details ?? "",
            existingMeta?.Category ?? "",
            replaceOriginalName: item.OriginalName);
        _pendingNexusImport.IsBulkUpdate = true;

        Task.Run(async () =>
        {
            try
            {
                await _nexusManager!.DownloadMod(item.ModId, item.FileId, item.FileName);
            }
            catch (Exception ex)
            {
                LogError($"[BULK] Download failed for {item.OriginalName}: {ex.Message}");
                _bulkUpdateFailed++;
                this.Invoke(ProcessNextBulkUpdate);
            }
        });
    }

    private void AdvanceBulkUpdateQueue(bool success)
    {
        if (!_bulkUpdateInProgress) return;
        if (success) _bulkUpdateCompleted++;
        else _bulkUpdateFailed++;
        ProcessNextBulkUpdate();
    }

    private void FinishBulkUpdate()
    {
        _bulkUpdateInProgress = false;
        _bulkUpdatePreflightInProgress = false;
        _bulkUpdateSuppressReplaceConfirm = false;
        _bulkUpdateQueue.Clear();
        SendMessageToWeb(JsonSerializer.Serialize(new
        {
            type = "BULK_UPDATE_COMPLETE",
            updated = _bulkUpdateCompleted,
            failed = _bulkUpdateFailed,
            total = _bulkUpdateTotal
        }));
        SendStatusMessage(
            "success",
            $"Bulk update finished: {_bulkUpdateCompleted} updated, {_bulkUpdateFailed} failed.",
            "bulk_update_complete_summary",
            new object[] { _bulkUpdateCompleted, _bulkUpdateFailed });
        _modUpdateCache.Clear();
        SendDataToWeb();
    }
}
