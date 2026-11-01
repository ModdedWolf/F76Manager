using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace F76ManagerApp.Managers
{
    public static partial class HkxAnimationReader
    {
        public sealed class BodyHit
        {
            public bool Ok { get; init; }
            public string Status { get; init; } = "not_found";
            public string Path { get; init; } = "";
            public string Context { get; init; } = "";
            public byte[] Bytes { get; init; }
        }

        private static readonly object BodyListCacheGate = new object();
        private static readonly Dictionary<string, List<string>> BodyListCache =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] BodyNameHints = { "body", "skin", "naked", "base" };

        public static BodyHit ResolveActorBody(string entryPath, string context, IReadOnlyList<Bone> skeletonBones, Action<string> logger = null)
        {
            string actorRoot = ActorRoot((entryPath ?? "").Replace('\\', '/').Trim().TrimStart('/'));
            if (string.IsNullOrEmpty(actorRoot))
                return new BodyHit { Status = "not_found" };

            var skeletonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (skeletonBones != null)
            {
                foreach (Bone bone in skeletonBones)
                {
                    if (!string.IsNullOrEmpty(bone?.Name))
                        skeletonNames.Add(bone.Name);
                }
            }

            var candidates = new List<(string Path, string Context, int Rank)>();
            CollectBodyCandidates(actorRoot, context, candidates);

            candidates = candidates
                .OrderByDescending(c => c.Rank)
                .ThenBy(c => c.Path.Length)
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            BodyHit bestNoMatch = null;
            foreach (var (path, ctx, _) in candidates)
            {
                byte[] bytes = TryReadAssetBytes(path, ctx, logger);
                if (bytes == null || bytes.Length == 0) continue;

                List<string> skinBones = NifMeshReader.PeekSkinBoneNames(bytes, logger);
                if (skinBones.Count == 0) continue;

                int matched = 0;
                foreach (string bone in skinBones)
                {
                    if (skeletonNames.Contains(bone)) matched++;
                }
                float ratio = skinBones.Count == 0 ? 0f : matched / (float)skinBones.Count;
                if (matched >= 8 && ratio >= 0.4f)
                {
                    return new BodyHit
                    {
                        Ok = true,
                        Status = "ok",
                        Path = path,
                        Context = ctx,
                        Bytes = bytes
                    };
                }

                if (bestNoMatch == null || matched > 0)
                {
                    bestNoMatch = new BodyHit
                    {
                        Status = "no_match",
                        Path = path,
                        Context = ctx
                    };
                }
            }

            return bestNoMatch ?? new BodyHit { Status = "not_found" };
        }

        private static void CollectBodyCandidates(string actorRoot, string context, List<(string Path, string Context, int Rank)> candidates)
        {
            void AddList(IEnumerable<string> paths, string ctx)
            {
                if (paths == null) return;
                foreach (string path in paths)
                {
                    string norm = (path ?? "").Replace('\\', '/').Trim().TrimStart('/');
                    if (!IsBodyCandidate(norm, actorRoot)) continue;
                    candidates.Add((norm, ctx, RankBodyPath(norm)));
                }
            }

            if (!string.IsNullOrWhiteSpace(context) && Directory.Exists(context))
                AddList(ListActorNifsInFolder(context, actorRoot), context);

            if (!string.IsNullOrWhiteSpace(context) && File.Exists(context) &&
                string.Equals(Path.GetExtension(context), ".ba2", StringComparison.OrdinalIgnoreCase))
            {
                AddList(ListActorNifsInArchive(context, actorRoot), context);
                string dir = Path.GetDirectoryName(context);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (string sibling in Directory.GetFiles(dir, "*.ba2", SearchOption.TopDirectoryOnly))
                    {
                        if (string.Equals(sibling, context, StringComparison.OrdinalIgnoreCase)) continue;
                        AddList(ListActorNifsInArchive(sibling, actorRoot), sibling);
                    }
                }
            }

            string dataPath = AppPaths.DataPath;
            if (!string.IsNullOrWhiteSpace(dataPath) && Directory.Exists(dataPath))
            {
                string looseRoot = Path.Combine(dataPath, actorRoot.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(looseRoot))
                    AddList(ListActorNifsInFolder(dataPath, actorRoot), dataPath);

                string[] patterns = { "SeventySix - Meshes*.ba2", "Fallout76 - Meshes*.ba2", "*Meshes*.ba2" };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string pattern in patterns)
                {
                    foreach (string archive in Directory.GetFiles(dataPath, pattern, SearchOption.TopDirectoryOnly))
                    {
                        if (!seen.Add(archive)) continue;
                        AddList(ListActorNifsInArchive(archive, actorRoot), archive);
                    }
                }
            }
        }

        private static bool IsBodyCandidate(string path, string actorRoot)
        {
            string lower = path.ToLowerInvariant();
            string root = (actorRoot ?? "").ToLowerInvariant();
            if (root.Length == 0 || !lower.StartsWith(root, StringComparison.Ordinal)) return false;
            if (!lower.EndsWith(".nif", StringComparison.Ordinal)) return false;
            if (lower.Contains("/animations/", StringComparison.Ordinal)) return false;
            if (lower.EndsWith("skeleton.nif", StringComparison.Ordinal)) return false;
            if (lower.Contains("skeleton_female", StringComparison.Ordinal)) return false;
            return true;
        }

        private static int RankBodyPath(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path ?? "").ToLowerInvariant();
            string lower = (path ?? "").Replace('\\', '/').ToLowerInvariant();
            int rank = 0;
            foreach (string hint in BodyNameHints)
            {
                if (name.Contains(hint, StringComparison.Ordinal)) rank += 100;
            }
            string actor = ActorNameFromRoot(lower);
            if (!string.IsNullOrEmpty(actor) && name.Equals(actor, StringComparison.Ordinal))
                rank += 80;
            if (lower.Contains("/characterassets/", StringComparison.Ordinal)) rank += 20;
            if (name.Contains("replace", StringComparison.Ordinal)) rank -= 40;
            if (name.Contains("armour", StringComparison.Ordinal) || name.Contains("armor", StringComparison.Ordinal)) rank -= 30;
            if (name.Contains("helmet", StringComparison.Ordinal) || name.Contains("hair", StringComparison.Ordinal)) rank -= 30;
            if (name.Contains("head", StringComparison.Ordinal) && !name.Contains("body", StringComparison.Ordinal)) rank -= 20;
            if (name.Contains("fx", StringComparison.Ordinal) || name.Contains("particle", StringComparison.Ordinal)) rank -= 50;
            if (name.StartsWith("1stperson", StringComparison.Ordinal)) rank -= 10;
            return rank;
        }

        private static string ActorNameFromRoot(string lowerPath)
        {
            const string marker = "meshes/actors/";
            int at = lowerPath.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return "";
            int start = at + marker.Length;
            int slash = lowerPath.IndexOf('/', start);
            if (slash < 0) return "";
            return lowerPath.Substring(start, slash - start);
        }

        private static List<string> ListActorNifsInFolder(string folder, string actorRoot)
        {
            string key = "folder|" + folder + "|" + actorRoot;
            lock (BodyListCacheGate)
            {
                if (BodyListCache.TryGetValue(key, out List<string> cached))
                    return cached;
            }

            var list = new List<string>();
            try
            {
                string root = Path.Combine(folder, actorRoot.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(root))
                {
                    foreach (string file in Directory.GetFiles(root, "*.nif", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(folder, file).Replace('\\', '/');
                        list.Add(rel);
                    }
                }
            }
            catch { }

            lock (BodyListCacheGate) { BodyListCache[key] = list; }
            return list;
        }

        private static List<string> ListActorNifsInArchive(string archive, string actorRoot)
        {
            string key = "ba2|" + archive + "|" + actorRoot;
            lock (BodyListCacheGate)
            {
                if (BodyListCache.TryGetValue(key, out List<string> cached))
                    return cached;
            }

            var list = new List<string>();
            try
            {
                string prefix = actorRoot.ToLowerInvariant();
                foreach (var entry in BA2Utility.ListEntries(archive).Entries)
                {
                    string path = (entry.Path ?? "").Replace('\\', '/');
                    if (path.ToLowerInvariant().StartsWith(prefix, StringComparison.Ordinal) &&
                        path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                        list.Add(path);
                }
            }
            catch { }

            lock (BodyListCacheGate) { BodyListCache[key] = list; }
            return list;
        }

        private static byte[] TryReadAssetBytes(string relativePath, string context, Action<string> logger)
        {
            try
            {
                if (Directory.Exists(context))
                {
                    string loose = Path.Combine(context, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(loose))
                        return File.ReadAllBytes(loose);
                }

                if (File.Exists(context) &&
                    string.Equals(Path.GetExtension(context), ".ba2", StringComparison.OrdinalIgnoreCase))
                {
                    try { return BA2Utility.ReadEntryBytes(context, relativePath, logger); }
                    catch { }
                }

                string dataPath = AppPaths.DataPath;
                if (!string.IsNullOrWhiteSpace(dataPath))
                {
                    string loose = Path.Combine(dataPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(loose))
                        return File.ReadAllBytes(loose);
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BUNDLE] Body read failed for {relativePath}: {ex.Message}");
            }
            return null;
        }

        public static void RemapSkinToSkeleton(NifMeshReader.Result mesh, IReadOnlyList<Bone> skeletonBones)
        {
            if (mesh?.Shapes == null || skeletonBones == null) return;
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < skeletonBones.Count; i++)
            {
                string name = skeletonBones[i]?.Name;
                if (!string.IsNullOrEmpty(name) && !byName.ContainsKey(name))
                    byName[name] = i;
            }

            foreach (NifMeshReader.Shape shape in mesh.Shapes)
            {
                if (shape.Joints == null || shape.Weights == null || shape.SkinBones == null) continue;
                int verts = shape.Joints.Length / 4;
                var map = new int[shape.SkinBones.Count];
                for (int i = 0; i < map.Length; i++)
                {
                    string name = shape.SkinBones[i];
                    map[i] = !string.IsNullOrEmpty(name) && byName.TryGetValue(name, out int bone) ? bone : -1;
                }

                for (int v = 0; v < verts; v++)
                {
                    int baseIndex = v * 4;
                    for (int k = 0; k < 4; k++)
                    {
                        int local = shape.Joints[baseIndex + k];
                        int mapped = local >= 0 && local < map.Length ? map[local] : -1;
                        if (mapped < 0 || mapped > ushort.MaxValue)
                        {
                            shape.Joints[baseIndex + k] = 0;
                            shape.Weights[baseIndex + k] = 0f;
                        }
                        else
                        {
                            shape.Joints[baseIndex + k] = (ushort)mapped;
                        }
                    }
                }
            }
        }
    }
}
