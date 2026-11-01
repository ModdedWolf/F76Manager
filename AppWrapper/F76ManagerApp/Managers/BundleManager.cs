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

    public class Ba2Replacement
    {
        public string Archive { get; set; } = "";
        public string Path { get; set; } = "";
        public string Source { get; set; } = "";
    }

    public class BundleEditTarget
    {
        public string Key { get; set; } = "";
        public string OutputDir { get; set; } = "";
        public string SourceDir { get; set; } = "";
        public string BaseName { get; set; } = "";
        public List<string> OldFiles { get; set; } = new List<string>();
    }

    public class BundleManager
    {
        private Action<string, string> _statusCallback;
        private Action<string> _logger;
        private Action<string> _errorLogger;
        private Action<string, F76ManagerApp.Managers.ModManager.ModMetadata> _metadataSaver;
        private Action<List<string>, bool> _toggleCallback;
        private readonly Func<string, string>? _archiveResolver;
        private readonly Func<string, string>? _modKeyMapper;
        private readonly Func<string>? _outputDirProvider;

        public BundleManager(Action<string> logger, Action<string, string> statusCallback, Action<string, F76ManagerApp.Managers.ModManager.ModMetadata> metadataSaver, Action<List<string>, bool> toggleCallback, Action<string> errorLogger = null,
            Func<string, string>? archiveResolver = null, Func<string, string>? modKeyMapper = null, Func<string>? outputDirProvider = null)
        {
            _logger = logger;
            _statusCallback = statusCallback;
            _metadataSaver = metadataSaver;
            _toggleCallback = toggleCallback;
            _errorLogger = errorLogger ?? logger;
            _archiveResolver = archiveResolver;
            _modKeyMapper = modKeyMapper;
            _outputDirProvider = outputDirProvider;
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
            CreateBundle(bundleName, modFiles, sourceFolders, partialExtractions, null, compressionLevel, format,
                onComplete == null ? null : (Action<List<string>>)(_ => onComplete()));
        }

        public void CreateBundle(string bundleName, List<string> modFiles, List<string>? sourceFolders, List<Ba2PartialSelection>? partialExtractions, List<FolderPartialSelection>? folderPartialExtractions, string compressionLevel, string format, Action<List<string>>? onComplete)
        {
            CreateBundle(bundleName, modFiles, sourceFolders, partialExtractions, folderPartialExtractions, null, compressionLevel, format, onComplete);
        }

        public void CreateBundle(string bundleName, List<string> modFiles, List<string>? sourceFolders, List<Ba2PartialSelection>? partialExtractions, List<FolderPartialSelection>? folderPartialExtractions, List<Ba2Replacement>? replacements, string compressionLevel, string format, Action<List<string>>? onComplete)
        {
            CreateBundle(bundleName, modFiles, sourceFolders, partialExtractions, folderPartialExtractions, replacements, null, compressionLevel, format, onComplete);
        }

        public void CreateBundle(string bundleName, List<string> modFiles, List<string>? sourceFolders, List<Ba2PartialSelection>? partialExtractions, List<FolderPartialSelection>? folderPartialExtractions, List<Ba2Replacement>? replacements, BundleEditTarget? editTarget, string compressionLevel, string format, Action<List<string>>? onComplete)
        {
            var folders = sourceFolders ?? new List<string>();
            var partials = partialExtractions ?? new List<Ba2PartialSelection>();
            var folderPartials = folderPartialExtractions ?? new List<FolderPartialSelection>();
            var fileReplacements = replacements ?? new List<Ba2Replacement>();
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

                    foreach (var folder in folders)
                    {
                        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                        {
                            _logger($"[BUNDLE] Warning: Folder not found: {folder}");
                            continue;
                        }
                        _logger($"[BUNDLE] Copying folder {folder}...");
                        _statusCallback?.Invoke("info", $"Bundling {bundleName}: Copying folder {Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}...");
                        CopyFolderIntoWorkspace(folder, tempDir, logger: _logger);
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
                        CopyFolderIntoWorkspace(folderPartial.Folder, tempDir, includeRelPaths, _logger);
                        anyContent = true;
                    }

                    string tempRoot = Path.GetFullPath(tempDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    foreach (var replacement in fileReplacements)
                    {
                        if (replacement == null || string.IsNullOrWhiteSpace(replacement.Path) || string.IsNullOrWhiteSpace(replacement.Source))
                            continue;
                        if (!File.Exists(replacement.Source))
                        {
                            _logger($"[BUNDLE] Warning: Replacement source not found, keeping original: {replacement.Source}");
                            continue;
                        }

                        string relPath = replacement.Path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                        string destPath = Path.GetFullPath(Path.Combine(tempDir, relPath));
                        if (!destPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger($"[BUNDLE] Warning: Rejected replacement outside the bundle workspace: {replacement.Path}");
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        File.Copy(replacement.Source, destPath, overwrite: true);
                        _logger($"[BUNDLE] Replaced {replacement.Path} with {replacement.Source}");
                        anyContent = true;
                    }

                    if (!anyContent)
                    {
                        throw new Exception("No files could be gathered from the selected sources.");
                    }

                    string baseName = bundleName.EndsWith(".ba2") ? bundleName.Substring(0, bundleName.Length - 4) : bundleName;
                    string bundlesDir = _outputDirProvider?.Invoke() ?? AppPaths.DataPath;
                    if (editTarget != null)
                    {
                        baseName = editTarget.BaseName;
                        bundlesDir = editTarget.OutputDir;
                    }
                    if (string.IsNullOrWhiteSpace(bundlesDir)) bundlesDir = AppPaths.DataPath;
                    Directory.CreateDirectory(bundlesDir);
                    string packOutDir = Path.Combine(tempDir, "_out");
                    Directory.CreateDirectory(packOutDir);
                    
                    var allFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories);
                    bool hasTextures = allFiles.Any(f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
                    bool hasGeneral = allFiles.Any(f => !f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));

                    bool wantGeneral = format.Equals("Auto", StringComparison.OrdinalIgnoreCase) || format.Equals("General", StringComparison.OrdinalIgnoreCase);
                    bool wantTextures = format.Equals("Auto", StringComparison.OrdinalIgnoreCase) || format.Equals("DDS", StringComparison.OrdinalIgnoreCase);

                    bool filteredGeneral = wantGeneral && hasGeneral;
                    bool filteredTextures = wantTextures && hasTextures;
                    if (!filteredGeneral && !filteredTextures)
                    {
                        if (hasGeneral || hasTextures)
                        {
                            _logger($"[BUNDLE] Format '{format}' matched no files (general={hasGeneral}, textures={hasTextures}); auto-packing available content.");
                            filteredGeneral = hasGeneral;
                            filteredTextures = hasTextures;
                        }
                        else
                        {
                            throw new Exception("Selected format has no matching files to pack.");
                        }
                    }

                    hasGeneral = filteredGeneral;
                    hasTextures = filteredTextures;

                    string gnrlDir = Path.Combine(tempDir, "Temp_GNRL");
                    string dx10Dir = Path.Combine(tempDir, "Temp_DX10");

                    if (hasGeneral) Directory.CreateDirectory(gnrlDir);
                    if (hasTextures) Directory.CreateDirectory(dx10Dir);

                    foreach (var file in allFiles)
                    {
                        bool isDds = file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
                        if (isDds && !hasTextures) continue;
                        if (!isDds && !hasGeneral) continue;

                        string relPath = Path.GetRelativePath(tempDir, file);
                        string targetRoot = isDds ? dx10Dir : gnrlDir;
                        
                        string destPath = Path.Combine(targetRoot, relPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        File.Move(file, destPath);
                    }

                    var generatedFiles = new List<string>();
                    var packedOutputs = new List<(string PackedPath, string TargetPath)>();

                    if (hasGeneral) {
                        _statusCallback?.Invoke("info", $"Bundling {bundleName}: Packing general assets...");
                        string mainBa2Name = baseName + ".ba2";
                        string mainTargetPath = Path.Combine(bundlesDir, mainBa2Name);
                        
                        var filesToPack = Directory.GetFiles(gnrlDir, "*.*", SearchOption.AllDirectories);
                        _logger($"[BUNDLE] Found {filesToPack.Length} files in {gnrlDir} for GNRL pack.");
                        if (filesToPack.Length == 0) _logger("[BUNDLE] WARNING: GNRL directory is empty!");
                        
                        _logger($"[BUNDLE] Packing general files to {mainTargetPath} (Compression: {compressionLevel})...");
                        string mainPackPath = Path.Combine(packOutDir, mainBa2Name);
                        BA2Utility.Pack(gnrlDir, mainPackPath, _logger, "GNRL", compressionLevel);
                        packedOutputs.Add((mainPackPath, mainTargetPath));
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
                        string texturePackPath = Path.Combine(packOutDir, textureBa2Name);
                        BA2Utility.Pack(dx10Dir, texturePackPath, _logger, "DX10", compressionLevel);
                        packedOutputs.Add((texturePackPath, textureTargetPath));
                        generatedFiles.Add(textureBa2Name);
                    }

                    if (generatedFiles.Count == 0) throw new Exception("No files were packed.");

                    foreach (var (packedPath, targetPath) in packedOutputs)
                        File.Move(packedPath, targetPath, overwrite: true);

                    if (editTarget == null)
                    {
                        string metadataKey = $"Bundles/{generatedFiles[0]}";
                        _metadataSaver(metadataKey, new F76ManagerApp.Managers.ModManager.ModMetadata {
                            Name = baseName,
                            Author = "F76 Manager",
                            Version = "1.0",
                            IsBundle = true,
                            IsEnabled = false,
                            Files = generatedFiles
                        });
                    }

                    string successMsg = editTarget != null
                        ? $"Saved bundle: {bundleName}"
                        : $"Successfully created bundle: {bundleName}";
                    _statusCallback?.Invoke("success", successMsg);
                    _logger($"[BUNDLE] Completed. Cleaning up {tempDir}");
                    
                    var modsToDisable = new List<string>(modFiles);
                    foreach (var partial in partialByArchive.Values)
                    {
                        modsToDisable.Add(GetModRefForToggle(partial.Archive));
                    }
                    if (editTarget != null)
                    {
                        var ownFiles = new HashSet<string>(
                            editTarget.OldFiles.Concat(generatedFiles).Select(f => Path.GetFileName(f.Replace('\\', '/'))),
                            StringComparer.OrdinalIgnoreCase);
                        modsToDisable.RemoveAll(m => ownFiles.Contains(Path.GetFileName(m.Replace('\\', '/'))));
                    }
                    _toggleCallback(modsToDisable.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), false);
                    
                    onComplete?.Invoke(generatedFiles);
                } catch (Exception ex) {
                    _errorLogger($"[BUNDLE] FAILED: {ex.Message}");
                    _statusCallback?.Invoke("error", $"Failed to create bundle: {ex.Message}");
                } finally {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch (Exception cleanupEx) { _logger($"[BUNDLE] Failed to clean temp directory '{tempDir}': {cleanupEx.Message}"); }
                }
            });
        }

        private string ResolveArchivePath(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return archivePath;
            if (_archiveResolver != null)
            {
                try { return _archiveResolver(archivePath); }
                catch (FileNotFoundException) { }
            }
            return Path.IsPathRooted(archivePath)
                ? Path.GetFullPath(archivePath)
                : Path.GetFullPath(Path.Combine(AppPaths.DataPath, archivePath));
        }

        private string GetModRefForToggle(string archivePath)
        {
            if (!Path.IsPathRooted(archivePath)) return archivePath.Replace('\\', '/');
            if (_modKeyMapper != null) return _modKeyMapper(archivePath);
            string fullPath = Path.GetFullPath(archivePath);
            if (!string.IsNullOrWhiteSpace(AppPaths.DataPath)
                && fullPath.StartsWith(AppPaths.DataPath, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(AppPaths.DataPath, fullPath);
            }
            return archivePath;
        }

        private static readonly HashSet<string> DataTopLevelDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "meshes", "textures", "materials", "sound", "sounds", "music", "interface", "scripts", "strings",
            "vis", "lodsettings", "terrain", "misc", "programs", "shadersfx", "seq", "video",
            "facegen", "geo", "grass"
        };

        private static readonly HashSet<string> AssetRootDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "meshes", "textures", "materials"
        };

        private static readonly HashSet<string> FolderJunkNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "thumbs.db", "desktop.ini", ".ds_store", "moduleconfig.xml",
            "readme.txt", "readme.md", "license.txt", "license.md", "changelog.txt", "changelog.md"
        };

        private static string NormalizeFolderRel(string relPath) =>
            (relPath ?? "").Replace('\\', '/').Trim().Trim('/');

        private static bool IsFolderJunk(string relPath)
        {
            var parts = NormalizeFolderRel(relPath).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return true;
            if (parts.Any(p => p.Equals("fomod", StringComparison.OrdinalIgnoreCase))) return true;
            string name = parts[^1];
            return FolderJunkNames.Contains(name) || Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds a mapper from a source folder's relative file paths to Data-relative paths (forward slashes).
        /// The mapper returns null for files that should not be bundled.
        /// </summary>
        internal static Func<string, string?> CreateFolderDataMapper(string sourceFolder, IEnumerable<string> relPaths)
        {
            var segs = Path.GetFullPath(sourceFolder)
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            string add = GetFolderPathDataPrefix(segs);
            string strip = "";
            if (add.Length == 0)
            {
                strip = FindDataRootPrefix(relPaths.Where(p => !IsFolderJunk(p)).Select(NormalizeFolderRel).ToList(), out bool foundRoot);
                if (!foundRoot)
                {
                    add = GetAssetRootAncestorPrefix(segs);
                    if (add.Length > 0) strip = "";
                }
            }

            return relPath =>
            {
                if (IsFolderJunk(relPath)) return null;
                string rel = NormalizeFolderRel(relPath);
                if (strip.Length > 0)
                {
                    if (!rel.StartsWith(strip, StringComparison.OrdinalIgnoreCase)) return null;
                    rel = rel.Substring(strip.Length);
                }
                if (add.Length > 0) return add + "/" + rel;
                if (rel.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) rel = rel.Substring("data/".Length);
                return rel.Length == 0 ? null : rel;
            };
        }

        /// <summary>Where the selected folder itself sits under Data, e.g. "textures/interface" for ...\Data\Textures\Interface.</summary>
        private static string GetFolderPathDataPrefix(string[] segs)
        {
            if (segs.Length == 0) return "";
            for (int i = segs.Length - 1; i >= Math.Max(0, segs.Length - 8); i--)
            {
                if (!segs[i].Equals("data", StringComparison.OrdinalIgnoreCase)) continue;
                if (i == segs.Length - 1) return "";
                if (DataTopLevelDirs.Contains(segs[i + 1])) return JoinSegments(segs, i + 1);
                break;
            }
            return DataTopLevelDirs.Contains(segs[^1]) ? segs[^1].ToLowerInvariant() : "";
        }

        /// <summary>For a folder like ...\textures\armor that is not under a Data folder and holds no Data root of its own.</summary>
        private static string GetAssetRootAncestorPrefix(string[] segs)
        {
            for (int i = Math.Max(0, segs.Length - 5); i < segs.Length; i++)
            {
                if (AssetRootDirs.Contains(segs[i])) return JoinSegments(segs, i);
            }
            return "";
        }

        private static string JoinSegments(string[] segs, int from) =>
            string.Join("/", segs.Skip(from)).ToLowerInvariant();

        /// <summary>Wrapper folders to strip so the contents start at the Data root, e.g. "ModName v1.2/Data/".</summary>
        private static string FindDataRootPrefix(List<string> rels, out bool foundRoot)
        {
            foundRoot = false;
            string prefix = "";
            var current = rels;
            for (int depth = 0; depth < 4 && current.Count > 0; depth++)
            {
                var tops = current.Select(r => { int i = r.IndexOf('/'); return i < 0 ? null : r.Substring(0, i); }).ToList();
                if (tops.Any(t => t != null && DataTopLevelDirs.Contains(t)))
                {
                    foundRoot = true;
                    break;
                }

                string? dataDir = tops.FirstOrDefault(t => t != null && t.Equals("data", StringComparison.OrdinalIgnoreCase));
                string? next = dataDir;
                if (next == null)
                {
                    var distinct = tops.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (distinct.Count != 1 || distinct[0] == null) break;
                    next = distinct[0]!;
                }

                string nextPrefix = next + "/";
                prefix += nextPrefix;
                current = current
                    .Where(r => r.StartsWith(nextPrefix, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Substring(nextPrefix.Length))
                    .ToList();
                if (dataDir != null)
                {
                    foundRoot = true;
                    break;
                }
            }
            return prefix;
        }

        internal static string NormalizeOverlayArchivePath(string path) =>
            (path ?? "").Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

        private static void CopyFolderIntoWorkspace(string sourceFolder, string tempDir, HashSet<string>? includeRelPaths = null, Action<string>? logger = null)
        {
            string normalizedSource = Path.GetFullPath(sourceFolder);
            var files = Directory.GetFiles(normalizedSource, "*.*", SearchOption.AllDirectories);
            var mapper = CreateFolderDataMapper(normalizedSource, files.Select(f => Path.GetRelativePath(normalizedSource, f)));
            int skipped = 0;
            foreach (var file in files)
            {
                string relPath = Path.GetRelativePath(normalizedSource, file);
                if (includeRelPaths != null)
                {
                    string relKey = relPath.Replace('\\', '/').ToLowerInvariant();
                    if (!includeRelPaths.Contains(relKey)) continue;
                }

                string? mapped = mapper(relPath);
                if (mapped == null)
                {
                    skipped++;
                    continue;
                }

                if (file.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase))
                {
                    logger?.Invoke($"[BUNDLE] Extracting archive found in folder: {relPath}");
                    try { BA2Utility.Extract(file, tempDir, logger ?? (_ => { })); }
                    catch (Exception ex) { logger?.Invoke($"[BUNDLE] Could not extract {relPath}: {ex.Message}"); }
                    continue;
                }

                string destPath = Path.Combine(tempDir, mapped.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                if (File.Exists(destPath)) logger?.Invoke($"[BUNDLE] Folder file overrides archive file: {relPath}");
                File.Copy(file, destPath, overwrite: true);
            }
            if (skipped > 0) logger?.Invoke($"[BUNDLE] Skipped {skipped} file(s) in {normalizedSource} that are not game data.");
        }
    }
}