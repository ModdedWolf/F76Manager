using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace F76ManagerApp.Managers;

public partial class ModManager
{
    public string ActiveDataPath => GetActiveDataPath();

    public string ResolveArchiveOnDisk(string keyOrPath)
    {
        if (string.IsNullOrWhiteSpace(keyOrPath))
            throw new FileNotFoundException("BA2 path is empty.");

        string raw = keyOrPath.Trim();
        if (Path.IsPathRooted(raw))
        {
            if (File.Exists(raw)) return Path.GetFullPath(raw);
            string? dataRelative = TryGetDataRelativeKey(raw);
            if (dataRelative == null)
                throw new FileNotFoundException($"BA2 file not found: {keyOrPath}", keyOrPath);
            raw = dataRelative;
        }

        string key = NormalizeMetadataKey(raw);
        string full = GetFullPath(key);
        if (!string.IsNullOrEmpty(full) && File.Exists(full)) return full;

        string? found = FindBundleFileOnDisk(Path.GetFileName(key));
        if (found != null) return found;

        throw new FileNotFoundException($"BA2 file not found: {keyOrPath}", keyOrPath);
    }

    public string ToActiveDataModKey(string keyOrPath)
    {
        if (string.IsNullOrWhiteSpace(keyOrPath)) return keyOrPath;
        if (!Path.IsPathRooted(keyOrPath)) return NormalizeMetadataKey(keyOrPath);

        string full = Path.GetFullPath(keyOrPath);
        if (IsUnderFolder(full, AppPaths.DisabledModsPath))
            return $"Disabled/{NormalizeMetadataKey(Path.GetRelativePath(AppPaths.DisabledModsPath, full))}";
        return TryGetDataRelativeKey(full) ?? keyOrPath;
    }

    private string? TryGetDataRelativeKey(string fullPath)
    {
        foreach (string root in new[] { GetActiveDataPath(), AppPaths.DataPath })
        {
            if (IsUnderFolder(fullPath, root))
                return NormalizeMetadataKey(Path.GetRelativePath(root, Path.GetFullPath(fullPath)));
        }
        return null;
    }

    private static bool IsUnderFolder(string fullPath, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(fullPath)) return false;
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(fullPath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex Ba2PartnerSuffix = new(
        @"^(?<stem>.+?)(?:\s*[-_]\s*|\s+)(?<suffix>textures\d*|tex|t|main|meshes|mesh|m|general|geometry)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, (string Stem, bool IsTexture)> Ba2PartnerStemCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static (string Stem, bool IsTexture) GetBa2PartnerStem(string fileName)
    {
        return Ba2PartnerStemCache.GetOrAdd(fileName ?? "", static name =>
        {
            string stemSource = Path.GetFileNameWithoutExtension(name).Trim();
            var match = Ba2PartnerSuffix.Match(stemSource);
            if (!match.Success) return (stemSource.ToLowerInvariant(), false);
            string suffix = match.Groups["suffix"].Value.ToLowerInvariant();
            bool isTexture = suffix.StartsWith("tex", StringComparison.Ordinal) || suffix == "t";
            return (match.Groups["stem"].Value.Trim().ToLowerInvariant(), isTexture);
        });
    }

    private static bool IsPlainBa2ModKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)) return false;
        string rest = key.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase)
            ? key.Substring("Disabled/".Length)
            : key;
        return !rest.Contains('/');
    }

    private static string StripDisabledKey(string key) =>
        key.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase) ? key.Substring("Disabled/".Length) : key;

    private List<string> FindBa2Partners(
        string modKey,
        IReadOnlyCollection<string> poolKeys,
        Dictionary<string, ModMetadata> metadata)
    {
        var partners = new List<string>();
        string self = NormalizeMetadataKey(modKey);
        if (!IsPlainBa2ModKey(self)) return partners;
        string selfFile = StripDisabledKey(self);

        if (metadata.TryGetValue(self, out var selfMeta) && selfMeta?.Files != null && selfMeta.Files.Count > 1)
        {
            foreach (var f in selfMeta.Files)
            {
                string norm = NormalizeMetadataKey(f ?? "");
                if (!IsPlainBa2ModKey(norm)) continue;
                if (string.Equals(StripDisabledKey(norm), selfFile, StringComparison.OrdinalIgnoreCase)) continue;
                string? poolMatch = poolKeys.FirstOrDefault(p =>
                    string.Equals(StripDisabledKey(p), StripDisabledKey(norm), StringComparison.OrdinalIgnoreCase));
                if (poolMatch != null) partners.Add(poolMatch);
            }
            if (partners.Count > 0) return partners.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var (stem, selfIsTexture) = GetBa2PartnerStem(selfFile);
        var group = poolKeys
            .Where(p => IsPlainBa2ModKey(p) &&
                        !string.Equals(StripDisabledKey(p), selfFile, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Key: p, Info: GetBa2PartnerStem(StripDisabledKey(p))))
            .Where(p => p.Info.Stem == stem)
            .ToList();

        var textures = group.Where(g => g.Info.IsTexture).Select(g => g.Key).ToList();
        var mains = group.Where(g => !g.Info.IsTexture).Select(g => g.Key).ToList();
        if (selfIsTexture)
        {
            if (mains.Count == 1)
            {
                partners.Add(mains[0]);
                partners.AddRange(textures);
            }
        }
        else
        {
            partners.AddRange(textures);
        }

        if (partners.Count == 0 && selfMeta?.NexusFileId is long fileId)
        {
            foreach (var p in poolKeys)
            {
                if (!IsPlainBa2ModKey(p)) continue;
                if (string.Equals(StripDisabledKey(p), selfFile, StringComparison.OrdinalIgnoreCase)) continue;
                if (metadata.TryGetValue(p, out var pm) && pm != null && !pm.IsBundle && !pm.IsLoose &&
                    pm.NexusFileId == fileId && pm.NexusModId == selfMeta.NexusModId)
                {
                    partners.Add(p);
                }
            }
        }

        return partners.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> BuildPartnerPoolFromListKeys(IEnumerable<string> listKeys, Dictionary<string, ModMetadata> metadata)
    {
        return listKeys
            .Select(NormalizeMetadataKey)
            .Where(k => IsPlainBa2ModKey(k) && !IsVanillaFile(Path.GetFileName(k)))
            .Where(k => !(metadata.TryGetValue(k, out var m) && m != null && (m.IsBundle || m.IsLoose)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> GetBundlePartnersForList(
        string normalizedFile,
        bool isBundle,
        bool isLoose,
        IReadOnlyCollection<string> pool,
        Dictionary<string, ModMetadata> metadata)
    {
        if (isBundle || isLoose || !IsPlainBa2ModKey(normalizedFile)) return new List<string>();
        return FindBa2Partners(normalizedFile, pool, metadata);
    }

    private List<string> CollectPlainBa2PoolKeys(Dictionary<string, ModMetadata> metadata)
    {
        var pool = new List<string>();
        string dataPath = GetActiveDataPath();
        if (!string.IsNullOrWhiteSpace(dataPath) && Directory.Exists(dataPath))
        {
            foreach (var f in Directory.GetFiles(dataPath, "*.ba2"))
            {
                string name = Path.GetFileName(f);
                if (IsVanillaFile(name)) continue;
                pool.Add(name);
            }
        }
        if (!string.IsNullOrWhiteSpace(AppPaths.DisabledModsPath) && Directory.Exists(AppPaths.DisabledModsPath))
        {
            foreach (var f in Directory.GetFiles(AppPaths.DisabledModsPath, "*.ba2"))
                pool.Add($"Disabled/{Path.GetFileName(f)}");
        }

        return pool
            .Where(k => !(metadata.TryGetValue(k, out var m) && m != null && (m.IsBundle || m.IsLoose)))
            .ToList();
    }

    private static bool FailKeyed(out string error, out string errorKey, out object[]? errorArgs, string message, string key, params object[] args)
    {
        error = message;
        errorKey = key;
        errorArgs = args.Length > 0 ? args : null;
        return false;
    }

    public bool MoveModToBundles(string originalName, out string error, out string newModKey, out List<string> movedKeys, out string errorKey, out object[]? errorArgs)
    {
        error = "";
        errorKey = "";
        errorArgs = null;
        newModKey = "";
        movedKeys = new List<string>();
        try
        {
            string key = NormalizeMetadataKey(originalName ?? "");
            if (!IsPlainBa2ModKey(key))
                return FailKeyed(out error, out errorKey, out errorArgs, "Only BA2 mods can be moved to BA2 Studio.", "move_bundles_only_ba2");

            string dataPath = GetActiveDataPath();
            if (string.IsNullOrWhiteSpace(dataPath) || !Directory.Exists(dataPath))
                return FailKeyed(out error, out errorKey, out errorArgs, "Game Data folder is not configured or does not exist.", "move_bundles_data_missing");

            var metadata = LoadMetadata();
            string selfMetaKey = ResolveMetadataKey(metadata, key);
            if (!string.IsNullOrEmpty(selfMetaKey) && metadata.TryGetValue(selfMetaKey, out var selfMeta) &&
                selfMeta != null && (selfMeta.IsBundle || selfMeta.IsLoose))
                return FailKeyed(out error, out errorKey, out errorArgs, "This mod is already a bundle or loose pack.", "move_bundles_already");

            var pool = CollectPlainBa2PoolKeys(metadata);
            var group = new List<string> { key };
            group.AddRange(FindBa2Partners(key, pool, metadata));
            group = group.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var moves = new List<(string OldKey, string Src, string Dst, string FileName)>();
            foreach (string k in group)
            {
                string src = GetFullPath(k);
                if (string.IsNullOrEmpty(src) || !File.Exists(src))
                {
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                        return FailKeyed(out error, out errorKey, out errorArgs, $"Mod file not found: {k}", "move_bundles_file_missing", k);
                    continue;
                }
                string fileName = Path.GetFileName(src);
                string dst = Path.Combine(dataPath, fileName);
                if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(dst))
                    return FailKeyed(out error, out errorKey, out errorArgs, $"A file named '{fileName}' already exists in your Data folder.", "move_bundles_name_exists", fileName);
                moves.Add((k, src, dst, fileName));
            }

            bool wasEnabled = !key.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase);

            foreach (var move in moves)
            {
                if (!string.Equals(Path.GetFullPath(move.Src), Path.GetFullPath(move.Dst), StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(move.Src, move.Dst);
                    _logger($"[BUNDLE] Moved to bundles: '{move.OldKey}' -> Data folder");
                }
            }

            string primaryFile = moves
                .Select(m => m.FileName)
                .FirstOrDefault(n => !GetBa2PartnerStem(n).IsTexture)
                ?? moves[0].FileName;

            ModMetadata merged = new ModMetadata { Name = Path.GetFileNameWithoutExtension(primaryFile) };
            var keysToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var orderedMoves = moves
                .OrderBy(m => string.Equals(m.FileName, primaryFile, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
            bool first = true;
            foreach (var move in orderedMoves)
            {
                foreach (string candidate in new[] { move.OldKey, move.FileName, $"Disabled/{move.FileName}" })
                {
                    string resolved = metadata.ContainsKey(candidate) ? candidate : "";
                    if (string.IsNullOrEmpty(resolved)) continue;
                    keysToRemove.Add(resolved);
                    var entry = metadata[resolved];
                    if (entry == null) continue;
                    if (first)
                    {
                        merged = entry;
                        first = false;
                    }
                    else
                    {
                        merged = MergeMetadataEntries(merged, entry);
                    }
                }
                movedKeys.Add(move.OldKey);
            }

            foreach (string k in keysToRemove)
                metadata.Remove(k);

            merged.IsBundle = true;
            merged.IsLoose = false;
            merged.IsEnabled = wasEnabled;
            merged.Files = orderedMoves.Select(m => m.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            metadata[primaryFile] = merged;
            SaveMetadata(metadata);

            newModKey = primaryFile;
            SyncArchiveListToCustomIni();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not move mod to BA2 Studio: {ex.Message}";
            errorKey = "mod_move_to_bundles_failed";
            errorArgs = new object[] { ex.Message };
            return false;
        }
    }

    private bool IsBundleFilesTarget(Dictionary<string, ModMetadata> metadata, string norm)
    {
        string bare = StripDisabledKey(norm);
        if (bare.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase)) return true;
        string key = ResolveMetadataKey(metadata, norm);
        if (!string.IsNullOrEmpty(key) && IsLoosePackKey(metadata, key)) return false;
        return IsBundleModKey(metadata, string.IsNullOrEmpty(key) ? norm : key);
    }

    private List<(string Rel, string AbsolutePath)> ResolveBundleArchives(BundleEditTarget bundle)
    {
        var archives = new List<(string Rel, string AbsolutePath)>();
        foreach (string file in bundle.OldFiles)
        {
            try
            {
                archives.Add((file, ResolveArchiveOnDisk(file)));
            }
            catch (FileNotFoundException)
            {
                _logger($"[BUNDLE] Bundle archive missing on disk: {file}");
            }
        }
        return archives;
    }

    private AddFilesResult AddFilesToBundle(string norm, IReadOnlyList<string> sources, Action<ImportProgress>? progressReporter)
    {
        var result = new AddFilesResult { OldKey = norm, NewKey = norm };
        AddFilesResult Fail(string message, string? key = null, object[]? args = null)
        {
            result.Ok = false;
            result.Error = message;
            _logger($"[ADDFILES] {message}");
            ReportStatus("error", message, key, args);
            return result;
        }

        var bundle = PrepareBundleEdit(norm, out string prepError);
        if (bundle == null)
            return string.Equals(prepError, "This mod is not a bundle.", StringComparison.Ordinal)
                ? Fail(prepError, "bundle_not_a_bundle")
                : Fail(prepError);
        result.OldKey = bundle.Key;
        result.NewKey = bundle.Key;

        var archives = ResolveBundleArchives(bundle);
        if (archives.Count == 0) return Fail($"Bundle archives not found: {bundle.Key}", "bundle_archives_missing", new object[] { bundle.Key });

        var progress = new ImportProgressContext(sources.Count + 2, progressReporter);
        progressReporter?.Invoke(new ImportProgress { Completed = 0, Total = sources.Count + 2, Percent = 0, Stage = "starting", CurrentItem = "" });

        string tempRoot = Path.Combine(Path.GetTempPath(), "F76Manager_AddFiles_" + Guid.NewGuid().ToString("N"));
        string incomingRoot = Path.Combine(tempRoot, "incoming");
        string workRoot = Path.Combine(tempRoot, "work");
        string gnrlDir = Path.Combine(tempRoot, "gnrl");
        string dx10Dir = Path.Combine(tempRoot, "dx10");
        string outDir = Path.Combine(tempRoot, "out");
        foreach (var dir in new[] { incomingRoot, workRoot, outDir })
            Directory.CreateDirectory(dir);

        try
        {
            var incoming = CollectIncomingFiles(sources, incomingRoot, progress, new List<string>(), out string collectError);
            if (incoming == null) return Fail(collectError);
            if (incoming.Count == 0) return Fail("No files were found to add.", "bundle_add_files_none");

            foreach (var (_, abs) in archives)
            {
                _logger($"[ADDFILES] Extracting bundle archive {abs}");
                BA2Utility.Extract(abs, workRoot, _logger);
            }

            foreach (var (rel, abs) in incoming)
            {
                string dest = Path.Combine(workRoot, rel.Replace("/", "\\"));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(abs, dest, overwrite: true);
                _logger($"[ADDFILES] Add/replace in bundle: {rel}");
            }
            progress.CompleteOne("processing", bundle.BaseName);

            foreach (var file in Directory.GetFiles(workRoot, "*", SearchOption.AllDirectories))
            {
                bool isDds = file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
                string dest = Path.Combine(isDds ? dx10Dir : gnrlDir, Path.GetRelativePath(workRoot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest);
            }

            var packed = new List<(string Packed, string Target, string Name)>();
            if (Directory.Exists(gnrlDir))
            {
                string name = bundle.BaseName + ".ba2";
                string packedPath = Path.Combine(outDir, name);
                BA2Utility.Pack(gnrlDir, packedPath, _logger, "GNRL");
                packed.Add((packedPath, Path.Combine(bundle.OutputDir, name), name));
            }
            if (Directory.Exists(dx10Dir))
            {
                string name = bundle.BaseName + " - Textures.ba2";
                string packedPath = Path.Combine(outDir, name);
                BA2Utility.Pack(dx10Dir, packedPath, _logger, "DX10");
                packed.Add((packedPath, Path.Combine(bundle.OutputDir, name), name));
            }
            if (packed.Count == 0) return Fail("Repacking the bundle produced no archives.", "bundle_repack_empty");

            foreach (var (packedPath, targetPath, _) in packed)
                File.Move(packedPath, targetPath, overwrite: true);

            result.NewKey = FinishBundleEdit(bundle, packed.Select(p => p.Name).ToList());
            result.Ok = true;
            result.FileCount = incoming.Count;
            _logger($"[ADDFILES] Added {incoming.Count} file(s) to bundle {result.NewKey}.");
            ReportStatus("success", $"Added {incoming.Count} file(s) to {bundle.BaseName}", "bundle_add_files_success", new object[] { incoming.Count, bundle.BaseName });
            return result;
        }
        catch (Exception ex)
        {
            return Fail($"Adding files to the bundle failed: {ex.Message}", "bundle_add_files_failed", new object[] { ex.Message });
        }
        finally
        {
            progress.CompleteAll("");
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); }
            catch (Exception cleanupEx) { _logger($"[ADDFILES] Temp cleanup failed: {cleanupEx.Message}"); }
        }
    }

    public BundleEditTarget? PrepareBundleEdit(string bundleKey, out string error)
    {
        error = "";
        try
        {
            string key = NormalizeMetadataKey(bundleKey ?? "");
            var metadata = LoadMetadata();
            string metaKey = ResolveMetadataKey(metadata, key);
            bool inBundlesFolder = key.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase);
            ModMetadata? meta = !string.IsNullOrEmpty(metaKey) && metadata.TryGetValue(metaKey, out var m) ? m : null;
            if (!(meta?.IsBundle ?? false) && !inBundlesFolder)
            {
                error = "This mod is not a bundle.";
                return null;
            }

            string primaryPath = ResolveArchiveOnDisk(string.IsNullOrEmpty(metaKey) ? key : metaKey);
            string sourceDir = Path.GetDirectoryName(primaryPath) ?? "";
            try { Directory.CreateDirectory(AppPaths.LiveBundlesPath); }
            catch (Exception dirEx) { _logger($"[BUNDLE] Could not create the shared Bundles folder: {dirEx.Message}"); }
            string baseName = Path.GetFileNameWithoutExtension(primaryPath);
            const string textureSuffix = " - Textures";
            if (baseName.EndsWith(textureSuffix, StringComparison.OrdinalIgnoreCase))
                baseName = baseName.Substring(0, baseName.Length - textureSuffix.Length);

            var oldFiles = (meta?.Files ?? new List<string>())
                .Select(f => NormalizeMetadataKey(f ?? ""))
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();
            if (oldFiles.Count == 0) oldFiles.Add(string.IsNullOrEmpty(metaKey) ? key : metaKey);

            return new BundleEditTarget
            {
                Key = string.IsNullOrEmpty(metaKey) ? key : metaKey,
                OutputDir = AppPaths.LiveBundlesPath,
                SourceDir = sourceDir,
                BaseName = baseName,
                OldFiles = oldFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public string FinishBundleEdit(BundleEditTarget target, List<string> generatedFiles)
    {
        var metadata = LoadMetadata();
        string oldKey = ResolveMetadataKey(metadata, target.Key);
        ModMetadata meta = !string.IsNullOrEmpty(oldKey) && metadata.TryGetValue(oldKey, out var existing) && existing != null
            ? existing
            : new ModMetadata { Name = target.BaseName, Author = "F76 Manager", IsEnabled = false };

        var generatedNames = new HashSet<string>(generatedFiles, StringComparer.OrdinalIgnoreCase);
        foreach (string old in target.OldFiles)
        {
            string fileName = Path.GetFileName(old);
            if (generatedNames.Contains(fileName)) continue;
            string stale = Path.Combine(target.OutputDir, fileName);
            try
            {
                if (File.Exists(stale))
                {
                    File.Delete(stale);
                    _logger($"[BUNDLE] Removed archive no longer produced by bundle: {fileName}");
                }
            }
            catch (Exception ex)
            {
                _logger($"[BUNDLE] Could not remove old bundle archive '{fileName}': {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(target.SourceDir))
        {
            string sourceFull = "";
            string outputFull = "";
            try
            {
                sourceFull = Path.GetFullPath(target.SourceDir);
                outputFull = Path.GetFullPath(target.OutputDir);
            }
            catch { }

            if (!string.IsNullOrEmpty(sourceFull) &&
                !string.Equals(sourceFull, outputFull, StringComparison.OrdinalIgnoreCase))
            {
                foreach (string old in target.OldFiles)
                {
                    string fileName = Path.GetFileName(old);
                    if (string.IsNullOrWhiteSpace(fileName)) continue;
                    string previous = Path.Combine(target.SourceDir, fileName);
                    try
                    {
                        if (!File.Exists(previous)) continue;
                        File.Delete(previous);
                        _logger($"[BUNDLE] Moved creation archive out of the platform folder: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        _logger($"[BUNDLE] Could not remove the old creation archive '{fileName}': {ex.Message}");
                    }
                }
            }
        }

        string prefix = target.Key.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase) ? "Bundles/" : "";
        string keepFile = generatedFiles.FirstOrDefault(f =>
            string.Equals(f, Path.GetFileName(target.Key), StringComparison.OrdinalIgnoreCase)) ?? generatedFiles[0];
        string newKey = prefix + keepFile;

        if (!string.IsNullOrEmpty(oldKey) && !string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase))
            metadata.Remove(oldKey);

        meta.IsBundle = true;
        meta.IsLoose = false;
        meta.Files = generatedFiles.Select(f => prefix + f).ToList();
        metadata[newKey] = meta;
        SaveMetadata(metadata);
        SyncArchiveListToCustomIni();
        return newKey;
    }
}
