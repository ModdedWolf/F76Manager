namespace F76ManagerApp.Managers;

public partial class ModManager
{
    public sealed class AddFilesResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string OldKey { get; set; } = "";
        public string NewKey { get; set; } = "";
        public int FileCount { get; set; }
    }

    public AddFilesResult AddFilesToMod(string modName, IReadOnlyList<string> sources, Action<ImportProgress>? progressReporter = null)
    {
        var result = new AddFilesResult();
        string norm = NormalizeMetadataKey(modName ?? "");
        result.OldKey = norm;
        result.NewKey = norm;

        AddFilesResult Fail(string message)
        {
            result.Ok = false;
            result.Error = message;
            _logger($"[ADDFILES] {message}");
            _statusReporter("error", message);
            return result;
        }

        if (string.IsNullOrWhiteSpace(norm)) return Fail("Missing mod name.");
        if (sources == null || sources.Count == 0) return Fail("No files were selected.");

        if (VirtualModMode) EnsureActiveDataFolders();

        var metadata = LoadMetadata();
        if (!TryResolveModFilesTarget(metadata, norm, out var target, out string resolveError))
            return Fail(resolveError);

        string key = target.Key;
        string newKey = target.NewKey;
        string packName = target.PackName;
        bool isLoose = target.IsLoose;
        bool enabled = target.Enabled;
        ModMetadata meta = target.Meta;
        var ownedBa2 = target.OwnedBa2;
        var keepRels = target.KeepRels;

        if (!isLoose && metadata.ContainsKey(newKey))
            return Fail($"A mod named {newKey} already exists. Rename one of them first.");

        string loosePackSafeKey = GetLoosePackSafeKey(newKey);
        string disabledRoot = Path.Combine(AppPaths.DisabledModsPath ?? "", "Loose", loosePackSafeKey);
        string destData = enabled ? GetActiveDataPath() : disabledRoot;
        string destStrings = enabled ? GetActiveStringsPath() : Path.Combine(disabledRoot, "Strings");

        var progress = new ImportProgressContext(sources.Count + 2, progressReporter);
        progressReporter?.Invoke(new ImportProgress { Completed = 0, Total = sources.Count + 2, Percent = 0, Stage = "starting", CurrentItem = "" });

        string tempRoot = Path.Combine(Path.GetTempPath(), "F76Manager_AddFiles_" + Guid.NewGuid().ToString("N"));
        string incomingRoot = Path.Combine(tempRoot, "incoming");
        string workRoot = Path.Combine(tempRoot, "work");
        string rollbackRoot = Path.Combine(tempRoot, "rollback");
        Directory.CreateDirectory(incomingRoot);
        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(rollbackRoot);

        var movedBa2 = new List<(string Original, string Backup)>();
        var createdFiles = new List<string>();
        var overwrittenFiles = new List<(string Original, string Backup)>();

        try
        {
            var incoming = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var emptyDirs = new List<string>();
            for (int i = 0; i < sources.Count; i++)
            {
                string src = sources[i];
                string display = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string sub = Path.Combine(incomingRoot, i.ToString());
                Directory.CreateDirectory(sub);

                if (Directory.Exists(src))
                {
                    CopyDirectory(src, Path.Combine(sub, display));
                }
                else if (File.Exists(src))
                {
                    string ext = Path.GetExtension(src).ToLowerInvariant();
                    if (ArchiveContainerExtensions.Contains(ext))
                    {
                        if (!TryExtractArchiveToDirectory(src, ext, sub, out string extractError))
                            return Fail(extractError);
                    }
                    else if (ext == ".ba2")
                    {
                        BA2Utility.Extract(src, sub, _logger);
                    }
                    else
                    {
                        if (ext != ".dll" && !IsImportJunkPath(display))
                            incoming[NormalizeStaticRel(display)] = src;
                        progress.CompleteOne("processing", display);
                        continue;
                    }
                }
                else
                {
                    return Fail($"Not found: {src}");
                }

                var normalized = CollectNormalizedExtractedFiles(sub, out var dirs);
                foreach (var (abs, rel) in normalized)
                {
                    string ext = Path.GetExtension(rel).ToLowerInvariant();
                    if (ext is ".dll" or ".ba2") continue;
                    incoming[NormalizeStaticRel(rel)] = abs;
                }
                emptyDirs.AddRange(dirs);
                progress.CompleteOne("processing", display);
            }

            if (incoming.Count == 0 && emptyDirs.Count == 0)
                return Fail("No files were found to add.");

            foreach (var (_, abs) in ownedBa2)
            {
                _logger($"[ADDFILES] Extracting existing archive {abs}");
                BA2Utility.Extract(abs, workRoot, _logger);
            }

            foreach (var (rel, abs) in incoming)
            {
                string dest = Path.Combine(workRoot, rel.Replace("/", "\\"));
                string? dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(abs, dest, overwrite: true);
                _logger($"[ADDFILES] Add/replace: {rel}");
            }
            progress.CompleteOne("processing", packName);

            foreach (var (_, abs) in ownedBa2)
            {
                string backup = Path.Combine(rollbackRoot, movedBa2.Count + "_" + Path.GetFileName(abs));
                File.Move(abs, backup);
                movedBa2.Add((abs, backup));
            }

            var addedRels = new List<string>();
            string workFull = Path.GetFullPath(workRoot);
            foreach (var file in Directory.GetFiles(workRoot, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(workFull, file).Replace("\\", "/");
                var (destPath, listRel) = ResolveLooseInstallDestination(rel, destData, destStrings);
                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                if (File.Exists(destPath))
                {
                    string backup = Path.Combine(rollbackRoot, "overwritten_" + overwrittenFiles.Count + "_" + Path.GetFileName(destPath));
                    File.Copy(destPath, backup, overwrite: true);
                    overwrittenFiles.Add((destPath, backup));
                }
                else
                {
                    createdFiles.Add(destPath);
                }
                File.Copy(file, destPath, overwrite: true);
                addedRels.Add(listRel);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newFiles = new List<string>();
            foreach (var rel in keepRels.Concat(addedRels))
            {
                string n = NormalizeMetadataKey(rel);
                if (!string.IsNullOrWhiteSpace(n) && seen.Add(n)) newFiles.Add(n);
            }

            meta.Files = newFiles;
            meta.IsLoose = true;
            meta.Name = packName;
            meta.IsEnabled = enabled;
            if (enabled && emptyDirs.Count > 0)
            {
                meta.Directories = (meta.Directories ?? new List<string>())
                    .Concat(EnsureModDirectories(destData, emptyDirs))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (!isLoose)
            {
                string fileName = Path.GetFileName(key);
                metadata.Remove(key);
                metadata.Remove(fileName);
                metadata.Remove($"Disabled/{fileName}");
            }
            metadata[newKey] = meta;

            bool packed;
            string packError;
            try
            {
                packed = TryPackLooseModToBa2(newKey, out packError, metadata);
            }
            catch (Exception packEx)
            {
                packed = false;
                packError = packEx.Message;
            }

            if (!packed)
            {
                RollbackAddFiles(createdFiles, overwrittenFiles, movedBa2, destData, packName);
                return Fail(string.IsNullOrWhiteSpace(packError) ? "Repacking the mod failed." : $"Repacking the mod failed: {packError}");
            }

            movedBa2.Clear();
            result.Ok = true;
            result.OldKey = key;
            result.NewKey = newKey;
            result.FileCount = incoming.Count;
            _logger($"[ADDFILES] Added {incoming.Count} file(s) to {newKey}" +
                    (string.Equals(key, newKey, StringComparison.OrdinalIgnoreCase) ? "." : $" (converted from {key})."));
            _statusReporter("success", $"Added {incoming.Count} file(s) to {packName}");

            try { SyncArchiveListToCustomIni(); }
            catch (Exception ex) { _logger($"[ADDFILES] Archive list refresh failed: {ex.Message}"); }

            return result;
        }
        catch (Exception ex)
        {
            RollbackAddFiles(createdFiles, overwrittenFiles, movedBa2, destData, packName);
            return Fail($"Adding files failed: {ex.Message}");
        }
        finally
        {
            progress.CompleteAll("");
            try
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            }
            catch (Exception cleanupEx)
            {
                _logger($"[ADDFILES] Temp cleanup failed: {cleanupEx.Message}");
            }
        }
    }

    private void RollbackAddFiles(
        List<string> createdFiles,
        List<(string Original, string Backup)> overwrittenFiles,
        List<(string Original, string Backup)> movedBa2,
        string destData,
        string packName)
    {
        foreach (var path in createdFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { _logger($"[ADDFILES] Rollback delete failed '{path}': {ex.Message}"); }
        }
        foreach (var (original, backup) in overwrittenFiles)
        {
            try { if (File.Exists(backup)) File.Copy(backup, original, overwrite: true); }
            catch (Exception ex) { _logger($"[ADDFILES] Rollback restore failed '{original}': {ex.Message}"); }
        }

        var restored = new HashSet<string>(movedBa2.Select(m => m.Original), StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { packName + ".ba2", packName + " - Textures.ba2" })
        {
            string partial = Path.Combine(destData, name);
            if (restored.Contains(partial)) continue;
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch (Exception ex) { _logger($"[ADDFILES] Rollback cleanup failed '{partial}': {ex.Message}"); }
        }

        foreach (var (original, backup) in movedBa2)
        {
            try { if (File.Exists(backup)) File.Move(backup, original, overwrite: true); }
            catch (Exception ex) { _logger($"[ADDFILES] Rollback restore failed '{original}': {ex.Message}"); }
        }
        movedBa2.Clear();
        _logger("[ADDFILES] Rolled back partial changes.");
    }

    private sealed class ModFilesTarget
    {
        public string Key { get; init; } = "";
        public string NewKey { get; init; } = "";
        public string PackName { get; init; } = "";
        public bool IsLoose { get; init; }
        public bool Enabled { get; init; }
        public ModMetadata Meta { get; init; } = new();
        public List<(string Rel, string AbsolutePath)> OwnedBa2 { get; init; } = new();
        public List<string> KeepRels { get; init; } = new();
    }

    private bool TryResolveModFilesTarget(
        Dictionary<string, ModMetadata> metadata,
        string norm,
        out ModFilesTarget target,
        out string error)
    {
        target = new ModFilesTarget();
        error = "";
        const string unsupported = "Files can only be added to loose mods and BA2 mods.";

        string bareNorm = norm.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase)
            ? norm.Substring("Disabled/".Length)
            : norm;
        if (IsGameRootInjectorListPath(norm) ||
            bareNorm.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase) ||
            bareNorm.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase) ||
            bareNorm.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase) ||
            IsConfigListKey(bareNorm))
        {
            error = unsupported;
            return false;
        }

        string key = ResolveMetadataKey(metadata, norm);
        bool isLoose = !string.IsNullOrEmpty(key) && IsLoosePackKey(metadata, key);
        if (!isLoose && IsBundleModKey(metadata, string.IsNullOrEmpty(key) ? norm : key))
        {
            error = unsupported;
            return false;
        }

        ModMetadata? meta = null;
        if (!string.IsNullOrEmpty(key)) metadata.TryGetValue(key, out meta);

        if (isLoose)
        {
            if (meta == null)
            {
                error = $"Loose mod not found: {norm}";
                return false;
            }
            string packName = SanitizeLoosePackName(!string.IsNullOrWhiteSpace(meta.Name) ? meta.Name : key);
            bool enabled = meta.IsEnabled;
            string safeKey = GetLoosePackSafeKey(key);
            string disabledRootForLookup = Path.Combine(AppPaths.DisabledModsPath ?? "", "Loose", safeKey);
            var ownedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                packName + ".ba2",
                packName + " - Textures.ba2"
            };

            var ownedBa2 = new List<(string Rel, string AbsolutePath)>();
            var keepRels = new List<string>();
            foreach (var raw in meta.Files ?? new List<string>())
            {
                string rel = NormalizeMetadataKey(raw ?? "");
                if (string.IsNullOrWhiteSpace(rel)) continue;
                if (ownedNames.Contains(Path.GetFileName(rel)) &&
                    TryResolveLoosePackFilePath(rel, safeKey, enabled, GetActiveDataPath(), GetActiveStringsPath(),
                        disabledRootForLookup, out string abs) &&
                    File.Exists(abs))
                {
                    ownedBa2.Add((rel, abs));
                    continue;
                }
                keepRels.Add(rel);
            }

            target = new ModFilesTarget
            {
                Key = key,
                NewKey = key,
                PackName = packName,
                IsLoose = true,
                Enabled = enabled,
                Meta = meta,
                OwnedBa2 = ownedBa2,
                KeepRels = keepRels
            };
            return true;
        }

        string lookupKey = string.IsNullOrEmpty(key) ? norm : key;
        string fileName = Path.GetFileName(lookupKey);
        if (!fileName.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase))
        {
            error = unsupported;
            return false;
        }

        string ba2Abs = GetFullPath(lookupKey);
        if (!File.Exists(ba2Abs))
        {
            string alt = lookupKey.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase)
                ? GetFullPath(fileName)
                : GetFullPath($"Disabled/{fileName}");
            if (!File.Exists(alt))
            {
                error = $"Mod file not found: {fileName}";
                return false;
            }
            ba2Abs = alt;
        }

        string disabledFull = Path.GetFullPath(AppPaths.DisabledModsPath ?? "")
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string plainPackName = SanitizeLoosePackName(Path.GetFileNameWithoutExtension(fileName));

        target = new ModFilesTarget
        {
            Key = lookupKey,
            NewKey = MakeLooseMetadataKey(plainPackName),
            PackName = plainPackName,
            IsLoose = false,
            Enabled = !Path.GetFullPath(ba2Abs).StartsWith(disabledFull, StringComparison.OrdinalIgnoreCase),
            Meta = meta ?? new ModMetadata { Name = Path.GetFileNameWithoutExtension(fileName) },
            OwnedBa2 = new List<(string Rel, string AbsolutePath)> { (lookupKey, ba2Abs) },
            KeepRels = new List<string>()
        };
        return true;
    }

    public List<(string Path, uint UnpackedSize)> ListModContents(string modName, out string error)
    {
        var entries = new List<(string Path, uint UnpackedSize)>();
        var metadata = LoadMetadata();
        if (!TryResolveModFilesTarget(metadata, NormalizeMetadataKey(modName ?? ""), out var target, out error))
            return entries;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archiveErrors = new List<string>();
        foreach (var (rel, abs) in target.OwnedBa2)
        {
            try
            {
                foreach (var entry in BA2Utility.ListEntries(abs, _logger).Entries)
                {
                    string path = (entry.Path ?? "").Replace("/", "\\");
                    if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
                        entries.Add((path, entry.UnpackedSize));
                }
            }
            catch (Exception ex)
            {
                archiveErrors.Add($"{Path.GetFileName(abs)}: {ex.Message}");
            }
        }

        foreach (var rel in target.KeepRels)
        {
            string path = rel.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase)
                ? rel.Substring("Disabled/".Length)
                : rel;
            path = path.Replace("/", "\\");
            if (seen.Add(path)) entries.Add((path, 0));
        }

        if (archiveErrors.Count > 0)
            error = "Could not read " + string.Join("; ", archiveErrors);
        return entries;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destDir, Path.GetRelativePath(sourceDir, file)), overwrite: true);
    }
}
