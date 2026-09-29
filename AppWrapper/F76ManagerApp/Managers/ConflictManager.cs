using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace F76ManagerApp.Managers
{
    public class ConflictManager
    {
        private readonly Action<string> _logger;

        public ConflictManager(Action<string> logger)
        {
            _logger = logger;
        }

        public class ConflictProvider
        {
            public string ModName { get; set; } = string.Empty;
            public string SourcePath { get; set; } = string.Empty;
            public string Kind { get; set; } = "mod";
            public long Size { get; set; }
            public string Fingerprint { get; set; } = string.Empty;
            public string ArchiveType { get; set; } = string.Empty;
        }

        public class Conflict
        {
            public string FilePath { get; set; } = string.Empty;
            public List<string> ModNames { get; set; } = new List<string>();
            public string Status { get; set; } = "differs";
            public List<ConflictProvider> Providers { get; set; } = new List<ConflictProvider>();
        }

        private class PathProvider
        {
            public string ModName { get; set; } = "";
            public string SourcePath { get; set; } = "";
            public string Kind { get; set; } = "mod";
            public string RelativePath { get; set; } = "";
        }

        public int LastConflictCount { get; private set; }
        public int LastDuplicateCount { get; private set; }

        private static readonly string[] KnownLooseAssetFolders =
        {
            "meshes", "textures", "materials", "scripts", "sound", "sounds", "music", "strings",
            "interface", "programs", "video", "lodsettings", "facegen", "misc", "shadersfx",
            "vis", "geo", "terrain", "grass"
        };

        public List<Conflict> DetectConflicts(List<string> enabledMods, List<string> searchPaths)
        {
            _logger?.Invoke($"[CONFLICT_DEBUG] DetectConflicts started with {enabledMods.Count} mods. (v3: ContentAware)");
            _logger?.Invoke($"[CONFLICT] Starting scan for {enabledMods.Count} mods.");

            var fileMap = new Dictionary<string, List<PathProvider>>(StringComparer.OrdinalIgnoreCase);
            var modFilesAbsolute = new HashSet<string>(enabledMods, StringComparer.OrdinalIgnoreCase);

            foreach (var modPath in enabledMods)
            {
                if (!File.Exists(modPath) && !Directory.Exists(modPath)) continue;

                string modName = Path.GetFileName(modPath);
                foreach (var provider in GetProvidersInMod(modPath, modName))
                {
                    string normalized = provider.RelativePath.Replace("\\", "/").ToLowerInvariant().TrimStart('/');
                    if (!fileMap.ContainsKey(normalized))
                        fileMap[normalized] = new List<PathProvider>();

                    if (!fileMap[normalized].Any(p =>
                            p.ModName.Equals(provider.ModName, StringComparison.OrdinalIgnoreCase) &&
                            p.SourcePath.Equals(provider.SourcePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        fileMap[normalized].Add(provider);
                    }
                }
            }

            foreach (var searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                _logger?.Invoke($"[CONFLICT] Scanning loose files in: {searchPath}");

                try
                {
                    foreach (var fullPath in EnumerateLooseAssetFiles(searchPath))
                    {
                        if (modFilesAbsolute.Contains(fullPath)) continue;

                        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                        if (ext == ".ba2" || ext == ".esm" || ext == ".esp" || ext == ".exe" || ext == ".dll") continue;

                        string relPath = Path.GetRelativePath(searchPath, fullPath).Replace("\\", "/").ToLowerInvariant().TrimStart('/');

                        if (fileMap.ContainsKey(relPath))
                        {
                            string sourceLabel = $"Loose File ({Path.GetFileName(fullPath)})";
                            if (!fileMap[relPath].Any(p => p.ModName.Equals(sourceLabel, StringComparison.OrdinalIgnoreCase)))
                            {
                                fileMap[relPath].Add(new PathProvider
                                {
                                    ModName = sourceLabel,
                                    SourcePath = fullPath,
                                    Kind = "loose",
                                    RelativePath = relPath
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[CONFLICT] Failed to scan search path {searchPath}: {ex.Message}");
                }
            }

            var conflicts = new List<Conflict>();
            int realCount = 0;
            int dupCount = 0;
            var ba2SizeCache = new Dictionary<string, (string ArchiveType, Dictionary<string, uint> Sizes)>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in fileMap)
            {
                if (entry.Value.Count <= 1) continue;

                var providers = new List<ConflictProvider>();
                foreach (var pp in entry.Value)
                {
                    providers.Add(BuildProviderSizeOnly(pp, entry.Key, ba2SizeCache));
                }

                bool hasDx10 = providers.Any(p => string.Equals(p.ArchiveType, "DX10", StringComparison.OrdinalIgnoreCase));
                bool hasLooseDds = providers.Any(p =>
                    !string.Equals(p.Kind, "ba2", StringComparison.OrdinalIgnoreCase) &&
                    (p.SourcePath ?? "").EndsWith(".dds", StringComparison.OrdinalIgnoreCase));

                string status;
                if (hasDx10 && hasLooseDds)
                {
                    status = "unknown";
                }
                else
                {
                    var distinctSizes = providers.Select(p => p.Size).Distinct().ToList();
                    if (distinctSizes.Count > 1)
                    {
                        status = "differs";
                    }
                    else
                    {
                        for (int i = 0; i < entry.Value.Count; i++)
                        {
                            FillProviderHash(providers[i], entry.Value[i], entry.Key);
                        }
                        status = ClassifyProviders(providers);
                    }
                }

                var conflict = new Conflict
                {
                    FilePath = entry.Key,
                    ModNames = entry.Value.Select(p => p.ModName).ToList(),
                    Status = status,
                    Providers = providers
                };
                conflicts.Add(conflict);

                if (status == "identical") dupCount++;
                else realCount++;
            }

            LastConflictCount = realCount;
            LastDuplicateCount = dupCount;

            if (conflicts.Count > 0)
            {
                _logger?.Invoke($"[CONFLICT] Found {realCount} real content conflicts and {dupCount} identical duplicates ({conflicts.Count} path overlaps).");
                foreach (var c in conflicts.Where(x => x.Status != "identical").Take(10))
                {
                    _logger?.Invoke($"[CONFLICT] {Path.GetFileName(c.FilePath)} [{c.Status}] is provided by: {string.Join(", ", c.ModNames)}");
                }
                if (realCount > 10) _logger?.Invoke($"[CONFLICT] ... and more real conflicts.");
            }
            else
            {
                _logger?.Invoke($"[CONFLICT] No file conflicts detected.");
            }
            return conflicts;
        }

        private static string ClassifyProviders(List<ConflictProvider> providers)
        {
            bool hasDx10 = providers.Any(p => string.Equals(p.ArchiveType, "DX10", StringComparison.OrdinalIgnoreCase));
            bool hasLooseDds = providers.Any(p =>
                !string.Equals(p.Kind, "ba2", StringComparison.OrdinalIgnoreCase) &&
                (p.SourcePath ?? "").EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
            if (hasDx10 && hasLooseDds)
                return "unknown";

            var ok = providers.Where(p => !string.IsNullOrEmpty(p.Fingerprint)).ToList();
            if (ok.Count < 2)
                return "unknown";

            if (ok.Count == providers.Count &&
                ok.Select(p => p.Fingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 &&
                ok.Select(p => p.Size).Distinct().Count() == 1)
            {
                return "identical";
            }

            if (ok.Select(p => p.Size).Distinct().Count() > 1)
                return "differs";

            if (ok.Select(p => p.Fingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                return "differs";

            return "unknown";
        }

        private ConflictProvider BuildProviderSizeOnly(PathProvider pp, string relativePath, Dictionary<string, (string ArchiveType, Dictionary<string, uint> Sizes)> ba2SizeCache)
        {
            var result = new ConflictProvider
            {
                ModName = pp.ModName,
                SourcePath = pp.SourcePath,
                Kind = pp.Kind
            };

            try
            {
                if (pp.Kind == "ba2" && File.Exists(pp.SourcePath))
                {
                    if (!ba2SizeCache.TryGetValue(pp.SourcePath, out var cached))
                    {
                        var sizeMap = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                        string archType = "GNRL";
                        try
                        {
                            var listed = BA2Utility.ListEntries(pp.SourcePath, _logger);
                            archType = listed.ArchiveType ?? "GNRL";
                            foreach (var e in listed.Entries)
                            {
                                string key = (e.Path ?? "").Replace("\\", "/").TrimStart('/').ToLowerInvariant();
                                sizeMap[key] = e.UnpackedSize;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.Invoke($"[CONFLICT] BA2 list failed for {Path.GetFileName(pp.SourcePath)}: {ex.Message}");
                        }
                        cached = (archType, sizeMap);
                        ba2SizeCache[pp.SourcePath] = cached;
                    }

                    result.ArchiveType = cached.ArchiveType;
                    if (cached.Sizes.TryGetValue(relativePath, out var sz))
                        result.Size = sz;
                }
                else if (File.Exists(pp.SourcePath))
                {
                    result.Size = new FileInfo(pp.SourcePath).Length;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[CONFLICT] Size probe failed for {pp.ModName}:{relativePath}: {ex.Message}");
            }

            return result;
        }

        private void FillProviderHash(ConflictProvider result, PathProvider pp, string relativePath)
        {
            try
            {
                if (pp.Kind == "ba2" && File.Exists(pp.SourcePath))
                {
                    var fp = BA2Utility.TryGetEntryFingerprint(pp.SourcePath, relativePath, _logger);
                    result.ArchiveType = string.IsNullOrEmpty(fp.ArchiveType) ? result.ArchiveType : fp.ArchiveType;
                    if (fp.Ok)
                    {
                        result.Size = fp.UnpackedSize;
                        result.Fingerprint = fp.Md5Hex;
                    }
                }
                else if (File.Exists(pp.SourcePath))
                {
                    result.Size = new FileInfo(pp.SourcePath).Length;
                    result.Fingerprint = HashFileMd5(pp.SourcePath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[CONFLICT] Hash failed for {pp.ModName}:{relativePath}: {ex.Message}");
            }
        }

        private static string HashFileMd5(string path)
        {
            using var md5 = MD5.Create();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] hash = md5.ComputeHash(fs);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static IEnumerable<string> EnumerateLooseAssetFiles(string dataRoot)
        {
            foreach (var folderName in KnownLooseAssetFolders)
            {
                string dir = Path.Combine(dataRoot, folderName);
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }
                foreach (var f in files)
                    yield return f;
            }

            IEnumerable<string> topFiles;
            try
            {
                topFiles = Directory.EnumerateFiles(dataRoot, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                yield break;
            }
            foreach (var f in topFiles)
            {
                string ext = Path.GetExtension(f);
                if (ext.Equals(".ba2", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return f;
            }
        }

        private IEnumerable<PathProvider> GetProvidersInMod(string path, string modName)
        {
            var results = new List<PathProvider>();
            try
            {
                if (File.Exists(path))
                {
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext == ".ba2")
                    {
                        foreach (var name in ParseBA2Names(path))
                        {
                            results.Add(new PathProvider
                            {
                                ModName = modName,
                                SourcePath = path,
                                Kind = "ba2",
                                RelativePath = name
                            });
                        }
                    }
                    else if (ext == ".esm" || ext == ".esp" || ext == ".strings" || ext == ".dlstrings" || ext == ".ilstrings")
                    {
                        string dataPath = AppPaths.DataPath.Replace("\\", "/").ToLowerInvariant().TrimEnd('/');
                        string filePath = path.Replace("\\", "/").ToLowerInvariant();
                        string rel;
                        if (filePath.Contains(dataPath + "/"))
                            rel = filePath.Substring(filePath.IndexOf(dataPath + "/") + (dataPath + "/").Length);
                        else
                            rel = Path.GetFileName(path).ToLowerInvariant();

                        results.Add(new PathProvider
                        {
                            ModName = modName,
                            SourcePath = path,
                            Kind = "plugin",
                            RelativePath = rel
                        });
                    }
                }
                else if (Directory.Exists(path))
                {
                    var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                    foreach (var f in files)
                    {
                        string rel = Path.GetRelativePath(path, f).Replace("\\", "/").ToLowerInvariant().TrimStart('/');
                        results.Add(new PathProvider
                        {
                            ModName = modName,
                            SourcePath = f,
                            Kind = "folder",
                            RelativePath = rel
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error identifying files in {Path.GetFileName(path)}: {ex.Message}");
            }
            return results;
        }

        private List<string> ParseBA2Names(string path)
        {
            var files = new List<string>();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 24) return files;

                    byte[] sig = br.ReadBytes(4);
                    string sigStr = Encoding.ASCII.GetString(sig);
                    if (sigStr != "BTDX") return files;

                    br.ReadUInt32();
                    br.ReadBytes(4);
                    uint numFiles = br.ReadUInt32();
                    ulong nameTableOffset = br.ReadUInt64();

                    if (numFiles == 0 || nameTableOffset == 0 || nameTableOffset >= (ulong)fs.Length) return files;

                    fs.Seek((long)nameTableOffset, SeekOrigin.Begin);

                    for (int i = 0; i < numFiles; i++)
                    {
                        if (fs.Position >= fs.Length - 2) break;

                        ushort len = br.ReadUInt16();
                        if (len == 0) continue;
                        if (fs.Position + len > fs.Length) break;

                        byte[] nameBytes = br.ReadBytes(len);
                        string name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
                        files.Add(name.Replace("\\", "/").ToLowerInvariant());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Failed to parse BA2 {Path.GetFileName(path)}: {ex.Message}");
            }
            _logger?.Invoke($"[BA2_DEBUG] Parsed {files.Count} files from {Path.GetFileName(path)}");
            return files;
        }
    }
}
