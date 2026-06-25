using System.Text.Json;

namespace F76ManagerApp.Managers;

public partial class ModManager
{
    private static readonly HashSet<string> DeployLooseConfigExtensions = ConfigFileMerger.LooseConfigExtensions;

    public void UpdateModOrder(List<string> currentOrder, bool updateMetadataLoadOrder = true)
    {
        try {
            _logger($"[DEPLOY] Initializing deployment... (Path: {GetActiveDataPath()})");
            
            var metadata = LoadMetadata();
            var archiveMods = new List<string>();
            var pluginMods = new List<string>();
            var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            if (updateMetadataLoadOrder) {
                _logger($"[ORDER] Refreshing load order for {currentOrder.Count} mods.");

                var enabledKeysInOrder = new List<string>();
                var enabledKeySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string modName in currentOrder)
                {
                    string key = modName;
                    if (!metadata.ContainsKey(key))
                    {
                        string fileName = Path.GetFileName(modName);
                        if (metadata.ContainsKey(fileName)) key = fileName;
                    }

                    if (!metadata.ContainsKey(key))
                    {
                        metadata[key] = new ModMetadata
                        {
                            Name = Path.GetFileNameWithoutExtension(key),
                            Files = new List<string> { key },
                            IsEnabled = false,
                            LoadOrder = 9999
                        };
                    }

                    if (enabledKeySet.Add(key))
                    {
                        enabledKeysInOrder.Add(key);
                    }
                }

                var fullOrdered = metadata.Keys
                    .OrderBy(k =>
                    {
                        if (metadata.TryGetValue(k, out var m) && m != null) return m.LoadOrder;
                        return 9999;
                    })
                    .ThenBy(k =>
                    {
                        if (metadata.TryGetValue(k, out var m) && m != null && !string.IsNullOrWhiteSpace(m.Name))
                            return m.Name;
                        return Path.GetFileNameWithoutExtension(k);
                    }, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var enabledQueue = new Queue<string>(enabledKeysInOrder);
                int orderIndex = 0;
                foreach (string slotKey in fullOrdered)
                {
                    string assignKey = enabledKeySet.Contains(slotKey) && enabledQueue.Count > 0
                        ? enabledQueue.Dequeue()
                        : slotKey;
                    if (metadata.ContainsKey(assignKey))
                        metadata[assignKey].LoadOrder = orderIndex++;
                }

                SaveMetadata(metadata);
            }
            _logger($"[MODS] Saved metadata with {currentOrder.Count} reordered items.");

            foreach (string modName in currentOrder)
            {
                string key = modName;
                if (!metadata.ContainsKey(key)) {
                    string fileName = Path.GetFileName(modName);
                    if (metadata.ContainsKey(fileName)) key = fileName;
                }

                bool foundInMetadata = false;
                List<string> filesResult = new List<string>();

                if (metadata.ContainsKey(key)) {
                    filesResult.AddRange(metadata[key].Files);
                } 

                if (filesResult.Count == 0) {
                     string potentialPath = Path.Combine(GetActiveDataPath(), modName);
                     if (File.Exists(potentialPath)) {
                         filesResult.Add(modName);
                         _logger($"[DEPLOY] Found file on disk (fallback): {modName}");
                     } else if (IsGameRootInjectorName(Path.GetFileName(modName.Replace("\\", "/"))))
                     {
                         string fn = Path.GetFileName(modName.Replace("\\", "/"));
                         if (modName.Replace("\\", "/").StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase) ||
                             (VirtualModMode && File.Exists(Path.Combine(AppPaths.ManagedStagingGameRootPath, fn))) ||
                             (!VirtualModMode && !string.IsNullOrEmpty(AppPaths.GameInstallRoot) &&
                              File.Exists(Path.Combine(AppPaths.GameInstallRoot, fn))))
                         {
                             filesResult.Add($"GameRoot/{fn}");
                             _logger($"[DEPLOY] Found game-root injector on disk (fallback): {fn}");
                         }
                         else
                             _logger($"[DEPLOY] WARNING: Game-root injector not found on disk: {modName}");
                     } else {
                         _logger($"[DEPLOY] WARNING: Mod not found in metadata or disk: {modName}");
                     }
                }

                foreach (string relativePath in filesResult)
                {
                    string normalizedPath = relativePath.Replace("\\", "/");
                    if (processedFiles.Contains(normalizedPath)) continue;

                    if (normalizedPath.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase))
                    {
                        processedFiles.Add(normalizedPath);
                        continue;
                    }

                    if (!VirtualModMode)
                    {
                        string extLoose = Path.GetExtension(normalizedPath).ToLowerInvariant();
                        if (DeployLooseConfigExtensions.Contains(extLoose))
                        {
                            try
                            {
                                string sourcePath = GetFullPath(normalizedPath);
                                string destinationPath = GetManagedRuntimeDestinationPath(normalizedPath);
                                if (!string.IsNullOrEmpty(destinationPath) && File.Exists(sourcePath))
                                {
                                    string? destinationDir = Path.GetDirectoryName(destinationPath);
                                    if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                                    {
                                        Directory.CreateDirectory(destinationDir);
                                    }

                                    bool shouldCopy = !File.Exists(destinationPath) ||
                                                      new FileInfo(sourcePath).Length != new FileInfo(destinationPath).Length;
                                    if (shouldCopy)
                                    {
                                        TrackManagedFileArtifact(destinationPath);
                                        File.Copy(sourcePath, destinationPath, true);
                                        _logger($"[DEPLOY] Staged loose config: {normalizedPath}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger($"[ERROR] Failed to stage loose config {normalizedPath}: {ex.Message}");
                            }

                            processedFiles.Add(normalizedPath);
                            continue;
                        }
                    }

                    if (!VirtualModMode && normalizedPath.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase))
                    {
                        string sourcePath = GetFullPath(normalizedPath);
                        string destPath = Path.Combine(AppPaths.DataPath, normalizedPath.Replace("/", "\\"));
                        
                        if (File.Exists(sourcePath))
                        {
                            try
                            {
                                string destDir = Path.GetDirectoryName(destPath);
                                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                                
                                if (!File.Exists(destPath) || new FileInfo(sourcePath).Length != new FileInfo(destPath).Length)
                                {
                                    if (!File.Exists(destPath))
                                    {
                                        TrackManagedFileArtifact(destPath);
                                    }
                                    File.Copy(sourcePath, destPath, true);
                                    _logger($"[DEPLOY] Staged bundle: {normalizedPath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger($"[ERROR] Failed to stage bundle {normalizedPath}: {ex.Message}");
                            }
                        }
                    }

                    if (normalizedPath.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase) &&
                        IsGameRootInjectorName(Path.GetFileName(normalizedPath)))
                    {
                        string injFn = Path.GetFileName(normalizedPath);
                        try
                        {
                            if (VirtualModMode)
                            {
                                if (!Directory.Exists(AppPaths.ManagedStagingGameRootPath))
                                    Directory.CreateDirectory(AppPaths.ManagedStagingGameRootPath);
                                string stagedDest = Path.Combine(AppPaths.ManagedStagingGameRootPath, injFn);
                                string srcDisabled = Path.Combine(AppPaths.DisabledModsPath, injFn);
                                string srcStaged = stagedDest;
                                string? copyFrom = null;
                                if (File.Exists(srcDisabled))
                                    copyFrom = srcDisabled;
                                else if (File.Exists(srcStaged))
                                    copyFrom = srcStaged;

                                if (copyFrom != null &&
                                    (!File.Exists(stagedDest) ||
                                     new FileInfo(copyFrom).Length != new FileInfo(stagedDest).Length))
                                {
                                    File.Copy(copyFrom, stagedDest, true);
                                    _logger($"[DEPLOY] Staged game-root injector: {injFn}");
                                }
                            }
                            else if (!string.IsNullOrEmpty(AppPaths.GameInstallRoot))
                            {
                                string liveDest = Path.Combine(AppPaths.GameInstallRoot, injFn);
                                string dis = Path.Combine(AppPaths.DisabledModsPath, injFn);
                                string staged = Path.Combine(AppPaths.ManagedStagingGameRootPath, injFn);
                                if (File.Exists(dis) &&
                                    (!File.Exists(liveDest) ||
                                     new FileInfo(dis).Length != new FileInfo(liveDest).Length))
                                {
                                    File.Copy(dis, liveDest, true);
                                    _logger($"[DEPLOY] Placed game-root injector from Disabled Mods: {injFn}");
                                }
                                else if (!File.Exists(liveDest) && File.Exists(staged))
                                {
                                    File.Copy(staged, liveDest, true);
                                    _logger($"[DEPLOY] Placed game-root injector from staging: {injFn}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger($"[ERROR] Failed to sync game-root injector {injFn}: {ex.Message}");
                        }

                        processedFiles.Add(normalizedPath);
                        continue;
                    }

                    string ext = Path.GetExtension(normalizedPath).ToLowerInvariant();
                    if (ext == ".ba2") 
                    {
                        archiveMods.Add(normalizedPath);
                        processedFiles.Add(normalizedPath);
                    }
                    else if (ext == ".esm" || ext == ".esp") 
                    {
                        string fileName = Path.GetFileName(normalizedPath);
                        pluginMods.Add(fileName);
                        processedFiles.Add(normalizedPath);
                    }
                }
            }

            string archiveKey = DetectArchiveKey();
            _logger($"[DEPLOY] Using INI key: {archiveKey}");

            if (VirtualModMode)
            {
                _logger("[DEPLOY] Virtual Mod Mode enabled: staged mod state saved. Runtime apply happens at launch.");
                _statusReporter("info", "Virtual Mod Mode: staged mod state saved. Mods will be applied at launch and cleaned after exit.");
                return;
            }

            if (archiveMods.Count > 0) {
                string archiveList = string.Join(",", archiveMods.Select(NormalizeArchiveForIni));
                
                foreach (var mod in archiveMods) {
                    if (mod.Contains(" ")) {
                        _logger($"[WARNING] Mod filename '{mod}' contains spaces. This may prevent it from loading in-game. Consider renaming it.");
                    }
                }

                TrackArchiveKeySnapshots(archiveKey);
                _configManager.UpdateBothInis("Archive", archiveKey, archiveList, onlyCustom: true);
                _logger($"[DEPLOY] Wrote {archiveMods.Count} archives to Custom.ini.");
            } else {
                _logger("[DEPLOY] No BA2 archives in deploy order; leaving existing Custom.ini archive list unchanged.");
            }
            
            UpdatePluginsTxt(pluginMods);

            UpdateStringMods();

            _logger("[MODS] Load order successfully applied.");
        } catch (Exception ex) {
            _logger($"[ERROR] UpdateModOrder failed: {ex.Message}");
            throw;
        }
    }

    public void PrepareManagedRuntimeState(List<string> currentOrder)
    {
        if (!VirtualModMode) return;

        try
        {
            _logger($"[MANAGED] Preparing runtime state for {currentOrder.Count} mods...");
            var metadata = LoadMetadata();
            var archiveMods = new List<string>();
            var pluginMods = new List<string>();
            var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string modName in currentOrder)
            {
                string key = modName;
                if (!metadata.ContainsKey(key))
                {
                    string fileName = Path.GetFileName(modName);
                    if (metadata.ContainsKey(fileName)) key = fileName;
                }

                var filesResult = new List<string>();
                if (metadata.ContainsKey(key)) filesResult.AddRange(metadata[key].Files);

                if (filesResult.Count == 0)
                {
                    string potentialPath = Path.Combine(AppPaths.ManagedStagingDataPath, modName);
                    if (File.Exists(potentialPath)) filesResult.Add(modName);
                    else if (IsGameRootInjectorName(Path.GetFileName(modName.Replace("\\", "/"))))
                    {
                        string fn = Path.GetFileName(modName.Replace("\\", "/"));
                        string grPath = Path.Combine(AppPaths.ManagedStagingGameRootPath, fn);
                        if (File.Exists(grPath)) filesResult.Add($"GameRoot/{fn}");
                    }
                }

                foreach (string relativePath in filesResult)
                {
                    string normalizedPath = relativePath.Replace("\\", "/");
                    if (processedFiles.Contains(normalizedPath)) continue;

                    string sourcePath = GetManagedRuntimeSourcePath(normalizedPath);
                    string destinationPath = GetManagedRuntimeDestinationPath(normalizedPath);
                    if (string.IsNullOrEmpty(destinationPath))
                    {
                        processedFiles.Add(normalizedPath);
                        continue;
                    }

                    if (!File.Exists(sourcePath))
                    {
                        _logger($"[MANAGED] Source file missing, skipping: {sourcePath}");
                        processedFiles.Add(normalizedPath);
                        continue;
                    }

                    string? destinationDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                    {
                        Directory.CreateDirectory(destinationDir);
                    }

                    bool shouldCopy = !File.Exists(destinationPath) ||
                                      new FileInfo(sourcePath).Length != new FileInfo(destinationPath).Length;
                    if (shouldCopy)
                    {
                        TrackManagedFileArtifact(destinationPath);
                        File.Copy(sourcePath, destinationPath, true);
                    }

                    string ext = Path.GetExtension(normalizedPath).ToLowerInvariant();
                    if (ext == ".ba2")
                    {
                        archiveMods.Add(normalizedPath);
                    }
                    else if (ext == ".esm" || ext == ".esp")
                    {
                        pluginMods.Add(Path.GetFileName(normalizedPath));
                    }

                    processedFiles.Add(normalizedPath);
                }
            }

            string archiveKey = DetectArchiveKey();
            if (archiveMods.Count > 0)
            {
                TrackArchiveKeySnapshots(archiveKey);
                string archiveList = string.Join(",", archiveMods.Select(NormalizeArchiveForIni));
                _configManager.UpdateBothInis("Archive", archiveKey, archiveList, onlyCustom: true);
            }
            else
            {
                _logger("[MANAGED] No BA2 archives in runtime order; leaving existing Custom.ini archive list unchanged.");
            }
            UpdatePluginsTxt(pluginMods);

            _logger($"[MANAGED] Runtime state prepared. Archives={archiveMods.Count}, Plugins={pluginMods.Count}");
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] PrepareManagedRuntimeState failed: {ex.Message}");
            throw;
        }
    }

    private string GetManagedRuntimeSourcePath(string normalizedPath)
    {
        if (normalizedPath.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, normalizedPath.Replace("/", "\\"));
        }

        if (normalizedPath.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(normalizedPath.Substring("GameRoot/".Length));
            return Path.Combine(AppPaths.ManagedStagingGameRootPath, fn);
        }

        return Path.Combine(AppPaths.ManagedStagingDataPath, normalizedPath.Replace("/", "\\"));
    }

    private string GetManagedRuntimeDestinationPath(string normalizedPath)
    {
        if (normalizedPath.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(AppPaths.GameInstallRoot)) return "";
            string fn = Path.GetFileName(normalizedPath.Substring("GameRoot/".Length));
            return Path.Combine(AppPaths.GameInstallRoot, fn);
        }

        if (normalizedPath.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase))
            return "";

        if (string.IsNullOrEmpty(AppPaths.DataPath)) return "";

        if (normalizedPath.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(AppPaths.DataPath, normalizedPath.Replace("/", "\\"));
        }

        if (normalizedPath.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase))
        {
            string fileName = normalizedPath.Substring("Strings/".Length).Replace("/", "\\");
            return Path.Combine(AppPaths.StringsPath, fileName);
        }

        return Path.Combine(AppPaths.DataPath, normalizedPath.Replace("/", "\\"));
    }

    private void UpdatePluginsTxt(List<string> plugins)
    {
        try {
            string pluginsPath = AppPaths.PluginsFilePath;
            string fallout76Dir = Path.GetDirectoryName(pluginsPath) ?? "";

            if (!string.IsNullOrEmpty(fallout76Dir) && !Directory.Exists(fallout76Dir)) Directory.CreateDirectory(fallout76Dir);
            TrackPluginsSnapshot();

            var lines = plugins.Select(p => "*" + p).ToList();
            File.WriteAllLines(pluginsPath, lines);
            _logger($"[MODS] plugins.txt updated with {plugins.Count} plugins.");
        } catch (Exception ex) {
            _logger($"[ERROR] Failed to update plugins.txt: {ex.Message}");
        }
    }



    private void UpdateStringMods()
    {
        try {
            string stringsDir = Path.Combine(GetActiveDataPath(), "Strings");
            if (!Directory.Exists(stringsDir)) return;

            var stringFiles = Directory.GetFiles(stringsDir, "*.*")
                .Where(f => f.EndsWith(".strings", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".dlstrings", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".ilstrings", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .ToList();

            _logger($"[MODS] detected {stringFiles.Count} string files in Data/Strings.");
        } catch (Exception ex) {
             _logger($"[ERROR] UpdateStringMods failed: {ex.Message}");
        }
    }

    private bool IsVanillaFile(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;
        if (relativePath.Replace("\\", "/").StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase))
            return false;
        string name = Path.GetFileName(relativePath);

        if (name.Equals("Fallout76Custom.ini", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("Fallout76Prefs.ini", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("Project76Custom.ini", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("Project76Prefs.ini", StringComparison.OrdinalIgnoreCase)) return false;

        if (name.StartsWith("SeventySix", StringComparison.OrdinalIgnoreCase)) {
             string ext = Path.GetExtension(name).ToLowerInvariant();
             if (ext != ".strings" && ext != ".dlstrings" && ext != ".ilstrings") return true;
        }

        if (name.Contains("Outline", StringComparison.OrdinalIgnoreCase)) return false; 
        if (name.Contains("ESP_", StringComparison.OrdinalIgnoreCase)) return false;

        if (name.StartsWith("Fallout76", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("Fallout76.esm", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("NW.esm", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("Update.esm", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("DLCCoast.esm", StringComparison.OrdinalIgnoreCase)) return true;

        var vanilla = GetBaseArchives();
        return vanilla.Any(v => v.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private List<string> GetBaseArchives()
    {
        return new List<string> {
            "Fallout76 - Animations.ba2",
            "Fallout76 - Interface.ba2",
            "Fallout76 - Main.ba2",
            "Fallout76 - Meshes.ba2",
            "Fallout76 - Meshes2.ba2",
            "Fallout76 - Sounds.ba2",
            "Fallout76 - Startup.ba2",
            "Fallout76 - Textures.ba2",
            "Fallout76 - Textures2.ba2",
            "Fallout76 - Textures3.ba2",
            "Fallout76 - Textures4.ba2",
            "Fallout76 - Textures5.ba2",
            "Fallout76 - Textures6.ba2"
        };
    }

    private string DetectArchiveKey()
    {
        const string archive2Key = "sResourceArchive2List";
        const string indexFileKey = "sResourceIndexFileList";

        if (!string.IsNullOrEmpty(ArchiveKeyPreference) && ArchiveKeyPreference != "auto")
        {
            _logger($"[INI-DETECT] Using user preference: {ArchiveKeyPreference}");
            return ArchiveKeyPreference;
        }

        try
        {
            string customIni = AppPaths.CustomIniPath;
            if (!File.Exists(customIni)) return archive2Key;

            string archive2Val = _configManager.ReadIniValue(customIni, "Archive", archive2Key);
            string indexFileVal = _configManager.ReadIniValue(customIni, "Archive", indexFileKey);

            bool hasArchive2 = archive2Val != null;
            bool hasIndexFile = indexFileVal != null;

            if (hasIndexFile && !hasArchive2)
            {
                _logger($"[INI-DETECT] Found {indexFileKey} only — using it.");
                return indexFileKey;
            }

            return archive2Key;
        }
        catch (Exception ex)
        {
            _logger($"[INI-DETECT] Detection failed, defaulting to {archive2Key}: {ex.Message}");
            return archive2Key;
        }
    }

    private static string NormalizeArchiveForIni(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string n = path.Replace("\\", "/").Trim();
        if (n.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
            n = n.Substring("Disabled/".Length);
        if (n.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase))
            return n;
        return Path.GetFileName(n);
    }

    private List<string> CollectDeployArchivesForModOrder(List<string> modOrder, Dictionary<string, ModMetadata> metadata)
    {
        var archiveMods = new List<string>();
        var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string modName in modOrder)
        {
            string key = modName;
            if (!metadata.ContainsKey(key))
            {
                string fileName = Path.GetFileName(modName);
                if (metadata.ContainsKey(fileName)) key = fileName;
            }

            var filesResult = new List<string>();
            if (metadata.ContainsKey(key))
                filesResult.AddRange(metadata[key].Files);

            if (filesResult.Count == 0)
            {
                string potentialPath = Path.Combine(GetActiveDataPath(), modName);
                if (File.Exists(potentialPath))
                    filesResult.Add(modName);
            }

            foreach (string relativePath in filesResult)
            {
                string normalizedPath = relativePath.Replace("\\", "/");
                if (processedFiles.Contains(normalizedPath)) continue;

                string extLoose = Path.GetExtension(normalizedPath).ToLowerInvariant();
                if (DeployLooseConfigExtensions.Contains(extLoose))
                {
                    processedFiles.Add(normalizedPath);
                    continue;
                }

                if (normalizedPath.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase) &&
                    IsGameRootInjectorName(Path.GetFileName(normalizedPath)))
                {
                    processedFiles.Add(normalizedPath);
                    continue;
                }

                if (Path.GetExtension(normalizedPath).Equals(".ba2", StringComparison.OrdinalIgnoreCase))
                {
                    archiveMods.Add(NormalizeArchiveForIni(normalizedPath));
                    processedFiles.Add(normalizedPath);
                }
            }
        }

        return archiveMods;
    }

    private List<string> GetEnabledModNamesInLoadOrder()
    {
        return GetModsList()
            .Select(m => (dynamic)m)
            .Where(m => (string)m.status == "enabled")
            .OrderBy(m => (int)(m.loadOrder ?? 9999))
            .Select(m => (string)m.originalName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
    }

    public List<string> GetExpectedEnabledArchiveList()
    {
        var metadata = LoadMetadata();
        var modOrder = GetEnabledModNamesInLoadOrder();
        return CollectDeployArchivesForModOrder(modOrder, metadata);
    }

    public (bool InSync, string State, string? Detail) GetDeploySyncStatus()
    {
        if (VirtualModMode)
            return (true, "virtual", null);

        try
        {
            var expected = GetExpectedEnabledArchiveList();
            string archiveKey = DetectArchiveKey();
            string customIni = AppPaths.CustomIniPath;

            if (string.IsNullOrWhiteSpace(customIni) || !File.Exists(customIni))
                return (false, "stale", "Custom.ini not found");

            string? currentVal = _configManager.ReadIniValue(customIni, "Archive", archiveKey) ?? "";
            var current = currentVal
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeArchiveForIni)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (current.Count != expected.Count)
                return (false, "stale", $"{current.Count} in INI vs {expected.Count} enabled");

            for (int i = 0; i < expected.Count; i++)
            {
                if (!string.Equals(current[i], expected[i], StringComparison.OrdinalIgnoreCase))
                    return (false, "stale", "Load order in INI differs from manager");
            }

            return (true, "fresh", null);
        }
        catch (Exception ex)
        {
            return (false, "unknown", ex.Message);
        }
    }
}
