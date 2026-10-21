using System;
using System.Collections.Generic;
using System.IO;

namespace F76ManagerApp.Managers
{
    public static class GameTextureIndex
    {
        public sealed class TextureSource
        {
            public string ArchivePath { get; init; }
            public string InternalPath { get; init; }
            public string LoosePath { get; init; }
            public string DisplayName { get; init; } = "";
            public bool IsLoose => !string.IsNullOrEmpty(LoosePath);
        }

        private sealed class IndexedFile
        {
            public string ArchivePath = "";
            public string InternalPath = "";
        }

        private static readonly object Gate = new object();
        private static string _indexedDataPath = "";
        private static string _fingerprint = "";
        private static Dictionary<string, IndexedFile> _files = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, string>> ArchiveListings = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static string Normalize(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/').Trim().TrimStart('/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized.Substring(2);
            normalized = normalized.ToLowerInvariant();
            int materialsAt = normalized.IndexOf("materials/", StringComparison.Ordinal);
            int texturesAt = normalized.IndexOf("textures/", StringComparison.Ordinal);
            int cut = materialsAt >= 0 && texturesAt >= 0 ? Math.Min(materialsAt, texturesAt)
                : materialsAt >= 0 ? materialsAt : texturesAt;
            if (cut > 0) normalized = normalized.Substring(cut);
            if (normalized.EndsWith(".dds", StringComparison.Ordinal) &&
                !normalized.StartsWith("textures/", StringComparison.Ordinal))
            {
                normalized = "textures/" + normalized;
            }
            else if ((normalized.EndsWith(".bgsm", StringComparison.Ordinal) || normalized.EndsWith(".bgem", StringComparison.Ordinal)) &&
                !normalized.StartsWith("materials/", StringComparison.Ordinal))
            {
                normalized = "materials/" + normalized;
            }
            return normalized;
        }

        public static bool TryResolve(string textureOrMaterialPath, string contextArchiveOrFolder, out TextureSource source)
        {
            source = null;
            string key = Normalize(textureOrMaterialPath);
            if (key.Length == 0) return false;

            if (Directory.Exists(contextArchiveOrFolder))
            {
                string looseInFolder = Path.Combine(contextArchiveOrFolder, key.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(looseInFolder))
                {
                    source = new TextureSource { LoosePath = looseInFolder, InternalPath = key, DisplayName = "folder" };
                    return true;
                }
            }

            if (File.Exists(contextArchiveOrFolder))
            {
                string sibling = SiblingTexturesArchive(contextArchiveOrFolder);
                if (TryReadKnown(sibling, key, out source)) return true;
            }

            string dataPath = AppPaths.DataPath;
            if (!string.IsNullOrWhiteSpace(dataPath))
            {
                string loose = Path.Combine(dataPath, key.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loose))
                {
                    source = new TextureSource { LoosePath = loose, InternalPath = key, DisplayName = "Data" };
                    return true;
                }
            }

            lock (Gate)
            {
                EnsureIndex(dataPath);
                if (_files.TryGetValue(key, out IndexedFile indexed) && File.Exists(indexed.ArchivePath))
                {
                    source = new TextureSource
                    {
                        ArchivePath = indexed.ArchivePath,
                        InternalPath = indexed.InternalPath,
                        DisplayName = Path.GetFileName(indexed.ArchivePath)
                    };
                    return true;
                }
            }

            return false;
        }

        public static byte[] ReadBytes(TextureSource source, int maxEdge, Action<string> logger = null)
        {
            if (source == null) throw new FileNotFoundException("Texture source is empty.");
            if (source.IsLoose) return File.ReadAllBytes(source.LoosePath);
            string path = source.InternalPath ?? "";
            if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                return BA2Utility.ReadTexturePreviewBytes(source.ArchivePath, path, maxEdge, logger);
            return BA2Utility.ReadEntryBytes(source.ArchivePath, path, logger);
        }

        private static bool TryReadKnown(string archive, string key, out TextureSource source)
        {
            source = null;
            if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive)) return false;
            Dictionary<string, string> listing;
            lock (Gate)
            {
                if (!ArchiveListings.TryGetValue(archive, out listing))
                {
                    listing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var entry in BA2Utility.ListEntries(archive).Entries)
                        {
                            string normalized = Normalize(entry.Path);
                            if (!listing.ContainsKey(normalized)) listing[normalized] = entry.Path;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                    ArchiveListings[archive] = listing;
                }
            }
            if (!listing.TryGetValue(key, out string internalPath)) return false;
            source = new TextureSource
            {
                ArchivePath = archive,
                InternalPath = internalPath,
                DisplayName = Path.GetFileName(archive)
            };
            return true;
        }

        private static string SiblingTexturesArchive(string archivePath)
        {
            string dir = Path.GetDirectoryName(archivePath) ?? "";
            string name = Path.GetFileNameWithoutExtension(archivePath) ?? "";
            if (name.EndsWith(" - Textures", StringComparison.OrdinalIgnoreCase))
                return archivePath;
            return Path.Combine(dir, name + " - Textures.ba2");
        }

        private static void EnsureIndex(string dataPath)
        {
            if (string.IsNullOrWhiteSpace(dataPath) || !Directory.Exists(dataPath))
            {
                _files = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
                _indexedDataPath = dataPath ?? "";
                _fingerprint = "";
                return;
            }

            var archives = new List<string>();
            foreach (string file in Directory.GetFiles(dataPath, "*.ba2", SearchOption.TopDirectoryOnly))
                archives.Add(file);
            archives.Sort(StringComparer.OrdinalIgnoreCase);

            string fingerprint = BuildFingerprint(archives);
            if (string.Equals(_indexedDataPath, dataPath, StringComparison.OrdinalIgnoreCase) &&
                fingerprint == _fingerprint)
            {
                return;
            }

            var map = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
            foreach (string archive in OrderArchives(archives))
                IndexArchive(archive, map);

            _files = map;
            _indexedDataPath = dataPath;
            _fingerprint = fingerprint;
        }

        private static string BuildFingerprint(List<string> archives)
        {
            var parts = new List<string>(archives.Count);
            foreach (string archive in archives)
            {
                long ticks = 0;
                try { ticks = File.GetLastWriteTimeUtc(archive).Ticks; } catch { }
                parts.Add(archive + "|" + ticks.ToString());
            }
            return string.Join("\n", parts);
        }

        private static IEnumerable<string> OrderArchives(List<string> archives)
        {
            var mods = new List<string>();
            var vanilla = new List<string>();
            foreach (string archive in archives)
            {
                string name = Path.GetFileName(archive);
                if (name.StartsWith("SeventySix - ", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsVanillaTextureArchive(name) || name.Equals("SeventySix - Materials.ba2", StringComparison.OrdinalIgnoreCase))
                        vanilla.Add(archive);
                }
                else
                {
                    mods.Add(archive);
                }
            }
            foreach (string archive in mods) yield return archive;
            foreach (string archive in vanilla) yield return archive;
        }

        private static bool IsVanillaTextureArchive(string fileName)
        {
            if (fileName.StartsWith("SeventySix - Textures", StringComparison.OrdinalIgnoreCase)) return true;
            if (fileName.StartsWith("SeventySix - GeneratedTextures", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void IndexArchive(string archive, Dictionary<string, IndexedFile> map)
        {
            BA2Utility.BA2ListResult listed;
            try { listed = BA2Utility.ListEntries(archive); }
            catch { return; }

            foreach (var entry in listed.Entries)
            {
                string key = Normalize(entry.Path);
                if (!key.StartsWith("textures/", StringComparison.Ordinal) &&
                    !key.EndsWith(".bgsm", StringComparison.Ordinal) &&
                    !key.EndsWith(".bgem", StringComparison.Ordinal))
                {
                    continue;
                }
                if (map.ContainsKey(key)) continue;
                map[key] = new IndexedFile { ArchivePath = archive, InternalPath = entry.Path };
            }
        }
    }
}
