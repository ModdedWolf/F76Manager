using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace F76ManagerApp.Managers
{
    public class Ba2PartialSelection
    {
        public string Archive { get; set; } = "";
        public List<string> Paths { get; set; } = new List<string>();
    }

    public class FolderPartialSelection
    {
        public string Folder { get; set; } = "";
        public List<string> Paths { get; set; } = new List<string>();
    }

    public class BundleManager
    {
        private Action<string, string> _statusCallback;
        private Action<string> _logger;
        private Action<string> _errorLogger;
        private Action<string, F76ManagerApp.Managers.ModManager.ModMetadata> _metadataSaver;
        private Action<List<string>, bool> _toggleCallback;

        public BundleManager(Action<string> logger, Action<string, string> statusCallback, Action<string, F76ManagerApp.Managers.ModManager.ModMetadata> metadataSaver, Action<List<string>, bool> toggleCallback, Action<string> errorLogger = null)
        {
            _logger = logger;
            _statusCallback = statusCallback;
            _metadataSaver = metadataSaver;
            _toggleCallback = toggleCallback;
            _errorLogger = errorLogger ?? logger;
        }

        public void CreateBundle(string bundleName, List<string> modFiles, string compressionLevel = "Default", Action onComplete = null)
        {
            CreateBundle(bundleName, modFiles, null, compressionLevel, "Auto", onComplete);
        }

        public void CreateBundle(string bundleName, List<string> modFiles, string compressionLevel, string format, Action onComplete)
        {
            CreateBundle(bundleName, modFiles, null, compressionLevel, format, onComplete);
        }

        public void CreateBundle(string bundleName, List<string> modFiles, List<string>? sourceFolders, string compressionLevel, string format, Action onComplete)
        {
            CreateBundle(bundleName, modFiles, sourceFolders, null, compressionLevel, format, onComplete);
        }

        public void CreateBundle(string bundleName, List<string> modFiles, List<string>? sourceFolders, List<Ba2PartialSelection>? partialExtractions, string compressionLevel, string format, Action onComplete)
        {
            CreateBundle(bundleName, modFiles, sourceFolders, partialExtractions, null, compressionLevel, format, onComplete);
        }

        public void CreateBundle(string bundleName, List<string> modFiles, List<string>? sourceFolders, List<Ba2PartialSelection>? partialExtractions, List<FolderPartialSelection>? folderPartialExtractions, string compressionLevel, string format, Action onComplete)
        {
            var folders = sourceFolders ?? new List<string>();
            var partials = partialExtractions ?? new List<Ba2PartialSelection>();
            var folderPartials = folderPartialExtractions ?? new List<FolderPartialSelection>();
            Task.Run(() =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "F76M_Bundle_" + Guid.NewGuid().ToString("N"));
                try
                {
                    var partialByArchive = new Dictionary<string, Ba2PartialSelection>(StringComparer.OrdinalIgnoreCase);
                    foreach (var partial in partials)
                    {
                        if (string.IsNullOrWhiteSpace(partial.Archive) || partial.Paths == null || partial.Paths.Count == 0)
                            continue;
                        partialByArchive[ResolveArchivePath(partial.Archive)] = partial;
                    }

                    int totalSources = modFiles.Count + folders.Count + folderPartials.Count + partialByArchive.Count;
                    _statusCallback?.Invoke("info", $"Bundling {totalSources} source(s) into {bundleName} (Compression: {compressionLevel}, Format: {format})...");
                    _logger($"[BUNDLE] Starting creation of '{bundleName}' in {tempDir} with compression '{compressionLevel}' and format '{format}' ({modFiles.Count} full BA2, {partialByArchive.Count} partial BA2, {folders.Count} folder(s), {folderPartials.Count} partial folder(s))");

                    if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

                    bool anyContent = false;

                    foreach (var folder in folders)
                    {
                        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                        {
                            _logger($"[BUNDLE] Warning: Folder not found: {folder}");
                            continue;
                        }
                        _logger($"[BUNDLE] Copying folder {folder}...");
                        _statusCallback?.Invoke("info", $"Bundling {bundleName}: Copying folder {Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}...");
                        CopyFolderIntoWorkspace(folder, tempDir);
                        anyContent = true;
                    }

                    foreach (var folderPartial in folderPartials)
                    {
                        if (folderPartial == null || string.IsNullOrWhiteSpace(folderPartial.Folder) || !Directory.Exists(folderPartial.Folder))
                        {
                            _logger($"[BUNDLE] Warning: Partial folder not found: {folderPartial?.Folder}");
                            continue;
                        }

                        var includeRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var path in folderPartial.Paths ?? new List<string>())
                        {
                            if (string.IsNullOrWhiteSpace(path)) continue;
                            includeRelPaths.Add(path.Replace('\\', '/').ToLowerInvariant());
                        }

                        if (includeRelPaths.Count == 0) continue;

                        _logger($"[BUNDLE] Copying partial folder {folderPartial.Folder} ({includeRelPaths.Count} file(s))...");
                        _statusCallback?.Invoke("info", $"Bundling {bundleName}: Copying folder {Path.GetFileName(folderPartial.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}...");
                        CopyFolderIntoWorkspace(folderPartial.Folder, tempDir, includeRelPaths);
                        anyContent = true;
                    }

                    int index = 0;
                    int totalMods = modFiles.Count;
                    foreach (var mod in modFiles) {
                        index++;
                        string fullPath = ResolveArchivePath(mod);
                        if (partialByArchive.ContainsKey(fullPath))
                            continue;

                        if (File.Exists(fullPath)) {
                            _logger($"[BUNDLE] Extracting {mod}...");
                            _statusCallback?.Invoke("info", $"Bundling {bundleName}: Extracting {index}/{totalMods}...");
                            try
                            {
                                BA2Utility.Extract(fullPath, tempDir, _logger);
                                anyContent = true;
                            }
                            catch (Exception ex)
                            {
                                string sizeHint = "";
                                try
                                {
                                    var fi = new FileInfo(fullPath);
                                    if (fi.Exists)
                                    {
                                        long mb = fi.Length / (1024 * 1024);
                                        sizeHint = $" (~{mb} MB)";
                                    }
                                }
                                catch (Exception sizeEx) { _logger($"[BUNDLE] Failed to read size for {mod}: {sizeEx.Message}"); }

                                _errorLogger($"[BUNDLE] Extraction failed for {mod}{sizeHint}: This archive is too large or uses an unsupported texture format for bundling. Details: {ex.Message}");
                            }
                        } else {
                            _logger($"[BUNDLE] Warning: Mod file not found: {fullPath}");
                        }
                    }

                    int partialIndex = 0;
                    int totalPartials = partialByArchive.Count;
                    foreach (var partial in partialByArchive.Values)
                    {
                        partialIndex++;
                        string fullPath = ResolveArchivePath(partial.Archive);
                        if (!File.Exists(fullPath))
                        {
                            _logger($"[BUNDLE] Warning: Partial archive not found: {fullPath}");
                            continue;
                        }

                        var includePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var path in partial.Paths)
                        {
                            if (string.IsNullOrWhiteSpace(path)) continue;
                            includePaths.Add(path.Replace("\\", "/").ToLowerInvariant());
                        }

                        if (includePaths.Count == 0) continue;

                        _logger($"[BUNDLE] Partial extract {Path.GetFileName(fullPath)} ({includePaths.Count} path(s))...");
                        _statusCallback?.Invoke("info", $"Bundling {bundleName}: Partial extract {partialIndex}/{totalPartials}...");
                        try
                        {
                            BA2Utility.Extract(fullPath, tempDir, _logger, includePaths);
                            anyContent = true;
                        }
                        catch (Exception ex)
                        {
                            _errorLogger($"[BUNDLE] Partial extraction failed for {partial.Archive}: {ex.Message}");
                        }
                    }

                    if (!anyContent)
                    {
                        throw new Exception("No files could be gathered from the selected sources.");
                    }

                    string baseName = bundleName.EndsWith(".ba2") ? bundleName.Substring(0, bundleName.Length - 4) : bundleName;
                    string bundlesDir = AppPaths.DataPath;
                    
                    var allFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories);
                    bool hasTextures = allFiles.Any(f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
                    bool hasGeneral = allFiles.Any(f => !f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));

                    bool wantGeneral = format.Equals("Auto", StringComparison.OrdinalIgnoreCase) || format.Equals("General", StringComparison.OrdinalIgnoreCase);
                    bool wantTextures = format.Equals("Auto", StringComparison.OrdinalIgnoreCase) || format.Equals("DDS", StringComparison.OrdinalIgnoreCase);

                    if (!wantGeneral) hasGeneral = false;
                    if (!wantTextures) hasTextures = false;

                    if (!hasGeneral && !hasTextures)
                    {
                        throw new Exception("Selected format has no matching files to pack.");
                    }

                    string gnrlDir = Path.Combine(tempDir, "Temp_GNRL");
                    string dx10Dir = Path.Combine(tempDir, "Temp_DX10");

                    if (hasGeneral) Directory.CreateDirectory(gnrlDir);
                    if (hasTextures) Directory.CreateDirectory(dx10Dir);

                    foreach (var file in allFiles)
                    {
                        string relPath = Path.GetRelativePath(tempDir, file);
                        bool isDds = file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
                        string targetRoot = isDds ? dx10Dir : gnrlDir;
                        
                        string destPath = Path.Combine(targetRoot, relPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                        File.Move(file, destPath);
                    }

                    var generatedFiles = new List<string>();

                    if (hasGeneral) {
                        _statusCallback?.Invoke("info", $"Bundling {bundleName}: Packing general assets...");
                        string mainBa2Name = baseName + ".ba2";
                        string mainTargetPath = Path.Combine(bundlesDir, mainBa2Name);
                        
                        var filesToPack = Directory.GetFiles(gnrlDir, "*.*", SearchOption.AllDirectories);
                        _logger($"[BUNDLE] Found {filesToPack.Length} files in {gnrlDir} for GNRL pack.");
                        if (filesToPack.Length == 0) _logger("[BUNDLE] WARNING: GNRL directory is empty!");
                        
                        _logger($"[BUNDLE] Packing general files to {mainTargetPath} (Compression: {compressionLevel})...");
                        BA2Utility.Pack(gnrlDir, mainTargetPath, _logger, "GNRL", compressionLevel);
                        generatedFiles.Add(mainBa2Name);
                    }

                    if (hasTextures) {
                        _statusCallback?.Invoke("info", $"Bundling {bundleName}: Packing texture assets...");
                        string textureBa2Name = baseName + " - Textures.ba2";
                        string textureTargetPath = Path.Combine(bundlesDir, textureBa2Name);

                        var filesToPack = Directory.GetFiles(dx10Dir, "*.*", SearchOption.AllDirectories);
                        _logger($"[BUNDLE] Found {filesToPack.Length} files in {dx10Dir} for DX10 pack.");
                        if (filesToPack.Length == 0) _logger("[BUNDLE] WARNING: DX10 directory is empty!");

                        _logger($"[BUNDLE] Packing texture files to {textureTargetPath} (Compression: {compressionLevel})...");
                        BA2Utility.Pack(dx10Dir, textureTargetPath, _logger, "DX10", compressionLevel);
                        generatedFiles.Add(textureBa2Name);
                    }

                    if (generatedFiles.Count == 0) throw new Exception("No files were packed.");

                    string metadataKey = generatedFiles[0]; 
                    _metadataSaver(metadataKey, new F76ManagerApp.Managers.ModManager.ModMetadata {
                        Name = baseName,
                        Author = "F76 Manager",
                        Version = "1.0",
                        IsBundle = true,
                        IsEnabled = false,
                        Files = generatedFiles
                    });

                    _statusCallback?.Invoke("success", $"Successfully created bundle: {bundleName}");
                    _logger($"[BUNDLE] Completed. Cleaning up {tempDir}");
                    
                    var modsToDisable = new List<string>(modFiles);
                    foreach (var partial in partialByArchive.Values)
                    {
                        modsToDisable.Add(GetModRefForToggle(partial.Archive));
                    }
                    _toggleCallback(modsToDisable.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), false);
                    
                    onComplete?.Invoke();
                } catch (Exception ex) {
                    _errorLogger($"[BUNDLE] FAILED: {ex.Message}");
                    _statusCallback?.Invoke("error", "Failed to create bundle.");
                } finally {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch (Exception cleanupEx) { _logger($"[BUNDLE] Failed to clean temp directory '{tempDir}': {cleanupEx.Message}"); }
                }
            });
        }

        private static string ResolveArchivePath(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return archivePath;
            return Path.IsPathRooted(archivePath)
                ? Path.GetFullPath(archivePath)
                : Path.GetFullPath(Path.Combine(AppPaths.DataPath, archivePath));
        }

        private static string GetModRefForToggle(string archivePath)
        {
            string fullPath = ResolveArchivePath(archivePath);
            if (!string.IsNullOrWhiteSpace(AppPaths.DataPath)
                && fullPath.StartsWith(AppPaths.DataPath, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(AppPaths.DataPath, fullPath);
            }
            return archivePath;
        }

        private static void CopyFolderIntoWorkspace(string sourceFolder, string tempDir, HashSet<string>? includeRelPaths = null)
        {
            string normalizedSource = Path.GetFullPath(sourceFolder);
            foreach (var file in Directory.GetFiles(normalizedSource, "*.*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(normalizedSource, file);
                if (includeRelPaths != null)
                {
                    string relKey = relPath.Replace('\\', '/').ToLowerInvariant();
                    if (!includeRelPaths.Contains(relKey)) continue;
                }
                string destPath = Path.Combine(tempDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(file, destPath, overwrite: true);
            }
        }
    }
}