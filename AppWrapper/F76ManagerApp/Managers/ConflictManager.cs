using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace F76ManagerApp.Managers
{
    public class ConflictManager
    {
        private readonly Action<string> _logger;

        public ConflictManager(Action<string> logger)
        {
            _logger = logger;
        }

        public class Conflict
        {
            public string FilePath { get; set; } = string.Empty;
            public List<string> ModNames { get; set; } = new List<string>();
        }

        public int LastConflictCount { get; private set; }

        public List<Conflict> DetectConflicts(List<string> enabledMods, List<string> searchPaths)
        {
            _logger?.Invoke($"[CONFLICT_DEBUG] DetectConflicts started with {enabledMods.Count} mods. (v2: PathResolveFix)");
            _logger?.Invoke($"[CONFLICT] Starting scan for {enabledMods.Count} mods.");
            
            var fileMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var modFilesAbsolute = new HashSet<string>(enabledMods, StringComparer.OrdinalIgnoreCase);

            foreach (var modPath in enabledMods)
            {
                if (!File.Exists(modPath) && !Directory.Exists(modPath)) continue;

                string modName = Path.GetFileName(modPath);
                var filesInMod = GetFilesInMod(modPath);
                

                foreach (var file in filesInMod)
                {
                    string normalized = file.Replace("\\", "/").ToLowerInvariant().TrimStart('/');
                    if (!fileMap.ContainsKey(normalized))
                    {
                        fileMap[normalized] = new List<string>();
                    }
                    if (!fileMap[normalized].Contains(modName))
                    {
                        fileMap[normalized].Add(modName);
                    }
                }
            }

            foreach (var searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                _logger?.Invoke($"[CONFLICT] Scanning loose files in: {searchPath}");

                try {
                    var allFiles = Directory.GetFiles(searchPath, "*.*", SearchOption.AllDirectories);
                    foreach (var fullPath in allFiles)
                    {
                        if (modFilesAbsolute.Contains(fullPath)) continue;

                        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                        if (ext == ".ba2" || ext == ".esm" || ext == ".esp" || ext == ".exe" || ext == ".dll") continue;

                        string relPath = Path.GetRelativePath(searchPath, fullPath).Replace("\\", "/").ToLowerInvariant().TrimStart('/');
                        
                        if (fileMap.ContainsKey(relPath))
                        {
                            string sourceLabel = $"Loose File ({Path.GetFileName(fullPath)})";
                            
                            if (!fileMap[relPath].Contains(sourceLabel))
                            {
                                fileMap[relPath].Add(sourceLabel);
                            }
                        }
                    }
                } catch (Exception ex) {
                    _logger?.Invoke($"[CONFLICT] Failed to scan search path {searchPath}: {ex.Message}");
                }
            }

            var conflicts = new List<Conflict>();
            foreach (var entry in fileMap)
            {
                if (entry.Value.Count > 1)
                {
                    conflicts.Add(new Conflict
                    {
                        FilePath = entry.Key,
                        ModNames = entry.Value
                    });
                }
            }

            LastConflictCount = conflicts.Count;
            if (conflicts.Count > 0)
            {
                _logger?.Invoke($"[CONFLICT] Found {conflicts.Count} file conflicts!");
                foreach (var c in conflicts.Take(10))
                {
                    _logger?.Invoke($"[CONFLICT] {Path.GetFileName(c.FilePath)} is provided by: {string.Join(", ", c.ModNames)}");
                }
                if (conflicts.Count > 10) _logger?.Invoke($"[CONFLICT] ... and {conflicts.Count - 10} more.");
            }
            else
            {
                _logger?.Invoke($"[CONFLICT] No file conflicts detected.");
            }
            return conflicts;
        }

        private List<string> GetFilesInMod(string path)
        {
            var results = new List<string>();
            try
            {
                if (File.Exists(path))
                {
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext == ".ba2")
                    {
                        return ParseBA2(path);
                    }
                    else if (ext == ".esm" || ext == ".esp" || ext == ".strings" || ext == ".dlstrings" || ext == ".ilstrings")
                    {
                        string dataPath = AppPaths.DataPath.Replace("\\", "/").ToLowerInvariant().TrimEnd('/');
                        string filePath = path.Replace("\\", "/").ToLowerInvariant();
                        
                        if (filePath.Contains(dataPath + "/")) {
                            string rel = filePath.Substring(filePath.IndexOf(dataPath + "/") + (dataPath + "/").Length);
                            results.Add(rel);
                        } else {
                            results.Add(Path.GetFileName(path).ToLowerInvariant());
                        }
                    }
                }
                else if (Directory.Exists(path))
                {
                    var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                    foreach (var f in files)
                    {
                        string rel = Path.GetRelativePath(path, f);
                        results.Add(rel.Replace("\\", "/").ToLowerInvariant().TrimStart('/'));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error identifying files in {Path.GetFileName(path)}: {ex.Message}");
            }
            return results;
        }

        private List<string> ParseBA2(string path)
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
                    if (sigStr != "BTDX") {
                        return files;
                    }

                    uint version = br.ReadUInt32();
                    string type = Encoding.ASCII.GetString(br.ReadBytes(4));
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
