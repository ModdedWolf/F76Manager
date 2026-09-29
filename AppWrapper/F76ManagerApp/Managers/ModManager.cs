using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace F76ManagerApp.Managers;

public partial class ModManager
{
    private GameConfigManager _configManager;
    private Action<string> _logger;
    private Action<string, string> _statusReporter;

    public string ArchiveKeyPreference { get; set; } = "auto";
    public bool VirtualModMode { get; set; } = false;

    public string SevenZipPath { get; set; } = "";

    public string RarExtractorPath { get; set; } = "";

    private readonly object _modsListCacheLock = new();
    private List<object>? _modsListCache;
    private string? _modsListCacheKey;

    public bool LastImportReportedIssue { get; private set; }

    private string GetActiveDataPath() => VirtualModMode ? AppPaths.ManagedStagingDataPath : AppPaths.DataPath;
    private string GetActiveStringsPath() => VirtualModMode ? AppPaths.ManagedStagingStringsPath : AppPaths.StringsPath;

    private void EnsureActiveDataFolders()
    {
        string dataPath = GetActiveDataPath();
        string stringsPath = GetActiveStringsPath();
        if (!string.IsNullOrEmpty(dataPath) && !Directory.Exists(dataPath)) Directory.CreateDirectory(dataPath);
        if (!string.IsNullOrEmpty(stringsPath) && !Directory.Exists(stringsPath)) Directory.CreateDirectory(stringsPath);
        if (VirtualModMode && !string.IsNullOrEmpty(AppPaths.ManagedStagingGameRootPath) && !Directory.Exists(AppPaths.ManagedStagingGameRootPath))
            Directory.CreateDirectory(AppPaths.ManagedStagingGameRootPath);
    }

    private static readonly string[] GameRootInjectorBaseNames = { "dxgi.dll", "d3d11.dll" };

    internal static bool IsGameRootInjectorName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        return GameRootInjectorBaseNames.Contains(Path.GetFileName(fileName), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGameRootInjectorListPath(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath)) return false;
        string n = normalizedPath.Replace("\\", "/").Trim();
        if (n.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase))
            return IsGameRootInjectorName(n.Substring("Disabled/GameRoot/".Length));
        if (n.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase))
            return IsGameRootInjectorName(n.Substring("GameRoot/".Length));
        return false;
    }

    private IEnumerable<string> DiscoverGameRootInjectorListPaths()
    {
        string installRoot = AppPaths.GameInstallRoot;
        string stagingRoot = AppPaths.ManagedStagingGameRootPath;
        foreach (string name in GameRootInjectorBaseNames)
        {
            string pathInstall = string.IsNullOrEmpty(installRoot) ? "" : Path.Combine(installRoot, name);
            string pathDisabled = Path.Combine(AppPaths.DisabledModsPath, name);
            string pathStaging = Path.Combine(stagingRoot, name);

            bool onInstall = !string.IsNullOrEmpty(pathInstall) && File.Exists(pathInstall);
            bool onDisabled = File.Exists(pathDisabled);
            bool onStaging = VirtualModMode && File.Exists(pathStaging);

            if (onInstall && (onDisabled || onStaging))
                _logger($"[WARN] Injector {name} exists in multiple locations; preferring game install folder.");

            if (onInstall)
            {
                yield return $"GameRoot/{name}";
                continue;
            }

            if (VirtualModMode && onStaging)
            {
                yield return $"GameRoot/{name}";
                continue;
            }

            if (onDisabled)
                yield return $"Disabled/GameRoot/{name}";
        }
    }

    private bool TryMoveGameRootInjector(string baseName, bool enable, out string error)
    {
        error = string.Empty;
        try
        {
            if (!IsGameRootInjectorName(baseName))
            {
                error = "Not a managed game-root injector DLL.";
                return false;
            }

            string enabledDir = VirtualModMode ? AppPaths.ManagedStagingGameRootPath : AppPaths.GameInstallRoot;
            if (string.IsNullOrEmpty(enabledDir))
            {
                error = "Game install path is not configured.";
                return false;
            }

            if (!Directory.Exists(AppPaths.DisabledModsPath))
                Directory.CreateDirectory(AppPaths.DisabledModsPath);
            if (VirtualModMode && !Directory.Exists(enabledDir))
                Directory.CreateDirectory(enabledDir);

            string enabledPath = Path.Combine(enabledDir, baseName);
            string disabledPath = Path.Combine(AppPaths.DisabledModsPath, baseName);

            if (enable)
            {
                if (File.Exists(disabledPath))
                {
                    File.Move(disabledPath, enabledPath, true);
                    _logger($"[MODS] Enabled game-root injector: moved {baseName} to {(VirtualModMode ? "managed staging GameRoot" : "game folder")}");
                }
            }
            else
            {
                if (File.Exists(enabledPath))
                {
                    File.Move(enabledPath, disabledPath, true);
                    _logger($"[MODS] Disabled game-root injector: moved {baseName} to Disabled Mods");
                }
                if (!VirtualModMode && !string.IsNullOrEmpty(AppPaths.GameInstallRoot))
                {
                    string livePath = Path.Combine(AppPaths.GameInstallRoot, baseName);
                    if (File.Exists(livePath))
                    {
                        File.Move(livePath, disabledPath, true);
                        _logger($"[MODS] Disabled game-root injector: moved {baseName} from game folder to Disabled Mods");
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private string? InstallGameRootInjector(
        string sourcePath,
        string fileName,
        Dictionary<string, ModMetadata> metadata,
        List<string> importedKeys,
        bool enable = true)
    {
        if (!IsGameRootInjectorName(fileName)) return null;
        fileName = Path.GetFileName(fileName);

        string enabledDir = VirtualModMode ? AppPaths.ManagedStagingGameRootPath : AppPaths.GameInstallRoot;
        if (enable && string.IsNullOrEmpty(enabledDir))
        {
            _logger($"[IMPORT] Game install path is not configured; {fileName} imported as disabled.");
            enable = false;
        }

        string destDir = enable ? enabledDir : AppPaths.DisabledModsPath;
        if (!Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        string dest = Path.Combine(destDir, fileName);
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, dest, true);

        string otherPath = enable
            ? Path.Combine(AppPaths.DisabledModsPath, fileName)
            : Path.Combine(enabledDir ?? "", fileName);
        if (!string.IsNullOrEmpty(enabledDir) && File.Exists(otherPath) &&
            !string.Equals(Path.GetFullPath(otherPath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(otherPath); }
            catch (Exception ex) { _logger($"[WARN] Could not remove old {fileName} copy '{otherPath}': {ex.Message}"); }
        }

        MigrateInjectorMetadataKey(metadata, fileName, enable);
        string key = enable ? $"GameRoot/{fileName}" : $"Disabled/GameRoot/{fileName}";
        if (!metadata.TryGetValue(key, out var meta) || meta == null)
        {
            metadata[key] = new ModMetadata
            {
                Name = Path.GetFileNameWithoutExtension(fileName),
                Files = new List<string> { key },
                IsEnabled = enable
            };
        }
        else
        {
            meta.IsEnabled = enable;
        }

        importedKeys.Add(key);
        _logger($"[IMPORT] Injector {fileName} installed to {(enable ? (VirtualModMode ? "managed staging GameRoot" : "game folder") : "Disabled Mods")}.");
        return key;
    }

    private void MigrateInjectorMetadataKey(Dictionary<string, ModMetadata> metadata, string baseName, bool nowEnabled)
    {
        string enabledKey = $"GameRoot/{baseName}";
        string disabledKey = $"Disabled/GameRoot/{baseName}";
        string oldKey = nowEnabled ? disabledKey : enabledKey;
        string newKey = nowEnabled ? enabledKey : disabledKey;

        if (metadata.TryGetValue(oldKey, out var meta) && meta != null)
        {
            metadata.Remove(oldKey);
            meta.IsEnabled = nowEnabled;
            if (meta.Files == null || meta.Files.Count == 0)
                meta.Files = new List<string> { newKey };
            else
                meta.Files = meta.Files.Select(f =>
                    string.Equals(f, oldKey, StringComparison.OrdinalIgnoreCase) ? newKey : f).ToList();
            metadata[newKey] = meta;
        }
        else if (metadata.TryGetValue(newKey, out var existing) && existing != null)
        {
            existing.IsEnabled = nowEnabled;
        }
    }

    private void MigrateDataModMetadataKey(Dictionary<string, ModMetadata> metadata, string fileNameOnly, bool nowEnabled)
    {
        string bareKey = NormalizeMetadataKey(fileNameOnly);
        if (string.IsNullOrWhiteSpace(bareKey) ||
            bareKey.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase) ||
            bareKey.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase) ||
            bareKey.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase) ||
            bareKey.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string enabledKey = bareKey;
        string disabledKey = $"Disabled/{bareKey}";
        string oldKey = nowEnabled ? disabledKey : enabledKey;
        string newKey = nowEnabled ? enabledKey : disabledKey;

        if (metadata.TryGetValue(oldKey, out var meta) && meta != null)
        {
            metadata.Remove(oldKey);
            meta.IsEnabled = nowEnabled;
            if (meta.Files == null || meta.Files.Count == 0)
                meta.Files = new List<string> { newKey };
            else
                meta.Files = meta.Files.Select(f =>
                    string.Equals(NormalizeMetadataKey(f), oldKey, StringComparison.OrdinalIgnoreCase) ? newKey : f).ToList();

            if (metadata.TryGetValue(newKey, out var existingAtNew) && existingAtNew != null)
                metadata[newKey] = MergeMetadataEntries(meta, existingAtNew);
            else
                metadata[newKey] = meta;
        }
        else if (metadata.TryGetValue(newKey, out var existing) && existing != null)
        {
            existing.IsEnabled = nowEnabled;
        }
    }

    private static IEnumerable<string> EnumerateDataModSiblingKeys(string relativeOrFileName)
    {
        string bare = NormalizeStaticRel(relativeOrFileName ?? "");
        if (bare.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
            bare = bare.Substring("Disabled/".Length);
        if (string.IsNullOrWhiteSpace(bare)) yield break;
        yield return bare;
        yield return $"Disabled/{bare}";
        string leaf = Path.GetFileName(bare);
        if (!string.IsNullOrWhiteSpace(leaf) &&
            !string.Equals(leaf, bare, StringComparison.OrdinalIgnoreCase))
        {
            yield return leaf;
            yield return $"Disabled/{leaf}";
        }
    }

    private ModMetadata MergeMissingFieldsFromSiblings(
        Dictionary<string, ModMetadata> metadata,
        ModMetadata meta,
        string normalizedFile,
        string fileNameOnly)
    {
        if (meta == null) meta = new ModMetadata();
        bool needsNexus = !meta.NexusModId.HasValue;
        bool needsAuthor = string.IsNullOrWhiteSpace(meta.Author) || meta.Author == "Unknown";
        bool needsVersion = string.IsNullOrWhiteSpace(meta.Version) || meta.Version == "1.0";
        bool needsUrl = string.IsNullOrWhiteSpace(meta.URL);
        bool needsDetails = string.IsNullOrWhiteSpace(meta.Details);
        bool needsName = string.IsNullOrWhiteSpace(meta.Name) || meta.Name == "Unknown Mod";
        if (!needsNexus && !needsAuthor && !needsVersion && !needsUrl && !needsDetails && !needsName)
            return meta;

        var siblingKeys = new List<string>(EnumerateDataModSiblingKeys(fileNameOnly))
        {
            $"Bundles/{fileNameOnly}",
            $"Strings/{fileNameOnly}"
        };
        if (IsGameRootInjectorName(fileNameOnly))
        {
            siblingKeys.Add($"GameRoot/{fileNameOnly}");
            siblingKeys.Add($"Disabled/GameRoot/{fileNameOnly}");
        }

        string currentKey = NormalizeMetadataKey(normalizedFile);
        foreach (var sibling in siblingKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(sibling, currentKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!metadata.TryGetValue(sibling, out var siblingMeta) || siblingMeta == null) continue;
            meta = MergeMetadataEntries(meta, siblingMeta);
        }
        return meta;
    }

    private bool IsManagedStagingEmpty()
    {
        string stagingData = AppPaths.ManagedStagingDataPath;
        string stagingStrings = AppPaths.ManagedStagingStringsPath;

        bool hasMainMods = Directory.Exists(stagingData) && Directory.GetFiles(stagingData, "*.*", SearchOption.TopDirectoryOnly)
            .Any(f => f.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase) ||
                      f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                      f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase));

        bool hasStrings = Directory.Exists(stagingStrings) && Directory.GetFiles(stagingStrings, "*.*", SearchOption.TopDirectoryOnly)
            .Any(f => f.EndsWith(".strings", StringComparison.OrdinalIgnoreCase) ||
                      f.EndsWith(".dlstrings", StringComparison.OrdinalIgnoreCase) ||
                      f.EndsWith(".ilstrings", StringComparison.OrdinalIgnoreCase));

        string stagingGameRoot = AppPaths.ManagedStagingGameRootPath;
        bool hasGameRootInjectors = Directory.Exists(stagingGameRoot) &&
            Directory.GetFiles(stagingGameRoot, "*.*", SearchOption.TopDirectoryOnly)
                .Any(f => IsGameRootInjectorName(Path.GetFileName(f)));

        return !hasMainMods && !hasStrings && !hasGameRootInjectors;
    }

    private int SyncLiveLooseConfigsIntoStaging()
    {
        if (!VirtualModMode) return 0;

        string liveDataPath = AppPaths.DataPath;
        string stagingData = AppPaths.ManagedStagingDataPath;
        if (string.IsNullOrWhiteSpace(liveDataPath) || !Directory.Exists(liveDataPath) ||
            string.IsNullOrWhiteSpace(stagingData))
            return 0;

        int copied = 0;
        try
        {
            if (!Directory.Exists(stagingData))
                Directory.CreateDirectory(stagingData);

            foreach (var rel in EnumerateLooseConfigListKeys(liveDataPath))
            {
                string key = NormalizeMetadataKey(rel);
                if (string.IsNullOrWhiteSpace(key) || IsVanillaFile(key)) continue;
                if (key.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase)) continue;

                string livePath = Path.Combine(liveDataPath, key.Replace("/", "\\"));
                if (!File.Exists(livePath)) continue;

                string disabledPath = Path.Combine(AppPaths.DisabledModsPath, key.Replace("/", "\\"));
                if (File.Exists(disabledPath)) continue;

                string stagingPath = Path.Combine(stagingData, key.Replace("/", "\\"));
                if (File.Exists(stagingPath)) continue;

                string? stagingDir = Path.GetDirectoryName(stagingPath);
                if (!string.IsNullOrEmpty(stagingDir) && !Directory.Exists(stagingDir))
                    Directory.CreateDirectory(stagingDir);

                File.Copy(livePath, stagingPath, false);
                copied++;
                _logger($"[MANAGED] Synced live loose config into staging: {key}");
            }
        }
        catch (Exception ex)
        {
            _logger($"[MANAGED] Failed to sync live loose configs into staging: {ex.Message}");
        }

        return copied;
    }

    public int EnsureManagedStagingHydrated()
    {
        if (!VirtualModMode) return 0;

        EnsureActiveDataFolders();
        LogLegacyManagedStagingPresence();

        int copied = SyncLiveLooseConfigsIntoStaging();

        if (!IsManagedStagingEmpty())
        {
            if (copied > 0)
                _logger($"[MANAGED] Synced {copied} live loose config(s) into existing staging.");
            return copied;
        }

        string liveDataPath = AppPaths.DataPath;
        string liveStringsPath = AppPaths.StringsPath;

        try
        {
            if (Directory.Exists(liveDataPath))
            {
                foreach (var file in Directory.GetFiles(liveDataPath, "*.*", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(file);
                    string ext = Path.GetExtension(fileName).ToLowerInvariant();
                    bool isModFile = ext == ".ba2" || ext == ".esm" || ext == ".esp" || ext == ".ini";
                    if (!isModFile || IsVanillaFile(fileName)) continue;

                    string destination = Path.Combine(AppPaths.ManagedStagingDataPath, fileName);
                    if (!File.Exists(destination))
                    {
                        File.Copy(file, destination, true);
                        copied++;
                    }
                }
            }

            if (Directory.Exists(liveStringsPath))
            {
                foreach (var file in Directory.GetFiles(liveStringsPath, "*.*", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(file);
                    if (!TryParseStringModDiskName(fileName, out _, out _)) continue;

                    string destination = Path.Combine(AppPaths.ManagedStagingStringsPath, fileName);
                    if (!File.Exists(destination))
                    {
                        File.Copy(file, destination, true);
                        copied++;
                    }
                }
            }

            string liveInstallRoot = AppPaths.GameInstallRoot;
            if (!string.IsNullOrEmpty(liveInstallRoot) && Directory.Exists(liveInstallRoot))
            {
                if (!Directory.Exists(AppPaths.ManagedStagingGameRootPath))
                    Directory.CreateDirectory(AppPaths.ManagedStagingGameRootPath);
                foreach (string name in GameRootInjectorBaseNames)
                {
                    string src = Path.Combine(liveInstallRoot, name);
                    string destination = Path.Combine(AppPaths.ManagedStagingGameRootPath, name);
                    if (!File.Exists(src) || File.Exists(destination)) continue;
                    File.Copy(src, destination, true);
                    copied++;
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[MANAGED] Failed to hydrate staging: {ex.Message}");
        }

        if (copied > 0)
        {
            _logger($"[MANAGED] Hydrated managed staging with {copied} existing mod files.");
        }

        return copied;
    }

    public bool IsVanillaModFile(string relativePath) => IsVanillaFile(relativePath);

    public int TransferModsToPlatform(
        string sourceDataDir, string sourceStringsDir, string sourceGameRootDir,
        string destDataDir, string destStringsDir, string destGameRootDir,
        bool overwrite)
    {
        int copied = 0;

        if (string.IsNullOrWhiteSpace(sourceDataDir) || !Directory.Exists(sourceDataDir))
        {
            _logger($"[TRANSFER] Source data folder not found: '{sourceDataDir}'");
            return 0;
        }

        try
        {
            Directory.CreateDirectory(destDataDir);

            foreach (var file in Directory.GetFiles(sourceDataDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                if (IsVanillaFile(fileName)) continue;
                string dest = Path.Combine(destDataDir, fileName);
                if (File.Exists(dest) && !overwrite) continue;
                CopyFileOrThrow(file, dest, true);
                copied++;
            }

            foreach (var dir in Directory.GetDirectories(sourceDataDir))
            {
                string folderName = Path.GetFileName(dir);
                if (folderName.Equals("Strings", StringComparison.OrdinalIgnoreCase)) continue;
                copied += CopyDirectoryRecursive(dir, Path.Combine(destDataDir, folderName), overwrite);
            }

            if (!string.IsNullOrWhiteSpace(sourceStringsDir) && Directory.Exists(sourceStringsDir))
            {
                Directory.CreateDirectory(destStringsDir);
                foreach (var file in Directory.GetFiles(sourceStringsDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".strings" && ext != ".dlstrings" && ext != ".ilstrings") continue;
                    string dest = Path.Combine(destStringsDir, Path.GetFileName(file));
                    if (File.Exists(dest) && !overwrite) continue;
                    CopyFileOrThrow(file, dest, true);
                    copied++;
                }
            }

            if (!string.IsNullOrWhiteSpace(sourceGameRootDir) && Directory.Exists(sourceGameRootDir) &&
                !string.IsNullOrWhiteSpace(destGameRootDir))
            {
                foreach (string name in GameRootInjectorBaseNames)
                {
                    string src = Path.Combine(sourceGameRootDir, name);
                    if (!File.Exists(src)) continue;
                    Directory.CreateDirectory(destGameRootDir);
                    string dest = Path.Combine(destGameRootDir, name);
                    if (File.Exists(dest) && !overwrite) continue;
                    CopyFileOrThrow(src, dest, true);
                    copied++;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger($"[TRANSFER] Failed copying mods: {ex.Message}");
            throw;
        }

        _logger($"[TRANSFER] Copied {copied} item(s) from '{sourceDataDir}' to '{destDataDir}'.");
        return copied;
    }

    private int CopyDirectoryRecursive(string sourceDir, string destDir, bool overwrite)
    {
        int count = 0;
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string dest = Path.Combine(destDir, Path.GetFileName(file));
            if (File.Exists(dest) && !overwrite) continue;
            CopyFileOrThrow(file, dest, true);
            count++;
        }
        foreach (var sub in Directory.GetDirectories(sourceDir))
            count += CopyDirectoryRecursive(sub, Path.Combine(destDir, Path.GetFileName(sub)), overwrite);
        return count;
    }

    private static void CopyFileOrThrow(string sourcePath, string destPath, bool overwrite)
    {
        try
        {
            File.Copy(sourcePath, destPath, overwrite);
        }
        catch (IOException ex)
        {
            throw new IOException($"Unable to copy '{Path.GetFileName(sourcePath)}': {ex.Message}", ex);
        }
    }

    private void LogLegacyManagedStagingPresence()
    {
        try
        {
            string legacyRoot = AppPaths.LegacyManagedStagingPath;
            if (!Directory.Exists(legacyRoot)) return;

            bool hasLegacyMods = Directory.GetFiles(legacyRoot, "*.*", SearchOption.TopDirectoryOnly)
                .Any(f => f.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase) ||
                          f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                          f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase));

            string legacyStrings = Path.Combine(legacyRoot, "Strings");
            bool hasLegacyStrings = Directory.Exists(legacyStrings) && Directory.GetFiles(legacyStrings, "*.*", SearchOption.TopDirectoryOnly)
                .Any(f => f.EndsWith(".strings", StringComparison.OrdinalIgnoreCase) ||
                          f.EndsWith(".dlstrings", StringComparison.OrdinalIgnoreCase) ||
                          f.EndsWith(".ilstrings", StringComparison.OrdinalIgnoreCase));

            if (hasLegacyMods || hasLegacyStrings)
            {
                _logger($"[MANAGED] Legacy shared staging detected at '{legacyRoot}'. It is intentionally ignored to prevent cross-platform contamination.");
            }
        }
        catch (Exception ex)
        {
            _logger($"[MANAGED] Failed while checking legacy managed staging: {ex.Message}");
        }
    }

    public ModManager(GameConfigManager configManager, Action<string> logger, Action<string, string> statusReporter)
    {
        _configManager = configManager;
        _logger = logger;
        _statusReporter = statusReporter;
#if DEBUG
        _logger($"[DEBUG] ModManager initialized.");
#endif
        
        if (!string.IsNullOrEmpty(AppPaths.BundlesPath) && !Directory.Exists(AppPaths.BundlesPath)) {
            Directory.CreateDirectory(AppPaths.BundlesPath);
            _logger($"[DEBUG] Created Bundles folder.");
        }
        
        if (!string.IsNullOrEmpty(AppPaths.DisabledModsPath) && !Directory.Exists(AppPaths.DisabledModsPath)) {
            Directory.CreateDirectory(AppPaths.DisabledModsPath);
            _logger($"[DEBUG] Created Disabled Mods folder.");
        }

        EnsureActiveDataFolders();
    }

    public void InvalidateModsListCache()
    {
        lock (_modsListCacheLock)
        {
            _modsListCache = null;
            _modsListCacheKey = null;
        }
    }

    private string GetModsListCacheKey() =>
        $"{VirtualModMode}|{GetActiveDataPath()}|{GetActiveStringsPath()}|{AppPaths.BundlesPath}|{AppPaths.DisabledModsPath}";

        public class ModMetadata
        {
            public string Name { get; set; } = "Unknown Mod";
            public string Author { get; set; } = "Unknown";
            public string Version { get; set; } = "1.0";
            public string URL { get; set; } = "";
            public string Category { get; set; } = "General";
            public string Details { get; set; } = "";
            public bool IsEnabled { get; set; } = false;
            public bool IsBundle { get; set; } = false;
            public bool IsLoose { get; set; } = false;
            public int LoadOrder { get; set; } = 0;
            public List<string> Files { get; set; } = new List<string>();
            public List<string> Directories { get; set; } = new List<string>();
            public long? NexusModId { get; set; }
            public long? NexusFileId { get; set; }
            public string NexusFileVersion { get; set; } = "";
            public long? NexusFileUploaded { get; set; }
        }



    private string NormalizeMetadataKey(string raw)
    {
        string key = (raw ?? "").Replace("\\", "/").Trim();
        while (key.Contains("  ")) key = key.Replace("  ", " ");
        while (key.Contains(" .")) key = key.Replace(" .", ".");
        return key;
    }

    private string NormalizeLooseMetadataKey(string raw)
    {
        return NormalizeMetadataKey(raw).ToLowerInvariant();
    }

    private ModMetadata MergeMetadataEntries(ModMetadata existing, ModMetadata incoming)
    {
        var result = existing ?? new ModMetadata();
        var source = incoming ?? new ModMetadata();

        if (string.IsNullOrWhiteSpace(result.Name) || result.Name == "Unknown Mod")
        {
            if (!string.IsNullOrWhiteSpace(source.Name) && source.Name != "Unknown Mod")
            {
                result.Name = source.Name;
            }
        }
        if ((string.IsNullOrWhiteSpace(result.Author) || result.Author == "Unknown") &&
            !string.IsNullOrWhiteSpace(source.Author) && source.Author != "Unknown")
        {
            result.Author = source.Author;
        }
        if ((string.IsNullOrWhiteSpace(result.Version) || result.Version == "1.0") &&
            !string.IsNullOrWhiteSpace(source.Version) && source.Version != "1.0")
        {
            result.Version = source.Version;
        }
        if (string.IsNullOrWhiteSpace(result.URL) && !string.IsNullOrWhiteSpace(source.URL)) result.URL = source.URL;
        if (!result.NexusModId.HasValue && source.NexusModId.HasValue) result.NexusModId = source.NexusModId;
        if (!result.NexusFileId.HasValue && source.NexusFileId.HasValue) result.NexusFileId = source.NexusFileId;
        if (string.IsNullOrWhiteSpace(result.NexusFileVersion) && !string.IsNullOrWhiteSpace(source.NexusFileVersion))
            result.NexusFileVersion = source.NexusFileVersion;
        if (!result.NexusFileUploaded.HasValue && source.NexusFileUploaded.HasValue)
            result.NexusFileUploaded = source.NexusFileUploaded;
        if ((string.IsNullOrWhiteSpace(result.Category) || result.Category == "General") &&
            !string.IsNullOrWhiteSpace(source.Category) && source.Category != "General")
        {
            result.Category = source.Category;
        }
        if (string.IsNullOrWhiteSpace(result.Details) && !string.IsNullOrWhiteSpace(source.Details))
        {
            result.Details = source.Details;
        }

        result.IsEnabled = result.IsEnabled || source.IsEnabled;
        result.IsBundle = result.IsBundle || source.IsBundle;
        result.IsLoose = result.IsLoose || source.IsLoose;

        bool existingLoadOrderIsDefault = result.LoadOrder == 0 || result.LoadOrder == 9999;
        bool sourceLoadOrderIsSpecific = source.LoadOrder != 0 && source.LoadOrder != 9999;
        if (existingLoadOrderIsDefault && sourceLoadOrderIsSpecific)
        {
            result.LoadOrder = source.LoadOrder;
        }

        var mergedFiles = new List<string>();
        if (result.Files != null) mergedFiles.AddRange(result.Files);
        if (source.Files != null) mergedFiles.AddRange(source.Files);
        result.Files = mergedFiles
            .Select(NormalizeMetadataKey)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.Directories = (result.Directories ?? new List<string>())
            .Concat(source.Directories ?? new List<string>())
            .Select(NormalizeMetadataKey)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    private Dictionary<string, ModMetadata> CanonicalizeMetadata(Dictionary<string, ModMetadata> source)
    {
        var canonical = new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in source ?? new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase))
        {
            string key = NormalizeMetadataKey(kvp.Key);
            if (string.IsNullOrWhiteSpace(key)) continue;

            var value = kvp.Value ?? new ModMetadata();
            if (key.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase))
            {
                value.IsBundle = true;
            }
            if (key.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase))
            {
                value.IsLoose = true;
            }

            if (canonical.TryGetValue(key, out var existing))
            {
                canonical[key] = MergeMetadataEntries(existing, value);
            }
            else
            {
                canonical[key] = MergeMetadataEntries(new ModMetadata(), value);
            }
        }
        return canonical;
    }

    private string ResolveMetadataKey(Dictionary<string, ModMetadata> metadata, string rawName)
    {
        string normalizedName = NormalizeMetadataKey(rawName);
        if (string.IsNullOrWhiteSpace(normalizedName)) return "";

        string fileName = NormalizeMetadataKey(Path.GetFileName(normalizedName));
        bool inputIsPrefixed =
            normalizedName.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase) ||
            normalizedName.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase) ||
            normalizedName.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase) ||
            normalizedName.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase) ||
            normalizedName.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase);

        var candidates = new List<string> { normalizedName };
        if (IsGameRootInjectorName(fileName))
        {
            candidates.Add($"GameRoot/{fileName}");
            candidates.Add($"Disabled/GameRoot/{fileName}");
        }

        if (inputIsPrefixed)
        {
            candidates.Add($"Disabled/{fileName}");
            candidates.Add($"Bundles/{fileName}");
            candidates.Add($"Strings/{fileName}");
            candidates.Add($"Loose/{fileName}");
            candidates.Add(fileName);
        }
        else
        {
            candidates.Add(fileName);
            candidates.Add($"Bundles/{fileName}");
            candidates.Add($"Disabled/{fileName}");
            candidates.Add($"Strings/{fileName}");
            candidates.Add($"Loose/{fileName}");
        }

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (metadata.ContainsKey(c)) return c;
        }

        string looseInput = NormalizeLooseMetadataKey(normalizedName);
        string looseFile = NormalizeLooseMetadataKey(fileName);
        return metadata.Keys.FirstOrDefault(k => {
            string full = NormalizeLooseMetadataKey(k);
            string file = NormalizeLooseMetadataKey(Path.GetFileName(k));
            return full == looseInput || file == looseInput || full == looseFile || file == looseFile;
        }) ?? "";
    }

    public ModMetadata? GetMetadataForMod(string originalName)
    {
        var metadata = LoadMetadata();
        string key = ResolveMetadataKey(metadata, NormalizeMetadataKey(originalName));
        if (string.IsNullOrEmpty(key)) return null;
        return metadata.TryGetValue(key, out var meta) ? meta : null;
    }

    private Dictionary<string, ModMetadata> LoadMetadata()
    {
        try {
            if (File.Exists(AppPaths.ModsMetadataFile)) {
                string json = File.ReadAllText(AppPaths.ModsMetadataFile);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var loaded = JsonSerializer.Deserialize<Dictionary<string, ModMetadata>>(json, options);
                var raw = new Dictionary<string, ModMetadata>(loaded ?? new(), StringComparer.OrdinalIgnoreCase);
                return CanonicalizeMetadata(raw);
            }
        } catch (Exception ex) {
            _logger($"[ERROR] Failed to load metadata from {AppPaths.ModsMetadataFile}: {ex.Message}");
        }
        return new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveMetadata(Dictionary<string, ModMetadata> metadata)
    {
        try {
            var canonical = CanonicalizeMetadata(metadata);
            string json = JsonSerializer.Serialize(canonical, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.ModsMetadataFile, json);
            InvalidateModsListCache();
        } catch (Exception ex) {
            _logger($"[ERROR] Failed to save metadata to {AppPaths.ModsMetadataFile}: {ex.Message}");
        }
    }

    public void UpdateModMetadata(string originalName, ModMetadata meta)
    {
        var allMeta = LoadMetadata();
        string targetKey = originalName;

        if (!allMeta.ContainsKey(targetKey))
        {
            string fileName = Path.GetFileName(originalName);
            string bundlePath = $"Bundles/{fileName}";
            
            if (allMeta.ContainsKey(fileName)) {
                targetKey = fileName;
                _logger($"[DEBUG] Using alternative key: '{fileName}'");
            }
            else if (allMeta.ContainsKey(bundlePath)) {
                targetKey = bundlePath;
                _logger($"[DEBUG] Using bundle path: '{bundlePath}'");
            }
            else if (IsGameRootInjectorName(fileName))
            {
                string gk = $"GameRoot/{fileName}";
                string dk = $"Disabled/GameRoot/{fileName}";
                if (allMeta.ContainsKey(gk)) targetKey = gk;
                else if (allMeta.ContainsKey(dk)) targetKey = dk;
            }
        }


        bool wasEnabled = allMeta.ContainsKey(targetKey) && allMeta[targetKey].IsEnabled;
        bool isLoose = IsLooseModKey(allMeta, originalName) || IsLooseModKey(allMeta, targetKey);
        bool isBundle = !isLoose && (IsBundleModKey(allMeta, originalName) || IsBundleModKey(allMeta, targetKey));
        bool isStrings = originalName.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase);
        bool isGameRootInjector = IsGameRootInjectorListPath(NormalizeMetadataKey(originalName)) ||
                                  IsGameRootInjectorListPath(NormalizeMetadataKey(targetKey));
        
        if (isLoose && wasEnabled != meta.IsEnabled)
        {
            if (!allMeta.TryGetValue(targetKey, out var looseMeta) || looseMeta == null)
                looseMeta = meta;
            if (!TryMoveLoosePackFiles(looseMeta, targetKey, meta.IsEnabled, out var looseMoveError))
                _logger($"[ERROR] Failed to move loose pack during metadata update: {looseMoveError}");
        }
        else if (!isBundle && !isStrings && isGameRootInjector && wasEnabled != meta.IsEnabled)
        {
            string injBase = Path.GetFileName(targetKey);
            if (!IsGameRootInjectorName(injBase))
                injBase = Path.GetFileName(originalName);
            if (!TryMoveGameRootInjector(injBase, meta.IsEnabled, out var injMoveError))
                _logger($"[ERROR] Failed to move game-root injector during metadata update: {injMoveError}");
            else
            {
                MigrateInjectorMetadataKey(allMeta, injBase, meta.IsEnabled);
                targetKey = meta.IsEnabled ? $"GameRoot/{injBase}" : $"Disabled/GameRoot/{injBase}";
            }
        }
        else if (!isBundle && !isLoose && !isStrings && !isGameRootInjector && wasEnabled != meta.IsEnabled)
        {
            string dataRel = NormalizeMetadataKey(originalName);
            if (dataRel.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
                dataRel = dataRel.Substring("Disabled/".Length);
            string moveKey = ConfigFileMerger.IsRootLooseConfigPath(dataRel)
                ? dataRel
                : Path.GetFileName(dataRel);
            if (!TryMoveModBetweenActiveAndDisabled(moveKey, meta.IsEnabled, out var moveError))
            {
                _logger($"[ERROR] Failed to move mod file during metadata update: {moveError}");
            }
            else
            {
                MigrateDataModMetadataKey(allMeta, moveKey, meta.IsEnabled);
                targetKey = meta.IsEnabled
                    ? NormalizeMetadataKey(moveKey)
                    : $"Disabled/{NormalizeMetadataKey(moveKey)}";
            }
        }

        if (allMeta.ContainsKey(targetKey))
        {
            var existing = allMeta[targetKey];
            if (meta.Name != "Unknown Mod") existing.Name = meta.Name;
            if (meta.Author != "Unknown") existing.Author = meta.Author;
            if (meta.Version != "1.0") existing.Version = meta.Version;
            if (!string.IsNullOrEmpty(meta.URL)) existing.URL = meta.URL;
            if (meta.NexusModId.HasValue) existing.NexusModId = meta.NexusModId;
            if (meta.NexusFileId.HasValue) existing.NexusFileId = meta.NexusFileId;
            if (!string.IsNullOrWhiteSpace(meta.NexusFileVersion)) existing.NexusFileVersion = meta.NexusFileVersion;
            if (meta.NexusFileUploaded.HasValue) existing.NexusFileUploaded = meta.NexusFileUploaded;
            if (meta.Category != "General") existing.Category = meta.Category;
            if (meta.Details != null) existing.Details = meta.Details;
            existing.IsEnabled = meta.IsEnabled;
            if (meta.IsBundle) existing.IsBundle = true;
            if (meta.IsLoose) existing.IsLoose = true;
            if (meta.LoadOrder != 0) existing.LoadOrder = meta.LoadOrder;
            if (meta.Files != null && meta.Files.Count > 0) existing.Files = meta.Files;
            
            if (targetKey != originalName)
            {
                allMeta.Remove(targetKey);
                allMeta[originalName] = existing;
            }
            else
            {
                allMeta[targetKey] = existing;
            }
        }
        else
        {
            if (originalName.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase)) {
                 meta.IsBundle = true;
            }
            if (originalName.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase)) {
                 meta.IsLoose = true;
            }
            allMeta[originalName] = meta;
        }
        SaveMetadata(allMeta);
    }

    public bool ToggleModEnabled(string fileName, bool enabled, out string? errorMessage)
    {
        errorMessage = null;
        var metadata = LoadMetadata();
        
        string cleanFileName = fileName;
        if (cleanFileName.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
        {
            cleanFileName = cleanFileName.Substring("Disabled/".Length);
        }
        
        bool isGameRootInj = IsGameRootInjectorListPath(cleanFileName);
        bool isLoose = !isGameRootInj && IsLooseModKey(metadata, cleanFileName);
        bool isBundle = !isGameRootInj && !isLoose && IsBundleModKey(metadata, cleanFileName);
        bool isStrings = cleanFileName.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase);
        bool isInBundlesFolder = cleanFileName.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase);
        
        bool moveSuccess = true;
        string moveError = "";
        string injBase = Path.GetFileName(cleanFileName);

        if (isGameRootInj)
        {
            if (!TryMoveGameRootInjector(injBase, enabled, out moveError))
            {
                moveSuccess = false;
                _logger($"[ERROR] Toggle game-root injector failed: {moveError}");
            }
        }
        else if (isLoose)
        {
            string looseKey = ResolveMetadataKey(metadata, cleanFileName);
            if (string.IsNullOrEmpty(looseKey)) looseKey = NormalizeMetadataKey(cleanFileName);
            if (!metadata.TryGetValue(looseKey, out var looseMeta) || looseMeta == null)
            {
                moveSuccess = false;
                moveError = "Loose pack metadata not found.";
            }
            else if (!TryMoveLoosePackFiles(looseMeta, looseKey, enabled, out moveError))
            {
                moveSuccess = false;
                _logger($"[ERROR] Toggle loose pack failed: {moveError}");
            }
        }
        else if (isStrings)
        {
            if (!TrySetStringModSuffixEnabled(cleanFileName, enabled, out moveError))
            {
                moveSuccess = false;
                _logger($"[ERROR] Toggle string mod failed: {moveError}");
            }
        }
        else if (!isBundle && !isStrings && !isInBundlesFolder)
        {
            string moveKey = ConfigFileMerger.IsRootLooseConfigPath(cleanFileName)
                ? NormalizeMetadataKey(cleanFileName)
                : Path.GetFileName(cleanFileName);
            if (!TryMoveModBetweenActiveAndDisabled(moveKey, enabled, out moveError))
            {
                moveSuccess = false;
                _logger($"[ERROR] Toggle move failed: {moveError}");
            }
        }
        
        if (moveSuccess) {
            if (isGameRootInj)
            {
                MigrateInjectorMetadataKey(metadata, injBase, enabled);
                string stableKey = enabled ? $"GameRoot/{injBase}" : $"Disabled/GameRoot/{injBase}";
                if (!metadata.ContainsKey(stableKey))
                {
                    metadata[stableKey] = new ModMetadata
                    {
                        Name = Path.GetFileNameWithoutExtension(injBase),
                        IsEnabled = enabled,
                        Files = new List<string> { stableKey }
                    };
                }
                else
                {
                    metadata[stableKey].IsEnabled = enabled;
                }
            }
            else if (isLoose)
            {
                string looseKey = ResolveMetadataKey(metadata, cleanFileName);
                if (string.IsNullOrEmpty(looseKey)) looseKey = NormalizeMetadataKey(cleanFileName);
                if (metadata.ContainsKey(looseKey))
                {
                    metadata[looseKey].IsEnabled = enabled;
                    metadata[looseKey].IsLoose = true;
                }
                else
                {
                    metadata[looseKey] = new ModMetadata
                    {
                        Name = SanitizeLoosePackName(looseKey),
                        IsEnabled = enabled,
                        IsLoose = true,
                        Files = new List<string>()
                    };
                }
            }
            else if (!isBundle && !isStrings && !isInBundlesFolder)
            {
                string dataKey = ConfigFileMerger.IsRootLooseConfigPath(cleanFileName)
                    ? NormalizeMetadataKey(cleanFileName)
                    : Path.GetFileName(cleanFileName);
                MigrateDataModMetadataKey(metadata, dataKey, enabled);
                string stableKey = enabled ? NormalizeMetadataKey(dataKey) : $"Disabled/{NormalizeMetadataKey(dataKey)}";
                if (!metadata.ContainsKey(stableKey))
                {
                    string resolved = ResolveMetadataKey(metadata, stableKey);
                    if (!string.IsNullOrEmpty(resolved) &&
                        !string.Equals(resolved, stableKey, StringComparison.OrdinalIgnoreCase) &&
                        metadata.TryGetValue(resolved, out var resolvedMeta) &&
                        resolvedMeta != null)
                    {
                        metadata.Remove(resolved);
                        resolvedMeta.IsEnabled = enabled;
                        if (resolvedMeta.Files == null || resolvedMeta.Files.Count == 0)
                            resolvedMeta.Files = new List<string> { stableKey };
                        metadata[stableKey] = resolvedMeta;
                    }
                    else
                    {
                        metadata[stableKey] = new ModMetadata
                        {
                            Name = Path.GetFileNameWithoutExtension(dataKey),
                            IsEnabled = enabled,
                            Files = new List<string> { stableKey }
                        };
                    }
                }
                else
                {
                    metadata[stableKey].IsEnabled = enabled;
                }
            }
            else if (metadata.ContainsKey(cleanFileName))
            {
                metadata[cleanFileName].IsEnabled = enabled;
            }
            else
            {
                metadata[cleanFileName] = new ModMetadata 
                { 
                    Name = Path.GetFileNameWithoutExtension(cleanFileName),
                    IsEnabled = enabled,
                    Files = new List<string> { cleanFileName }
                };
            }
            SaveMetadata(metadata);
            SyncArchiveListToCustomIni();
        } else {
            errorMessage = moveError;
            _logger($"[ERROR] Toggle mod failed: {moveError}");
        }

        return moveSuccess;
    }

    private static IEnumerable<string> EnumerateLooseConfigListKeys(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot))
            yield break;

        foreach (var path in Directory.GetFiles(dataRoot, "*.*"))
        {
            string ext = Path.GetExtension(path);
            if (!ConfigFileMerger.IsLooseConfigExtension(ext)) continue;
            string name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) continue;
            yield return name;
        }

        foreach (var dir in Directory.GetDirectories(dataRoot))
        {
            string top = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(top) ||
                ConfigFileMerger.KnownDataAssetFolders.Contains(top))
                continue;

            IEnumerable<string> nested;
            try
            {
                nested = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var path in nested)
            {
                string ext = Path.GetExtension(path);
                if (!ConfigFileMerger.IsLooseConfigExtension(ext)) continue;
                string rel = Path.GetRelativePath(dataRoot, path).Replace("\\", "/");
                if (!ConfigFileMerger.IsRootLooseConfigPath(rel)) continue;
                yield return rel;
            }
        }
    }

    private static IEnumerable<string> EnumerateDisabledLooseConfigListKeys()
    {
        string root = AppPaths.DisabledModsPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            yield break;

        foreach (var path in Directory.GetFiles(root, "*.*"))
        {
            string ext = Path.GetExtension(path);
            if (!ConfigFileMerger.IsLooseConfigExtension(ext)) continue;
            string name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) continue;
            yield return $"Disabled/{name}";
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            string top = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(top) ||
                string.Equals(top, "Loose", StringComparison.OrdinalIgnoreCase) ||
                ConfigFileMerger.KnownDataAssetFolders.Contains(top))
                continue;

            IEnumerable<string> nested;
            try
            {
                nested = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var path in nested)
            {
                string ext = Path.GetExtension(path);
                if (!ConfigFileMerger.IsLooseConfigExtension(ext)) continue;
                string rel = Path.GetRelativePath(root, path).Replace("\\", "/");
                if (string.IsNullOrWhiteSpace(rel) ||
                    rel.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!ConfigFileMerger.IsRootLooseConfigPath(rel)) continue;
                yield return $"Disabled/{rel}";
            }
        }
    }

    public List<object> GetModsList()
    {
        var cacheKey = GetModsListCacheKey();
        lock (_modsListCacheLock)
        {
            if (_modsListCache != null && string.Equals(_modsListCacheKey, cacheKey, StringComparison.Ordinal))
                return _modsListCache;
        }

        var mods = new List<object>();
        var metadata = LoadMetadata();
        bool metadataChanged = false;

        string activeDataPath = GetActiveDataPath();
        string activeStringsPath = GetActiveStringsPath();

        if (VirtualModMode)
        {
            EnsureActiveDataFolders();
            EnsureManagedStagingHydrated();
        }

        if (!Directory.Exists(activeDataPath)) return mods;

        if (!Directory.Exists(activeDataPath)) {
            _logger($"[ERROR] GetModsList: Active data path not found: '{activeDataPath}'");
            _statusReporter("error", $"Data folder not found at: {activeDataPath}. Please check settings.");
            return mods;
        }


        var files = Directory.GetFiles(activeDataPath, "*.*")
            .Where(f => f.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase) || 
                        f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || 
                        f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".strings", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".dlstrings", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".ilstrings", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();

        foreach (var configKey in EnumerateLooseConfigListKeys(activeDataPath))
        {
            if (!files.Contains(configKey, StringComparer.OrdinalIgnoreCase))
                files.Add(configKey);
        }

        foreach (var core in DiscoverCoreIniListPaths())
        {
            if (!files.Contains(core, StringComparer.OrdinalIgnoreCase))
                files.Add(core);

            if (!metadata.ContainsKey(core))
            {
                string coreFileName = Path.GetFileName(core);
                metadata[core] = new ModMetadata
                {
                    Name = coreFileName,
                    IsEnabled = true,
                    Files = new List<string> { core }
                };
                metadataChanged = true;
            }
            else if (metadata[core].Files == null || metadata[core].Files.Count == 0)
            {
                metadata[core].Files = new List<string> { core };
                metadataChanged = true;
            }
        }
            

        string stringsDir = activeStringsPath;
        var disabledStringKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(stringsDir))
        {
            var seenStringKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(stringsDir, "*.*"))
            {
                if (!TryParseStringModDiskName(Path.GetFileName(path), out string canonical, out bool disabledSuffix))
                    continue;
                string key = $"Strings/{canonical}";
                if (!seenStringKeys.Add(key))
                {
                    if (!disabledSuffix) disabledStringKeys.Remove(key);
                    continue;
                }
                files.Add(key);
                if (disabledSuffix) disabledStringKeys.Add(key);
            }
#if DEBUG
            _logger($"[DEBUG] Found {seenStringKeys.Count} string files in {stringsDir} ({disabledStringKeys.Count} disabled)");
#endif
        }
#if DEBUG
        else
        {
             _logger($"[DEBUG] Strings directory not found: {stringsDir}");
        }
#endif

        string bundlesDir = AppPaths.BundlesPath;
        if (!Directory.Exists(bundlesDir)) Directory.CreateDirectory(bundlesDir);

        var bFiles = Directory.GetFiles(bundlesDir, "*.ba2")
            .Select(f => $"Bundles/{Path.GetFileName(f)}");
        files.AddRange(bFiles);
#if DEBUG
        _logger($"[DEBUG] Found {bFiles.Count()} master bundle files in {bundlesDir}");
#endif

        if (Directory.Exists(AppPaths.DisabledModsPath))
        {
            var dFiles = Directory.GetFiles(AppPaths.DisabledModsPath, "*.*")
                .Where(f => f.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
                .Select(f => $"Disabled/{Path.GetFileName(f)}")
                .ToList();
            foreach (var configKey in EnumerateDisabledLooseConfigListKeys())
            {
                if (!dFiles.Contains(configKey, StringComparer.OrdinalIgnoreCase))
                    dFiles.Add(configKey);
            }
            files.AddRange(dFiles);
#if DEBUG
            _logger($"[DEBUG] Found {dFiles.Count} disabled mod files in {AppPaths.DisabledModsPath}");
#endif
        }

        files.AddRange(DiscoverGameRootInjectorListPaths());

        var discoveredKeys = new HashSet<string>(
            files.Select(NormalizeMetadataKey).Where(k => !string.IsNullOrWhiteSpace(k)),
            StringComparer.OrdinalIgnoreCase);

        var looseOwnedPaths = BuildLooseOwnedPathSet(metadata, discoveredKeys);

        foreach (var file in files) 
        {
            string normalizedFile = NormalizeMetadataKey(file);
            string fileNameOnly = Path.GetFileName(normalizedFile);
            
            if (IsVanillaFile(fileNameOnly)) continue;
            bool ownedByLoosePack =
                looseOwnedPaths.Contains(normalizedFile) || looseOwnedPaths.Contains(fileNameOnly);
            if (ownedByLoosePack && !ConfigFileMerger.IsRootLooseConfigPath(normalizedFile))
                continue;

            bool isSpecialPath =
                normalizedFile.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase) ||
                normalizedFile.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase) ||
                normalizedFile.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase) ||
                normalizedFile.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase) ||
                normalizedFile.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase) ||
                normalizedFile.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase);

            if (!isSpecialPath)
            {
                string siblingKey = normalizedFile.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase)
                    ? NormalizeMetadataKey(normalizedFile.Substring("Disabled/".Length))
                    : $"Disabled/{NormalizeMetadataKey(normalizedFile)}";

                if (!metadata.ContainsKey(normalizedFile) &&
                    !string.IsNullOrWhiteSpace(siblingKey) &&
                    metadata.ContainsKey(siblingKey) &&
                    !discoveredKeys.Contains(siblingKey))
                {
                    metadata[normalizedFile] = metadata[siblingKey];
                    metadata.Remove(siblingKey);
                    metadataChanged = true;
                }
                else if (metadata.ContainsKey(normalizedFile) &&
                         !string.IsNullOrWhiteSpace(siblingKey) &&
                         metadata.ContainsKey(siblingKey) &&
                         !discoveredKeys.Contains(siblingKey))
                {
                    metadata[normalizedFile] = MergeMetadataEntries(metadata[normalizedFile], metadata[siblingKey]);
                    metadata.Remove(siblingKey);
                    metadataChanged = true;
                }
            }

            string name = Path.GetFileNameWithoutExtension(normalizedFile);
            
            
            string resolvedKey = ResolveMetadataKey(metadata, normalizedFile);
            ModMetadata meta;
            if (!string.IsNullOrEmpty(resolvedKey) && metadata.TryGetValue(resolvedKey, out var resolvedMeta) && resolvedMeta != null) {
                meta = resolvedMeta;
            } else {
                meta = new ModMetadata { Name = name };
                if (normalizedFile.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase)) {
                    meta.IsBundle = true;
                }
            }

            bool lackedNexus = !meta.NexusModId.HasValue;
            bool lackedAuthor = string.IsNullOrWhiteSpace(meta.Author) || meta.Author == "Unknown";
            bool lackedDetails = string.IsNullOrWhiteSpace(meta.Details);
            bool lackedUrl = string.IsNullOrWhiteSpace(meta.URL);
            meta = MergeMissingFieldsFromSiblings(metadata, meta, normalizedFile, fileNameOnly);
            if (!string.IsNullOrEmpty(resolvedKey) && metadata.ContainsKey(resolvedKey))
            {
                metadata[resolvedKey] = meta;
                if ((lackedNexus && meta.NexusModId.HasValue) ||
                    (lackedAuthor && !string.IsNullOrWhiteSpace(meta.Author) && meta.Author != "Unknown") ||
                    (lackedDetails && !string.IsNullOrWhiteSpace(meta.Details)) ||
                    (lackedUrl && !string.IsNullOrWhiteSpace(meta.URL)))
                {
                    metadataChanged = true;
                }
            }


#if DEBUG
            if (!string.IsNullOrEmpty(resolvedKey)) {
                string displayName = (!string.IsNullOrWhiteSpace(meta.Name) && meta.Name.Trim() != "Unknown Mod") ? meta.Name.Trim() : fileNameOnly;
                _logger($"[DEBUG] Recognized mod: {displayName}");
            }
#endif
            
            bool isActuallyEnabled;
            bool isBundle = meta.IsBundle || normalizedFile.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase);
            bool isLoose = meta.IsLoose || normalizedFile.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase);
            
            if (isBundle || isLoose) {
                isActuallyEnabled = meta.IsEnabled;
            } else if (disabledStringKeys.Contains(normalizedFile)) {
                isActuallyEnabled = false;
                if (meta.IsEnabled) meta.IsEnabled = false;
            } else {
                isActuallyEnabled = !normalizedFile.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase);
                
                if (meta.IsEnabled != isActuallyEnabled) {
                    meta.IsEnabled = isActuallyEnabled;
                }
            }

            if (string.IsNullOrWhiteSpace(resolvedKey))
            {
                var minimal = new ModMetadata
                {
                    Name = (!string.IsNullOrWhiteSpace(meta.Name) && meta.Name.Trim() != "Unknown Mod")
                        ? meta.Name.Trim()
                        : Path.GetFileNameWithoutExtension(fileNameOnly).Trim(),
                    IsEnabled = isActuallyEnabled,
                    IsBundle = isBundle,
                    IsLoose = isLoose,
                    LoadOrder = meta.LoadOrder,
                    Details = meta.Details ?? "",
                    Author = meta.Author,
                    Version = meta.Version,
                    URL = meta.URL,
                    Category = meta.Category,
                    Files = (meta.Files != null && meta.Files.Count > 0) ? meta.Files : new List<string> { normalizedFile }
                };
                if (!metadata.ContainsKey(normalizedFile))
                {
                    metadata[normalizedFile] = minimal;
                    metadataChanged = true;
                }
                else if (metadata[normalizedFile].Files == null || metadata[normalizedFile].Files.Count == 0)
                {
                    metadata[normalizedFile].Files = minimal.Files;
                    metadataChanged = true;
                }
                else
                {
                    if ((normalizedFile.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                         normalizedFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                         normalizedFile.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) &&
                        !(normalizedFile.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase) ||
                          normalizedFile.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase) ||
                          normalizedFile.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase) ||
                          normalizedFile.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase) ||
                          normalizedFile.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase)))
                    {
                        metadata[normalizedFile].Files ??= new List<string>();
                        if (!metadata[normalizedFile].Files.Any(f =>
                                string.Equals(NormalizeMetadataKey(f ?? ""), normalizedFile, StringComparison.OrdinalIgnoreCase)))
                        {
                            metadata[normalizedFile].Files.Add(normalizedFile);
                            metadataChanged = true;
                        }
                    }
                }

                if (meta.Files == null || meta.Files.Count == 0)
                {
                    meta.Files = new List<string> { normalizedFile };
                }
            }
            
            string resolvedDisplayName = (!string.IsNullOrWhiteSpace(meta.Name) && meta.Name.Trim() != "Unknown Mod")
                ? meta.Name.Trim()
                : Path.GetFileNameWithoutExtension(fileNameOnly).Trim();

            string displayVersion = !string.IsNullOrWhiteSpace(meta.NexusFileVersion)
                ? meta.NexusFileVersion.Trim()
                : (!string.IsNullOrWhiteSpace(meta.Version) && meta.Version != "1.0" ? meta.Version.Trim() : meta.Version);

            mods.Add(new {
                originalName = normalizedFile, 
                name = resolvedDisplayName,
                author = meta.Author,
                version = displayVersion,
                details = meta.Details ?? "",
                status = isActuallyEnabled ? "enabled" : "disabled",
                type = isLoose
                    ? ResolvePackedModListType(meta, normalizedFile)
                    : Path.GetExtension(normalizedFile).ToLower().Replace(".", ""),
                isBundle = isBundle,
                isLoose = isLoose && FilesHavePackableLooseAssets(meta.Files),
                loadOrder = meta.LoadOrder,
                url = meta.URL ?? "",
                nexusModId = meta.NexusModId,
                nexusFileId = meta.NexusFileId,
                nexusFileVersion = meta.NexusFileVersion ?? "",
                nexusFileUploaded = meta.NexusFileUploaded,
                files = (meta.Files ?? new List<string>())
                    .Select(f => NormalizeMetadataKey(f ?? ""))
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });
        }

        foreach (var kvp in metadata)
        {
            string looseKey = NormalizeMetadataKey(kvp.Key);
            if (string.IsNullOrWhiteSpace(looseKey)) continue;
            bool isLooseEntry = (kvp.Value?.IsLoose == true) ||
                                looseKey.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase);
            if (!isLooseEntry || kvp.Value == null) continue;
            if (mods.Any(m => string.Equals((string)((dynamic)m).originalName, looseKey, StringComparison.OrdinalIgnoreCase)))
                continue;

            var meta = kvp.Value;
            meta.IsLoose = true;
            string resolvedDisplayName = (!string.IsNullOrWhiteSpace(meta.Name) && meta.Name.Trim() != "Unknown Mod")
                ? meta.Name.Trim()
                : SanitizeLoosePackName(looseKey);
            string displayVersion = !string.IsNullOrWhiteSpace(meta.NexusFileVersion)
                ? meta.NexusFileVersion.Trim()
                : (!string.IsNullOrWhiteSpace(meta.Version) && meta.Version != "1.0" ? meta.Version.Trim() : meta.Version);

            bool showAsLoose = FilesHavePackableLooseAssets(meta.Files) || FilesAreOnlyStringMods(meta.Files);
            mods.Add(new {
                originalName = looseKey,
                name = resolvedDisplayName,
                author = meta.Author,
                version = displayVersion,
                details = meta.Details ?? "",
                status = meta.IsEnabled ? "enabled" : "disabled",
                type = ResolvePackedModListType(meta, looseKey),
                isBundle = false,
                isLoose = showAsLoose,
                loadOrder = meta.LoadOrder,
                url = meta.URL ?? "",
                nexusModId = meta.NexusModId,
                nexusFileId = meta.NexusFileId,
                nexusFileVersion = meta.NexusFileVersion ?? "",
                nexusFileUploaded = meta.NexusFileUploaded,
                files = (meta.Files ?? new List<string>())
                    .Select(f => NormalizeMetadataKey(f ?? ""))
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });
        }

        if (metadataChanged)
        {
            SaveMetadata(metadata);
        }

        mods = mods.OrderBy(m => ((dynamic)m).loadOrder).ThenBy(m => ((dynamic)m).name).ToList();

        lock (_modsListCacheLock)
        {
            _modsListCache = mods;
            _modsListCacheKey = cacheKey;
        }

        return mods;
    }

    private static IEnumerable<string> DiscoverCoreIniListPaths()
    {
        var results = new List<string>();
        try
        {
            GameConfigManager.EnsureF76AddToOverlayStubs();

            string docs = AppPaths.DocumentsPath;
            if (!string.IsNullOrWhiteSpace(docs))
            {
                string custom = AppPaths.CustomIniPath;
                if (!string.IsNullOrWhiteSpace(custom))
                    results.Add($"CoreIni/{Path.GetFileName(custom)}");

                string prefs = AppPaths.PrefsIniPath;
                if (!string.IsNullOrWhiteSpace(prefs))
                    results.Add($"CoreIni/{Path.GetFileName(prefs)}");
            }

            foreach (var overlayName in AppPaths.F76AddToOverlayFileNames)
                results.Add($"CoreIni/{overlayName}");
        }
        catch
        {
        }
        return results;
    }

    public List<string> GetEnabledModPaths()
    {
        var metadata = LoadMetadata();
        var paths = new List<string>();
#if DEBUG
        _logger($"[DEBUG] GetEnabledModPaths: Metadata count: {metadata.Count}");
#endif
        foreach (var kvp in metadata)
        {
            if (kvp.Value.IsEnabled)
            {
#if DEBUG
                _logger($"[DEBUG] Mod Enabled: '{kvp.Key}' (Title: {kvp.Value.Name})");
#endif
                if (kvp.Value.Files != null && kvp.Value.Files.Count > 0)
                {
                    foreach(var f in kvp.Value.Files) {
                        string full = GetFullPath(f);
                        paths.Add(full);
#if DEBUG
                        _logger($"[DEBUG]   -> Adding File: {full} (Exists: {File.Exists(full)})");
#endif
                    }
                }
                else
                {
                    string full = GetFullPath(kvp.Key);
                    paths.Add(full);
#if DEBUG
                    _logger($"[DEBUG]   -> Adding Key Path: {full} (Exists: {File.Exists(full)})");
#endif
                }
            }
        }
        var result = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
#if DEBUG
        _logger($"[DEBUG] GetEnabledModPaths: Returning {result.Count} unique existent paths.");
#endif
        return result;
    }

    private static bool IsWinRarFamilyExe(string executablePath)
    {
        string name = Path.GetFileName(executablePath);
        return name.Equals("unrar.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("rar.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("winrar.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string? RegistryValueToExpandedString(object? value)
    {
        if (value == null) return null;
        string? s = value as string ?? Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static object? ReadRegistryKeyDefaultValue(RegistryKey key)
    {
        return key.GetValue("") ?? key.GetValue(null);
    }

    private static string? NormalizeToExistingFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = path.Trim();
            if (!Path.IsPathRooted(path)) return null;
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadAppPathsDefaultExe(RegistryKey hive, string subKey)
    {
        using var k = hive.OpenSubKey(subKey);
        if (k == null) return null;
        string? raw = RegistryValueToExpandedString(ReadRegistryKeyDefaultValue(k));
        return NormalizeToExistingFilePath(raw);
    }

    private static string? FirstWinRarCliInDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return null;
        foreach (var name in new[] { "UnRAR.exe", "Rar.exe", "WinRAR.exe" })
        {
            string p = Path.Combine(directory, name);
            if (File.Exists(p)) return Path.GetFullPath(p);
        }
        return null;
    }

    private static string? WinRarPickFromResolvedExe(string? resolvedExePath)
    {
        if (resolvedExePath == null) return null;
        string exeName = Path.GetFileName(resolvedExePath);
        if (exeName.Equals("WinRAR.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? pick = FirstWinRarCliInDirectory(Path.GetDirectoryName(resolvedExePath));
            return pick ?? resolvedExePath;
        }
        if (exeName.Equals("UnRAR.exe", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("Rar.exe", StringComparison.OrdinalIgnoreCase))
            return resolvedExePath;
        return null;
    }

    private static string? TryParseShellOpenCommandToWinRarExe(object? cmdVal)
    {
        string? cmd = RegistryValueToExpandedString(cmdVal);
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = cmd.Trim();
        if (cmd.Length >= 2 && cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 1)
            {
                string candidate = cmd.Substring(1, end - 1);
                return NormalizeToExistingFilePath(candidate);
            }
        }

        int space = cmd.IndexOf(' ', StringComparison.Ordinal);
        string first = space > 0 ? cmd.Substring(0, space) : cmd;
        return NormalizeToExistingFilePath(first);
    }

    private static string? EnumerateAppPathsForWinRarFamily(RegistryKey hive, string appPathsSubKey)
    {
        using var apps = hive.OpenSubKey(appPathsSubKey);
        if (apps == null) return null;
        foreach (var subName in apps.GetSubKeyNames())
        {
            if (subName.IndexOf("rar", StringComparison.OrdinalIgnoreCase) < 0) continue;
            string? exePath = TryReadAppPathsDefaultExe(hive, $"{appPathsSubKey}\\{subName}");
            string? pick = WinRarPickFromResolvedExe(exePath);
            if (pick != null) return pick;
        }
        return null;
    }

    private static string? TryWinRarFromClassesRootOpenCommand()
    {
        string[] keys =
        {
            @"WinRAR\shell\open\command",
            @"Applications\WinRAR.exe\shell\open\command"
        };
        foreach (var rel in keys)
        {
            using var k = Registry.ClassesRoot.OpenSubKey(rel);
            if (k == null) continue;
            string? winRar = TryParseShellOpenCommandToWinRarExe(ReadRegistryKeyDefaultValue(k));
            string? pick = WinRarPickFromResolvedExe(winRar);
            if (pick != null) return pick;
        }

        foreach (var rel in keys)
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\" + rel);
            if (k == null) continue;
            string? winRar = TryParseShellOpenCommandToWinRarExe(ReadRegistryKeyDefaultValue(k));
            string? pick = WinRarPickFromResolvedExe(winRar);
            if (pick != null) return pick;
        }

        return null;
    }

    public static string? AutoDetectSevenZipExecutable()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var sub7 in new[] { @"SOFTWARE\7-Zip", @"SOFTWARE\WOW6432Node\7-Zip" })
            {
                using var k = hive.OpenSubKey(sub7);
                if (k == null) continue;
                foreach (var valueName in new[] { "Path64", "Path" })
                {
                    string? folder = RegistryValueToExpandedString(k.GetValue(valueName));
                    if (string.IsNullOrWhiteSpace(folder)) continue;
                    folder = folder.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string seven = Path.Combine(folder, "7z.exe");
                    if (File.Exists(seven)) return Path.GetFullPath(seven);
                }
            }
        }

        string[] appPathsRoots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"
        };
        string[] app7zNames = { "7zFM.exe", "7zG.exe", "7z.exe" };
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in appPathsRoots)
            {
                foreach (var name in app7zNames)
                {
                    string? full = TryReadAppPathsDefaultExe(hive, $@"{root}\{name}");
                    if (full == null) continue;
                    string? exeDir = Path.GetDirectoryName(full);
                    if (exeDir == null) continue;
                    string seven = Path.Combine(exeDir, "7z.exe");
                    if (File.Exists(seven)) return Path.GetFullPath(seven);
                }
            }
        }

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in appPathsRoots)
            {
                using var apps = hive.OpenSubKey(root);
                if (apps == null) continue;
                foreach (var subName in apps.GetSubKeyNames())
                {
                    if (!subName.StartsWith("7z", StringComparison.OrdinalIgnoreCase)) continue;
                    string? full = TryReadAppPathsDefaultExe(hive, $@"{root}\{subName}");
                    if (full == null) continue;
                    string? exeDir = Path.GetDirectoryName(full);
                    if (exeDir == null) continue;
                    string seven = Path.Combine(exeDir, "7z.exe");
                    if (File.Exists(seven)) return Path.GetFullPath(seven);
                }
            }
        }

        var fallbacks = new List<string>();
        try
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf))
            {
                fallbacks.Add(Path.Combine(pf, "7-Zip", "7z.exe"));
                fallbacks.Add(Path.Combine(pf, "7-Zip", "7zG.exe"));
            }
            if (!string.IsNullOrEmpty(pfx86))
            {
                fallbacks.Add(Path.Combine(pfx86, "7-Zip", "7z.exe"));
                fallbacks.Add(Path.Combine(pfx86, "7-Zip", "7zG.exe"));
            }
        }
        catch { }

        fallbacks.Add(@"C:\Program Files\7-Zip\7z.exe");
        fallbacks.Add(@"C:\Program Files\7-Zip\7zG.exe");
        fallbacks.Add(@"C:\Program Files (x86)\7-Zip\7z.exe");
        fallbacks.Add(@"C:\Program Files (x86)\7-Zip\7zG.exe");

        foreach (var p in fallbacks.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(p)) return Path.GetFullPath(p);
        }
        return null;
    }

    public static string? AutoDetectRarExtractorExecutable()
    {
        string[] appPathSubKeys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\UnRAR.exe",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\UnRAR.exe",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Rar.exe",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\Rar.exe"
        };

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var sub in appPathSubKeys)
            {
                string? full = TryReadAppPathsDefaultExe(hive, sub);
                string? pick = WinRarPickFromResolvedExe(full);
                if (pick != null) return pick;
            }
        }

        string[] appPathsRoots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"
        };
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in appPathsRoots)
            {
                string? fromEnum = EnumerateAppPathsForWinRarFamily(hive, root);
                if (fromEnum != null) return fromEnum;
            }
        }

        string? fromShell = TryWinRarFromClassesRootOpenCommand();
        if (fromShell != null) return fromShell;

        var winRarDirs = new List<string>();
        try
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf)) winRarDirs.Add(Path.Combine(pf, "WinRAR"));
            if (!string.IsNullOrEmpty(pfx86)) winRarDirs.Add(Path.Combine(pfx86, "WinRAR"));
        }
        catch { }

        winRarDirs.Add(@"C:\Program Files\WinRAR");
        winRarDirs.Add(@"C:\Program Files (x86)\WinRAR");

        foreach (var dir in winRarDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? pick = FirstWinRarCliInDirectory(dir);
            if (pick != null) return pick;
        }

        return null;
    }

    private string? ResolveSevenZipExecutable()
    {
        if (!string.IsNullOrWhiteSpace(SevenZipPath) && File.Exists(SevenZipPath.Trim()))
            return Path.GetFullPath(SevenZipPath.Trim());

        string? d = AutoDetectSevenZipExecutable();
        if (d != null) _logger($"[IMPORT] Auto-detected 7-Zip: {d}");
        return d;
    }

    private string? ResolveRarExtractorExecutable()
    {
        if (!string.IsNullOrWhiteSpace(RarExtractorPath) && File.Exists(RarExtractorPath.Trim()))
            return Path.GetFullPath(RarExtractorPath.Trim());

        string? rar = AutoDetectRarExtractorExecutable();

        bool rarIsGuiOnly = rar != null &&
            Path.GetFileName(rar).Equals("WinRAR.exe", StringComparison.OrdinalIgnoreCase);
        if (rar != null && !rarIsGuiOnly)
        {
            _logger($"[IMPORT] Auto-detected WinRAR/RAR CLI: {rar}");
            return rar;
        }

        string? seven = ResolveSevenZipExecutable();
        if (seven != null)
        {
            _logger($"[IMPORT] Using 7-Zip to extract .rar: {seven}");
            return seven;
        }

        if (rar != null)
        {
            _logger($"[IMPORT] No CLI extractor found; falling back to WinRAR GUI for .rar: {rar}");
            return rar;
        }

        return null;
    }

    private bool TryExtractArchiveToDirectory(string archivePath, string ext, string targetDir, out string error)
    {
        error = "";
        string fileName = Path.GetFileName(archivePath);
        try
        {
            if (ext == ".zip")
            {
                _logger($"[IMPORT] Extracting ZIP: {fileName}");
                System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
                return true;
            }

            bool isRar = ext == ".rar";
            string? toolExe = isRar ? ResolveRarExtractorExecutable() : ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(toolExe))
            {
                error = isRar
                    ? "Can't extract this .rar: no archive tool found. Install 7-Zip or WinRAR, then re-import (or set the path under Settings → Installation Paths). 7-Zip is the easiest option and also handles .rar."
                    : "7-Zip not found. Install 7-Zip or set the path under Settings → Installation Paths (it is usually auto-detected).";
                _logger(isRar
                    ? "[ERROR] No .rar extractor found (settings, WinRAR/UnRAR, and 7-Zip all unavailable)."
                    : "[ERROR] 7-Zip executable not found (settings + default locations).");
                return false;
            }

            _logger($"[IMPORT] Extracting {(isRar ? "RAR" : "7z")} archive: {fileName} using {toolExe}");
            string tempInputFile = Path.Combine(targetDir, "input_archive" + ext);
            File.Copy(archivePath, tempInputFile, true);
            int exitCode = RunArchiveExtractor(toolExe, tempInputFile, targetDir, out string toolOutput);
            if (exitCode != 0)
            {
                _logger($"[ERROR] {(isRar ? "RAR" : "7z")} extraction exit code {exitCode} for {fileName} using {Path.GetFileName(toolExe)}. Tool output: {toolOutput}");
                error = isRar
                    ? $"RAR extraction failed (code {exitCode}). Check the archive and your RAR/7-Zip tool path in Settings."
                    : $"7-Zip extraction failed (code {exitCode}). Check the archive and your 7-Zip path in Settings.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Archive extraction failed for {fileName}: {ex.Message}");
            error = $"Archive extraction failed: {ex.Message}";
            return false;
        }
    }

    private static int RunArchiveExtractor(string executablePath, string archivePath, string extractToDir, out string output)
    {
        string args;
        if (IsWinRarFamilyExe(executablePath))
        {
            string dest = extractToDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            dest += Path.DirectorySeparatorChar;
            args = $"x -y \"{archivePath}\" \"{dest}\"";
        }
        else
        {
            args = $"x \"{archivePath}\" -o\"{extractToDir}\" -y";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(startInfo);
        if (p == null) { output = ""; return -1; }

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        output = string.Join(
            Environment.NewLine,
            new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return p.ExitCode;
    }

    private static readonly HashSet<string> KnownDataFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "meshes", "textures", "materials", "scripts", "sound", "sounds", "music", "strings",
        "interface", "programs", "video", "lodsettings", "facegen", "misc", "shadersfx",
        "vis", "geo", "terrain", "grass",
        "modsdata", "moddata", "configuration"
    };

    private static readonly HashSet<string> ManagedFlatExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ba2", ".esm", ".esp", ".strings", ".dlstrings", ".ilstrings", ".ini", ".json", ".txt", ".toml"
    };

    private static readonly HashSet<string> ArchiveContainerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar"
    };

    private static readonly HashSet<string> KnownAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nif", ".dds", ".bgem", ".bgsm", ".hkx", ".pex", ".psc", ".wav", ".xwm", ".fuz", ".lip",
        ".swf", ".gfx", ".seq", ".lod", ".bto", ".btr", ".dlod", ".tri", ".sst", ".cmp", ".csc",
        ".gid", ".uvd", ".ttf", ".otf", ".xml", ".yml", ".yaml"
    };

    private static readonly HashSet<string> JunkFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "thumbs.db", "desktop.ini", ".ds_store", "moduleconfig.xml",
        "readme.txt", "readme.md", "license.txt", "license.md", "changelog.txt", "changelog.md"
    };

    private static bool IsImportJunkPath(string relativePath)
    {
        string norm = (relativePath ?? "").Replace("\\", "/").Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(norm)) return true;
        var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p.Equals("fomod", StringComparison.OrdinalIgnoreCase))) return true;
        string fileName = parts.Length > 0 ? parts[^1] : "";
        if (JunkFileNames.Contains(fileName)) return true;
        if (fileName.Equals("ModuleConfig.xml", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsManagedFlatExtension(string ext) =>
        ManagedFlatExtensions.Contains(ext ?? "");

    private static bool IsLooseAssetRelativePath(string relativePath)
    {
        string norm = NormalizeStaticRel(relativePath);
        if (string.IsNullOrWhiteSpace(norm) || IsImportJunkPath(norm)) return false;
        string ext = Path.GetExtension(norm);
        if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return false;
        if (ArchiveContainerExtensions.Contains(ext)) return false;
        if (norm.Contains('/')) return true;
        return KnownAssetExtensions.Contains(ext);
    }

    private static string NormalizeStaticRel(string relativePath) =>
        (relativePath ?? "").Replace("\\", "/").Trim().TrimStart('/');

    private static string SanitizeLoosePackName(string displayName)
    {
        string name = Path.GetFileNameWithoutExtension(displayName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "LooseMod";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Replace(' ', '_').Replace('+', '_');
        while (name.Contains("__")) name = name.Replace("__", "_");
        return name.Trim('_');
    }

    private static string MakeLooseMetadataKey(string displayName) =>
        $"Loose/{SanitizeLoosePackName(displayName)}";

    private static string GetLoosePackSafeKey(string metadataKey)
    {
        string key = NormalizeStaticRel(metadataKey);
        if (key.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase))
            key = key.Substring("Loose/".Length);
        return SanitizeLoosePackName(key);
    }

    private List<(string AbsolutePath, string RelativePath)> CollectNormalizedExtractedFiles(string rootDir) =>
        CollectNormalizedExtractedFiles(rootDir, out _);

    private List<(string AbsolutePath, string RelativePath)> CollectNormalizedExtractedFiles(
        string rootDir,
        out List<string> emptyDirectories)
    {
        var result = new List<(string, string)>();
        emptyDirectories = new List<string>();
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return result;

        string rootFull = Path.GetFullPath(rootDir);
        var allFiles = Directory.GetFiles(rootDir, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith("input_archive", StringComparison.OrdinalIgnoreCase)) return false;
                string e = Path.GetExtension(f);
                if (ArchiveContainerExtensions.Contains(e)) return false;
                return true;
            })
            .ToList();

        var rels = allFiles
            .Select(f =>
            {
                string rel = Path.GetRelativePath(rootFull, f).Replace("\\", "/");
                return (AbsolutePath: f, RelativePath: rel);
            })
            .Where(t => !IsImportJunkPath(t.RelativePath))
            .ToList();

        var dirRels = Directory.GetDirectories(rootDir, "*", SearchOption.AllDirectories)
            .Where(d => !Directory.EnumerateFileSystemEntries(d).Any())
            .Select(d => Path.GetRelativePath(rootFull, d).Replace("\\", "/"))
            .Where(d => !d.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(p => p.Equals("fomod", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (rels.Count == 0) return result;

        var topSegments = rels
            .Select(t => t.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (topSegments.Count == 1 &&
            !KnownDataFolderNames.Contains(topSegments[0]) &&
            !IsManagedFlatExtension(Path.GetExtension(topSegments[0])))
        {
            string wrapper = topSegments[0] + "/";
            bool allUnderWrapper = rels.All(t =>
                t.RelativePath.StartsWith(wrapper, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.RelativePath, topSegments[0], StringComparison.OrdinalIgnoreCase));
            if (allUnderWrapper)
            {
                rels = rels
                    .Where(t => t.RelativePath.StartsWith(wrapper, StringComparison.OrdinalIgnoreCase))
                    .Select(t => (t.AbsolutePath, RelativePath: t.RelativePath.Substring(wrapper.Length)))
                    .Where(t => !string.IsNullOrWhiteSpace(t.RelativePath))
                    .ToList();
                dirRels = dirRels
                    .Select(d => d.StartsWith(wrapper, StringComparison.OrdinalIgnoreCase) ? d.Substring(wrapper.Length) : d)
                    .ToList();
            }
        }

        rels = rels
            .Select(t =>
            {
                string rel = t.RelativePath;
                if (rel.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring("Data/".Length);
                return (t.AbsolutePath, RelativePath: rel);
            })
            .Where(t => !string.IsNullOrWhiteSpace(t.RelativePath) && !IsImportJunkPath(t.RelativePath))
            .ToList();

        emptyDirectories = dirRels
            .Select(d => d.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) ? d.Substring("Data/".Length) : d)
            .Where(d => !d.Equals("Data", StringComparison.OrdinalIgnoreCase) &&
                        ConfigFileMerger.IsNonAssetDataDirectory(d))
            .Select(NormalizeMetadataKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.AddRange(rels);
        return result;
    }

    private List<string> EnsureModDirectories(string dataRoot, IEnumerable<string>? directories)
    {
        var created = new List<string>();
        if (string.IsNullOrWhiteSpace(dataRoot) || directories == null) return created;

        string rootFull = Path.GetFullPath(dataRoot);
        foreach (var raw in directories)
        {
            string rel = NormalizeMetadataKey(raw ?? "").Trim('/');
            if (!ConfigFileMerger.IsNonAssetDataDirectory(rel)) continue;

            string full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace("/", "\\")));
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                if (!Directory.Exists(full))
                {
                    Directory.CreateDirectory(full);
                    _logger($"[DEPLOY] Created mod folder: {rel}");
                }
                created.Add(rel);
            }
            catch (Exception ex)
            {
                _logger($"[ERROR] Failed to create mod folder '{rel}': {ex.Message}");
            }
        }
        return created;
    }

    private static bool IsConfigListKey(string key)
    {
        string rel = NormalizeStaticRel(key);
        return ConfigFileMerger.IsLooseConfigExtension(Path.GetExtension(rel)) &&
               ConfigFileMerger.IsRootLooseConfigPath(rel);
    }

    private static bool IsNestedLooseConfig(string relativePath)
    {
        string norm = NormalizeStaticRel(relativePath);
        return norm.Contains('/') && ConfigFileMerger.IsRootLooseConfigPath(norm);
    }

    private bool TreeHasLooseAssets(IEnumerable<(string AbsolutePath, string RelativePath)> files) =>
        files.Any(t => IsLooseAssetRelativePath(t.RelativePath) && !IsNestedLooseConfig(t.RelativePath));

    private void InstallLoosePackage(
        string displayName,
        List<(string AbsolutePath, string RelativePath)> files,
        Dictionary<string, ModMetadata> metadata,
        List<string> importedKeys,
        ImportProgressContext progress,
        IReadOnlyList<string>? emptyDirectories = null)
    {
        string packName = SanitizeLoosePackName(displayName);
        string metaKey = MakeLooseMetadataKey(packName);
        var installedRels = new List<string>();
        string dataRoot = GetActiveDataPath();
        string stringsRoot = GetActiveStringsPath();
        var installedDirs = EnsureModDirectories(dataRoot, emptyDirectories);

        progress.AddUnits(Math.Max(files.Count - 1, 0));

        foreach (var (abs, rel) in files)
        {
            try
            {
                string ext = Path.GetExtension(rel).ToLowerInvariant();
                string fileNameOnly = Path.GetFileName(rel);

                if (ext == ".dll")
                {
                    InstallGameRootInjector(abs, fileNameOnly, metadata, importedKeys);
                    progress.CompleteOne("processing", fileNameOnly);
                    continue;
                }

                var (destPath, listRel) = ResolveLooseInstallDestination(rel, dataRoot, stringsRoot);

                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                string normalizedSource = Path.GetFullPath(abs);
                string normalizedDest = Path.GetFullPath(destPath);
                if (!string.Equals(normalizedSource, normalizedDest, StringComparison.OrdinalIgnoreCase))
                {
                    if (ConfigFileMerger.IsLooseConfigExtension(ext) &&
                        ConfigFileMerger.IsRootLooseConfigPath(listRel) &&
                        File.Exists(destPath))
                    {
                        if (ConfigFileMerger.TryMergeAdditive(destPath, abs, out var merged, out var mergeSummary))
                        {
                            ConfigFileMerger.WriteMergedFile(destPath, merged);
                            _logger($"[IMPORT] Merged config {listRel}: {mergeSummary}");
                        }
                        else
                        {
                            _logger($"[IMPORT] Config merge failed for {listRel}; kept existing file.");
                        }
                    }
                    else
                    {
                        File.Copy(abs, destPath, true);
                    }
                }

                installedRels.Add(listRel);
                _logger($"[IMPORT] Loose file: {listRel}");
                progress.CompleteOne("processing", fileNameOnly);
            }
            catch (Exception ex)
            {
                _logger($"[ERROR] Loose install failed for '{rel}': {ex.Message}");
                progress.CompleteOne("processing", Path.GetFileName(rel));
            }
        }

        installedRels = installedRels
            .Select(NormalizeMetadataKey)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (installedRels.Count == 0)
        {
            _logger($"[IMPORT] Loose package '{packName}' produced no files.");
            _statusReporter("warning", $"'{displayName}' extracted but contained no installable loose mod files.");
            LastImportReportedIssue = true;
            return;
        }

        if (metadata.TryGetValue(metaKey, out var existing) && existing != null)
        {
            existing.Name = packName;
            existing.IsLoose = true;
            existing.IsEnabled = true;
            existing.Files = installedRels;
            existing.Directories = installedDirs;
        }
        else
        {
            metadata[metaKey] = new ModMetadata
            {
                Name = packName,
                IsLoose = true,
                IsEnabled = true,
                Files = installedRels,
                Directories = installedDirs
            };
        }

        foreach (var rel in installedRels)
        {
            if (string.Equals(rel, metaKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase)) continue;
            if (metadata.ContainsKey(rel) &&
                (rel.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase) ||
                 rel.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                 rel.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)))
            {
                metadata.Remove(rel);
            }
        }

        importedKeys.Add(metaKey);
        _statusReporter("success", $"Imported loose mod: {packName} ({installedRels.Count} files)");
        _logger($"[IMPORT] Loose package '{metaKey}' installed with {installedRels.Count} files under Data.");

        if (TryPackLooseModToBa2(metaKey, out var packError, metadata))
        {
            _statusReporter("success", $"Imported and packed: {packName}");
            _logger($"[IMPORT] Loose package '{metaKey}' auto-packed to BA2.");
        }
        else if (!string.IsNullOrWhiteSpace(packError))
        {
            _logger($"[IMPORT] Auto-pack skipped for '{metaKey}': {packError}");
        }
    }

    private (string DestPath, string ListRel) ResolveLooseInstallDestination(string rel, string dataRoot, string stringsRoot)
    {
        string ext = Path.GetExtension(rel).ToLowerInvariant();
        string fileNameOnly = Path.GetFileName(rel);
        if (ext is ".strings" or ".dlstrings" or ".ilstrings")
            return (Path.Combine(stringsRoot, fileNameOnly), $"Strings/{fileNameOnly}");
        if (ext is ".esp" or ".esm" or ".ba2")
            return (Path.Combine(dataRoot, fileNameOnly), NormalizeMetadataKey(fileNameOnly));
        if (ConfigFileMerger.IsLooseConfigExtension(ext) && !rel.Contains('/'))
            return (Path.Combine(dataRoot, fileNameOnly), NormalizeMetadataKey(fileNameOnly));
        return (Path.Combine(dataRoot, rel.Replace("/", "\\")), NormalizeMetadataKey(rel));
    }

    private static bool ShouldKeepLooseOutsideBa2(string relativePath)
    {
        string ext = Path.GetExtension(relativePath ?? "").ToLowerInvariant();
        if (ext is ".esp" or ".esm" or ".ba2"
            or ".strings" or ".dlstrings" or ".ilstrings"
            or ".dll")
            return true;

        if (ConfigFileMerger.IsNonAssetDataFolderPath(relativePath))
            return true;

        if (ConfigFileMerger.IsLooseConfigExtension(ext))
            return ConfigFileMerger.IsRootLooseConfigPath(relativePath);

        return false;
    }

    private static bool FilesHavePackableLooseAssets(IEnumerable<string>? files)
    {
        if (files == null) return false;
        foreach (var raw in files)
        {
            string rel = NormalizeStaticRel(raw ?? "");
            if (string.IsNullOrWhiteSpace(rel)) continue;
            if (!ShouldKeepLooseOutsideBa2(rel)) return true;
        }
        return false;
    }

    private static bool IsStringListPath(string? relativePath)
    {
        string ext = Path.GetExtension(relativePath ?? "").ToLowerInvariant();
        return ext is ".strings" or ".dlstrings" or ".ilstrings";
    }

    private static bool FilesAreOnlyStringMods(IEnumerable<string>? files)
    {
        if (files == null) return false;
        bool any = false;
        foreach (var raw in files)
        {
            string rel = NormalizeStaticRel(raw ?? "");
            if (string.IsNullOrWhiteSpace(rel)) continue;
            any = true;
            if (!IsStringListPath(rel)) return false;
        }
        return any;
    }

    private static string ResolvePackedModListType(ModMetadata meta, string fallbackKey)
    {
        var files = meta.Files ?? new List<string>();
        if (FilesHavePackableLooseAssets(files)) return "loose";
        if (FilesAreOnlyStringMods(files)) return "strings";
        if (files.Any(f => (f ?? "").EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)))
            return "ba2";
        string ext = Path.GetExtension(fallbackKey).ToLowerInvariant().TrimStart('.');
        return string.IsNullOrWhiteSpace(ext) ? "ba2" : ext;
    }

    public void PackLooseModToBa2(string modKey, Action? onComplete = null)
    {
        Task.Run(() =>
        {
            try
            {
                if (!TryPackLooseModToBa2(modKey, out var error))
                {
                    _statusReporter("error", string.IsNullOrWhiteSpace(error)
                        ? "Failed to pack loose mod to BA2."
                        : error);
                    return;
                }

                _statusReporter("success", $"Packed loose mod to BA2: {Path.GetFileName(modKey.TrimEnd('/'))}");
            }
            catch (Exception ex)
            {
                _logger($"[ERROR] PackLooseModToBa2 failed: {ex.Message}");
                _statusReporter("error", $"Failed to pack loose mod: {ex.Message}");
            }
            finally
            {
                try { onComplete?.Invoke(); } catch {  }
            }
        });
    }

    private bool TryPackLooseModToBa2(
        string modKey,
        out string errorMessage,
        Dictionary<string, ModMetadata>? metadataOverride = null)
    {
        errorMessage = "";
        string key = NormalizeMetadataKey(modKey ?? "");
        if (string.IsNullOrWhiteSpace(key))
        {
            errorMessage = "Missing mod name.";
            return false;
        }

        var metadata = metadataOverride ?? LoadMetadata();
        if (!metadata.TryGetValue(key, out var meta) || meta == null)
        {
            string looseKey = key.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase)
                ? key
                : MakeLooseMetadataKey(key);
            if (!metadata.TryGetValue(looseKey, out meta) || meta == null)
            {
                errorMessage = $"Loose mod not found: {modKey}";
                return false;
            }
            key = looseKey;
        }

        if (!meta.IsLoose && !key.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Only loose packs can be packed to BA2 this way.";
            return false;
        }

        if (meta.Files == null || meta.Files.Count == 0)
        {
            errorMessage = "Loose pack has no files.";
            return false;
        }

        string packName = SanitizeLoosePackName(!string.IsNullOrWhiteSpace(meta.Name) ? meta.Name : key);
        string safeKey = GetLoosePackSafeKey(key);
        bool enabled = meta.IsEnabled;
        string dataRoot = GetActiveDataPath();
        string stringsRoot = GetActiveStringsPath();
        string disabledRoot = Path.Combine(AppPaths.DisabledModsPath, "Loose", safeKey);

        var keepRels = new List<string>();
        var packItems = new List<(string AbsolutePath, string RelativePath)>();

        foreach (var raw in meta.Files)
        {
            string rel = NormalizeMetadataKey(raw ?? "");
            if (string.IsNullOrWhiteSpace(rel)) continue;

            if (ShouldKeepLooseOutsideBa2(rel))
            {
                keepRels.Add(rel);
                continue;
            }

            if (!TryResolveLoosePackFilePath(rel, safeKey, enabled, dataRoot, stringsRoot, disabledRoot, out string abs) ||
                !File.Exists(abs))
            {
                _logger($"[LOOSE] Skip missing pack source: {rel}");
                continue;
            }

            string packRel = rel.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase)
                ? rel.Substring("Disabled/".Length)
                : rel;
            if (packRel.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase))
                packRel = packRel.Substring("Loose/".Length);
            packItems.Add((abs, packRel.Replace("\\", "/")));
        }

        if (packItems.Count == 0)
        {
            errorMessage = "No packable assets found (meshes/textures/etc.). ESP/strings/config stay loose.";
            return false;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "F76Manager_LoosePack_" + Guid.NewGuid().ToString("N"));
        string gnrlDir = Path.Combine(tempRoot, "GNRL");
        string dx10Dir = Path.Combine(tempRoot, "DX10");
        Directory.CreateDirectory(gnrlDir);
        Directory.CreateDirectory(dx10Dir);

        try
        {
            bool hasGeneral = false;
            bool hasTextures = false;

            foreach (var (abs, rel) in packItems)
            {
                bool isDds = rel.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
                string targetRoot = isDds ? dx10Dir : gnrlDir;
                if (isDds) hasTextures = true;
                else hasGeneral = true;

                string dest = Path.Combine(targetRoot, rel.Replace("/", "\\"));
                string? destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);
                File.Copy(abs, dest, overwrite: true);
            }

            string ba2DestRoot = enabled ? dataRoot : disabledRoot;
            if (!Directory.Exists(ba2DestRoot)) Directory.CreateDirectory(ba2DestRoot);

            var newBa2Names = new List<string>();

            if (hasGeneral)
            {
                string ba2Name = packName + ".ba2";
                string ba2Path = Path.Combine(ba2DestRoot, ba2Name);
                _logger($"[LOOSE] Packing general assets -> {ba2Path}");
                BA2Utility.Pack(gnrlDir, ba2Path, _logger, "GNRL", "Default");
                newBa2Names.Add(ba2Name);
            }

            if (hasTextures)
            {
                string ba2Name = packName + " - Textures.ba2";
                string ba2Path = Path.Combine(ba2DestRoot, ba2Name);
                _logger($"[LOOSE] Packing texture assets -> {ba2Path}");
                BA2Utility.Pack(dx10Dir, ba2Path, _logger, "DX10", "Default");
                newBa2Names.Add(ba2Name);
            }

            if (newBa2Names.Count == 0)
            {
                errorMessage = "No BA2 archives were produced.";
                return false;
            }

            foreach (var (abs, _) in packItems)
            {
                try
                {
                    if (File.Exists(abs)) File.Delete(abs);
                }
                catch (Exception ex)
                {
                    _logger($"[LOOSE] Could not delete packed source '{abs}': {ex.Message}");
                }
            }

            var updatedFiles = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in keepRels)
            {
                if (seen.Add(rel)) updatedFiles.Add(rel);
            }
            foreach (var ba2 in newBa2Names)
            {
                if (seen.Add(ba2)) updatedFiles.Add(ba2);
            }

            meta.Files = updatedFiles;
            meta.IsLoose = true;
            meta.Name = packName;
            metadata[key] = meta;

            foreach (var ba2 in newBa2Names)
            {
                if (metadata.ContainsKey(ba2) &&
                    !string.Equals(ba2, key, StringComparison.OrdinalIgnoreCase))
                {
                    metadata.Remove(ba2);
                }
            }

            SaveMetadata(metadata);
                    _logger($"[LOOSE] Packed '{key}' into {string.Join(", ", newBa2Names)}.");
            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            }
            catch (Exception cleanupEx)
            {
                _logger($"[LOOSE] Temp cleanup failed: {cleanupEx.Message}");
            }
        }
    }

    private static bool TryResolveLoosePackFilePath(
        string rel,
        string safeKey,
        bool enabled,
        string dataRoot,
        string stringsRoot,
        string disabledRoot,
        out string absolutePath)
    {
        absolutePath = "";
        string clean = rel.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase)
            ? rel.Substring("Disabled/".Length)
            : rel;

        string activePath;
        string disabledPath;

        if (clean.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(clean);
            activePath = Path.Combine(stringsRoot, fn);
            disabledPath = Path.Combine(disabledRoot, "Strings", fn);
        }
        else
        {
            activePath = Path.Combine(dataRoot, clean.Replace("/", "\\"));
            disabledPath = Path.Combine(disabledRoot, clean.Replace("/", "\\"));
        }

        if (enabled)
        {
            if (File.Exists(activePath)) { absolutePath = activePath; return true; }
            if (File.Exists(disabledPath)) { absolutePath = disabledPath; return true; }
        }
        else
        {
            if (File.Exists(disabledPath)) { absolutePath = disabledPath; return true; }
            if (File.Exists(activePath)) { absolutePath = activePath; return true; }
        }

        return false;
    }

    private void ImportFromExtractedTree(
        string tempDir,
        string displayArchiveName,
        Dictionary<string, ModMetadata> metadata,
        List<string> importedKeys,
        ImportProgressContext progress)
    {
        var normalized = CollectNormalizedExtractedFiles(tempDir, out var emptyDirectories);
        if (normalized.Count == 0)
        {
            _logger($"[IMPORT] No valid mod files found in {displayArchiveName} after extraction.");
            _statusReporter("warning", $"'{displayArchiveName}' extracted but contained no mod files (BA2/ESM/ESP/strings/INI/loose). It may be a FOMOD/installer archive that needs manual placement.");
            LastImportReportedIssue = true;
            return;
        }

        if (TreeHasLooseAssets(normalized))
        {
            InstallLoosePackage(displayArchiveName, normalized, metadata, importedKeys, progress, emptyDirectories);
            return;
        }

        ImportFlatTree(displayArchiveName, normalized, emptyDirectories, metadata, importedKeys, progress);
    }

    private void ImportFlatTree(
        string displayArchiveName,
        List<(string AbsolutePath, string RelativePath)> normalized,
        List<string> emptyDirectories,
        Dictionary<string, ModMetadata> metadata,
        List<string> importedKeys,
        ImportProgressContext progress)
    {
        var nestedConfigs = normalized.Where(t => IsNestedLooseConfig(t.RelativePath)).ToList();
        var managedAbs = normalized
            .Where(t => !IsNestedLooseConfig(t.RelativePath))
            .Where(t =>
            {
                string e = Path.GetExtension(t.RelativePath).ToLowerInvariant();
                return IsManagedFlatExtension(e) ||
                       (e == ".dll" && IsGameRootInjectorName(Path.GetFileName(t.RelativePath)));
            })
            .Select(t => t.AbsolutePath)
            .ToList();

        if (managedAbs.Count == 0 && nestedConfigs.Count == 0)
        {
            _logger($"[IMPORT] No valid mod files found in {displayArchiveName} after extraction.");
            _statusReporter("warning", $"'{displayArchiveName}' extracted but contained no mod files (BA2/ESM/ESP/strings/INI). It may be a FOMOD/installer archive that needs manual placement.");
            LastImportReportedIssue = true;
            return;
        }

        progress.AddUnits(Math.Max(managedAbs.Count + nestedConfigs.Count - 1, 0));
        int firstNewKey = importedKeys.Count;
        if (managedAbs.Count > 0)
            ImportFilesInternal(managedAbs, metadata, importedKeys, progress);
        ImportNestedConfigFiles(nestedConfigs, metadata, importedKeys, progress);
        AttachDirectoriesToImportedMod(emptyDirectories, metadata, importedKeys.Skip(firstNewKey).ToList());
    }

    private void ImportNestedConfigFiles(
        List<(string AbsolutePath, string RelativePath)> configs,
        Dictionary<string, ModMetadata> metadata,
        List<string> importedKeys,
        ImportProgressContext progress)
    {
        string dataRoot = GetActiveDataPath();
        foreach (var (abs, rel) in configs)
        {
            string key = NormalizeMetadataKey(NormalizeStaticRel(rel));
            try
            {
                string destPath = Path.Combine(dataRoot, key.Replace("/", "\\"));
                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                if (File.Exists(destPath))
                {
                    if (ConfigFileMerger.TryMergeAdditive(destPath, abs, out var merged, out var mergeSummary))
                    {
                        ConfigFileMerger.WriteMergedFile(destPath, merged);
                        _logger($"[IMPORT] Merged config {key}: {mergeSummary}");
                        if (!mergeSummary.StartsWith("No new", StringComparison.OrdinalIgnoreCase))
                            _statusReporter?.Invoke("info", $"Merged {key}: {mergeSummary}");
                    }
                    else
                    {
                        _logger($"[IMPORT] Config merge failed for {key}; kept existing file.");
                        _statusReporter?.Invoke("warning", $"Could not merge {key}; your existing file was kept.");
                    }
                }
                else
                {
                    File.Copy(abs, destPath, true);
                    _logger($"[IMPORT] Imported config: {key}");
                }

                importedKeys.Add(key);
                if (!metadata.TryGetValue(key, out var meta) || meta == null)
                {
                    metadata[key] = new ModMetadata
                    {
                        Name = Path.GetFileNameWithoutExtension(key),
                        Files = new List<string> { key },
                        IsEnabled = true
                    };
                }
                else
                {
                    meta.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                _logger($"[ERROR] Failed to import config {key}: {ex.Message}");
            }
            progress.CompleteOne("processing", Path.GetFileName(key));
        }
    }

    private void AttachDirectoriesToImportedMod(
        List<string> emptyDirectories,
        Dictionary<string, ModMetadata> metadata,
        List<string> newKeys)
    {
        if (emptyDirectories == null || emptyDirectories.Count == 0 || newKeys.Count == 0) return;

        string? ownerKey = newKeys.FirstOrDefault(k =>
                               k.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase) ||
                               k.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                               k.EndsWith(".esm", StringComparison.OrdinalIgnoreCase))
                           ?? newKeys[0];
        if (!metadata.TryGetValue(ownerKey, out var owner) || owner == null) return;

        var created = EnsureModDirectories(GetActiveDataPath(), emptyDirectories);
        owner.Directories = (owner.Directories ?? new List<string>())
            .Concat(created)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool TryImportDirectoryAsLoosePackage(
        string directoryPath,
        Dictionary<string, ModMetadata> metadata,
        List<string> importedKeys,
        ImportProgressContext progress)
    {
        var normalized = CollectNormalizedExtractedFiles(directoryPath, out var emptyDirectories);
        if (normalized.Count == 0)
            return false;

        string displayName = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (TreeHasLooseAssets(normalized))
        {
            InstallLoosePackage(displayName, normalized, metadata, importedKeys, progress, emptyDirectories);
            return true;
        }

        if (normalized.Any(t => IsNestedLooseConfig(t.RelativePath)))
        {
            ImportFlatTree(displayName, normalized, emptyDirectories, metadata, importedKeys, progress);
            return true;
        }

        return false;
    }

    private bool IsLooseModKey(Dictionary<string, ModMetadata> metadata, string modKey)
    {
        if (string.IsNullOrWhiteSpace(modKey)) return false;
        string normalized = NormalizeMetadataKey(modKey);
        if (normalized.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase))
            return true;

        string resolvedKey = ResolveMetadataKey(metadata, normalized);
        return !string.IsNullOrEmpty(resolvedKey) &&
               metadata.TryGetValue(resolvedKey, out var meta) &&
               meta != null &&
               meta.IsLoose;
    }

    private HashSet<string> BuildLooseOwnedPathSet(
        Dictionary<string, ModMetadata> metadata,
        HashSet<string> discoveredKeys)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in metadata)
        {
            if (kvp.Value?.Files == null || kvp.Value.Files.Count == 0) continue;

            string primary = NormalizeMetadataKey(kvp.Key);
            bool isLoosePack =
                kvp.Value.IsLoose ||
                primary.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase);
            bool ownerShown = isLoosePack || discoveredKeys.Contains(primary);
            if (!ownerShown) continue;
            if (!isLoosePack && kvp.Value.Files.Count <= 1) continue;

            foreach (var f in kvp.Value.Files)
            {
                string norm = NormalizeMetadataKey(f ?? "");
                if (string.IsNullOrWhiteSpace(norm)) continue;
                if (string.Equals(norm, primary, StringComparison.OrdinalIgnoreCase)) continue;
                owned.Add(norm);
                owned.Add(Path.GetFileName(norm));
                if (norm.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
                    owned.Add(norm.Substring("Disabled/".Length));
            }
        }
        return owned;
    }

    private bool TryMoveLoosePackFiles(ModMetadata meta, string metadataKey, bool enabled, out string errorMessage)
    {
        errorMessage = "";
        if (meta?.Files == null || meta.Files.Count == 0)
        {
            errorMessage = "Loose pack has no files.";
            return false;
        }

        string safeKey = GetLoosePackSafeKey(metadataKey);
        string disabledRoot = Path.Combine(AppPaths.DisabledModsPath, "Loose", safeKey);
        string dataRoot = GetActiveDataPath();
        string stringsRoot = GetActiveStringsPath();

        foreach (var raw in meta.Files)
        {
            string rel = NormalizeMetadataKey(raw ?? "");
            if (string.IsNullOrWhiteSpace(rel)) continue;

            try
            {
                string activePath;
                string disabledPath;

                if (rel.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase))
                {
                    string fn = Path.GetFileName(rel);
                    activePath = Path.Combine(stringsRoot, fn);
                    disabledPath = Path.Combine(disabledRoot, "Strings", fn);
                }
                else if (rel.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase) ||
                         rel.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                else
                {
                    string clean = rel.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase)
                        ? rel.Substring("Disabled/".Length)
                        : rel;
                    activePath = Path.Combine(dataRoot, clean.Replace("/", "\\"));
                    disabledPath = Path.Combine(disabledRoot, clean.Replace("/", "\\"));
                }

                if (enabled)
                {
                    if (!File.Exists(disabledPath))
                    {
                        if (File.Exists(activePath)) continue;
                        _logger($"[LOOSE] Missing disabled file while enabling: {disabledPath}");
                        continue;
                    }
                    string? destDir = Path.GetDirectoryName(activePath);
                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);
                    if (File.Exists(activePath)) File.Delete(activePath);
                    File.Move(disabledPath, activePath);
                }
                else
                {
                    if (!File.Exists(activePath))
                    {
                        if (File.Exists(disabledPath)) continue;
                        _logger($"[LOOSE] Missing active file while disabling: {activePath}");
                        continue;
                    }
                    string? destDir = Path.GetDirectoryName(disabledPath);
                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);
                    if (File.Exists(disabledPath)) File.Delete(disabledPath);
                    File.Move(activePath, disabledPath);
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                _logger($"[ERROR] Loose toggle failed for '{rel}': {ex.Message}");
                return false;
            }
        }

        TryDeleteEmptyDirectories(disabledRoot);
        return true;
    }

    private static void TryDeleteEmptyDirectories(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir, false);
                }
                catch {  }
            }
            if (!Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root, false);
        }
        catch {  }
    }

    private void CollectExtractedModFilesAndImport(
        string tempDir,
        string displayArchiveName,
        Dictionary<string, ModMetadata> metadata,
        List<string> importedKeys,
        ImportProgressContext progress)
    {
        ImportFromExtractedTree(tempDir, displayArchiveName, metadata, importedKeys, progress);
    }

    public List<string> ImportMod()
    {
        using (var ofd = new OpenFileDialog
               {
                   Multiselect = true,
                   Filter =
                       "Mod files (*.ba2;*.esm;*.esp;*.zip;*.7z;*.rar;*.strings;*.ini;*.json;*.txt;*.toml;*.dll)|*.ba2;*.esm;*.esp;*.zip;*.7z;*.rar;*.strings;*.dlstrings;*.ilstrings;*.ini;*.json;*.txt;*.toml;*.dll"
               })
        {
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                var fNames = ofd.FileNames;
                _logger($"[DEBUG] OpenFileDialog returned {fNames.Length} files: {string.Join(" | ", fNames)}");
                return ImportFiles(fNames.ToList());
            }
        }
        return new List<string>();
    }

    public sealed class ImportProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public int Percent { get; set; }
        public string Stage { get; set; } = "processing";
        public string CurrentItem { get; set; } = "";
    }

    private sealed class ImportProgressContext
    {
        private readonly Action<ImportProgress>? _reporter;
        private int _lastPercent = -1;
        private string _lastStage = "";
        private string _lastItem = "";

        public int CompletedUnits { get; private set; } = 0;
        public int TotalUnits { get; private set; }

        public ImportProgressContext(int initialUnits, Action<ImportProgress>? reporter)
        {
            TotalUnits = Math.Max(1, initialUnits);
            _reporter = reporter;
        }

        public void AddUnits(int amount)
        {
            if (amount <= 0) return;
            TotalUnits += amount;
            Report("scanning", "");
        }

        public void CompleteOne(string stage, string currentItem)
        {
            CompletedUnits = Math.Min(TotalUnits, CompletedUnits + 1);
            Report(stage, currentItem);
        }

        public void CompleteAll(string currentItem)
        {
            CompletedUnits = TotalUnits;
            Report("complete", currentItem);
        }

        private void Report(string stage, string currentItem)
        {
            if (_reporter == null) return;
            int percent = (int)Math.Round((double)CompletedUnits * 100 / Math.Max(1, TotalUnits));
            percent = Math.Clamp(percent, 0, 100);
            if (percent < _lastPercent) percent = _lastPercent;

            bool changed = percent != _lastPercent ||
                           !string.Equals(stage, _lastStage, StringComparison.Ordinal) ||
                           !string.Equals(currentItem, _lastItem, StringComparison.Ordinal);
            if (!changed) return;

            _lastPercent = percent;
            _lastStage = stage;
            _lastItem = currentItem;
            _reporter(new ImportProgress {
                Completed = CompletedUnits,
                Total = TotalUnits,
                Percent = percent,
                Stage = stage,
                CurrentItem = currentItem
            });
        }
    }

    public List<string> ImportFiles(List<string> files, Action<ImportProgress>? progressReporter = null)
    {
        LastImportReportedIssue = false;

        if (VirtualModMode)
        {
            EnsureActiveDataFolders();
        }

        if (string.IsNullOrEmpty(AppPaths.DataPath) || !Directory.Exists(AppPaths.DataPath))
        {
            if (VirtualModMode)
            {
                EnsureActiveDataFolders();
            }
            else
            {
                _statusReporter("error", $"Game Data Path is invalid. Please configure it in Settings.");
                _logger($"[ERROR] ImportFiles halted: DataPath invalid: '{AppPaths.DataPath}'");
                LastImportReportedIssue = true;
                return new List<string>();
            }
        }

        int initialUnits = Math.Max(files.Count, 1);
        var progress = new ImportProgressContext(initialUnits, progressReporter);
        progressReporter?.Invoke(new ImportProgress { Completed = 0, Total = initialUnits, Percent = 0, Stage = "starting", CurrentItem = "" });

        _logger($"[DEBUG] ImportFiles received {files.Count} files.");
        _logger($"[IMPORT] [PC] Preparing to import {files.Count} files...");
        var metadata = LoadMetadata();
        var importedKeys = new List<string>();
        ImportFilesInternal(files, metadata, importedKeys, progress);
        SaveMetadata(metadata);
        progress.CompleteAll("");
        return importedKeys;
    }

    private void ImportFilesInternal(List<string> files, Dictionary<string, ModMetadata> metadata, List<string> importedKeys, ImportProgressContext progress)
    {
        foreach (var file in files)
        {
            try {
                if (Directory.Exists(file))
                {
                    _logger($"[IMPORT] Processing Directory: {file}");
                    if (TryImportDirectoryAsLoosePackage(file, metadata, importedKeys, progress))
                    {
                        progress.CompleteOne("processing", Path.GetFileName(file));
                        continue;
                    }

                    var searchExtensions = new[]
                    {
                        ".ba2", ".esm", ".esp", ".strings", ".dlstrings", ".ilstrings", ".ini", ".json", ".txt", ".toml", ".zip", ".7z", ".rar"
                    };
                    var found = Directory.GetFiles(file, "*.*", SearchOption.AllDirectories)
                        .Where(f =>
                        {
                            string e = Path.GetExtension(f).ToLowerInvariant();
                            return searchExtensions.Contains(e) ||
                                   (e == ".dll" && IsGameRootInjectorName(Path.GetFileName(f)));
                        })
                        .ToList();
                    progress.AddUnits(Math.Max(found.Count - 1, 0));
                    if (found.Any()) ImportFilesInternal(found, metadata, importedKeys, progress);
                    progress.CompleteOne("processing", Path.GetFileName(file));
                    continue;
                }

                string fileName = Path.GetFileName(file);
                string ext = Path.GetExtension(fileName).ToLowerInvariant();

                try {
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var br = new BinaryReader(fs)) {
                        if (fs.Length >= 4) {
                            var magic = br.ReadBytes(4);
                            string magicStr = System.Text.Encoding.ASCII.GetString(magic);
                            
                            if (magicStr == "BTDX") {
                                if (ext != ".ba2") {
                                    _logger($"[IMPORT] Detected BA2 file with wrong extension '{ext}'. Renaming processing to .ba2.");
                                    ext = ".ba2";
                                    fileName = Path.ChangeExtension(fileName, ".ba2");
                                }
                            }
                            else if (magic[0] == 0x50 && magic[1] == 0x4B) {
                                ext = ".zip"; 
                            }
                            else if (magic[0] == 0x37 && magic[1] == 0x7A && magic[2] == 0xBC && magic[3] == 0xAF) {
                                ext = ".7z";
                            }
                            else if (magicStr == "Rar!") {
                                ext = ".rar";
                            }
                        }
                    }
                } catch (Exception headerEx) {
                     _logger($"[WARN] Could not read file header for {fileName}: {headerEx.Message}");
                }
                
                if (ext is ".zip" or ".7z" or ".rar")
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "F76M_Import_" + Guid.NewGuid().ToString().Substring(0, 8));
                    Directory.CreateDirectory(tempDir);
                    try
                    {
                        if (TryExtractArchiveToDirectory(file, ext, tempDir, out string extractError))
                        {
                            CollectExtractedModFilesAndImport(tempDir, fileName, metadata, importedKeys, progress);
                        }
                        else
                        {
                            _statusReporter("error", extractError);
                            LastImportReportedIssue = true;
                        }
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch (Exception cleanupEx) { _logger($"[IMPORT] Failed to remove temp directory '{tempDir}': {cleanupEx.Message}"); }
                    }
                    progress.CompleteOne("processing", fileName);
                    continue;
                }

                if (ext == ".dll")
                {
                    if (!IsGameRootInjectorName(fileName))
                    {
                        _statusReporter("error", "Only dxgi.dll and d3d11.dll can be imported as DLL mods.");
                        progress.CompleteOne("processing", fileName);
                        continue;
                    }

                    InstallGameRootInjector(file, fileName, metadata, importedKeys);
                    progress.CompleteOne("processing", fileName);
                    continue;
                }

                string targetDir = GetActiveDataPath();
                if (ext == ".strings" || ext == ".dlstrings" || ext == ".ilstrings")
                {
                    targetDir = GetActiveStringsPath();
                    if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                }
                string destPath = Path.Combine(targetDir, fileName);
                
                string normalizedSource = Path.GetFullPath(file);
                string normalizedDest = Path.GetFullPath(destPath);
                
                if (!string.Equals(normalizedSource, normalizedDest, StringComparison.OrdinalIgnoreCase)) {
                    if (ConfigFileMerger.IsLooseConfigExtension(ext) && File.Exists(destPath))
                    {
                        if (ConfigFileMerger.TryMergeAdditive(destPath, file, out var merged, out var mergeSummary))
                        {
                            ConfigFileMerger.WriteMergedFile(destPath, merged);
                            _logger($"[IMPORT] Merged config {fileName}: {mergeSummary}");
                            if (!mergeSummary.StartsWith("No new", StringComparison.OrdinalIgnoreCase))
                                _statusReporter?.Invoke("info", $"Merged {fileName}: {mergeSummary}");
                        }
                        else
                        {
                            _logger($"[IMPORT] Config merge failed for {fileName}; kept existing file.");
                            _statusReporter?.Invoke("warning", $"Could not merge {fileName}; your existing file was kept.");
                        }
                    }
                    else
                    {
                        File.Copy(file, destPath, true);
                        _logger($"[IMPORT] [PC] Successfully imported mod: {fileName}");
                    }
                } else {
                    _logger($"[IMPORT] File already in target location: {fileName}. Skipping copy.");
                }

                if (ext is ".ba2" or ".esp" or ".esm")
                    RemoveStaleDisabledCopy(fileName, metadata);

                string relativePath = (targetDir == AppPaths.StringsPath) ? $"Strings/{fileName}" : fileName;
                importedKeys.Add(relativePath);

                if (!metadata.ContainsKey(relativePath)) {
                    metadata[relativePath] = new ModMetadata { 
                        Name = Path.GetFileNameWithoutExtension(fileName), 
                        Files = new List<string> { relativePath },
                        IsEnabled = true
                    };
                } else {
                    metadata[relativePath].IsEnabled = true;
                }
                progress.CompleteOne("processing", fileName);
            } catch (Exception ex) {
                _logger($"[ERROR] Failed to import {file}: {ex.Message}");
                progress.CompleteOne("processing", Path.GetFileName(file));
            }
        }
    }
    private void RemoveStaleDisabledCopy(string fileName, Dictionary<string, ModMetadata> metadata)
    {
        if (string.IsNullOrWhiteSpace(AppPaths.DisabledModsPath)) return;

        string disabledPath = Path.Combine(AppPaths.DisabledModsPath, fileName);
        if (File.Exists(disabledPath))
        {
            try
            {
                File.Delete(disabledPath);
                _logger($"[IMPORT] Removed old disabled copy of {fileName}.");
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Could not remove old disabled copy '{disabledPath}': {ex.Message}");
            }
        }

        string disabledKey = $"Disabled/{fileName}";
        if (!metadata.TryGetValue(disabledKey, out var disabledMeta) || disabledMeta == null) return;

        metadata.Remove(disabledKey);
        if (disabledMeta.Files != null)
        {
            disabledMeta.Files = disabledMeta.Files
                .Select(f => NormalizeMetadataKey(f ?? ""))
                .Select(f => f.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase) ? f.Substring("Disabled/".Length) : f)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        metadata[fileName] = metadata.TryGetValue(fileName, out var liveMeta) && liveMeta != null
            ? MergeMetadataEntries(liveMeta, disabledMeta)
            : disabledMeta;
    }

    public void BulkUpdateModStatus(List<string> enabledMods)
    {
        var metadata = LoadMetadata();
        string activeDataPath = GetActiveDataPath();
        var files = new List<string>();
        if (Directory.Exists(activeDataPath))
        {
            files.AddRange(Directory.GetFiles(activeDataPath, "*.*")
                .Where(f => f.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName));
            files.AddRange(EnumerateLooseConfigListKeys(activeDataPath));
        }

        if (Directory.Exists(AppPaths.BundlesPath))
        {
            var bFiles = Directory.GetFiles(AppPaths.BundlesPath, "*.ba2")
                .Select(f => $"Bundles/{Path.GetFileName(f)}");
            files.AddRange(bFiles);
        }

        files.AddRange(DiscoverGameRootInjectorListPaths());

        foreach (var file in files)
        {
            string normalized = file.Replace("\\", "/");
            if (IsVanillaFile(Path.GetFileName(normalized))) continue;
            
            if (!metadata.ContainsKey(normalized)) {
                metadata[normalized] = new ModMetadata { Name = Path.GetFileNameWithoutExtension(normalized), Files = new List<string> { normalized } };
            }
            metadata[normalized].IsEnabled = enabledMods.Contains(normalized);
        }
        SaveMetadata(metadata);
    }

    public string LastRenameFrom { get; private set; } = "";
    public string LastRenameTo { get; private set; } = "";

    public void RenameMod(string currentName, string newName, string details = "")
    {
        LastRenameFrom = "";
        LastRenameTo = "";
        try 
        {
            string normCurrent = NormalizeMetadataKey(currentName);
            if (IsGameRootInjectorListPath(normCurrent))
            {
                _statusReporter("error", "Cannot rename game injector DLLs (dxgi.dll / d3d11.dll).");
                return;
            }

            var metadata = LoadMetadata();
            string metaKey = ResolveMetadataKey(metadata, normCurrent);
            if (IsLoosePackKey(metadata, string.IsNullOrEmpty(metaKey) ? normCurrent : metaKey))
            {
                RenameLoosePack(metadata, string.IsNullOrEmpty(metaKey) ? normCurrent : metaKey, newName, details);
                return;
            }

            string currentPath = GetFullPath(currentName);
            
            string dir = Path.GetDirectoryName(string.IsNullOrEmpty(metaKey) ? currentName : metaKey) ?? "";
            string baseNewName = newName;
            if (!Path.HasExtension(baseNewName))
            {
                string ext = Path.GetExtension(string.IsNullOrEmpty(metaKey) ? currentName : metaKey);
                if (!string.IsNullOrEmpty(ext)) baseNewName += ext;
            }

            string newRelativePath = string.IsNullOrEmpty(dir) ? baseNewName : Path.Combine(dir, baseNewName).Replace("\\", "/");
            string newPath = GetFullPath(newRelativePath);
            newName = NormalizeMetadataKey(newRelativePath);

            if (!File.Exists(currentPath))
            {
                if (!string.IsNullOrEmpty(metaKey))
                    currentPath = GetFullPath(metaKey);
            }

            if (!File.Exists(currentPath))
            {
                _statusReporter("error", $"File not found: {currentName}");
                return;
            }

            if (File.Exists(newPath))
            {
                _statusReporter("error", $"A file with the name {newName} already exists.");
                return;
            }

            File.Move(currentPath, newPath);
            _logger($"[MODS] Renamed {currentName} to {newName}");

            string removeKey = !string.IsNullOrEmpty(metaKey) && metadata.ContainsKey(metaKey)
                ? metaKey
                : (metadata.ContainsKey(normCurrent) ? normCurrent : currentName);

            if (metadata.ContainsKey(removeKey))
            {
                var meta = metadata[removeKey];
                metadata.Remove(removeKey);
                
                meta.Name = Path.GetFileNameWithoutExtension(newName);
                meta.Details = details ?? "";
                meta.Files = new List<string> { newName };
                
                metadata[newName] = meta;
                SaveMetadata(metadata);
            }
            else
            {
                var newMeta = new ModMetadata { 
                    Name = Path.GetFileNameWithoutExtension(newName),
                    Details = details ?? "",
                    Files = new List<string> { newName },
                    IsEnabled = false,
                    IsBundle = currentName.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)
                };
                metadata[newName] = newMeta;
                SaveMetadata(metadata);
            }

            _statusReporter("success", $"Renamed to {newName}");

        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Rename failed: {ex.Message}");
            _statusReporter?.Invoke("error", $"Rename failed: {ex.Message}");
        }
    }

    private bool IsLoosePackKey(Dictionary<string, ModMetadata> metadata, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (key.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase)) return true;
        return metadata.TryGetValue(key, out var meta) && meta != null && meta.IsLoose;
    }

    private void RenameLoosePack(Dictionary<string, ModMetadata> metadata, string metaKey, string requestedName, string details)
    {
        if (!metadata.TryGetValue(metaKey, out var meta) || meta == null)
        {
            _statusReporter("error", $"File not found: {metaKey}");
            return;
        }

        string requested = (requestedName ?? "").Replace('\\', '/').Trim().Trim('/');
        if (requested.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase))
            requested = requested.Substring("Loose/".Length).Trim('/');

        if (string.IsNullOrWhiteSpace(requested))
        {
            _statusReporter("error", "Enter a mod name.");
            return;
        }

        if (requested.Contains('/'))
        {
            _statusReporter("error", "Keep the name as Loose\\Name. Extra folders aren't part of a loose mod name.");
            return;
        }

        string sanitized = SanitizeLoosePackName(requested);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            _statusReporter("error", "Enter a mod name.");
            return;
        }

        string newKey = MakeLooseMetadataKey(sanitized);
        meta.Details = details ?? "";
        meta.IsLoose = true;
        meta.Name = sanitized;

        if (string.Equals(newKey, metaKey, StringComparison.OrdinalIgnoreCase))
        {
            metadata[metaKey] = meta;
            SaveMetadata(metadata);
            _statusReporter("success", $"Saved {newKey}");
            return;
        }

        if (metadata.ContainsKey(newKey))
        {
            _statusReporter("error", $"A mod named {newKey} already exists.");
            return;
        }

        string oldPack = GetLoosePackSafeKey(metaKey);
        if (!TryRenameLoosePackOnDisk(meta, oldPack, sanitized, out string diskError))
        {
            _statusReporter("error", string.IsNullOrWhiteSpace(diskError) ? "Rename failed." : diskError);
            return;
        }

        metadata.Remove(metaKey);
        metadata[newKey] = meta;
        SaveMetadata(metadata);
        LastRenameFrom = metaKey;
        LastRenameTo = newKey;
        _logger($"[MODS] Renamed loose pack {metaKey} to {newKey}");
        _statusReporter("success", $"Renamed to {newKey}");

        try { SyncArchiveListToCustomIni(); }
        catch (Exception ex) { _logger($"[MODS] Archive list refresh after loose rename failed: {ex.Message}"); }
    }

    private bool TryRenameLoosePackOnDisk(ModMetadata meta, string oldPack, string newPack, out string error)
    {
        error = "";
        if (string.Equals(oldPack, newPack, StringComparison.OrdinalIgnoreCase))
            return true;

        var pairs = new List<(string OldFile, string NewFile)>
        {
            (oldPack + ".ba2", newPack + ".ba2"),
            (oldPack + " - Textures.ba2", newPack + " - Textures.ba2")
        };

        string dataRoot = GetActiveDataPath();
        string oldDisabled = Path.Combine(AppPaths.DisabledModsPath, "Loose", oldPack);
        string newDisabled = Path.Combine(AppPaths.DisabledModsPath, "Loose", newPack);

        foreach (var (oldFile, newFile) in pairs)
        {
            foreach (string root in new[] { dataRoot, oldDisabled })
            {
                string src = Path.Combine(root, oldFile);
                if (!File.Exists(src)) continue;
                string dest = Path.Combine(root, newFile);
                if (File.Exists(dest))
                {
                    error = $"A file named {newFile} already exists.";
                    return false;
                }
            }
        }

        if (Directory.Exists(oldDisabled) &&
            Directory.Exists(newDisabled) &&
            !string.Equals(Path.GetFullPath(oldDisabled), Path.GetFullPath(newDisabled), StringComparison.OrdinalIgnoreCase))
        {
            error = $"A loose mod folder named {newPack} already exists.";
            return false;
        }

        foreach (var (oldFile, newFile) in pairs)
        {
            foreach (string root in new[] { dataRoot, oldDisabled })
            {
                string src = Path.Combine(root, oldFile);
                if (!File.Exists(src)) continue;
                File.Move(src, Path.Combine(root, newFile));
                _logger($"[MODS] Renamed loose archive {oldFile} to {newFile}");
            }
        }

        if (Directory.Exists(oldDisabled) &&
            !string.Equals(Path.GetFullPath(oldDisabled), Path.GetFullPath(newDisabled), StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(newDisabled);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);
            Directory.Move(oldDisabled, newDisabled);
        }

        var fileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (oldFile, newFile) in pairs)
            fileMap[oldFile] = newFile;

        if (meta.Files != null)
        {
            meta.Files = meta.Files.Select(raw =>
            {
                string rel = NormalizeMetadataKey(raw ?? "");
                string file = Path.GetFileName(rel);
                if (!fileMap.TryGetValue(file, out var mapped)) return rel;
                string dir = (Path.GetDirectoryName(rel) ?? "").Replace('\\', '/');
                return string.IsNullOrEmpty(dir) ? mapped : dir + "/" + mapped;
            }).ToList();
        }

        return true;
    }

    public void SaveModDetails(string modName, string details = "")
    {
        try
        {
            var metadata = LoadMetadata();
            string normalizedName = NormalizeMetadataKey(modName);
            if (string.IsNullOrWhiteSpace(normalizedName)) return;

            string targetKey = ResolveMetadataKey(metadata, normalizedName);

            if (metadata.ContainsKey(targetKey))
            {
                metadata[targetKey].Details = details ?? "";
            }
            else
            {
                string createKey = NormalizeMetadataKey(targetKey);
                if (string.IsNullOrEmpty(createKey))
                {
                    createKey = normalizedName;
                }

                metadata[createKey] = new ModMetadata
                {
                    Name = Path.GetFileNameWithoutExtension(Path.GetFileName(createKey)).Trim(),
                    Details = details ?? "",
                    Files = new List<string> { createKey }
                };
            }

            string finalKey = string.IsNullOrWhiteSpace(targetKey) ? normalizedName : NormalizeMetadataKey(targetKey);
            string finalFileName = NormalizeMetadataKey(Path.GetFileName(finalKey));
            var siblingKeys = new List<string> {
                finalFileName,
                $"Disabled/{finalFileName}",
                $"Bundles/{finalFileName}",
                $"Strings/{finalFileName}"
            };
            if (IsGameRootInjectorName(finalFileName))
            {
                siblingKeys.Add($"GameRoot/{finalFileName}");
                siblingKeys.Add($"Disabled/GameRoot/{finalFileName}");
            }
            foreach (var sibling in siblingKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(sibling, finalKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (metadata.TryGetValue(sibling, out var siblingMeta) && siblingMeta != null)
                {
                    siblingMeta.Details = details ?? "";
                }
            }

            SaveMetadata(metadata);
            _logger($"[MODS] SaveModDetails resolvedKey='{finalKey}' detailsLen={(details ?? "").Length}");
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Save mod details failed: {ex.Message}");
            _statusReporter?.Invoke("error", $"Save mod details failed: {ex.Message}");
        }
    }

    public void DeleteMod(string fileName)
    {
        var metadata = LoadMetadata();
        
        _logger($"[DELETE] Request to delete: '{fileName}'");

        string resolvedKey = ResolveMetadataKey(metadata, fileName);
        if (string.IsNullOrWhiteSpace(resolvedKey))
            resolvedKey = NormalizeMetadataKey(fileName);

        bool isMarkedBundle = false;
        if (!string.IsNullOrWhiteSpace(resolvedKey) &&
            metadata.TryGetValue(resolvedKey, out var resolvedMeta) &&
            resolvedMeta != null &&
            resolvedMeta.IsBundle)
        {
            isMarkedBundle = true;
        }
        
        string alternateName = fileName.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".ba2";
        if (!isMarkedBundle)
        {
            string altResolved = ResolveMetadataKey(metadata, alternateName);
            if (!string.IsNullOrWhiteSpace(altResolved) &&
                metadata.TryGetValue(altResolved, out var altMeta) &&
                altMeta != null &&
                altMeta.IsBundle)
            {
                isMarkedBundle = true;
                resolvedKey = altResolved;
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedKey))
            resolvedKey = NormalizeMetadataKey(fileName);

        List<string> filesToDelete = new List<string>();
        if (!string.IsNullOrWhiteSpace(resolvedKey) &&
            metadata.TryGetValue(resolvedKey, out var metaForFiles) &&
            metaForFiles?.Files != null &&
            metaForFiles.Files.Count > 0)
        {
            filesToDelete.AddRange(metaForFiles.Files);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(resolvedKey))
                filesToDelete.Add(resolvedKey);
            filesToDelete.Add(NormalizeMetadataKey(fileName));
        }

        filesToDelete = filesToDelete
            .Select(NormalizeMetadataKey)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool deletedAny = false;
        bool isLooseDelete = (!string.IsNullOrWhiteSpace(resolvedKey) &&
                              metadata.TryGetValue(resolvedKey, out var looseDelMeta) &&
                              looseDelMeta != null &&
                              (looseDelMeta.IsLoose || resolvedKey.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase))) ||
                             fileName.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase);
        string looseSafeKey = isLooseDelete ? GetLoosePackSafeKey(resolvedKey) : "";

        foreach (var f in filesToDelete)
        {
            string cleanF = f.Replace("Disabled/", "").Replace("Bundles/", "").Replace("Strings/", "")
                .Replace("GameRoot/", "").Replace("CoreIni/", "").Replace("Loose/", "");
            
            var possiblePaths = new List<string> {
                Path.Combine(GetActiveDataPath(), cleanF),
                Path.Combine(AppPaths.DisabledModsPath, cleanF),
                Path.Combine(AppPaths.BundlesPath, cleanF),
                Path.Combine(GetActiveDataPath(), f.Replace("/", "\\")),
                GetFullPath(f),
                GetFullPath(resolvedKey),
                GetFullPath(NormalizeMetadataKey(fileName))
            };

            if (VirtualModMode &&
                IsConfigListKey(f) &&
                !f.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(AppPaths.DataPath))
            {
                string liveRel = cleanF.Replace("/", "\\");
                if (!string.IsNullOrWhiteSpace(liveRel))
                    possiblePaths.Add(Path.Combine(AppPaths.DataPath, liveRel));
            }

            if (isLooseDelete && !string.IsNullOrWhiteSpace(looseSafeKey))
            {
                string looseRel = f.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine("Strings", Path.GetFileName(f))
                    : cleanF.Replace("/", "\\");
                possiblePaths.Add(Path.Combine(AppPaths.DisabledModsPath, "Loose", looseSafeKey, looseRel));
                if (f.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase))
                    possiblePaths.Add(Path.Combine(GetActiveStringsPath(), Path.GetFileName(f)));
            }

            string injBn = Path.GetFileName(f.Replace("\\", "/"));
            if (IsGameRootInjectorName(injBn))
            {
                if (!string.IsNullOrEmpty(AppPaths.GameInstallRoot))
                    possiblePaths.Add(Path.Combine(AppPaths.GameInstallRoot, injBn));
                possiblePaths.Add(Path.Combine(AppPaths.ManagedStagingGameRootPath, injBn));
            }

            foreach (var path in possiblePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path))
                {
                    try {
                        File.Delete(path);
                        deletedAny = true;
                        _logger($"[DELETE] Physically deleted file: {path}");
                    } catch (Exception ex) {
                        _logger($"[ERROR] Failed to delete {path}: {ex.Message}");
                    }
                }
            }
        }

        if (isLooseDelete && !string.IsNullOrWhiteSpace(looseSafeKey))
        {
            string looseDisabledRoot = Path.Combine(AppPaths.DisabledModsPath, "Loose", looseSafeKey);
            TryDeleteEmptyDirectories(looseDisabledRoot);
            foreach (var f in filesToDelete)
            {
                try
                {
                    string clean = NormalizeMetadataKey(f).Replace("Disabled/", "").Replace("Strings/", "");
                    if (clean.Contains('/'))
                    {
                        string dir = Path.Combine(GetActiveDataPath(), Path.GetDirectoryName(clean.Replace("/", "\\")) ?? "");
                        TryDeleteEmptyDirectories(dir);
                    }
                }
                catch {  }
            }
        }

        var keysToRemove = metadata.Keys
            .Where(k =>
                string.Equals(k, resolvedKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, NormalizeMetadataKey(fileName), StringComparison.OrdinalIgnoreCase) ||
                filesToDelete.Any(f => string.Equals(k, f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        string baseName = Path.GetFileName(resolvedKey);
        if (!string.IsNullOrWhiteSpace(baseName))
        {
            var siblingCandidates = new List<string> { baseName, $"Disabled/{baseName}" };
            if (IsGameRootInjectorName(baseName) ||
                resolvedKey.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase) ||
                resolvedKey.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase))
            {
                siblingCandidates.Add($"GameRoot/{baseName}");
                siblingCandidates.Add($"Disabled/GameRoot/{baseName}");
            }
            if (resolvedKey.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase))
                siblingCandidates.Add($"Bundles/{baseName}");
            if (resolvedKey.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase) ||
                resolvedKey.StartsWith("Disabled/Strings/", StringComparison.OrdinalIgnoreCase))
                siblingCandidates.Add($"Strings/{baseName}");
            if (resolvedKey.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase))
                siblingCandidates.Add($"Loose/{baseName}");

            foreach (var sibling in siblingCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (metadata.ContainsKey(sibling) &&
                    !keysToRemove.Contains(sibling, StringComparer.OrdinalIgnoreCase))
                    keysToRemove.Add(sibling);
            }
        }

        if (keysToRemove.Count > 0)
        {
            foreach (var key in keysToRemove)
                metadata.Remove(key);
            SaveMetadata(metadata);
            _logger($"[DELETE] Metadata record(s) removed: {string.Join(", ", keysToRemove)}");
        }
        else
        {
            InvalidateModsListCache();
        }

        if (deletedAny) {
            _statusReporter?.Invoke("success", $"Deleted mod: {Path.GetFileName(resolvedKey)}");
        } else {
             _logger($"[DELETE] Finished, but no physical files were deleted for '{fileName}' (maybe already gone).");
        }

        SyncArchiveListToCustomIni();
    }

    public int DeleteAllMods()
    {
        var names = GetModsList()
            .Select(m => (string)((dynamic)m).originalName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in names.ToList())
            DeleteMod(name);

        return names.Count;
    }

    public bool PromoteBundleToMod(string originalName, out string error, out string newModKey)
    {
        error = "";
        newModKey = "";
        try
        {
            string normalized = (originalName ?? "").Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = "Bundle name is empty.";
                return false;
            }

            string fileName = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                error = "Invalid bundle file name.";
                return false;
            }

            string bundleListKey = normalized.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : $"Bundles/{fileName}";

            string dataPath = GetActiveDataPath();
            if (string.IsNullOrWhiteSpace(dataPath) || !Directory.Exists(dataPath))
            {
                error = "Game Data folder is not configured or does not exist.";
                return false;
            }

            var metadata = LoadMetadata();
            string metaKey = ResolveMetadataKey(metadata, NormalizeMetadataKey(normalized));
            if (string.IsNullOrEmpty(metaKey))
                metaKey = ResolveMetadataKey(metadata, NormalizeMetadataKey(bundleListKey));

            var filesToMove = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fileName };
            if (!string.IsNullOrEmpty(metaKey) && metadata.TryGetValue(metaKey, out var bundleMeta) && bundleMeta?.Files != null)
            {
                foreach (var listed in bundleMeta.Files)
                {
                    string listedName = Path.GetFileName((listed ?? "").Replace('\\', '/'));
                    if (!string.IsNullOrWhiteSpace(listedName))
                        filesToMove.Add(listedName);
                }
            }

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string textureCompanion = $"{baseName} - Textures.ba2";
            if (FindBundleFileOnDisk(textureCompanion) != null)
                filesToMove.Add(textureCompanion);

            var promotedFileNames = new List<string>();
            foreach (string fn in filesToMove)
            {
                var locations = FindAllBundleFileLocations(fn, dataPath);
                if (locations.Count == 0)
                    continue;

                string dst = Path.Combine(dataPath, fn);
                string chosenSource = locations.FirstOrDefault(l =>
                    string.Equals(l, dst, StringComparison.OrdinalIgnoreCase))
                    ?? locations.FirstOrDefault(l =>
                        !string.IsNullOrEmpty(AppPaths.DisabledModsPath) &&
                        l.StartsWith(AppPaths.DisabledModsPath, StringComparison.OrdinalIgnoreCase))
                    ?? locations.FirstOrDefault(l =>
                        !string.IsNullOrEmpty(AppPaths.BundlesPath) &&
                        l.StartsWith(AppPaths.BundlesPath, StringComparison.OrdinalIgnoreCase))
                    ?? locations[0];

                if (!string.Equals(chosenSource, dst, StringComparison.OrdinalIgnoreCase) && File.Exists(dst))
                {
                    error = $"A file named '{fn}' already exists in your Data folder.";
                    return false;
                }

                if (!string.Equals(chosenSource, dst, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(chosenSource, dst);
                    _logger($"[BUNDLE] Promoted to mod: '{fn}' -> Data folder");
                }
                else
                {
                    _logger($"[BUNDLE] Promoted to mod: '{fn}' already in Data folder");
                }

                promotedFileNames.Add(fn);
                RemoveLeftoverBundleCopies(fn, dst, dataPath);
            }

            if (promotedFileNames.Count == 0)
            {
                error = $"Bundle file not found: {fileName}";
                return false;
            }

            ModMetadata promoted;
            if (!string.IsNullOrEmpty(metaKey) && metadata.TryGetValue(metaKey, out var existingMeta) && existingMeta != null)
                promoted = existingMeta;
            else
                promoted = new ModMetadata { Name = baseName };

            promoted.IsBundle = false;
            promoted.IsEnabled = true;
            promoted.Files = promotedFileNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var keysToRemove = metadata.Keys
                .Where(k =>
                    string.Equals(k, bundleListKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(k, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(k, fileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(k, $"Disabled/{fileName}", StringComparison.OrdinalIgnoreCase) ||
                    (k.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(Path.GetFileName(k), fileName, StringComparison.OrdinalIgnoreCase)) ||
                    (k.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(Path.GetFileName(k), fileName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (string key in keysToRemove)
                metadata.Remove(key);

            newModKey = fileName;
            metadata[fileName] = promoted;
            SaveMetadata(metadata);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void DeleteBundleFile(string fileName)
    {
        string path = GetFullPath(fileName);
            var metadata = LoadMetadata();
            
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger($"[BUNDLE] Permanently deleted bundle file: {fileName}");
            }

            if (metadata.ContainsKey(fileName))
            {
                metadata.Remove(fileName);
                SaveMetadata(metadata);
            }
        }

    public string ResolveListKeyToFullPath(string relativePath) => GetFullPath(relativePath);

    private static bool TryParseStringModDiskName(string? fileName, out string canonicalName, out bool disabledSuffix)
    {
        disabledSuffix = false;
        canonicalName = "";
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        string name = fileName.Trim();
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            disabledSuffix = true;
            name = name.Substring(0, name.Length - ".disabled".Length);
        }
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is not (".strings" or ".dlstrings" or ".ilstrings")) return false;
        canonicalName = Path.GetFileNameWithoutExtension(name) + ext;
        return !string.IsNullOrWhiteSpace(canonicalName);
    }

    private string ResolveStringModDiskPath(string canonicalFileName)
    {
        string dir = GetActiveStringsPath();
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(canonicalFileName)) return "";
        string enabledPath = Path.Combine(dir, canonicalFileName);
        if (File.Exists(enabledPath)) return enabledPath;
        if (!Directory.Exists(dir)) return enabledPath;
        string? disabledHit = Directory.GetFiles(dir, canonicalFileName + ".disabled").FirstOrDefault();
        return disabledHit ?? enabledPath;
    }

    private bool TrySetStringModSuffixEnabled(string listKey, bool enabled, out string errorMessage)
    {
        errorMessage = "";
        string canonical = Path.GetFileName((listKey ?? "").Replace("\\", "/"));
        if (!TryParseStringModDiskName(canonical, out canonical, out _))
        {
            errorMessage = "Not a string mod.";
            return false;
        }

        string dir = GetActiveStringsPath();
        if (string.IsNullOrWhiteSpace(dir))
        {
            errorMessage = "Strings folder is not set.";
            return false;
        }
        Directory.CreateDirectory(dir);

        string enabledPath = Path.Combine(dir, canonical);
        string? disabledHit = Directory.GetFiles(dir, canonical + ".disabled").FirstOrDefault();
        try
        {
            if (enabled)
            {
                if (File.Exists(enabledPath))
                {
                    if (!string.IsNullOrEmpty(disabledHit) && File.Exists(disabledHit))
                        File.Delete(disabledHit);
                    return true;
                }
                if (string.IsNullOrEmpty(disabledHit) || !File.Exists(disabledHit))
                {
                    errorMessage = "String file not found.";
                    return false;
                }
                File.Move(disabledHit, enabledPath);
                return true;
            }

            if (!File.Exists(enabledPath))
                return !string.IsNullOrEmpty(disabledHit) && File.Exists(disabledHit);
            if (!string.IsNullOrEmpty(disabledHit) && File.Exists(disabledHit))
                File.Delete(disabledHit);
            File.Move(enabledPath, enabledPath + ".disabled");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private string GetFullPath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return "";
        string normalized = relativePath.Replace("\\", "/");
        if (normalized.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(fn)) return "";
            if (AppPaths.IsF76AddToOverlayFileName(fn))
                return AppPaths.GetF76AddToOverlayPath(fn);
            return Path.Combine(AppPaths.DocumentsPath ?? "", fn);
        }
        if (normalized.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, normalized.Replace("/", "\\"));
        }
        if (normalized.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(normalized.Substring("Disabled/GameRoot/".Length));
            return Path.Combine(AppPaths.DisabledModsPath, fn);
        }
        if (normalized.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(normalized.Substring("GameRoot/".Length));
            string root = VirtualModMode ? AppPaths.ManagedStagingGameRootPath : AppPaths.GameInstallRoot;
            return Path.Combine(root, fn);
        }
        if (normalized.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(normalized.Substring("Strings/".Length));
            return ResolveStringModDiskPath(fn);
        }
        if (normalized.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
        {
            string fileName = normalized.Substring("Disabled/".Length);
            return Path.Combine(AppPaths.DisabledModsPath, fileName.Replace("/", "\\"));
        }
        return Path.Combine(GetActiveDataPath(), normalized.Replace("/", "\\"));
    }

    public void ToggleMods(List<string> modKeys, bool enabled)
    {
        var metadata = LoadMetadata();
        bool changed = false;
        foreach (var key in modKeys)
        {
            string normalized = key.Replace("\\", "/");
            if (metadata.ContainsKey(normalized))
            {
                metadata[normalized].IsEnabled = enabled;
                changed = true;
            }
        }
        if (changed) SaveMetadata(metadata);
    }

    public static long? TryParseNexusModIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var uri = new Uri(url.Trim());
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i].Equals("mods", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(segments[i + 1], out long modId))
                {
                    return modId;
                }
            }
        }
        catch { }
        return null;
    }

    public void ApplyNexusLinkageToImportedKeys(
        IEnumerable<string> importedKeys,
        long nexusModId,
        long nexusFileId,
        string? nexusFileVersion,
        long? nexusFileUploaded,
        string? modName = null,
        string? author = null,
        string? details = null,
        string? category = null)
    {
        var metadata = LoadMetadata();
        bool changed = false;
        string version = (nexusFileVersion ?? "").Trim();
        string nexusUrl = $"https://www.nexusmods.com/fallout76/mods/{nexusModId}";
        string displayName = (modName ?? "").Trim();
        string displayAuthor = (author ?? "").Trim();
        string displayDetails = (details ?? "").Trim();
        string displayCategory = (category ?? "").Trim();

        var keys = (importedKeys ?? Array.Empty<string>()).ToList();
        int linkedRowCount = keys
            .Select(NormalizeMetadataKey)
            .Where(k => !string.IsNullOrEmpty(k) && !IsConfigListKey(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        bool multiFile = linkedRowCount > 1;

        foreach (var rawKey in keys)
        {
            string key = ResolveMetadataKey(metadata, NormalizeMetadataKey(rawKey));
            if (string.IsNullOrEmpty(key)) key = NormalizeMetadataKey(rawKey);
            if (string.IsNullOrEmpty(key)) continue;
            if (IsConfigListKey(key)) continue;

            if (!metadata.TryGetValue(key, out var meta) || meta == null)
            {
                meta = new ModMetadata
                {
                    Name = Path.GetFileNameWithoutExtension(key),
                    Files = new List<string> { key }
                };
                metadata[key] = meta;
            }

            meta.NexusModId = nexusModId;
            meta.NexusFileId = nexusFileId;
            if (!string.IsNullOrWhiteSpace(version))
            {
                meta.NexusFileVersion = version;
                meta.Version = version;
            }
            if (nexusFileUploaded.HasValue) meta.NexusFileUploaded = nexusFileUploaded;
            if (string.IsNullOrWhiteSpace(meta.URL)) meta.URL = nexusUrl;

            if (!string.IsNullOrWhiteSpace(displayName) && !displayName.Equals("Unknown Mod", StringComparison.OrdinalIgnoreCase))
            {
                string fileBase = Path.GetFileNameWithoutExtension(key);
                string rowName = multiFile ? $"{displayName} ({fileBase})" : displayName;
                if (string.IsNullOrWhiteSpace(meta.Name) || meta.Name.Equals("Unknown Mod", StringComparison.OrdinalIgnoreCase) ||
                    meta.Name.Equals(fileBase, StringComparison.OrdinalIgnoreCase) ||
                    (multiFile && meta.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase)))
                {
                    meta.Name = rowName;
                }
            }
            if (!string.IsNullOrWhiteSpace(displayAuthor) && !displayAuthor.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(meta.Author) || meta.Author.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    meta.Author = displayAuthor;
            }
            if (!string.IsNullOrWhiteSpace(displayDetails))
            {
                if (string.IsNullOrWhiteSpace(meta.Details))
                    meta.Details = displayDetails;
            }
            if (!string.IsNullOrWhiteSpace(displayCategory) && !displayCategory.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(meta.Category) || meta.Category.Equals("General", StringComparison.OrdinalIgnoreCase))
                    meta.Category = displayCategory;
            }

            changed = true;
        }

        if (changed) SaveMetadata(metadata);
    }

    public List<string> ReplaceModAfterNexusUpdate(string replaceOriginalName, IReadOnlyList<string> importedKeys)
    {
        var finalKeys = new List<string>();
        if (string.IsNullOrWhiteSpace(replaceOriginalName) || importedKeys == null || importedKeys.Count == 0)
            return finalKeys;

        var metadata = LoadMetadata();
        string oldKey = ResolveMetadataKey(metadata, NormalizeMetadataKey(replaceOriginalName));
        if (string.IsNullOrEmpty(oldKey)) oldKey = NormalizeMetadataKey(replaceOriginalName);

        var newKeys = importedKeys
            .Select(NormalizeMetadataKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (newKeys.Count == 0) return finalKeys;

        var newKeySet = new HashSet<string>(newKeys, StringComparer.OrdinalIgnoreCase);
        metadata.TryGetValue(oldKey, out var oldMeta);

        bool preserveDisabled = oldKey.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase) ||
                                (oldMeta != null && !oldMeta.IsEnabled);
        bool oldWasLoosePack = oldKey.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase) ||
                               (oldMeta?.IsLoose ?? false);

        var oldTracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { oldKey };
        if (oldMeta?.Files != null)
        {
            foreach (var f in oldMeta.Files)
            {
                string nk = NormalizeMetadataKey(f);
                if (!string.IsNullOrEmpty(nk)) oldTracked.Add(nk);
            }
        }

        if (oldMeta?.NexusModId is long linkedModId && linkedModId > 0)
        {
            foreach (var kvp in metadata)
            {
                if (kvp.Value?.NexusModId != linkedModId) continue;
                string metaKey = NormalizeMetadataKey(kvp.Key);
                if (!string.IsNullOrEmpty(metaKey)) oldTracked.Add(metaKey);
                if (kvp.Value.Files == null) continue;
                foreach (var f in kvp.Value.Files)
                {
                    string fileKey = NormalizeMetadataKey(f);
                    if (!string.IsNullOrEmpty(fileKey)) oldTracked.Add(fileKey);
                }
            }
        }

        oldTracked.RemoveWhere(IsConfigListKey);

        var newFileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nk in newKeys)
        {
            newFileSet.Add(StripDisabledPrefix(nk));
            if (metadata.TryGetValue(nk, out var newMeta) && newMeta?.Files != null)
            {
                foreach (var f in newMeta.Files)
                {
                    string nf = NormalizeMetadataKey(f ?? "");
                    if (!string.IsNullOrEmpty(nf)) newFileSet.Add(StripDisabledPrefix(nf));
                }
            }
        }

        foreach (var tracked in oldTracked)
        {
            if (newKeySet.Contains(tracked) || newFileSet.Contains(StripDisabledPrefix(tracked))) continue;
            TryDeletePhysicalModFile(tracked);
        }

        foreach (var metaKey in metadata.Keys.ToList())
        {
            string normalized = NormalizeMetadataKey(metaKey);
            if (oldTracked.Contains(normalized) && !newKeySet.Contains(normalized))
                metadata.Remove(metaKey);
        }

        foreach (var nk in newKeys)
        {
            string key = ResolveMetadataKey(metadata, nk);
            if (string.IsNullOrEmpty(key)) key = nk;

            if (!metadata.TryGetValue(key, out var meta) || meta == null)
            {
                meta = new ModMetadata
                {
                    Name = Path.GetFileNameWithoutExtension(Path.GetFileName(key)),
                    Files = new List<string> { key },
                    IsEnabled = !preserveDisabled
                };
                metadata[key] = meta;
            }

            if (oldMeta != null && !IsConfigListKey(key))
            {
                if (!string.IsNullOrWhiteSpace(oldMeta.Details)) meta.Details = oldMeta.Details;
                if (oldMeta.LoadOrder != 0) meta.LoadOrder = oldMeta.LoadOrder;
                if (!oldWasLoosePack &&
                    !string.IsNullOrWhiteSpace(oldMeta.Name) && !oldMeta.Name.Equals("Unknown Mod", StringComparison.OrdinalIgnoreCase))
                    meta.Name = oldMeta.Name;
                if (!string.IsNullOrWhiteSpace(oldMeta.Author) && !oldMeta.Author.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    meta.Author = oldMeta.Author;
                if (!string.IsNullOrWhiteSpace(oldMeta.Category) && !oldMeta.Category.Equals("General", StringComparison.OrdinalIgnoreCase))
                    meta.Category = oldMeta.Category;
            }

            if (meta.Files == null || meta.Files.Count == 0)
                meta.Files = new List<string> { key };
        }

        SaveMetadata(metadata);

        bool toggled = false;
        foreach (var nk in newKeys)
        {
            string activeKey = StripDisabledPrefix(nk);
            if (string.IsNullOrWhiteSpace(activeKey)) continue;

            if (preserveDisabled)
            {
                ToggleModEnabled(activeKey, false, out _);
                toggled = true;
            }
            else if (nk.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase))
            {
                ToggleModEnabled(activeKey, true, out _);
                toggled = true;
            }
        }
        if (toggled) metadata = LoadMetadata();

        foreach (var nk in newKeys)
        {
            string resolved = ResolveMetadataKey(metadata, nk);
            if (string.IsNullOrEmpty(resolved)) resolved = nk;
            if (!finalKeys.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                finalKeys.Add(resolved);
        }

        _logger($"[NEXUS] Replaced mod '{oldKey}' -> {string.Join(", ", finalKeys)}");
        return finalKeys;
    }

    private static string StripDisabledPrefix(string key)
    {
        string norm = NormalizeStaticRel(key);
        return norm.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase)
            ? norm.Substring("Disabled/".Length)
            : norm;
    }

    private void TryDeletePhysicalModFile(string listKey)
    {
        if (string.IsNullOrWhiteSpace(listKey)) return;

        string cleanF = listKey.Replace("Disabled/", "").Replace("Bundles/", "").Replace("Strings/", "");
        var possiblePaths = new List<string>
        {
            Path.Combine(GetActiveDataPath(), cleanF),
            Path.Combine(AppPaths.DisabledModsPath, cleanF),
            Path.Combine(AppPaths.BundlesPath, cleanF),
            Path.Combine(GetActiveDataPath(), listKey.Replace("/", "\\")),
            GetFullPath(listKey)
        };

        string injBn = Path.GetFileName(listKey.Replace("\\", "/"));
        if (IsGameRootInjectorName(injBn))
        {
            if (!string.IsNullOrEmpty(AppPaths.GameInstallRoot))
                possiblePaths.Add(Path.Combine(AppPaths.GameInstallRoot, injBn));
            possiblePaths.Add(Path.Combine(AppPaths.ManagedStagingGameRootPath, injBn));
        }

        foreach (var path in possiblePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                File.Delete(path);
                _logger($"[NEXUS] Removed superseded mod file: {path}");
            }
            catch (Exception ex)
            {
                _logger($"[ERROR] Failed to remove superseded file {path}: {ex.Message}");
            }
        }
    }

    public sealed class NexusRemoveFlags
    {
        public bool Url { get; set; }
        public bool ModId { get; set; }
        public bool FileId { get; set; }
        public bool Version { get; set; }
        public bool Uploaded { get; set; }

        public bool Any => Url || ModId || FileId || Version || Uploaded;
    }

    public bool UpdateNexusLinkage(
        string originalName,
        long? nexusModId,
        long? nexusFileId,
        string? nexusFileVersion,
        long? nexusFileUploaded,
        string? url = null,
        bool clearExisting = false,
        NexusRemoveFlags? remove = null)
    {
        var metadata = LoadMetadata();
        string key = ResolveMetadataKey(metadata, NormalizeMetadataKey(originalName));
        if (string.IsNullOrEmpty(key)) key = NormalizeMetadataKey(originalName);
        if (string.IsNullOrEmpty(key)) return false;

        if (!metadata.TryGetValue(key, out var meta) || meta == null)
        {
            meta = new ModMetadata
            {
                Name = Path.GetFileNameWithoutExtension(key),
                Files = new List<string> { key }
            };
            metadata[key] = meta;
        }

        remove ??= new NexusRemoveFlags();

        if (clearExisting)
        {
            meta.NexusModId = null;
            meta.NexusFileId = null;
            meta.NexusFileUploaded = null;
            meta.URL = "";
        }
        else
        {
            if (remove.Url) meta.URL = "";
            if (remove.ModId) meta.NexusModId = null;
            if (remove.FileId) meta.NexusFileId = null;
            if (remove.Uploaded) meta.NexusFileUploaded = null;
            if (remove.Version) meta.NexusFileVersion = "";
        }

        if (nexusModId.HasValue) meta.NexusModId = nexusModId;
        if (nexusFileId.HasValue) meta.NexusFileId = nexusFileId;
        if (!string.IsNullOrWhiteSpace(nexusFileVersion))
        {
            meta.NexusFileVersion = nexusFileVersion.Trim();
            meta.Version = meta.NexusFileVersion;
        }
        if (nexusFileUploaded.HasValue) meta.NexusFileUploaded = nexusFileUploaded;
        if (!string.IsNullOrWhiteSpace(url)) meta.URL = url.Trim();
        else if (nexusModId.HasValue && string.IsNullOrWhiteSpace(meta.URL) && !remove.Url)
            meta.URL = $"https://www.nexusmods.com/fallout76/mods/{nexusModId.Value}";

        SaveMetadata(metadata);
        return true;
    }

    private bool IsBundleModKey(Dictionary<string, ModMetadata> metadata, string modKey)
    {
        if (string.IsNullOrWhiteSpace(modKey)) return false;
        string normalized = NormalizeMetadataKey(modKey);
        if (normalized.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase))
            return true;

        string fileOnly = Path.GetFileName(normalized.Replace("Disabled/", ""));
        if (!string.IsNullOrWhiteSpace(fileOnly) &&
            metadata.TryGetValue($"Bundles/{fileOnly}", out var bundleMeta) &&
            bundleMeta.IsBundle)
        {
            return true;
        }

        string resolvedKey = ResolveMetadataKey(metadata, normalized);
        return !string.IsNullOrEmpty(resolvedKey) &&
               metadata.TryGetValue(resolvedKey, out var resolvedMeta) &&
               resolvedMeta.IsBundle;
    }

    private string? FindBundleFileOnDisk(string fileName, string? dataPath = null)
    {
        foreach (string path in FindAllBundleFileLocations(fileName, dataPath))
            return path;
        return null;
    }

    private List<string> FindAllBundleFileLocations(string fileName, string? dataPath = null)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(fileName)) return found;

        dataPath ??= GetActiveDataPath();
        var searchDirs = new List<string>();
        if (!string.IsNullOrEmpty(AppPaths.BundlesPath) && Directory.Exists(AppPaths.BundlesPath))
            searchDirs.Add(AppPaths.BundlesPath);
        if (!string.IsNullOrEmpty(AppPaths.DisabledModsPath) && Directory.Exists(AppPaths.DisabledModsPath))
            searchDirs.Add(AppPaths.DisabledModsPath);
        if (!string.IsNullOrWhiteSpace(dataPath) && Directory.Exists(dataPath))
            searchDirs.Add(dataPath);

        foreach (string dir in searchDirs)
        {
            string candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                found.Add(candidate);
        }

        return found;
    }

    private void RemoveLeftoverBundleCopies(string fileName, string keepPath, string dataPath)
    {
        foreach (string dir in new[] { AppPaths.BundlesPath, AppPaths.DisabledModsPath })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            string extra = Path.Combine(dir, fileName);
            if (File.Exists(extra) && !string.Equals(extra, keepPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(extra);
                _logger($"[BUNDLE] Removed leftover bundle copy: '{extra}'");
            }
        }
    }

    public List<string> RestoreParkedLooseConfigs()
    {
        var restored = new List<string>();
        if (string.IsNullOrWhiteSpace(AppPaths.DisabledModsPath) || !Directory.Exists(AppPaths.DisabledModsPath))
            return restored;

        foreach (var disabledKey in EnumerateDisabledLooseConfigListKeys().ToList())
        {
            string rel = disabledKey;
            if (rel.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring("Disabled/".Length);
            if (string.IsNullOrWhiteSpace(rel) || !ConfigFileMerger.IsRootLooseConfigPath(rel))
                continue;
            if (ShouldSkipParkedLooseConfigRestore(rel))
                continue;

            if (!ToggleModEnabled(disabledKey, true, out string? err))
            {
                if (!TryMoveModBetweenActiveAndDisabled(rel, true, out string moveErr))
                {
                    _logger($"[MIGRATION] Failed to restore parked config '{rel}': {err ?? moveErr}");
                    continue;
                }

                var metadata = LoadMetadata();
                MigrateDataModMetadataKey(metadata, NormalizeMetadataKey(rel), true);
                string key = NormalizeMetadataKey(rel);
                if (!metadata.TryGetValue(key, out var meta) || meta == null)
                {
                    metadata[key] = new ModMetadata
                    {
                        Name = Path.GetFileNameWithoutExtension(rel),
                        IsEnabled = true,
                        Files = new List<string> { key }
                    };
                }
                else
                {
                    meta.IsEnabled = true;
                    meta.Files = new List<string> { key };
                }
                metadata.Remove($"Disabled/{key}");
                metadata.Remove($"Disabled/{rel.Replace("\\", "/")}");
                SaveMetadata(metadata);
            }

            restored.Add(NormalizeMetadataKey(rel));
            _logger($"[MIGRATION] Restored parked loose config to Data: {rel}");
        }

        if (restored.Count > 0)
            InvalidateModsListCache();
        return restored;
    }

    private static bool ShouldSkipParkedLooseConfigRestore(string relativePath)
    {
        string name = Path.GetFileName(relativePath) ?? "";
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.StartsWith("OPTIONAL-", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("_HowTo", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("SeventySix_", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool TryMoveModBetweenActiveAndDisabled(string relativePath, bool enable, out string error)
    {
        error = string.Empty;
        try
        {
            string rel = (relativePath ?? "").Replace("/", "\\").TrimStart('\\');
            if (string.IsNullOrWhiteSpace(rel))
            {
                error = "Mod file path is empty.";
                return false;
            }

            string dataFilePath = Path.Combine(GetActiveDataPath(), rel);
            string disabledFilePath = Path.Combine(AppPaths.DisabledModsPath, rel);

            if (!Directory.Exists(AppPaths.DisabledModsPath))
            {
                Directory.CreateDirectory(AppPaths.DisabledModsPath);
                _logger($"[MODS] Created Disabled Mods folder: {AppPaths.DisabledModsPath}");
            }

            bool dataExists = File.Exists(dataFilePath);
            bool disabledExists = File.Exists(disabledFilePath);
            if (!dataExists && !disabledExists)
            {
                error = "Mod file not found on this platform.";
                return false;
            }

            if (enable && disabledExists)
            {
                string? destDir = Path.GetDirectoryName(dataFilePath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);
                File.Move(disabledFilePath, dataFilePath, true);
                _logger($"[MODS] Enabled mod: Moved '{rel}' from Disabled Mods to Data folder");
            }
            else if (!enable && dataExists)
            {
                string? destDir = Path.GetDirectoryName(disabledFilePath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);
                File.Move(dataFilePath, disabledFilePath, true);
                _logger($"[MODS] Disabled mod: Moved '{rel}' to Disabled Mods folder");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

