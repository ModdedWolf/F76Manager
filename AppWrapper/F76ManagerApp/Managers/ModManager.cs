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

    /// <summary>True when the most recent import already surfaced a specific problem to the user
    /// (missing extractor, failed extraction, archive contained no mods). Lets callers suppress the
    /// generic "no valid mod files" banner so the actionable message isn't masked.</summary>
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

    public int EnsureManagedStagingHydrated()
    {
        if (!VirtualModMode) return 0;

        EnsureActiveDataFolders();
        LogLegacyManagedStagingPresence();

        if (!IsManagedStagingEmpty()) return 0;

        int copied = 0;
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
                    string ext = Path.GetExtension(fileName).ToLowerInvariant();
                    bool isStringFile = ext == ".strings" || ext == ".dlstrings" || ext == ".ilstrings";
                    if (!isStringFile) continue;

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

    /// <summary>Exposes the internal vanilla-file check so callers (e.g. cross-platform INI merge) can
    /// avoid copying base-game archive entries.</summary>
    public bool IsVanillaModFile(string relativePath) => IsVanillaFile(relativePath);

    /// <summary>
    /// Copies all non-vanilla mod content from one platform's active location to another's:
    /// top-level mod files, mod asset subfolders (e.g. UniMap_MapBooks / UniMap_Custom), string files,
    /// and game-root injector DLLs. Vanilla game files and the Strings folder are handled specially.
    /// Returns the number of items copied. Throws UnauthorizedAccessException so the caller can surface
    /// an elevation-specific message (Xbox installs are protected).
    /// </summary>
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

            // 1. Top-level mod files (skip vanilla base-game files).
            foreach (var file in Directory.GetFiles(sourceDataDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                if (IsVanillaFile(fileName)) continue;
                string dest = Path.Combine(destDataDir, fileName);
                if (File.Exists(dest) && !overwrite) continue;
                CopyFileOrThrow(file, dest, true);
                copied++;
            }

            // 2. Mod asset subfolders (e.g. UniMap_MapBooks, UniMap_Custom). "Strings" is handled below.
            foreach (var dir in Directory.GetDirectories(sourceDataDir))
            {
                string folderName = Path.GetFileName(dir);
                if (folderName.Equals("Strings", StringComparison.OrdinalIgnoreCase)) continue;
                copied += CopyDirectoryRecursive(dir, Path.Combine(destDataDir, folderName), overwrite);
            }

            // 3. String files.
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

            // 4. Game-root injector DLLs (dxgi.dll / d3d11.dll).
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
        _logger($"[DEBUG] ModManager initialized.");
        
        if (!string.IsNullOrEmpty(AppPaths.BundlesPath) && !Directory.Exists(AppPaths.BundlesPath)) {
            Directory.CreateDirectory(AppPaths.BundlesPath);
            _logger($"[DEBUG] Created Bundles folder.");
        }
        
        if (!string.IsNullOrEmpty(AppPaths.DisabledModsPath) && !Directory.Exists(AppPaths.DisabledModsPath)) {
            Directory.CreateDirectory(AppPaths.DisabledModsPath);
            _logger($"[DEBUG] Created Disabled Mods folder.");
        }

        EnsureActiveDataFolders();
        EnsureManagedStagingHydrated();
    }

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
            public int LoadOrder { get; set; } = 0;
            public List<string> Files { get; set; } = new List<string>();
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
            candidates.Add(fileName);
        }
        else
        {
            candidates.Add(fileName);
            candidates.Add($"Bundles/{fileName}");
            candidates.Add($"Disabled/{fileName}");
            candidates.Add($"Strings/{fileName}");
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
        bool isBundle = IsBundleModKey(allMeta, originalName) || IsBundleModKey(allMeta, targetKey);
        bool isStrings = originalName.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase);
        bool isGameRootInjector = IsGameRootInjectorListPath(NormalizeMetadataKey(originalName)) ||
                                  IsGameRootInjectorListPath(NormalizeMetadataKey(targetKey));
        
        if (!isBundle && !isStrings && isGameRootInjector && wasEnabled != meta.IsEnabled)
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
        else if (!isBundle && !isStrings && !isGameRootInjector && wasEnabled != meta.IsEnabled)
        {
            string fileNameOnly = Path.GetFileName(originalName);
            if (!TryMoveModBetweenActiveAndDisabled(fileNameOnly, meta.IsEnabled, out var moveError))
            {
                _logger($"[ERROR] Failed to move mod file during metadata update: {moveError}");
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
        bool isBundle = !isGameRootInj && IsBundleModKey(metadata, cleanFileName);
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
        else if (!isBundle && !isStrings && !isInBundlesFolder)
        {
            string fileNameOnly = Path.GetFileName(cleanFileName);
            if (!TryMoveModBetweenActiveAndDisabled(fileNameOnly, enabled, out moveError))
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
        } else {
            errorMessage = moveError;
            _logger($"[ERROR] Toggle mod failed: {moveError}");
        }

        return moveSuccess;
    }

    public List<object> GetModsList()
    {
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
                        f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".strings", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".dlstrings", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".ilstrings", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();

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
        if (Directory.Exists(stringsDir))
        {
            var sFiles = Directory.GetFiles(stringsDir, "*.*")
                .Where(f => f.EndsWith(".strings", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".dlstrings", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".ilstrings", StringComparison.OrdinalIgnoreCase))
                .Select(f => $"Strings/{Path.GetFileName(f)}");
            files.AddRange(sFiles);
            _logger($"[DEBUG] Found {sFiles.Count()} string files in {stringsDir}");
        }
        else 
        {
             _logger($"[DEBUG] Strings directory not found: {stringsDir}");
        }

        string bundlesDir = AppPaths.BundlesPath;
        if (!Directory.Exists(bundlesDir)) Directory.CreateDirectory(bundlesDir);

        var bFiles = Directory.GetFiles(bundlesDir, "*.ba2")
            .Select(f => $"Bundles/{Path.GetFileName(f)}");
        files.AddRange(bFiles);
        _logger($"[DEBUG] Found {bFiles.Count()} master bundle files in {bundlesDir}");

        if (Directory.Exists(AppPaths.DisabledModsPath))
        {
            var dFiles = Directory.GetFiles(AppPaths.DisabledModsPath, "*.*")
                .Where(f => f.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Select(f => $"Disabled/{Path.GetFileName(f)}");
            files.AddRange(dFiles);
            _logger($"[DEBUG] Found {dFiles.Count()} disabled mod files in {AppPaths.DisabledModsPath}");
        }

        files.AddRange(DiscoverGameRootInjectorListPaths());

        var discoveredKeys = new HashSet<string>(
            files.Select(NormalizeMetadataKey).Where(k => !string.IsNullOrWhiteSpace(k)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in files) 
        {
            string normalizedFile = NormalizeMetadataKey(file);
            string fileNameOnly = Path.GetFileName(normalizedFile);
            
            if (IsVanillaFile(fileNameOnly)) continue;

            bool isConfigLike =
                normalizedFile.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                normalizedFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                normalizedFile.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            bool isSpecialPath =
                normalizedFile.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase) ||
                normalizedFile.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase) ||
                normalizedFile.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase) ||
                normalizedFile.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase) ||
                normalizedFile.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase);

            if (isConfigLike && !isSpecialPath)
            {
                string siblingKey = normalizedFile.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase)
                    ? NormalizeMetadataKey(Path.GetFileName(normalizedFile))
                    : $"Disabled/{NormalizeMetadataKey(Path.GetFileName(normalizedFile))}";

                if (!metadata.ContainsKey(normalizedFile) &&
                    !string.IsNullOrWhiteSpace(siblingKey) &&
                    metadata.ContainsKey(siblingKey) &&
                    !discoveredKeys.Contains(siblingKey))
                {
                    metadata[normalizedFile] = metadata[siblingKey];
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

            if (string.IsNullOrWhiteSpace(meta.Details))
            {
                var siblingKeys = new List<string> {
                    NormalizeMetadataKey(Path.GetFileName(normalizedFile)),
                    $"Disabled/{Path.GetFileName(normalizedFile)}",
                    $"Bundles/{Path.GetFileName(normalizedFile)}",
                    $"Strings/{Path.GetFileName(normalizedFile)}"
                };
                if (IsGameRootInjectorName(fileNameOnly))
                {
                    siblingKeys.Add($"GameRoot/{fileNameOnly}");
                    siblingKeys.Add($"Disabled/GameRoot/{fileNameOnly}");
                }
                foreach (var sibling in siblingKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!metadata.TryGetValue(sibling, out var siblingMeta) || siblingMeta == null) continue;
                    if (!string.IsNullOrWhiteSpace(siblingMeta.Details))
                    {
                        meta.Details = siblingMeta.Details;
                        break;
                    }
                }
            }


            if (!string.IsNullOrEmpty(resolvedKey)) {
                string displayName = (!string.IsNullOrWhiteSpace(meta.Name) && meta.Name.Trim() != "Unknown Mod") ? meta.Name.Trim() : fileNameOnly;
                _logger($"[DEBUG] Recognized mod: {displayName}");
            }
            
            bool isActuallyEnabled;
            bool isBundle = meta.IsBundle || normalizedFile.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase);
            
            if (isBundle) {
                isActuallyEnabled = meta.IsEnabled;
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
                type = Path.GetExtension(normalizedFile).ToLower().Replace(".", ""),
                isBundle = isBundle,
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

        return mods;
    }

    private static IEnumerable<string> DiscoverCoreIniListPaths()
    {
        var results = new List<string>();
        try
        {
            string docs = AppPaths.DocumentsPath;
            if (string.IsNullOrWhiteSpace(docs)) return results;

            string custom = AppPaths.CustomIniPath;
            if (!string.IsNullOrWhiteSpace(custom))
                results.Add($"CoreIni/{Path.GetFileName(custom)}");

            string prefs = AppPaths.PrefsIniPath;
            if (!string.IsNullOrWhiteSpace(prefs))
                results.Add($"CoreIni/{Path.GetFileName(prefs)}");
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
        _logger($"[DEBUG] GetEnabledModPaths: Metadata count: {metadata.Count}");
        foreach (var kvp in metadata)
        {
            if (kvp.Value.IsEnabled)
            {
                _logger($"[DEBUG] Mod Enabled: '{kvp.Key}' (Title: {kvp.Value.Name})");
                if (kvp.Value.Files != null && kvp.Value.Files.Count > 0)
                {
                    foreach(var f in kvp.Value.Files) {
                        string full = GetFullPath(f);
                        paths.Add(full);
                        _logger($"[DEBUG]   -> Adding File: {full} (Exists: {File.Exists(full)})");
                    }
                }
                else
                {
                    string full = GetFullPath(kvp.Key);
                    paths.Add(full);
                    _logger($"[DEBUG]   -> Adding Key Path: {full} (Exists: {File.Exists(full)})");
                }
            }
        }
        var result = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _logger($"[DEBUG] GetEnabledModPaths: Returning {result.Count} unique existent paths.");
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
        // Explicit user-configured path always wins.
        if (!string.IsNullOrWhiteSpace(RarExtractorPath) && File.Exists(RarExtractorPath.Trim()))
            return Path.GetFullPath(RarExtractorPath.Trim());

        string? rar = AutoDetectRarExtractorExecutable();

        // UnRAR.exe / Rar.exe are reliable headless CLI extractors — use them directly.
        bool rarIsGuiOnly = rar != null &&
            Path.GetFileName(rar).Equals("WinRAR.exe", StringComparison.OrdinalIgnoreCase);
        if (rar != null && !rarIsGuiOnly)
        {
            _logger($"[IMPORT] Auto-detected WinRAR/RAR CLI: {rar}");
            return rar;
        }

        // 7-Zip can also extract .rar (incl. RAR5). Prefer it over the WinRAR GUI, which is
        // unreliable when launched headless with redirected output.
        string? seven = ResolveSevenZipExecutable();
        if (seven != null)
        {
            _logger($"[IMPORT] Using 7-Zip to extract .rar: {seven}");
            return seven;
        }

        // Last resort: the WinRAR GUI executable.
        if (rar != null)
        {
            _logger($"[IMPORT] No CLI extractor found; falling back to WinRAR GUI for .rar: {rar}");
            return rar;
        }

        return null;
    }

    /// <summary>Runs 7-Zip-style or WinRAR-family CLI extraction. Returns exit code (0 = success for both tools).
    /// Captures the tool's combined stdout/stderr in <paramref name="output"/> for diagnostics.</summary>
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

        // Drain both pipes concurrently BEFORE WaitForExit to avoid a deadlock when a tool
        // fills its output buffer (classic ProcessStartInfo redirect pitfall).
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

    private void CollectExtractedModFilesAndImport(
        string tempDir,
        string displayArchiveName,
        Dictionary<string, ModMetadata> metadata,
        List<string> importedKeys,
        ImportProgressContext progress)
    {
        var extractedFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                string e = Path.GetExtension(f).ToLowerInvariant();
                return e == ".ba2" || e == ".esm" || e == ".esp" || e == ".strings" || e == ".dlstrings" ||
                       e == ".ilstrings" || e == ".ini" || e == ".json" || e == ".txt" ||
                       (e == ".dll" && IsGameRootInjectorName(Path.GetFileName(f)));
            }).ToList();
        progress.AddUnits(Math.Max(extractedFiles.Count - 1, 0));

        if (extractedFiles.Any())
        {
            ImportFilesInternal(extractedFiles, metadata, importedKeys, progress);
        }
        else
        {
            _logger($"[IMPORT] No valid mod files found in {displayArchiveName} after extraction.");
            _statusReporter("warning", $"'{displayArchiveName}' extracted but contained no mod files (BA2/ESM/ESP/strings/INI). It may be a FOMOD/installer archive that needs manual placement.");
            LastImportReportedIssue = true;
        }
    }

    public List<string> ImportMod()
    {
        using (var ofd = new OpenFileDialog
               {
                   Multiselect = true,
                   Filter =
                       "Mod files (*.ba2;*.esm;*.esp;*.zip;*.7z;*.rar;*.strings;*.ini;*.json;*.txt;*.dll)|*.ba2;*.esm;*.esp;*.zip;*.7z;*.rar;*.strings;*.dlstrings;*.ilstrings;*.ini;*.json;*.txt;*.dll"
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
                    var searchExtensions = new[]
                    {
                        ".ba2", ".esm", ".esp", ".strings", ".dlstrings", ".ilstrings", ".ini", ".json", ".txt", ".zip", ".7z", ".rar"
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
                
                if (ext == ".zip")
                {
                    _logger($"[IMPORT] Extracting ZIP: {fileName}");
                    string tempDir = Path.Combine(Path.GetTempPath(), "F76M_Import_" + Guid.NewGuid().ToString().Substring(0, 8));
                    Directory.CreateDirectory(tempDir);
                    try {
                        System.IO.Compression.ZipFile.ExtractToDirectory(file, tempDir);
                        var extractedFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories)
                            .Where(f => {
                                string e = Path.GetExtension(f).ToLowerInvariant();
                                return e == ".ba2" || e == ".esm" || e == ".esp" || e == ".strings" || e == ".dlstrings" || e == ".ilstrings" ||
                                       e == ".ini" || e == ".json" || e == ".txt" ||
                                       (e == ".dll" && IsGameRootInjectorName(Path.GetFileName(f)));
                            }).ToList();
                        progress.AddUnits(Math.Max(extractedFiles.Count - 1, 0));
                        
                        if (extractedFiles.Any()) {
                            ImportFilesInternal(extractedFiles, metadata, importedKeys, progress);
                        }
                    } finally {
                        try { Directory.Delete(tempDir, true); } catch (Exception cleanupEx) { _logger($"[IMPORT] Failed to remove temp directory '{tempDir}': {cleanupEx.Message}"); }
                    }
                    progress.CompleteOne("processing", fileName);
                    continue; 
                }
                
                if (ext == ".7z")
                {
                    string? toolExe = ResolveSevenZipExecutable();
                    if (string.IsNullOrEmpty(toolExe))
                    {
                        _statusReporter("error",
                            "7-Zip not found. Install 7-Zip or set the path under Settings → Installation Paths (it is usually auto-detected).");
                        _logger("[ERROR] 7-Zip executable not found (settings + default locations).");
                        LastImportReportedIssue = true;
                        progress.CompleteOne("processing", fileName);
                        continue;
                    }

                    _logger($"[IMPORT] Extracting 7z archive: {fileName} using {toolExe}");
                    string tempDir = Path.Combine(Path.GetTempPath(), "F76M_Import_" + Guid.NewGuid().ToString().Substring(0, 8));
                    Directory.CreateDirectory(tempDir);
                    string tempInputFile = Path.Combine(tempDir, "input_archive" + ext);
                    File.Copy(file, tempInputFile, true);
                    try
                    {
                        int exitCode = RunArchiveExtractor(toolExe, tempInputFile, tempDir, out string toolOutput);
                        if (exitCode != 0)
                        {
                            _logger($"[ERROR] 7z extraction exit code {exitCode} for {fileName}. Tool output: {toolOutput}");
                            _statusReporter("error", $"7-Zip extraction failed (code {exitCode}). Check the archive and your 7-Zip path in Settings.");
                            LastImportReportedIssue = true;
                        }
                        else
                        {
                            CollectExtractedModFilesAndImport(tempDir, fileName, metadata, importedKeys, progress);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger($"[ERROR] 7z extraction failed: {ex.Message}");
                        _statusReporter("error", $"Archive extraction failed: {ex.Message}");
                        LastImportReportedIssue = true;
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch (Exception cleanupEx) { _logger($"[IMPORT] Failed to remove temp directory '{tempDir}': {cleanupEx.Message}"); }
                    }
                    progress.CompleteOne("processing", fileName);
                    continue;
                }

                if (ext == ".rar")
                {
                    string? toolExe = ResolveRarExtractorExecutable();
                    if (string.IsNullOrEmpty(toolExe))
                    {
                        _statusReporter("error",
                            "Can't extract this .rar: no archive tool found. Install 7-Zip or WinRAR, then re-import (or set the path under Settings → Installation Paths). 7-Zip is the easiest option and also handles .rar.");
                        _logger("[ERROR] No .rar extractor found (settings, WinRAR/UnRAR, and 7-Zip all unavailable).");
                        LastImportReportedIssue = true;
                        progress.CompleteOne("processing", fileName);
                        continue;
                    }

                    _logger($"[IMPORT] Extracting RAR archive: {fileName} using {toolExe}");
                    string tempDir = Path.Combine(Path.GetTempPath(), "F76M_Import_" + Guid.NewGuid().ToString().Substring(0, 8));
                    Directory.CreateDirectory(tempDir);
                    string tempInputFile = Path.Combine(tempDir, "input_archive" + ext);
                    File.Copy(file, tempInputFile, true);
                    try
                    {
                        int exitCode = RunArchiveExtractor(toolExe, tempInputFile, tempDir, out string toolOutput);
                        if (exitCode != 0)
                        {
                            _logger($"[ERROR] RAR extraction exit code {exitCode} for {fileName} using {Path.GetFileName(toolExe)}. Tool output: {toolOutput}");
                            _statusReporter("error", $"RAR extraction failed (code {exitCode}). Check the archive and your RAR/7-Zip tool path in Settings.");
                            LastImportReportedIssue = true;
                        }
                        else
                        {
                            CollectExtractedModFilesAndImport(tempDir, fileName, metadata, importedKeys, progress);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger($"[ERROR] RAR extraction failed: {ex.Message}");
                        _statusReporter("error", $"Archive extraction failed: {ex.Message}");
                        LastImportReportedIssue = true;
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

                    if (!Directory.Exists(AppPaths.DisabledModsPath))
                        Directory.CreateDirectory(AppPaths.DisabledModsPath);
                    string injDest = Path.Combine(AppPaths.DisabledModsPath, fileName);
                    string normalizedInjSource = Path.GetFullPath(file);
                    string normalizedInjDest = Path.GetFullPath(injDest);
                    if (!string.Equals(normalizedInjSource, normalizedInjDest, StringComparison.OrdinalIgnoreCase))
                        File.Copy(file, injDest, true);
                    else
                        _logger($"[IMPORT] Injector DLL already in Disabled Mods: {fileName}");

                    string injRelative = $"Disabled/GameRoot/{fileName}";
                    importedKeys.Add(injRelative);
                    if (!metadata.ContainsKey(injRelative))
                    {
                        metadata[injRelative] = new ModMetadata
                        {
                            Name = Path.GetFileNameWithoutExtension(fileName),
                            Files = new List<string> { injRelative },
                            IsEnabled = false
                        };
                    }
                    else
                        metadata[injRelative].IsEnabled = false;
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
                            f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName));
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

    public void RenameMod(string currentName, string newName, string details = "")
    {
        try 
        {
            string normCurrent = NormalizeMetadataKey(currentName);
            if (IsGameRootInjectorListPath(normCurrent))
            {
                _statusReporter("error", "Cannot rename game injector DLLs (dxgi.dll / d3d11.dll).");
                return;
            }

            var metadata = LoadMetadata();
            string currentPath = GetFullPath(currentName);
            
            string dir = Path.GetDirectoryName(currentName) ?? "";
            string baseNewName = newName;
            if (!Path.HasExtension(baseNewName))
            {
                string ext = Path.GetExtension(currentName);
                if (!string.IsNullOrEmpty(ext)) baseNewName += ext;
            }

            string newRelativePath = string.IsNullOrEmpty(dir) ? baseNewName : Path.Combine(dir, baseNewName).Replace("\\", "/");
            string newPath = GetFullPath(newRelativePath);
            newName = newRelativePath;

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

            if (metadata.ContainsKey(currentName))
            {
                var meta = metadata[currentName];
                metadata.Remove(currentName);
                
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
                    IsBundle = currentName.EndsWith(".ba2")
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

        bool isMarkedBundle = false;
        if (metadata.ContainsKey(fileName) && metadata[fileName].IsBundle) isMarkedBundle = true;
        
        string alternateName = fileName.EndsWith(".ba2") ? fileName : fileName + ".ba2";
        if (!isMarkedBundle && metadata.ContainsKey(alternateName) && metadata[alternateName].IsBundle) {
            isMarkedBundle = true; 
            fileName = alternateName; 
        }

        List<string> filesToDelete = new List<string>();
        if (metadata.ContainsKey(fileName) && metadata[fileName].Files != null && metadata[fileName].Files.Count > 0)
        {
            filesToDelete.AddRange(metadata[fileName].Files);
        }
        else
        {
            filesToDelete.Add(fileName);
        }

        bool deletedAny = false;
        foreach (var f in filesToDelete)
        {
            string cleanF = f.Replace("Disabled/", "").Replace("Bundles/", "").Replace("Strings/", "");
            
            var possiblePaths = new List<string> {
                Path.Combine(GetActiveDataPath(), cleanF),
                Path.Combine(AppPaths.DisabledModsPath, cleanF),
                Path.Combine(AppPaths.BundlesPath, cleanF),
                Path.Combine(GetActiveDataPath(), f.Replace("/", "\\")),
                GetFullPath(f)
            };
            string injBn = Path.GetFileName(f.Replace("\\", "/"));
            if (IsGameRootInjectorName(injBn))
            {
                if (!string.IsNullOrEmpty(AppPaths.GameInstallRoot))
                    possiblePaths.Add(Path.Combine(AppPaths.GameInstallRoot, injBn));
                possiblePaths.Add(Path.Combine(AppPaths.ManagedStagingGameRootPath, injBn));
            }

            foreach (var path in possiblePaths)
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

        if (metadata.ContainsKey(fileName)) {
            metadata.Remove(fileName);
            SaveMetadata(metadata);
            _logger($"[DELETE] Metadata record for '{fileName}' removed.");
        }

        if (deletedAny) {
            _statusReporter("success", $"Deleted mod: {Path.GetFileName(fileName)}");
        } else {
             _logger($"[DELETE] Finished, but no physical files were deleted for '{fileName}' (maybe already gone).");
        }
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

    private string GetFullPath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return "";
        string normalized = relativePath.Replace("\\", "/");
        if (normalized.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(fn)) return "";
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

        foreach (var rawKey in importedKeys ?? Array.Empty<string>())
        {
            string key = ResolveMetadataKey(metadata, NormalizeMetadataKey(rawKey));
            if (string.IsNullOrEmpty(key)) key = NormalizeMetadataKey(rawKey);
            if (string.IsNullOrEmpty(key)) continue;

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
                if (string.IsNullOrWhiteSpace(meta.Name) || meta.Name.Equals("Unknown Mod", StringComparison.OrdinalIgnoreCase) ||
                    meta.Name.Equals(Path.GetFileNameWithoutExtension(key), StringComparison.OrdinalIgnoreCase))
                {
                    meta.Name = displayName;
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

        foreach (var tracked in oldTracked)
        {
            if (newKeySet.Contains(tracked)) continue;
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

            if (oldMeta != null)
            {
                if (!string.IsNullOrWhiteSpace(oldMeta.Details)) meta.Details = oldMeta.Details;
                if (oldMeta.LoadOrder != 0) meta.LoadOrder = oldMeta.LoadOrder;
                if (!string.IsNullOrWhiteSpace(oldMeta.Name) && !oldMeta.Name.Equals("Unknown Mod", StringComparison.OrdinalIgnoreCase))
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

        if (preserveDisabled)
        {
            foreach (var nk in newKeys)
            {
                string fileOnly = Path.GetFileName(nk.Replace("Disabled/", ""));
                if (!string.IsNullOrWhiteSpace(fileOnly))
                    ToggleModEnabled(fileOnly, false, out _);
            }
            metadata = LoadMetadata();
        }

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

    private bool TryMoveModBetweenActiveAndDisabled(string fileNameOnly, bool enable, out string error)
    {
        error = string.Empty;
        try
        {
            string dataFilePath = Path.Combine(GetActiveDataPath(), fileNameOnly);
            string disabledFilePath = Path.Combine(AppPaths.DisabledModsPath, fileNameOnly);

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
                File.Move(disabledFilePath, dataFilePath, true);
                _logger($"[MODS] Enabled mod: Moved '{fileNameOnly}' from Disabled Mods to Data folder");
            }
            else if (!enable && dataExists)
            {
                File.Move(dataFilePath, disabledFilePath, true);
                _logger($"[MODS] Disabled mod: Moved '{fileNameOnly}' to Disabled Mods folder");
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

