using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Globalization;
using F76ManagerApp.Managers;

namespace F76ManagerApp;

public partial class Form1
{
    private static readonly HashSet<string> EditableModConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json",
        ".txt",
        ".ini"
    };

    private readonly object _importDropLock = new();
    private bool _isImportRunning = false;
    private readonly List<string> _queuedImportFiles = new();

    private static string BuildNexusDownloadArchivePath(long modId, string fileName)
    {
        string downloadsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Nexus", "downloads");
        return Path.GetFullPath(Path.Combine(downloadsDir, $"{modId}_{fileName}"));
    }

    private PendingNexusImport BuildPendingNexusImport(
        long modId,
        long fileId,
        string fileName,
        string fileVersion,
        long? fileUploaded,
        string modName = "",
        string author = "",
        string details = "",
        string category = "",
        string replaceOriginalName = "")
    {
        return new PendingNexusImport
        {
            ModId = modId,
            FileId = fileId,
            FileVersion = fileVersion ?? "",
            FileUploaded = fileUploaded,
            ModName = modName ?? "",
            Author = author ?? "",
            Details = details ?? "",
            Category = category ?? "",
            ExpectedArchivePath = BuildNexusDownloadArchivePath(modId, fileName),
            ReplaceOriginalName = replaceOriginalName ?? ""
        };
    }

    private static string GetPendingReplaceOriginalName(PendingNexusImport? existing, long modId)
    {
        if (existing == null || modId <= 0) return "";
        if (existing.ModId != modId) return "";
        return existing.ReplaceOriginalName ?? "";
    }

    private void CompleteModUpdateReplace(string replaceOriginalName, IReadOnlyList<string> importedKeys, bool fromBulkUpdate)
    {
        if (string.IsNullOrWhiteSpace(replaceOriginalName) || importedKeys == null || importedKeys.Count == 0)
            return;

        var replacedKeys = _modManager.ReplaceModAfterNexusUpdate(replaceOriginalName, importedKeys);
        if (replacedKeys.Count > 0)
        {
            ReplaceModInActiveProfile(replaceOriginalName, replacedKeys);
            LogActivity($"[NEXUS] Update replaced '{replaceOriginalName}' with {replacedKeys.Count} file(s).");
            SendStatusMessage(
                "success",
                $"Mod update installed. Removed previous version of {Path.GetFileName(replaceOriginalName)}.",
                "mod_update_replaced",
                new object[] { Path.GetFileName(replaceOriginalName) });
        }

        _modUpdateCache.Clear();
        if (fromBulkUpdate && _bulkUpdateInProgress)
            AdvanceBulkUpdateQueue(replacedKeys.Count > 0);
    }

    private void PromptModUpdateReplace(string replaceOriginalName, IReadOnlyList<string> importedKeys)
    {
        SendMessageToWeb(JsonSerializer.Serialize(new
        {
            type = "CONFIRM_MOD_UPDATE_REPLACE",
            originalName = replaceOriginalName,
            importedKeys,
            displayName = Path.GetFileName(replaceOriginalName.Replace("Disabled/", "").Replace("Bundles/", ""))
        }));
    }

    private bool _collectionImportInProgress;

    private sealed class CollectionImportProgress
    {
        public long TotalBytes;
        public long DownloadedBytes;
        public long CurrentModBaseBytes;
        public int CurrentIndex;
        public int TotalMods;
        public int LastReportedPercent = -1;
        public readonly HashSet<long> CountedUnknownFileIds = new();
    }

    private CollectionImportProgress? _collectionProgress;
    private readonly object _collectionProgressLock = new();

    private void SendCollectionDownloadProgress(bool force = false)
    {
        CollectionImportProgress? progress;
        lock (_collectionProgressLock)
            progress = _collectionProgress;
        if (progress == null)
            return;

        int percent;
        int current;
        int total;
        long downloaded;
        long totalBytes;

        lock (_collectionProgressLock)
        {
            current = progress.CurrentIndex;
            total = progress.TotalMods;
            downloaded = progress.DownloadedBytes;
            totalBytes = progress.TotalBytes;

            if (totalBytes > 0)
                percent = (int)Math.Min(100, downloaded * 100L / totalBytes);
            else if (total > 0)
                percent = current > 0 ? (int)Math.Min(99, (current - 1) * 100L / total) : 0;
            else
                percent = 0;

            if (!force && percent == progress.LastReportedPercent && percent < 100)
                return;
            progress.LastReportedPercent = percent;
        }

        SendMessageToWeb(JsonSerializer.Serialize(new
        {
            type = "NEXUS_COLLECTION_DOWNLOAD_PROGRESS",
            percent,
            downloadedBytes = downloaded,
            totalBytes,
            current,
            total
        }));
    }

    // --- Single (non-collection) Nexus download: persistent banner + cancellation + logging ---

    private CancellationTokenSource? _nexusDownloadCts;
    private readonly object _nexusDownloadLock = new();

    /// <summary>Starts tracking a single Nexus download: cancels any previous one, shows an initial
    /// persistent banner, and returns the token to pass to DownloadMod.</summary>
    private CancellationToken BeginNexusDownload(string fileName)
    {
        CancellationToken token;
        lock (_nexusDownloadLock)
        {
            try { _nexusDownloadCts?.Cancel(); } catch { /* ignore */ }
            _nexusDownloadCts?.Dispose();
            _nexusDownloadCts = new CancellationTokenSource();
            token = _nexusDownloadCts.Token;
        }

        SafeInvoke(() => SendNexusDownloadBanner(
            $"Preparing download: {fileName}…", percent: 0, cancelable: true, done: false));
        return token;
    }

    /// <summary>Clears download tracking and removes the persistent banner from the UI.</summary>
    private void EndNexusDownload()
    {
        lock (_nexusDownloadLock)
        {
            _nexusDownloadCts?.Dispose();
            _nexusDownloadCts = null;
        }
        SafeInvoke(() => SendNexusDownloadBanner("", percent: 100, cancelable: false, done: true));
    }

    private void CancelNexusDownload()
    {
        lock (_nexusDownloadLock)
        {
            if (_nexusDownloadCts == null)
                return;
            LogActivity("[NEXUS] Cancel requested for active download.");
            try { _nexusDownloadCts.Cancel(); } catch { /* ignore */ }
        }
    }

    private void SendNexusDownloadBanner(string text, int percent, bool cancelable, bool done)
    {
        SendMessageToWeb(JsonSerializer.Serialize(new
        {
            type = "STATUS",
            status = new
            {
                type = "info",
                text,
                isProgress = true,
                progressId = "nexus-download",
                cancelable,
                percent = Math.Clamp(percent, 0, 100),
                done
            }
        }));
    }

    /// <summary>Marshals to the UI thread, swallowing errors during shutdown / disposed handle.</summary>
    private void SafeInvoke(Action action)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) { return; }
            this.Invoke(action);
        }
        catch { /* form closing / handle gone */ }
    }

    private NexusManager.DownloadProgressOptions BuildSingleDownloadProgressOptions(string fileName)
    {
        long? knownTotal = null;
        int lastPercent = -1;
        DateTime lastUi = DateTime.MinValue;
        DateTime lastLog = DateTime.MinValue;

        return new NexusManager.DownloadProgressOptions
        {
            SuppressInfoMessages = true,
            OnContentLengthDiscovered = len => { if (len > 0) knownTotal = len; },
            OnBytesProgress = (read, total) =>
            {
                long? effectiveTotal = (total.HasValue && total.Value > 0) ? total : knownTotal;
                int percent = (effectiveTotal.HasValue && effectiveTotal.Value > 0)
                    ? (int)Math.Min(100, read * 100L / effectiveTotal.Value)
                    : -1;

                var now = DateTime.UtcNow;

                // Periodic logging so a slow download leaves a visible trail in the activity log.
                bool shouldLog = percent >= 0
                    ? (percent / 5 != (lastPercent < 0 ? -1 : lastPercent) / 5)
                    : (now - lastLog).TotalSeconds >= 3;
                if (shouldLog)
                {
                    lastLog = now;
                    string sizePart = effectiveTotal.HasValue
                        ? $"{FormatSize(read)} / {FormatSize(effectiveTotal.Value)}"
                        : FormatSize(read);
                    LogActivity($"[NEXUS] Downloading {fileName}: {(percent >= 0 ? percent + "% " : "")}({sizePart})");
                }

                // Throttle UI updates to percent changes (or ~250ms for unknown-size files).
                bool uiDue = (percent >= 0 && percent != lastPercent) ||
                             (now - lastUi).TotalMilliseconds >= 250;
                if (!uiDue) return;
                lastUi = now;
                lastPercent = percent;

                string text = effectiveTotal.HasValue
                    ? (percent >= 0
                        ? $"Downloading {fileName}… {percent}% ({FormatSize(read)} / {FormatSize(effectiveTotal.Value)})"
                        : $"Downloading {fileName}… ({FormatSize(read)} / {FormatSize(effectiveTotal.Value)})")
                    : $"Downloading {fileName}… {FormatSize(read)}";

                SafeInvoke(() => SendNexusDownloadBanner(text, percent < 0 ? 0 : percent, cancelable: true, done: false));
            }
        };
    }

    /// <summary>Downloads a single Nexus file with a cancelable, persistent progress banner.</summary>
    private async Task DownloadSingleModWithProgressAsync(
        long modId, long fileId, string fileName, string? nxmKey = null, string? nxmExpires = null)
    {
        if (_nexusManager == null) return;

        CancellationToken token = BeginNexusDownload(fileName);
        try
        {
            var options = BuildSingleDownloadProgressOptions(fileName);
            await _nexusManager.DownloadMod(modId, fileId, fileName, nxmKey, nxmExpires, options, token);
        }
        finally
        {
            EndNexusDownload();
        }
    }

    private NexusManager.DownloadProgressOptions BuildCollectionDownloadProgressOptions(long fileId)
    {
        return new NexusManager.DownloadProgressOptions
        {
            SuppressInfoMessages = true,
            OnContentLengthDiscovered = contentLength =>
            {
                if (contentLength <= 0)
                    return;

                lock (_collectionProgressLock)
                {
                    if (_collectionProgress == null || _collectionProgress.CountedUnknownFileIds.Contains(fileId))
                        return;
                    _collectionProgress.CountedUnknownFileIds.Add(fileId);
                    _collectionProgress.TotalBytes += contentLength;
                }

                this.Invoke(SendCollectionDownloadProgress);
            },
            OnBytesProgress = (bytesReadThisFile, _) =>
            {
                lock (_collectionProgressLock)
                {
                    if (_collectionProgress == null)
                        return;
                    _collectionProgress.DownloadedBytes = _collectionProgress.CurrentModBaseBytes + bytesReadThisFile;
                }

                this.Invoke(SendCollectionDownloadProgress);
            }
        };
    }

    private async Task HandleNxmLinkAsync(string url)
    {
        if (_nexusManager == null) return;

        if (NexusManager.TryParseNxmCollectionLink(url, out var collectionSlug, out var collectionRevision))
        {
            _ = Task.Run(async () => await HandleCollectionImportAsync(collectionSlug, collectionRevision));
            return;
        }

        if (NexusManager.TryParseCollectionUrl(url, out collectionSlug, out collectionRevision))
        {
            _ = Task.Run(async () => await HandleCollectionImportAsync(collectionSlug, collectionRevision));
            return;
        }

        if (!NexusManager.TryParseNxmLink(url, out var modId, out var fileId, out var nxmKey, out var nxmExpires))
        {
            string message = url.Contains("collection", StringComparison.OrdinalIgnoreCase)
                ? "Unsupported collection link. Log in to Nexus Mods and try again, or paste the collection URL in Settings."
                : "Invalid NXM link.";
            string key = url.Contains("collection", StringComparison.OrdinalIgnoreCase)
                ? "nexus_collection_invalid_link"
                : "nexus_download_failed";
            SendStatusMessage("error", message, key);
            return;
        }

        SendStatusMessage("info", $"Processing NXM Link: Mod {modId}, File {fileId}...");

        await Task.Run(async () =>
        {
            var info = await _nexusManager.ResolveImportInfoAsync(modId, fileId);
            if (info == null)
            {
                this.Invoke(() => SendStatusMessage("error", "Could not resolve Nexus mod info.", "nexus_download_failed"));
                return;
            }

            info.NxmKey = nxmKey;
            info.NxmExpires = nxmExpires;

            this.Invoke(() =>
            {
                string replaceOriginalName = GetPendingReplaceOriginalName(_pendingNexusImport, info.ModId);
                _pendingNexusImport = BuildPendingNexusImport(
                    info.ModId,
                    info.FileId,
                    info.FileName,
                    info.FileVersion,
                    info.FileUploaded,
                    info.ModName,
                    info.Author,
                    info.Details,
                    info.Category,
                    replaceOriginalName: replaceOriginalName);
            });

            await DownloadSingleModWithProgressAsync(
                info.ModId,
                info.FileId,
                info.FileName,
                info.NxmKey,
                info.NxmExpires);
        });
    }

    private async Task HandleCollectionImportAsync(string slug, int? revision)
    {
        if (_nexusManager == null)
            return;

        if (_collectionImportInProgress)
        {
            SendStatusMessage("warning", "A collection import is already in progress.", "nexus_collection_in_progress");
            return;
        }

        if (!nexusLoggedIn)
        {
            SendStatusMessage("error", "Log in to Nexus Mods in Settings before importing a collection.", "nexus_collection_login_required");
            return;
        }

        _collectionImportInProgress = true;

        try
        {
            SendStatusMessage("info", "Fetching collection from Nexus...", "nexus_collection_fetching");

            var collection = await _nexusManager.FetchCollectionModsAsync(slug, revision);
            if (!string.IsNullOrWhiteSpace(collection.Error))
            {
                SendStatusMessage("error", collection.Error, "nexus_collection_fetch_failed");
                return;
            }

            if (collection.Mods.Count == 0)
            {
                SendStatusMessage("error", "Collection has no downloadable mods.", "nexus_collection_empty");
                return;
            }

            string collectionLabel = string.IsNullOrWhiteSpace(collection.CollectionName)
                ? collection.Slug
                : collection.CollectionName;

            var downloadQueue = new List<(NexusManager.CollectionModEntry Entry, NexusManager.NexusImportInfo Info, int CollectionIndex)>();
            long totalBytes = 0;
            int unknownSizeCount = 0;
            int failed = 0;
            int collectionIndex = 0;

            foreach (var entry in collection.Mods)
            {
                collectionIndex++;
                long modId = entry.ModId;
                long fileId = entry.FileId;

                if (modId <= 0)
                {
                    failed++;
                    LogError($"[NEXUS] Collection mod skipped: missing mod ID for file {fileId}.");
                    SendStatusMessage(
                        "warning",
                        $"Skipped a collection mod (file {fileId}) — missing mod ID.",
                        "nexus_collection_mod_skipped",
                        new object[] { fileId });
                    continue;
                }

                try
                {
                    var info = await _nexusManager.ResolveImportInfoAsync(modId, fileId);
                    if (info == null || info.ModId <= 0 || info.FileId <= 0)
                    {
                        failed++;
                        LogError($"[NEXUS] Collection mod skipped: could not resolve mod/file for file {fileId}.");
                        SendStatusMessage(
                            "warning",
                            $"Skipped a collection mod (file {fileId}) — could not resolve mod info.",
                            "nexus_collection_mod_skipped",
                            new object[] { fileId });
                        continue;
                    }

                    if (info.FileSizeBytes > 0)
                        totalBytes += info.FileSizeBytes;
                    else
                        unknownSizeCount++;

                    downloadQueue.Add((entry, info, collectionIndex));
                }
                catch (Exception ex)
                {
                    failed++;
                    LogError($"[NEXUS] Collection resolve failed (file {fileId}): {ex.Message}");
                    SendStatusMessage(
                        "warning",
                        $"Failed to queue mod file {fileId}: {ex.Message}",
                        "nexus_collection_mod_failed",
                        new object[] { fileId, ex.Message });
                }
            }

            if (unknownSizeCount > 0)
                LogActivity($"[NEXUS] Collection import: {unknownSizeCount} mod file(s) have unknown size; progress will update when downloads begin.");

            lock (_collectionProgressLock)
            {
                _collectionProgress = new CollectionImportProgress
                {
                    TotalBytes = totalBytes,
                    TotalMods = collection.Mods.Count
                };
            }

            SendMessageToWeb(JsonSerializer.Serialize(new
            {
                type = "NEXUS_COLLECTION_STARTED",
                collectionName = collectionLabel,
                slug = collection.Slug,
                revision = collection.RevisionNumber,
                total = collection.Mods.Count,
                totalBytes
            }));

            SendCollectionDownloadProgress(force: true);

            int queued = 0;

            foreach (var (entry, info, orderIndex) in downloadQueue)
            {
                long modId = info.ModId;
                long fileId = info.FileId;
                string fileName = string.IsNullOrWhiteSpace(entry.FileName) ? info.FileName : entry.FileName;
                string fileVersion = string.IsNullOrWhiteSpace(entry.FileVersion) ? info.FileVersion : entry.FileVersion;

                lock (_collectionProgressLock)
                {
                    if (_collectionProgress != null)
                        _collectionProgress.CurrentIndex = orderIndex;
                }
                SendCollectionDownloadProgress(force: true);

                try
                {
                    lock (_collectionProgressLock)
                    {
                        if (_collectionProgress != null)
                            _collectionProgress.CurrentModBaseBytes = _collectionProgress.DownloadedBytes;
                    }

                    this.Invoke(() =>
                    {
                        _pendingNexusImport = BuildPendingNexusImport(
                            modId,
                            fileId,
                            fileName,
                            fileVersion,
                            info.FileUploaded,
                            info.ModName,
                            info.Author,
                            info.Details,
                            info.Category);
                    });

                    var progressOptions = BuildCollectionDownloadProgressOptions(fileId);
                    await _nexusManager.DownloadMod(modId, fileId, fileName, progressOptions: progressOptions);
                    queued++;
                }
                catch (Exception ex)
                {
                    failed++;
                    LogError($"[NEXUS] Collection mod download failed (file {fileId}): {ex.Message}");
                    SendStatusMessage(
                        "warning",
                        $"Failed to queue mod file {fileId}: {ex.Message}",
                        "nexus_collection_mod_failed",
                        new object[] { fileId, ex.Message });
                }

                SendCollectionDownloadProgress(force: true);
            }

            SendCollectionDownloadProgress(force: true);

            lock (_collectionProgressLock)
            {
                if (_collectionProgress != null && _collectionProgress.TotalBytes > 0)
                    _collectionProgress.DownloadedBytes = _collectionProgress.TotalBytes;
            }
            SendCollectionDownloadProgress(force: true);

            SendMessageToWeb(JsonSerializer.Serialize(new
            {
                type = "NEXUS_COLLECTION_COMPLETE",
                collectionName = collectionLabel,
                queued,
                failed,
                total = collection.Mods.Count
            }));

            if (failed == 0)
            {
                SendStatusMessage(
                    "success",
                    $"Collection \"{collectionLabel}\" queued: {queued} mod{(queued == 1 ? "" : "s")} downloading.",
                    "nexus_collection_complete",
                    new object[] { collectionLabel, queued });
            }
            else
            {
                SendStatusMessage(
                    "warning",
                    $"Collection \"{collectionLabel}\" finished with {queued} queued and {failed} skipped/failed.",
                    "nexus_collection_complete_partial",
                    new object[] { collectionLabel, queued, failed });
            }

            PersistImportedCollection(collection, collectionLabel);
        }
        finally
        {
            _collectionImportInProgress = false;
            lock (_collectionProgressLock)
                _collectionProgress = null;
        }
    }

    private List<string> NormalizeImportPaths(List<string> files)
    {
        return files
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => {
                try { return Path.GetFullPath(f).Trim(); }
                catch (Exception ex) { LogError($"[IMPORT] Failed to normalize path '{f}': {ex.Message}"); return f.Trim(); }
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool TryGetString(JsonElement root, string propertyName, out string value, bool required = false)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            if (required) LogError($"[RCV] Missing required property '{propertyName}'.");
            return !required;
        }
        if (prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return true;
        }
        if (required) LogError($"[RCV] Invalid type for '{propertyName}'. Expected string.");
        return false;
    }

    private bool TryGetBool(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            LogError($"[RCV] Missing required property '{propertyName}'.");
            return false;
        }
        if (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
        {
            value = prop.GetBoolean();
            return true;
        }
        LogError($"[RCV] Invalid type for '{propertyName}'. Expected boolean.");
        return false;
    }

    private bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            LogError($"[RCV] Missing required property '{propertyName}'.");
            return false;
        }
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value))
        {
            return true;
        }
        LogError($"[RCV] Invalid type/value for '{propertyName}'. Expected int.");
        return false;
    }

    private bool TryGetInt64(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            LogError($"[RCV] Missing required property '{propertyName}'.");
            return false;
        }
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value))
        {
            return true;
        }
        LogError($"[RCV] Invalid type/value for '{propertyName}'. Expected long.");
        return false;
    }

    private bool TryGetArray(JsonElement root, string propertyName, out JsonElement arrayElement)
    {
        arrayElement = default;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            LogError($"[RCV] Missing required property '{propertyName}'.");
            return false;
        }
        if (prop.ValueKind == JsonValueKind.Array)
        {
            arrayElement = prop;
            return true;
        }
        LogError($"[RCV] Invalid type for '{propertyName}'. Expected array.");
        return false;
    }

    private List<string> GetStringList(JsonElement root, string propertyName)
    {
        if (!TryGetArray(root, propertyName, out var arr)) return new List<string>();
        return arr.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? string.Empty)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
    }

    private List<F76ManagerApp.Managers.Ba2PartialSelection> ParseBa2PartialList(JsonElement root)
    {
        var result = new List<F76ManagerApp.Managers.Ba2PartialSelection>();
        if (!TryGetArray(root, "ba2Partial", out var arr)) return result;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetString(item, "archive", out var archive) || string.IsNullOrWhiteSpace(archive)) continue;

            var paths = GetStringList(item, "paths");
            if (paths.Count == 0) continue;

            result.Add(new F76ManagerApp.Managers.Ba2PartialSelection
            {
                Archive = archive,
                Paths = paths
            });
        }

        return result;
    }

    private List<F76ManagerApp.Managers.FolderPartialSelection> ParseFolderPartialList(JsonElement root)
    {
        var result = new List<F76ManagerApp.Managers.FolderPartialSelection>();
        if (!TryGetArray(root, "folderPartial", out var arr)) return result;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetString(item, "folder", out var folder) || string.IsNullOrWhiteSpace(folder)) continue;

            var paths = GetStringList(item, "paths");
            if (paths.Count == 0) continue;

            result.Add(new F76ManagerApp.Managers.FolderPartialSelection
            {
                Folder = folder,
                Paths = paths
            });
        }

        return result;
    }

    private bool TryGetRgb(JsonElement root, out int r, out int g, out int b)
    {
        r = 0; g = 0; b = 0;
        return TryGetInt32(root, "r", out r) &&
               TryGetInt32(root, "g", out g) &&
               TryGetInt32(root, "b", out b);
    }

    private void HandleAddOrImportFilesMessage(JsonElement root, bool fallbackToPicker)
    {
        var files = GetStringList(root, "files");
        if (files.Count > 0)
        {
            this.Invoke(new Action(() => HandleAddModFiles(files)));
            return;
        }

        if (fallbackToPicker)
        {
            this.Invoke(HandleAddMod);
        }
    }

    private async void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try 
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var typeProp))
            {
                string type = typeProp.GetString() ?? "";
                LogActivity($"[RCV] {type}");

                switch (type)
                {
                    case "GET_DATA": SendDataToWeb(); break;
                    case "SYNC_TWEAKS_FROM_INI":
                        SyncTweakValuesFromIni();
                        SendDataToWeb();
                        break;
                    case "CLEAR_ERROR_LOG": HandleClearErrorLog(); break;
                    case "DEPLOY_MODS": HandleDeploy(root); break;
                    case "ADD_MOD":
                        HandleAddOrImportFilesMessage(root, fallbackToPicker: true);
                        break;
                    case "DELETE_MOD": 
                        if (TryGetString(root, "name", out var dModName, required: true))
                        {
                            LogActivity($"[RCV-DELETE] Request to delete: '{dModName}'");
                            HandleDeleteMod(dModName);
                        }
                        break;
                    case "DELETE_ALL_MODS":
                        HandleDeleteAllMods();
                        break;
                    case "LAUNCH_GAME": await HandleLaunchGame(); break;
                    case "APPLY_CHANGES": await HandleApplyChanges(); break;
                    case "TEST_CONFIG": HandleTestConfig(); break;
                    case "DEPLOY_ALL": HandleDeployAll(root); break;
                    case "UPDATE_SETTING":
                        if (TryGetString(root, "key", out var key, required: true) && root.TryGetProperty("value", out var value))
                        {
                            await HandleUpdateSetting(key, value);
                        }
                        else
                        {
                            LogError("[RCV] UPDATE_SETTING ignored due to missing/invalid payload.");
                        }
                        break;
                    case "UPDATE_SETTINGS_BATCH": 
                        string? pn = null;
                        if (root.TryGetProperty("presetName", out var pElem) && pElem.ValueKind == JsonValueKind.String) pn = pElem.GetString();
                        if (root.TryGetProperty("settings", out var settingsElem))
                        {
                            await HandleUpdateSettingsBatch(settingsElem, pn);
                        }
                        else
                        {
                            LogError("[RCV] UPDATE_SETTINGS_BATCH missing 'settings'.");
                        }
                        break;
                    case "UPDATE_MOD_ORDER":
                        if (root.TryGetProperty("order", out var orderElem)) HandleUpdateModOrder(orderElem);
                        else LogError("[RCV] UPDATE_MOD_ORDER missing 'order'.");
                        break;
                    case "TOGGLE_MOD":
                        if (TryGetString(root, "name", out var toggleName, required: true) &&
                            TryGetBool(root, "enabled", out var toggleEnabled))
                        {
                            HandleToggleMod(toggleName, toggleEnabled);
                        }
                        break;
                    case "BULK_TOGGLE_MODS":
                        if (TryGetBool(root, "enabled", out var bulkEnabled) &&
                            root.TryGetProperty("names", out var namesElem) &&
                            namesElem.ValueKind == JsonValueKind.Array)
                        {
                            HandleBulkToggleMods(bulkEnabled, namesElem);
                        }
                        else
                        {
                            LogError("[RCV] BULK_TOGGLE_MODS missing 'enabled' or 'names' array.");
                        }
                        break;
                    case "UPDATE_MOD_GROUPS":
                        if (root.TryGetProperty("groups", out var groupsElem)) HandleUpdateModGroups(groupsElem);
                        else LogError("[RCV] UPDATE_MOD_GROUPS missing 'groups'.");
                        break;
                    case "UPDATE_MOD_METADATA":
                        if (TryGetString(root, "originalName", out var originalName, required: true) &&
                            root.TryGetProperty("metadata", out var metadataElem))
                        {
                            HandleUpdateModMetadata(originalName, metadataElem);
                        }
                        else
                        {
                            LogError("[RCV] UPDATE_MOD_METADATA missing fields.");
                        }
                        break;
                    case "NAVIGATE_TO":
                        HandleNavigation(TryGetString(root, "section", out var section) ? section : "dashboard");
                        break;
                    case "BROWSE_FOLDER":
                        HandleBrowseFolder(
                            TryGetString(root, "target", out var target) ? target : "",
                            TryGetString(root, "path", out var browsePath) ? browsePath : null);
                        break;
                    case "BROWSE_EXECUTABLE":
                        HandleBrowseExecutable(
                            TryGetString(root, "target", out var fileTarget) ? fileTarget : "",
                            TryGetString(root, "path", out var fileBrowsePath) ? fileBrowsePath : null);
                        break;
                    case "IMPORT_USER_THEME":
                        HandleImportUserTheme();
                        break;
                    case "OPEN_THEMES_FOLDER":
                        HandleOpenThemesFolder();
                        break;
                    case "RELOAD_USER_THEMES":
                        HandleReloadUserThemes();
                        break;
                    case "SAVE_MANAGER_SETTINGS":
                        if (root.TryGetProperty("settings", out var managerSettingsElem)) HandleSaveManagerSettings(managerSettingsElem);
                        else LogError("[RCV] SAVE_MANAGER_SETTINGS missing 'settings'.");
                        break;
                    case "CREATE_PROFILE": 
                        if (TryGetString(root, "name", out var cpName, required: true))
                        {
                            string cpMode = (root.TryGetProperty("mode", out var mProp) && mProp.ValueKind == JsonValueKind.String) ? (mProp.GetString() ?? "clone") : "clone";
                            HandleCreateProfile(cpName, cpMode);
                        }
                        break;
                    case "DELETE_PROFILE":
                        if (TryGetString(root, "name", out var deleteProfileName, required: true)) HandleDeleteProfile(deleteProfileName);
                        break;
                    case "SWITCH_PROFILE":
                        if (TryGetString(root, "name", out var switchProfileName, required: true)) HandleSwitchProfile(switchProfileName);
                        break;
                    case "CHECK_CONFLICTS": RefreshConflictCount(true); break;
                    case "OPEN_IN_BROWSER":
                        if (TryGetString(root, "url", out var browserUrl, required: true)) HandleOpenInBrowser(browserUrl);
                        break;
                    case "BACKUP_INI": HandleBackupIni(); break;
                    case "BACKUP_MODS": HandleBackupMods(); break;
                    case "BACKUP_CONFIGS": HandleBackupConfigs(); break;
                    case "DELETE_ALL_CONFIGS": HandleDeleteAllConfigs(); break;
                    case "RESET_CONFIG": HandleResetConfig(); break;
                    case "CLEAR_CACHE": await HandleClearCacheTask(); break;
                    case "SWITCH_PLATFORM": HandleSwitchPlatform(); break;
                    case "TRANSFER_MODS_ACROSS_PLATFORMS":
                    {
                        string transferDirection = TryGetString(root, "direction", out var tDir) ? tDir : "to-other";
                        bool transferOverwrite = !TryGetBool(root, "overwrite", out var tOw) || tOw;
                        HandleTransferModsAcrossPlatforms(transferDirection, transferOverwrite);
                        break;
                    }
                    case "SAVE_PIPBOY_COLORS":
                        if (TryGetRgb(root, out var r, out var g, out var b))
                        {
                            HandleSavePipboyColors(r, g, b);
                        }
                        break;
                    case "GET_INTERFACE_COLORS": HandleGetInterfaceColors(); break;
                    case "SAVE_INTERFACE_COLORS":
                        {
                            string icTarget = TryGetString(root, "target", out var targetColor) ? targetColor : "pipboy";
                            if (TryGetRgb(root, out var icR, out var icG, out var icB))
                            {
                                HandleSaveInterfaceColors(icTarget, icR, icG, icB);
                            }
                        }
                        break;
                    case "SAVE_INTERFACE_COLORS_BATCH":
                        if (root.TryGetProperty("colors", out var colorsElem)) HandleSaveInterfaceColorsBatch(colorsElem);
                        else LogError("[RCV] SAVE_INTERFACE_COLORS_BATCH missing 'colors'.");
                        break;
                    case "JS_LOG":
                        if (TryGetString(root, "message", out var logMessage)) LogActivity($"[JS-LOG] {logMessage}");
                        break;
                    case "ENDORSEMENT_CONFIRMED": _endorsementManager.ConfirmEndorsement(); break;
                    case "ENDORSEMENT_DISMISSED": _endorsementManager.DismissEndorsement(); break;
                    case "LOAD_LANGUAGE":
                        HandleLoadLanguage(TryGetString(root, "lang", out var lang) ? lang : "en-US");
                        break;
                    case "CREATE_BUNDLE":
                        {
                            string bName = TryGetString(root, "name", out var name) ? name : "Bundle";
                            var bMods = GetStringList(root, "mods");
                            var bFolders = GetStringList(root, "folders");
                            var bPartial = ParseBa2PartialList(root);
                            var bFolderPartial = ParseFolderPartialList(root);
                            if (bMods.Count == 0 && bFolders.Count == 0 && bPartial.Count == 0 && bFolderPartial.Count == 0)
                            {
                                LogError("[RCV] CREATE_BUNDLE missing or empty 'mods', 'folders', 'ba2Partial', and 'folderPartial'.");
                                break;
                            }
                            string bCompression = (root.TryGetProperty("compression", out var cProp) && cProp.ValueKind == JsonValueKind.String) ? (cProp.GetString() ?? "Default") : "Default";
                            string bFormat = (root.TryGetProperty("format", out var formatProp) && formatProp.ValueKind == JsonValueKind.String) ? (formatProp.GetString() ?? "Auto") : "Auto";

                            _bundleManager.CreateBundle(bName, bMods, bFolders, bPartial, bFolderPartial, bCompression, bFormat, () => this.Invoke(() => {
                                RefreshConflictCount();
                                SendDataToWeb();
                            }));
                        }
                        break;
                    case "BUNDLE_BROWSE_FOLDER":
                        HandleBundleBrowseFolder();
                        break;
                    case "BUNDLE_EXTRACT":
                        if (TryGetString(root, "archive", out var extractArchive, required: true))
                        {
                            var extractPaths = GetStringList(root, "paths");
                            HandleBundleExtract(extractArchive, extractPaths);
                        }
                        break;
                    case "BUNDLE_LIST_BA2":
                        if (TryGetString(root, "path", out var listBa2Path, required: true))
                            HandleBundleListBa2(listBa2Path);
                        break;
                    case "BUNDLE_LIST_GAME_BA2S":
                        HandleBundleListGameBa2s();
                        break;
                    case "BUNDLE_LIST_FOLDER":
                        if (TryGetString(root, "path", out var listFolderPath, required: true))
                            HandleBundleListFolder(listFolderPath);
                        break;
                    case "RENAME_MOD": 
                        if (TryGetString(root, "currentName", out var oldN, required: true) &&
                            TryGetString(root, "newName", out var newN, required: true))
                        {
                            string renameDetails = TryGetString(root, "details", out var detailsValue) ? detailsValue : "";
                            HandleRenameMod(oldN, newN, renameDetails);
                        }
                        break;
                    case "SAVE_MOD_DETAILS":
                        if (TryGetString(root, "name", out var detailsName, required: true))
                        {
                            string modDetails = TryGetString(root, "details", out var detailsText) ? detailsText : "";
                            HandleSaveModDetails(detailsName, modDetails);
                        }
                        break;
                    case "READ_MOD_FILE":
                        if (TryGetString(root, "name", out var readModName, required: true) &&
                            TryGetString(root, "relativePath", out var readRelativePath, required: true))
                        {
                            HandleReadModFile(readModName, readRelativePath);
                        }
                        break;
                    case "WRITE_MOD_FILE":
                        if (TryGetString(root, "name", out var writeModName, required: true) &&
                            TryGetString(root, "relativePath", out var writeRelativePath, required: true) &&
                            TryGetString(root, "content", out var writeContent, required: true))
                        {
                            HandleWriteModFile(writeModName, writeRelativePath, writeContent);
                        }
                        break;
                    case "ADOPT_DATA_FILE":
                        if (TryGetString(root, "relativePath", out var adoptRelativePath, required: true) &&
                            TryGetString(root, "targetMod", out var adoptTargetMod, required: true))
                        {
                            HandleAdoptDataFile(adoptRelativePath, adoptTargetMod);
                        }
                        break;
                    case "GET_INI_CONTENT":
                        if (TryGetString(root, "iniType", out var iniType, required: true)) HandleGetIniContent(iniType);
                        break;
                    case "SAVE_INI_CONTENT":
                        if (TryGetString(root, "iniType", out var saveIniType, required: true) &&
                            TryGetString(root, "content", out var iniContent, required: true))
                        {
                            HandleSaveIniContent(saveIniType, iniContent);
                        }
                        break;
                    case "DELETE_BUNDLE_FILE":
                        try {
                            string bundleName = TryGetString(root, "name", out var rawBundleName) ? rawBundleName : "";
                            if (!string.IsNullOrEmpty(bundleName)) {
                                _modManager.DeleteBundleFile(bundleName);
                                SendStatusMessage("success", $"Deleted bundle file: {bundleName}", "delete_success", new object[] { bundleName });
                            }
                            SendDataToWeb(); 
                        } catch (Exception ex) {
                             LogError($"Delete Bundle Error: {ex.Message}");
                             SendStatusMessage("error", "Failed to delete bundle file.", "delete_failed");
                        }
                        break;
                    case "PROMOTE_BUNDLE_TO_MOD":
                        if (TryGetString(root, "name", out var promoteBundleName, required: true))
                            HandlePromoteBundleToMod(promoteBundleName);
                        break;
                    case "BROWSE_FILE":
                        try {
                            string title = (root.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String) ? (tProp.GetString() ?? "Select File") : "Select File";
                            string filter = (root.TryGetProperty("filter", out var fProp) && fProp.ValueKind == JsonValueKind.String) ? (fProp.GetString() ?? "All files (*.*)|*.*") : "All files (*.*)|*.*";
                            string? initialDir = null;
                            if (root.TryGetProperty("initialDirectory", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                                initialDir = idProp.GetString();
                            SyncAppPaths();
                            if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir))
                                initialDir = Directory.Exists(AppPaths.DataPath) ? AppPaths.DataPath : null;
                            this.Invoke(() => {
                                using (var ofd = new OpenFileDialog { Title = title, Filter = filter, Multiselect = true }) {
                                    if (!string.IsNullOrWhiteSpace(initialDir))
                                        ofd.InitialDirectory = initialDir;
                                    if (ofd.ShowDialog() == DialogResult.OK) {
                                        SendMessageToWeb(JsonSerializer.Serialize(new {
                                            type = "FILE_SELECTED",
                                            files = ofd.FileNames
                                        }));
                                    }
                                }
                            });
                        } catch (Exception ex) {
                            LogError($"Browse File Error: {ex.Message}");
                        }
                        break;
                    case "IMPORT_FILES":
                        HandleAddOrImportFilesMessage(root, fallbackToPicker: false);
                        break;
                    
                    case "NEXUS_SEARCH":
                        if (_nexusManager != null) {
                            if (TryGetString(root, "query", out var query, required: true))
                            {
                                Task.Run(async () => {
                                    string res = await _nexusManager.SearchMods(query);
                                    SendMessageToWeb(JsonSerializer.Serialize(new { type = "NEXUS_SEARCH_RESULT", data = res }));
                                });
                            }
                        }
                        break;
                        
                    case "NEXUS_GET_FILES":
                        if (_nexusManager != null) {
                            if (TryGetInt64(root, "modId", out var modId))
                            {
                                Task.Run(async () => {
                                    string res = await _nexusManager.GetModFiles(modId);
                                    SendMessageToWeb(JsonSerializer.Serialize(new { type = "NEXUS_FILES_RESULT", modId = modId, data = res }));
                                });
                            }
                        }
                        break;
                        
                    case "NEXUS_LOGIN":
                        HandleNexusLogin();
                        break;

                    case "NEXUS_LOGOUT":
                        HandleNexusLogout();
                        break;

                    case "NEXUS_IMPORT_COLLECTION":
                        if (_nexusManager != null)
                        {
                            string importUrl = "";
                            string importSlug = "";
                            int? importRevision = null;

                            if (TryGetString(root, "url", out var collectionUrl) &&
                                !string.IsNullOrWhiteSpace(collectionUrl))
                            {
                                importUrl = collectionUrl.Trim();
                            }

                            if (TryGetString(root, "slug", out var slugValue) &&
                                !string.IsNullOrWhiteSpace(slugValue))
                            {
                                importSlug = slugValue.Trim();
                            }

                            if (root.TryGetProperty("revision", out var revisionProp) &&
                                revisionProp.ValueKind == JsonValueKind.Number &&
                                revisionProp.TryGetInt32(out int revisionValue) &&
                                revisionValue > 0)
                            {
                                importRevision = revisionValue;
                            }

                            Task.Run(async () =>
                            {
                                if (!string.IsNullOrWhiteSpace(importUrl))
                                {
                                    if (NexusManager.TryParseNxmCollectionLink(importUrl, out importSlug, out importRevision) ||
                                        NexusManager.TryParseCollectionUrl(importUrl, out importSlug, out importRevision))
                                    {
                                        await HandleCollectionImportAsync(importSlug, importRevision);
                                        return;
                                    }

                                    this.Invoke(() => SendStatusMessage(
                                        "error",
                                        "Invalid Nexus collection URL.",
                                        "nexus_collection_invalid_url"));
                                    return;
                                }

                                if (!string.IsNullOrWhiteSpace(importSlug))
                                {
                                    await HandleCollectionImportAsync(importSlug, importRevision);
                                    return;
                                }

                                this.Invoke(() => SendStatusMessage(
                                    "error",
                                    "Provide a collection URL or slug.",
                                    "nexus_collection_missing_input"));
                            });
                        }
                        break;
                        
                    case "NEXUS_DOWNLOAD":
                        if (_nexusManager != null) {
                            if (TryGetInt64(root, "modId", out var modId) &&
                                TryGetInt64(root, "fileId", out var fileId))
                            {
                                string fileName = TryGetString(root, "fileName", out var nexusFileName) ? nexusFileName : "unknown_file";
                                string fileVersion = TryGetString(root, "fileVersion", out var nexusFileVer) ? nexusFileVer : "";
                                long? fileUploaded = null;
                                if (root.TryGetProperty("fileUploaded", out var upProp) && upProp.ValueKind == JsonValueKind.Number)
                                    fileUploaded = upProp.GetInt64();
                                string modName = TryGetString(root, "modName", out var nexusModName) ? nexusModName : "";
                                string author = TryGetString(root, "author", out var nexusAuthor) ? nexusAuthor : "";
                                string details = TryGetString(root, "details", out var nexusDetails) ? nexusDetails : "";
                                string category = TryGetString(root, "category", out var nexusCategory) ? nexusCategory : "";
                                _pendingNexusImport = BuildPendingNexusImport(
                                    modId,
                                    fileId,
                                    fileName,
                                    fileVersion,
                                    fileUploaded,
                                    modName,
                                    author,
                                    details,
                                    category);
                                Task.Run(async () => {
                                    if (string.IsNullOrWhiteSpace(modName))
                                    {
                                        var info = await _nexusManager.ResolveImportInfoAsync(modId, fileId);
                                        if (info != null)
                                        {
                                            this.Invoke(() =>
                                            {
                                                string replaceOriginalName = GetPendingReplaceOriginalName(_pendingNexusImport, info.ModId);
                                                _pendingNexusImport = BuildPendingNexusImport(
                                                    info.ModId,
                                                    info.FileId,
                                                    string.IsNullOrWhiteSpace(info.FileName) ? fileName : info.FileName,
                                                    string.IsNullOrWhiteSpace(info.FileVersion) ? fileVersion : info.FileVersion,
                                                    info.FileUploaded ?? fileUploaded,
                                                    info.ModName,
                                                    info.Author,
                                                    info.Details,
                                                    info.Category,
                                                    replaceOriginalName: replaceOriginalName);
                                            });
                                            await DownloadSingleModWithProgressAsync(
                                                modId,
                                                fileId,
                                                string.IsNullOrWhiteSpace(info.FileName) ? fileName : info.FileName);
                                            return;
                                        }
                                    }
                                    await DownloadSingleModWithProgressAsync(modId, fileId, fileName);
                                });
                            }
                        }
                        break;

                    case "CANCEL_NEXUS_DOWNLOAD":
                        CancelNexusDownload();
                        break;

                    case "CHECK_MOD_UPDATES":
                        HandleCheckModUpdates(root);
                        break;

                    case "MOD_UPDATE_REPLACE_RESPONSE":
                        if (TryGetString(root, "originalName", out var replaceOrigResp, required: true))
                        {
                            bool confirmed = TryGetBool(root, "confirmed", out var replaceConfirmed) && replaceConfirmed;
                            var importedKeys = new List<string>();
                            if (root.TryGetProperty("importedKeys", out var importedKeysElem) &&
                                importedKeysElem.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in importedKeysElem.EnumerateArray())
                                {
                                    if (item.ValueKind == JsonValueKind.String)
                                    {
                                        string importedKey = item.GetString() ?? "";
                                        if (!string.IsNullOrWhiteSpace(importedKey)) importedKeys.Add(importedKey);
                                    }
                                }
                            }

                            if (confirmed && importedKeys.Count > 0)
                            {
                                CompleteModUpdateReplace(replaceOrigResp, importedKeys, fromBulkUpdate: false);
                                RefreshConflictCount(false, sendDataUpdate: false);
                                SendDataToWeb();
                            }
                            else if (!confirmed)
                            {
                                SendStatusMessage(
                                    "info",
                                    $"Kept the previous version of {Path.GetFileName(replaceOrigResp)}.",
                                    "mod_update_remove_cancelled",
                                    new object[] { Path.GetFileName(replaceOrigResp) });
                            }
                        }
                        break;

                    case "UPDATE_MOD_FROM_NEXUS":
                        if (_bulkUpdateInProgress || _bulkUpdatePreflightInProgress)
                        {
                            SendStatusMessage("warning", "Bulk update already in progress.", "bulk_update_in_progress");
                            break;
                        }
                        if (_nexusManager != null &&
                            TryGetString(root, "originalName", out var updateOrigName, required: true))
                        {
                            long updateModId = 0;
                            long updateFileId = 0;
                            string updateFileName = "mod_update";
                            if (TryGetInt64(root, "modId", out var uModId)) updateModId = uModId;
                            if (TryGetInt64(root, "fileId", out var uFileId)) updateFileId = uFileId;
                            if (TryGetString(root, "fileName", out var uFn)) updateFileName = uFn;
                            string uVer = TryGetString(root, "fileVersion", out var uVerVal) ? uVerVal : "";
                            long? uUploaded = null;
                            if (root.TryGetProperty("fileUploaded", out var uUp) && uUp.ValueKind == JsonValueKind.Number)
                                uUploaded = uUp.GetInt64();

                            if (updateModId <= 0 || updateFileId <= 0)
                            {
                                var meta = _modManager.GetMetadataForMod(updateOrigName);
                                if (meta != null)
                                {
                                    if (updateModId <= 0 && meta.NexusModId.HasValue) updateModId = meta.NexusModId.Value;
                                }
                                if (_modUpdateCache.TryGetValue(updateOrigName, out var cached) && cached.HasUpdate)
                                {
                                    if (updateFileId <= 0 && cached.LatestFileId.HasValue) updateFileId = cached.LatestFileId.Value;
                                    if (string.IsNullOrEmpty(updateFileName) && !string.IsNullOrEmpty(cached.LatestFileName))
                                        updateFileName = cached.LatestFileName;
                                    if (string.IsNullOrEmpty(uVer) && !string.IsNullOrEmpty(cached.LatestVersion))
                                        uVer = cached.LatestVersion;
                                    if (!uUploaded.HasValue && cached.LatestUploaded.HasValue)
                                        uUploaded = cached.LatestUploaded;
                                }
                            }

                            if (updateModId > 0 && updateFileId > 0)
                            {
                                string uModName = "";
                                string uAuthor = "";
                                string uDetails = "";
                                string uCategory = "";
                                var existingMeta = _modManager.GetMetadataForMod(updateOrigName);
                                if (existingMeta != null)
                                {
                                    if (!string.IsNullOrWhiteSpace(existingMeta.Name)) uModName = existingMeta.Name;
                                    if (!string.IsNullOrWhiteSpace(existingMeta.Author)) uAuthor = existingMeta.Author;
                                    if (!string.IsNullOrWhiteSpace(existingMeta.Details)) uDetails = existingMeta.Details;
                                    if (!string.IsNullOrWhiteSpace(existingMeta.Category)) uCategory = existingMeta.Category;
                                }

                                _pendingNexusImport = BuildPendingNexusImport(
                                    updateModId,
                                    updateFileId,
                                    updateFileName,
                                    uVer,
                                    uUploaded,
                                    uModName,
                                    uAuthor,
                                    uDetails,
                                    uCategory,
                                    replaceOriginalName: updateOrigName);
                                Task.Run(async () => {
                                    await DownloadSingleModWithProgressAsync(updateModId, updateFileId, updateFileName);
                                });
                            }
                            else
                            {
                                SendStatusMessage("error", "This mod is not linked to Nexus or no update file is available.", "mod_update_unavailable");
                            }
                        }
                        break;
                    case "BULK_UPDATE_MODS":
                        HandleBulkUpdateMods();
                        break;
                    case "LIST_BACKUPS":
                        HandleListBackups();
                        break;
                    case "PREVIEW_BACKUP":
                        HandlePreviewBackup(root);
                        break;
                    case "RESTORE_BACKUP":
                        HandleRestoreBackup(root);
                        break;
                    case "DELETE_BACKUP":
                        HandleDeleteBackup(root);
                        break;
                    case "DELETE_ALL_BACKUPS":
                        HandleDeleteAllBackups();
                        break;
                    case "CHECK_COLLECTION_REVISION":
                        HandleCheckCollectionRevision();
                        break;
                    case "APPLY_COLLECTION_REVISION":
                        HandleApplyCollectionRevision(root);
                        break;
                    default:
                        LogActivity($"[RCV] Unknown message type: '{type}'");
                        break;
                }
            }
            else
            {
                LogError("[RCV] Ignored non-object message or message without 'type'.");
            }
        } catch (Exception ex) { LogError($"Message parsing error: {ex}"); }
    }

    private void HandleNavigation(string section) => lastSection = section;

    private void HandleDeploy(JsonElement root)
    {
        try {
            bool force = root.TryGetProperty("force", out var fProp) && fProp.GetBoolean();
            var modsArray = root.GetProperty("mods");
            var modsList = new List<string>();
            foreach (var m in modsArray.EnumerateArray()) modsList.Add(m.GetString() ?? "");
            LogActivity($"[DEPLOY_MODS] Parsed {modsList.Count} mods from message. Force={force}. Mods: [{string.Join(", ", modsList.Take(5))}]");
            HandleDeploy(modsList, force);
        } catch (Exception ex) { LogError($"Deploy Request Parse Failed: {ex.Message}"); }
    }

    private void HandleDeploy(List<string> mods, bool force)
    {
        Task.Run(() => {
            try {
                SyncAppPaths();
                LogActivity($"[DEPLOY] HandleDeploy called. ModCount={mods.Count}, Force={force}");

                if (mods.Count > 0) {
                    _modManager.BulkUpdateModStatus(mods);
                    LogActivity($"[DEPLOY] BulkUpdateModStatus completed for {mods.Count} mods.");
                }

                if (!force) {
                    var searchPaths = new List<string> { Path.Combine(gamePath, "Data") };
                    var enabledModPaths = _modManager.GetEnabledModPaths();
                    
                    LogActivity($"[UI] Pre-deploy conflict check for {enabledModPaths.Count} mods.");
                    var conflicts = _conflictManager.DetectConflicts(enabledModPaths, searchPaths);
                    
                    if (conflicts != null && conflicts.Count > 0) {
                        LogActivity($"[UI] Conflicts detected before deploy. Showing modal. requestedMods count={mods.Count}");
                        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                        string json = JsonSerializer.Serialize(new { 
                            type = "conflicts_found", 
                            conflicts = conflicts,
                            requestedMods = mods,
                            isFromDeploy = true
                        }, options);
                        LogActivity($"[DEPLOY] conflicts_found JSON length={json.Length}");
                        SendMessageToWeb(json);
                        return;
                    }
                }

                if (virtualModMode)
                {
                    LogActivity("[DEPLOY] Virtual Mod Mode active: performing staging-only deployment.");
                    _modManager.UpdateModOrder(mods, true);
                    this.Invoke(() => SendStatusMessage(
                        "info",
                        "Virtual Mod Mode is enabled. Deployment is staged now, then applied at launch and cleaned after game exit."
                    ));
                    SendDataToWeb();
                    return;
                }

                LogActivity($"[DEPLOY] Proceeding with UpdateModOrder for {mods.Count} mods.");
                _modManager.UpdateModOrder(mods, true);
                LogActivity($"[DEPLOY] UpdateModOrder completed. Sending success message with count={mods.Count}.");
                this.Invoke(() => SendStatusMessage("success", $"Successfully deployed {mods.Count} mod{(mods.Count == 1 ? "" : "s")}!", "deploy_success", new object[] { mods.Count }));
                SendDataToWeb();
            } catch (Exception ex) {
                LogError($"[DEPLOY] HandleDeploy exception: {ex}");
                this.Invoke(() => SendStatusMessage("error", $"Deployment failed: {ex.Message}", "deploy_failed", new object[] { ex.Message }));
            }
        });
    }

    private void HandleDeployAll(JsonElement root)
    {
        try {
            bool forceDeploy = root.TryGetProperty("force", out var fElem) && fElem.GetBoolean();
            List<string> enabledMods = new List<string>();

            if (root.TryGetProperty("mods", out var mElem) && mElem.ValueKind == JsonValueKind.Array) {
                enabledMods = mElem.EnumerateArray()
                    .Select(m => m.GetString() ?? "")
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .ToList();
            }

            if (enabledMods.Count == 0) {
                enabledMods = _modManager.GetModsList()
                   .Where(m => (string)((dynamic)m).status == "enabled")
                   .Select(m => (string)((dynamic)m).originalName)
                   .ToList();
            }

            HandleDeploy(enabledMods, forceDeploy); 
        } catch (Exception ex) {
            LogError($"DEPLOY_ALL Case Error: {ex.Message}");
            SendStatusMessage("error", $"Failed to start deployment: {ex.Message}", "deploy_failed", new object[] { ex.Message });
        }
    }

    private async Task HandleLaunchGame()
    {
        try {
            bool preparedManagedRuntime = false;

            if (virtualModMode)
            {
                int staleCleanup = _modManager.CleanupManagedArtifacts();
                LogActivity($"[LAUNCH] Managed stale cleanup completed. Entries processed: {staleCleanup}");
                int hydrated = _modManager.EnsureManagedStagingHydrated();
                LogActivity($"[LAUNCH] Managed staging hydration copied {hydrated} files.");

                var enabledMods = _modManager.GetModsList()
                    .Where(m => (string)((dynamic)m).status == "enabled")
                    .Select(m => (string)((dynamic)m).originalName)
                    .ToList();

                _modManager.PrepareManagedRuntimeState(enabledMods);
                preparedManagedRuntime = true;
                SendStatusMessage("info", "Managed mode runtime state prepared. Cleanup will run after confirmed game exit.");
            }

            string exeName = _platformManager.GetGameExeName();
            string fullPath = Path.Combine(gamePath, exeName);
            if (File.Exists(fullPath)) {
                string platformLabel = _platformManager.GetPlatformLabel();
                SendStatusMessage("info", $"Launching Fallout 76 through {platformLabel}...", "launching_game_via_platform", new object[] { platformLabel });

                Process.Start(new ProcessStartInfo(fullPath) { WorkingDirectory = gamePath });
                lastGameLaunch = DateTime.Now.ToString("g");
                SendDataToWeb();

                Task.Run(async () =>
                {
                    try
                    {
                        DateTime launchUtc = DateTime.UtcNow;
                        bool gameDetected = await WaitForGameProcessStart(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(1));

                        if (gameDetected)
                        {
                            DateTime detectedAt = DateTime.UtcNow;
                            LogActivity($"[LAUNCH] Game process detected at {detectedAt:O}");
                            this.Invoke(() =>
                            {
                                SendStatusMessage("success", "Game launched successfully!", "game_launched");
                            });
                        }
                        else
                        {
                            LogActivity("[LAUNCH] Game process was not detected within start timeout.");
                        }

                        if (preparedManagedRuntime)
                        {
                            if (gameDetected)
                            {
                                bool stableExit = await WaitForGameProcessStableExit(
                                    minimumRuntimeFromLaunch: TimeSpan.FromSeconds(20),
                                    absenceGraceWindow: TimeSpan.FromSeconds(30),
                                    pollInterval: TimeSpan.FromSeconds(2),
                                    maxWaitAfterDetect: TimeSpan.FromHours(8),
                                    launchUtc: launchUtc
                                );

                                if (stableExit)
                                {
                                    RunManagedCleanup("stable game exit detected");
                                }
                                else
                                {
                                    LogActivity("[LAUNCH] Stable exit not confirmed within max wait window. Cleanup deferred.");
                                }
                            }
                            else
                            {
                                LogActivity("[LAUNCH] Managed runtime kept because process detection timed out without hard failure.");
                                this.Invoke(() =>
                                {
                                    SendStatusMessage("warning", "Managed mode launch detection timed out; cleanup was deferred to avoid premature removal.");
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"[LAUNCH] Managed launch watcher failed: {ex.Message}");
                        if (preparedManagedRuntime)
                        {
                            RunManagedCleanup("watcher exception recovery");
                        }
                    }
                });
            } else {
                if (preparedManagedRuntime)
                {
                    RunManagedCleanup("executable not found");
                }
                SendStatusMessage("error", "Game executable not found. Please check your path in Settings.", "game_exe_not_found");
            }
        } catch (Exception ex) {
            LogError($"Launch Error: {ex.Message}");
            if (virtualModMode)
            {
                RunManagedCleanup("launch exception");
            }
        }
        await Task.CompletedTask;
    }

    private async Task<bool> WaitForGameProcessStart(TimeSpan timeout, TimeSpan pollInterval)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsGameRunning()) return true;
            await Task.Delay(pollInterval);
        }
        return false;
    }

    private async Task<bool> WaitForGameProcessStableExit(
        TimeSpan minimumRuntimeFromLaunch,
        TimeSpan absenceGraceWindow,
        TimeSpan pollInterval,
        TimeSpan maxWaitAfterDetect,
        DateTime launchUtc)
    {
        DateTime detectWaitDeadline = DateTime.UtcNow + maxWaitAfterDetect;
        DateTime? firstAbsentAt = null;
        bool graceLogged = false;

        while (DateTime.UtcNow < detectWaitDeadline)
        {
            bool running = IsGameRunning();
            DateTime now = DateTime.UtcNow;

            if (running)
            {
                if (firstAbsentAt.HasValue)
                {
                    LogActivity("[LAUNCH] Game process resumed during exit grace period. Resetting grace timer.");
                }
                firstAbsentAt = null;
                graceLogged = false;
                await Task.Delay(pollInterval);
                continue;
            }

            if (!firstAbsentAt.HasValue)
            {
                firstAbsentAt = now;
                LogActivity($"[LAUNCH] Game process became absent at {now:O}. Starting {absenceGraceWindow.TotalSeconds:0}s grace window.");
            }

            if (!graceLogged)
            {
                TimeSpan runtime = now - launchUtc;
                if (runtime < minimumRuntimeFromLaunch)
                {
                    LogActivity($"[LAUNCH] Exit seen before minimum runtime ({minimumRuntimeFromLaunch.TotalSeconds:0}s). Waiting for stability.");
                }
                graceLogged = true;
            }

            bool absenceStable = (now - firstAbsentAt.Value) >= absenceGraceWindow;
            bool minimumRuntimeMet = (now - launchUtc) >= minimumRuntimeFromLaunch;
            if (absenceStable && minimumRuntimeMet)
            {
                LogActivity("[LAUNCH] Stable game exit confirmed.");
                return true;
            }

            await Task.Delay(pollInterval);
        }

        return false;
    }

    private void RunManagedCleanup(string reason)
    {
        int cleaned = _modManager.CleanupManagedArtifacts();
        LogActivity($"[LAUNCH] Managed cleanup triggered ({reason}). Entries processed: {cleaned}");
        this.Invoke(() =>
        {
            SendStatusMessage("info", $"Managed mode cleanup complete ({reason}). Restored/removed {cleaned} runtime artifacts.");
        });
    }

    private async Task HandleApplyChanges()
    {
        SendStatusMessage("success", "Changes applied successfully.", "changes_applied");
        await Task.CompletedTask;
    }

    private void HandleTestConfig()
    {
        SyncAppPaths();
        LogActivity("[CONFIG-TEST] Running configuration check...");

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            LogActivity("[CONFIG-TEST] Failed: game path is empty.");
            SendStatusMessage("error", "Game path is not set. Choose your Fallout 76 folder in Settings.", "config_test_game_path_empty");
            return;
        }

        if (!Directory.Exists(gamePath))
        {
            LogActivity($"[CONFIG-TEST] Failed: game path does not exist: {gamePath}");
            SendStatusMessage("error", "Game folder not found.", "config_test_game_path_missing", new object[] { gamePath });
            return;
        }

        string dataDir = Path.Combine(gamePath, "Data");
        if (!Directory.Exists(dataDir))
        {
            LogActivity("[CONFIG-TEST] Failed: Data folder missing under game path.");
            SendStatusMessage("error", "Game Data folder not found.", "config_test_data_missing", new object[] { gamePath });
            return;
        }

        string exeName = _platformManager.GetGameExeName();
        string exeFull = Path.Combine(gamePath, exeName);
        if (!File.Exists(exeFull))
        {
            LogActivity($"[CONFIG-TEST] Failed: executable not found: {exeFull}");
            SendStatusMessage("error", "Game executable not found. Please check your path in Settings.", "game_exe_not_found");
            return;
        }

        bool hasOptionalIssues = false;

        if (string.IsNullOrWhiteSpace(documentsPath))
        {
            hasOptionalIssues = true;
            LogActivity("[CONFIG-TEST] Warning: documents (INI) path is empty.");
        }
        else if (!Directory.Exists(documentsPath))
        {
            hasOptionalIssues = true;
            LogActivity($"[CONFIG-TEST] Warning: documents folder does not exist: {documentsPath}");
        }
        else
        {
            string iniPrefix = _platformManager.GetIniPrefix();
            string customIni = Path.Combine(documentsPath, $"{iniPrefix}Custom.ini");
            if (!File.Exists(customIni))
                LogActivity($"[CONFIG-TEST] Note: {iniPrefix}Custom.ini not found (normal before the first game run).");
        }

        if (!string.IsNullOrWhiteSpace(localAppDataPath) && !Directory.Exists(localAppDataPath))
        {
            hasOptionalIssues = true;
            LogActivity($"[CONFIG-TEST] Warning: Local AppData folder does not exist: {localAppDataPath}");
        }

        if (!nexusLoggedIn)
            LogActivity("[CONFIG-TEST] Note: Nexus sign-in not active (optional; use Mod Manager Download / NXM links).");

        string s7 = sevenZipPath.Trim();
        if (!string.IsNullOrEmpty(s7) && !File.Exists(s7))
        {
            hasOptionalIssues = true;
            LogActivity($"[CONFIG-TEST] Warning: 7-Zip path invalid: {s7}");
        }

        string rar = rarExtractorPath.Trim();
        if (!string.IsNullOrEmpty(rar) && !File.Exists(rar))
        {
            hasOptionalIssues = true;
            LogActivity($"[CONFIG-TEST] Warning: RAR extractor path invalid: {rar}");
        }

        if (hasOptionalIssues)
        {
            SendStatusMessage("warning",
                "Game install looks valid, but some optional paths or tools need attention. See the activity log for details.",
                "config_test_success_with_warnings");
        }
        else
        {
            SendStatusMessage("success", "Configuration test successful.", "config_test_success");
        }

        LogActivity("[CONFIG-TEST] Check complete.");
    }

    private async Task HandleUpdateSetting(string key, JsonElement value)
    {
        ApplyIndividualSettingObject(key, value);
        SaveSettings();
        SendStatusMessage("success", "Settings saved successfully.", "settings_saved");
        SendDataToWeb();
        await Task.CompletedTask;
    }

    private async Task HandleUpdateSettingsBatch(JsonElement settings, string presetName)
    {
        if (!string.IsNullOrEmpty(presetName)) activeTweaksPreset = presetName;
        foreach (var prop in settings.EnumerateObject()) {
            ApplyIndividualSettingObject(prop.Name, prop.Value, writeIni: true);
        }
        SaveSettings();
        SendDataToWeb();
        if (!string.IsNullOrEmpty(presetName)) {
             SendStatusMessage("success", "Presets applied successfully.", "applied_preset_banner", new object[] { presetName });
        } else {
             SendStatusMessage("success", "Settings saved successfully.", "tweak_saved_msg");
        }
        await Task.CompletedTask;
    }

    private void HandleSaveManagerSettings(JsonElement settings)
    {
        try {
            bool previousVirtualMode = virtualModMode;

            if (settings.TryGetProperty("gamePath", out var gp)) {
                gamePath = gp.GetString() ?? gamePath;
                if (_platformManager.CurrentPlatform == GamePlatform.Steam) steamGamePath = gamePath;
                else xboxGamePath = gamePath;
            }
            if (settings.TryGetProperty("documentsPath", out var dp)) {
                documentsPath = dp.GetString() ?? documentsPath;
                if (_platformManager.CurrentPlatform == GamePlatform.Steam) steamDocsPath = documentsPath;
                else xboxDocsPath = documentsPath;
            }
            if (settings.TryGetProperty("localAppDataPath", out var lap)) {
                localAppDataPath = lap.GetString() ?? localAppDataPath;
                if (_platformManager.CurrentPlatform == GamePlatform.Steam) steamLocalPath = localAppDataPath;
                else xboxLocalPath = localAppDataPath;
            }
            if (settings.TryGetProperty("stringsPath", out var sp)) {
                stringsPath = sp.GetString() ?? stringsPath;
                if (_platformManager.CurrentPlatform == GamePlatform.Steam) steamStringsPath = stringsPath;
                else xboxStringsPath = stringsPath;
            }

            if (settings.TryGetProperty("minimizeToTray", out var mt)) minimizeToTray = mt.GetBoolean();
            if (settings.TryGetProperty("uiAnimations", out var ua)) uiAnimations = ua.GetBoolean();
            if (settings.TryGetProperty("platformBadgeGlow", out var pbg)) platformBadgeGlow = pbg.GetBoolean();
            if (settings.TryGetProperty("syncPlatforms", out var spr)) syncPlatforms = spr.GetBoolean();
            if (settings.TryGetProperty("autoForceDeploy", out var afd)) autoForceDeploy = afd.GetBoolean();
            if (settings.TryGetProperty("virtualModMode", out var vmm)) virtualModMode = vmm.GetBoolean();
            else if (settings.TryGetProperty("managedVanillaMode", out var mvm)) virtualModMode = mvm.GetBoolean();
            if (settings.TryGetProperty("configEditorSpellCheck", out var cesc)) configEditorSpellCheck = cesc.GetBoolean();
            if (settings.TryGetProperty("confirmBeforeDeleteMod", out var cbddm)) confirmBeforeDeleteMod = cbddm.GetBoolean();
            if (settings.TryGetProperty("confirmBeforeRemoveOldModOnUpdate", out var cbroou)) confirmBeforeRemoveOldModOnUpdate = cbroou.GetBoolean();
            if (settings.TryGetProperty("language", out var lang)) applicationLanguage = lang.GetString() ?? applicationLanguage;
            if (settings.TryGetProperty("uiTheme", out var ut)) {
                uiTheme = NormalizeUiTheme(ut.GetString() ?? uiTheme);
            }
            if (settings.TryGetProperty("archiveKeyName", out var akn)) archiveKeyName = akn.GetString() ?? archiveKeyName;
            if (settings.TryGetProperty("sevenZipPath", out var szp)) sevenZipPath = szp.GetString() ?? sevenZipPath;
            if (settings.TryGetProperty("rarExtractorPath", out var rep)) rarExtractorPath = rep.GetString() ?? rarExtractorPath;
            if (settings.TryGetProperty("keybinds", out var kb) && kb.ValueKind == JsonValueKind.Object)
                keybindsJson = kb.GetRawText();
            
            LogActivity($"[SETTINGS] Manager settings saved by user. GamePath: {gamePath}, Language: {applicationLanguage}");
            SaveSettings();
            SyncAppPaths();
            EnsureAllPrefsIniWritable();

            if (previousVirtualMode != virtualModMode)
            {
                if (virtualModMode)
                {
                    int hydrated = _modManager.EnsureManagedStagingHydrated();
                    LogActivity($"[SETTINGS] VirtualModMode enabled. Staging hydration copied {hydrated} files.");
                }

                int cleaned = _modManager.CleanupManagedArtifacts();
                LogActivity($"[SETTINGS] VirtualModMode changed to {virtualModMode}. Cleanup processed {cleaned} entries.");
            }

            SendStatusMessage("success", "Settings saved successfully.", "manager_settings_saved");
            SendDataToWeb();
        } catch (Exception ex) {
            LogError($"Save Manager Settings Failed: {ex.Message}");
            SendStatusMessage("error", "Failed to save settings.", "save_settings_failed");
        }
    }

    private void HandleSavePipboyColors(int r, int g, int b)
    {
        if (_platformManager.IsXbox()) {
            xboxPipboyRed = r; xboxPipboyGreen = g; xboxPipboyBlue = b;
        } else {
            steamPipboyRed = r; steamPipboyGreen = g; steamPipboyBlue = b;
        }
        SaveSettings();

        float fR = r / 255.0f;
        float fG = g / 255.0f;
        float fB = b / 255.0f;
        string sR = fR.ToString("0.000", CultureInfo.InvariantCulture);
        string sG = fG.ToString("0.000", CultureInfo.InvariantCulture);
        string sB = fB.ToString("0.000", CultureInfo.InvariantCulture);

        _configManager.UpdateBothInis("Pipboy", "fPipboyEffectColorR", sR);
        _configManager.UpdateBothInis("Pipboy", "fPipboyEffectColorG", sG);
        _configManager.UpdateBothInis("Pipboy", "fPipboyEffectColorB", sB);

        SendStatusMessage("success", "Interface colors updated.", "interface_colors_updated");
        SendDataToWeb();
    }

    private void HandleGetInterfaceColors()
    {
        try
        {
            string customPath = Path.Combine(documentsPath, $"{AppPaths.IniPrefix}Custom.ini");
            string prefsPath = Path.Combine(documentsPath, $"{AppPaths.IniPrefix}Prefs.ini");
            string iniPath = File.Exists(customPath) ? customPath : prefsPath;

            var result = new Dictionary<string, object>();
            result["type"] = "INTERFACE_COLORS";

            string pbR = _configManager.ReadIniValue(iniPath, "Pipboy", "fPipboyEffectColorR");
            string pbG = _configManager.ReadIniValue(iniPath, "Pipboy", "fPipboyEffectColorG");
            string pbB = _configManager.ReadIniValue(iniPath, "Pipboy", "fPipboyEffectColorB");
            bool pbExists = pbR != null && pbG != null && pbB != null;
            result["pipboy"] = new {
                r = pbExists ? (int)Math.Round(float.Parse(pbR, CultureInfo.InvariantCulture) * 255) : 26,
                g = pbExists ? (int)Math.Round(float.Parse(pbG, CultureInfo.InvariantCulture) * 255) : 255,
                b = pbExists ? (int)Math.Round(float.Parse(pbB, CultureInfo.InvariantCulture) * 255) : 128,
                exists = pbExists
            };

            string qbR = _configManager.ReadIniValue(iniPath, "Pipboy", "fQuickBoyEffectColorR");
            string qbG = _configManager.ReadIniValue(iniPath, "Pipboy", "fQuickBoyEffectColorG");
            string qbB = _configManager.ReadIniValue(iniPath, "Pipboy", "fQuickBoyEffectColorB");
            bool qbExists = qbR != null && qbG != null && qbB != null;
            result["quickboy"] = new {
                r = qbExists ? (int)Math.Round(float.Parse(qbR, CultureInfo.InvariantCulture) * 255) : -1,
                g = qbExists ? (int)Math.Round(float.Parse(qbG, CultureInfo.InvariantCulture) * 255) : -1,
                b = qbExists ? (int)Math.Round(float.Parse(qbB, CultureInfo.InvariantCulture) * 255) : -1,
                exists = qbExists
            };

            string paR = _configManager.ReadIniValue(iniPath, "Pipboy", "fPAEffectColorR");
            string paG = _configManager.ReadIniValue(iniPath, "Pipboy", "fPAEffectColorG");
            string paB = _configManager.ReadIniValue(iniPath, "Pipboy", "fPAEffectColorB");
            bool paExists = paR != null && paG != null && paB != null;
            result["pa"] = new {
                r = paExists ? (int)Math.Round(float.Parse(paR, CultureInfo.InvariantCulture) * 255) : -1,
                g = paExists ? (int)Math.Round(float.Parse(paG, CultureInfo.InvariantCulture) * 255) : -1,
                b = paExists ? (int)Math.Round(float.Parse(paB, CultureInfo.InvariantCulture) * 255) : -1,
                exists = paExists
            };

            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            SendMessageToWeb(json);
            LogActivity("[INI-READ] Interface colors read from INI.");
        }
        catch (Exception ex)
        {
            LogError($"HandleGetInterfaceColors Error: {ex.Message}");
        }
    }

    private void HandleSaveInterfaceColors(string target, int r, int g, int b)
    {
        if (_platformManager.IsXbox())
        {
            switch (target)
            {
                case "pipboy": xboxPipboyRed = r; xboxPipboyGreen = g; xboxPipboyBlue = b; break;
                case "quickboy": xboxQuickboyRed = r; xboxQuickboyGreen = g; xboxQuickboyBlue = b; break;
                case "pa": xboxPaRed = r; xboxPaGreen = g; xboxPaBlue = b; break;
            }
        }
        else
        {
            switch (target)
            {
                case "pipboy": steamPipboyRed = r; steamPipboyGreen = g; steamPipboyBlue = b; break;
                case "quickboy": steamQuickboyRed = r; steamQuickboyGreen = g; steamQuickboyBlue = b; break;
                case "pa": steamPaRed = r; steamPaGreen = g; steamPaBlue = b; break;
            }
        }
        SaveSettings();

        float fR = r / 255.0f;
        float fG = g / 255.0f;
        float fB = b / 255.0f;
        string sR = fR.ToString("0.000", CultureInfo.InvariantCulture);
        string sG = fG.ToString("0.000", CultureInfo.InvariantCulture);
        string sB = fB.ToString("0.000", CultureInfo.InvariantCulture);

        switch (target)
        {
            case "pipboy":
                    _configManager.UpdateBothInis("Pipboy", "fPipboyEffectColorR", sR);
                    _configManager.UpdateBothInis("Pipboy", "fPipboyEffectColorG", sG);
                    _configManager.UpdateBothInis("Pipboy", "fPipboyEffectColorB", sB);
                break;
            case "quickboy":
                    _configManager.UpdateBothInis("Pipboy", "fQuickBoyEffectColorR", sR);
                    _configManager.UpdateBothInis("Pipboy", "fQuickBoyEffectColorG", sG);
                    _configManager.UpdateBothInis("Pipboy", "fQuickBoyEffectColorB", sB);
                break;
            case "pa":
                    _configManager.UpdateBothInis("Pipboy", "fPAEffectColorR", sR);
                    _configManager.UpdateBothInis("Pipboy", "fPAEffectColorG", sG);
                    _configManager.UpdateBothInis("Pipboy", "fPAEffectColorB", sB);
                break;
        }

        string label = target == "pipboy" ? "Pip-Boy" : target == "quickboy" ? "Quick-Boy" : "Power Armor";
        SendStatusMessage("success", $"{label} colors updated.", "interface_colors_updated");
        SendDataToWeb();
    }

    private void HandleSaveInterfaceColorsBatch(JsonElement colorsArray)
    {
        int count = 0;
        foreach (var item in colorsArray.EnumerateArray())
        {
            try
            {
                string target = item.GetProperty("target").GetString() ?? "";
                int r = item.GetProperty("r").GetInt32();
                int g = item.GetProperty("g").GetInt32();
                int b = item.GetProperty("b").GetInt32();

                if (_platformManager.IsXbox())
                {
                    switch (target)
                    {
                        case "pipboy": xboxPipboyRed = r; xboxPipboyGreen = g; xboxPipboyBlue = b; break;
                        case "quickboy": xboxQuickboyRed = r; xboxQuickboyGreen = g; xboxQuickboyBlue = b; break;
                        case "pa": xboxPaRed = r; xboxPaGreen = g; xboxPaBlue = b; break;
                    }
                }
                else
                {
                    switch (target)
                    {
                        case "pipboy": steamPipboyRed = r; steamPipboyGreen = g; steamPipboyBlue = b; break;
                        case "quickboy": steamQuickboyRed = r; steamQuickboyGreen = g; steamQuickboyBlue = b; break;
                        case "pa": steamPaRed = r; steamPaGreen = g; steamPaBlue = b; break;
                    }
                }

                float fR = r / 255.0f;
                float fG = g / 255.0f;
                float fB = b / 255.0f;
                string sR = fR.ToString("0.000", CultureInfo.InvariantCulture);
                string sG = fG.ToString("0.000", CultureInfo.InvariantCulture);
                string sB = fB.ToString("0.000", CultureInfo.InvariantCulture);

                switch (target)
                {
                    case "pipboy":
                            _configManager.UpdateBothInis("Pipboy", "fPipboyEffectColorR", sR);
                            _configManager.UpdateBothInis("Pipboy", "fPipboyEffectColorG", sG);
                            _configManager.UpdateBothInis("Pipboy", "fPipboyEffectColorB", sB);
                        break;
                    case "quickboy":
                            _configManager.UpdateBothInis("Pipboy", "fQuickBoyEffectColorR", sR);
                            _configManager.UpdateBothInis("Pipboy", "fQuickBoyEffectColorG", sG);
                            _configManager.UpdateBothInis("Pipboy", "fQuickBoyEffectColorB", sB);
                        break;
                    case "pa":
                            _configManager.UpdateBothInis("Pipboy", "fPAEffectColorR", sR);
                            _configManager.UpdateBothInis("Pipboy", "fPAEffectColorG", sG);
                            _configManager.UpdateBothInis("Pipboy", "fPAEffectColorB", sB);
                        break;
                }
                count++;
            }
            catch (Exception ex)
            {
                LogError($"Batch Color Save Error for item: {ex.Message}");
            }
        }

        SaveSettings();
        if (count > 1) {
            SendStatusMessage("success", "All interface colors synced and updated.", "interface_colors_synced");
        } else {
            SendStatusMessage("success", "Interface colors updated.", "interface_colors_updated");
        }
        SendDataToWeb();
    }

    private string ResolveFolderPathForTarget(string target)
    {
        return target switch
        {
            "game" => gamePath ?? "",
            "docs" => documentsPath ?? "",
            "localAppData" => localAppDataPath ?? "",
            "strings" => stringsPath ?? "",
            _ => gamePath ?? ""
        };
    }

    private void HandleAddMod() { 
        this.Invoke(() => { 
            var result = _modManager.ImportMod();
            if (result != null && result.Count > 0 && activeProfile != "Default Profile")
            {
                var p = profiles.FirstOrDefault(x => x.Name == activeProfile);
                if (p != null) {
                    foreach(var f in result) {
                        string key = f.Replace("\\", "/");
                        if (!p.ProfileMods.Contains(key, StringComparer.OrdinalIgnoreCase)) 
                            p.ProfileMods.Add(key);
                    }
                    SaveProfiles();
                }
            }
        }); 
        RefreshConflictCount(false, sendDataUpdate: false); 
        SendDataToWeb(); 
    }
    private void SendImportProgressBanner(ModManager.ImportProgress progress)
    {
        if (_collectionImportInProgress)
            return;

        string text = progress.Percent >= 100
            ? $"Import complete. {progress.Percent}% ({progress.Completed}/{progress.Total})"
            : $"Importing mods... {progress.Percent}% ({progress.Completed}/{progress.Total})";

        SendMessageToWeb(JsonSerializer.Serialize(new {
            type = "STATUS",
            status = new {
                type = "success",
                text = text,
                isProgress = true,
                progressId = "mod-import",
                done = progress.Percent >= 100,
                percent = progress.Percent,
                completed = progress.Completed,
                total = progress.Total
            }
        }));
    }

    private void HandleAddModFiles(List<string> files) {
        var normalizedFiles = NormalizeImportPaths(files);
        if (normalizedFiles.Count == 0) return;

        lock (_importDropLock)
        {
            if (_isImportRunning)
            {
                foreach (var file in normalizedFiles)
                {
                    if (!_queuedImportFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                    {
                        _queuedImportFiles.Add(file);
                    }
                }
                LogActivity($"[IMPORT] Queued {normalizedFiles.Count} item(s) while import is active.");
                return;
            }
            _isImportRunning = true;
        }

        LogActivity($"[IMPORT] Handling import request for {normalizedFiles.Count} items.");

        PendingNexusImport? pendingNexus = null;
        if (_pendingNexusImport != null)
        {
            var candidate = _pendingNexusImport;
            bool pathMatches = string.IsNullOrEmpty(candidate.ExpectedArchivePath) ||
                normalizedFiles.Any(f => string.Equals(Path.GetFullPath(f), candidate.ExpectedArchivePath, StringComparison.OrdinalIgnoreCase));
            bool isUpdateReplace = !string.IsNullOrWhiteSpace(candidate.ReplaceOriginalName);

            if (pathMatches || isUpdateReplace)
            {
                pendingNexus = candidate;
                _pendingNexusImport = null;
            }
        }

        Task.Run(() => {
            var result = _modManager.ImportFiles(normalizedFiles, progress => SendImportProgressBanner(progress));

            this.Invoke(() => {
                List<string> nextQueuedBatch = new();
                try
                {
                    int addedToProfile = 0;

                    if (result != null && result.Count > 0)
                    {
                        if (pendingNexus != null)
                        {
                            try
                            {
                                _modManager.ApplyNexusLinkageToImportedKeys(
                                    result,
                                    pendingNexus.ModId,
                                    pendingNexus.FileId,
                                    pendingNexus.FileVersion,
                                    pendingNexus.FileUploaded,
                                    pendingNexus.ModName,
                                    pendingNexus.Author,
                                    pendingNexus.Details,
                                    pendingNexus.Category);

                                if (!string.IsNullOrWhiteSpace(pendingNexus.ReplaceOriginalName))
                                {
                                    if (confirmBeforeRemoveOldModOnUpdate && !_bulkUpdateSuppressReplaceConfirm)
                                    {
                                        PromptModUpdateReplace(pendingNexus.ReplaceOriginalName, result);
                                    }
                                    else
                                    {
                                        CompleteModUpdateReplace(pendingNexus.ReplaceOriginalName, result, pendingNexus.IsBulkUpdate);
                                    }
                                }
                                else
                                {
                                    if (!_bulkUpdateInProgress)
                                        _modUpdateCache.Clear();
                                }
                            }
                            catch (Exception ex)
                            {
                                LogError($"[NEXUS] Failed to apply linkage after import: {ex.Message}");
                            }
                        }

                        if (activeProfile != "Default Profile")
                        {
                            var p = profiles.FirstOrDefault(x => x.Name == activeProfile);
                            if (p != null) {
                                foreach(var f in result) {
                                    string key = f.Replace("\\", "/");
                                    if (!p.ProfileMods.Contains(key, StringComparer.OrdinalIgnoreCase)) {
                                        p.ProfileMods.Add(key);
                                        addedToProfile++;
                                    }
                                }
                                SaveProfiles();
                                LogActivity($"[IMPORT] Added {addedToProfile} new mods to profile '{activeProfile}'.");
                            }
                        }

                        string profileContext = activeProfile == "Default Profile" ? "" : $" and added {addedToProfile} to '{activeProfile}'";
                        LogActivity($"[IMPORT] Completed import of {result.Count} mod files to {AppPaths.DataPath}{profileContext}.");
                    }
                    else
                    {
                         // If the import already surfaced a specific problem (e.g. missing extractor,
                         // failed extraction, or an installer archive with no loose mod files), don't
                         // mask it with the generic banner.
                         if (!_modManager.LastImportReportedIssue)
                         {
                             if (normalizedFiles.Count == 1) {
                                 SendStatusMessage("warning", "Import finished but no valid mod files (BA2/ESP/ESM) were found.", "import_no_valid_files");
                             } else {
                                 SendStatusMessage("info", "Import finished. No new mod files were added.", "import_no_new_files");
                             }
                         }
                         if (pendingNexus?.IsBulkUpdate == true && _bulkUpdateInProgress)
                             AdvanceBulkUpdateQueue(false);
                    }

                    RefreshConflictCount(false, sendDataUpdate: false);
                    SendDataToWeb();

                    if (pendingNexus != null && result != null && result.Count > 0)
                    {
                        SendMessageToWeb(JsonSerializer.Serialize(new { type = "REQUEST_MOD_UPDATE_CHECK" }));
                    }
                }
                finally
                {
                    lock (_importDropLock)
                    {
                        _isImportRunning = false;
                        if (_queuedImportFiles.Count > 0)
                        {
                            nextQueuedBatch = NormalizeImportPaths(_queuedImportFiles);
                            _queuedImportFiles.Clear();
                        }
                    }
                }

                if (nextQueuedBatch.Count > 0)
                {
                    LogActivity($"[IMPORT] Processing queued follow-up batch ({nextQueuedBatch.Count} items).");
                    HandleAddModFiles(nextQueuedBatch);
                }
            });
        });
    }
    private void HandleDeleteAllMods()
    {
        Task.Run(() =>
        {
            try
            {
                SyncAppPaths();
                var modNames = _modManager.GetModsList()
                    .Select(m => (string)((dynamic)m).originalName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (modNames.Count == 0)
                {
                    this.Invoke(() => SendStatusMessage("info", "No mods to delete.", "delete_all_mods_none"));
                    return;
                }

                LogActivity($"[RCV-DELETE-ALL] Request to delete {modNames.Count} mods.");

                int deletedCount = 0;
                if (activeProfile != "Default Profile")
                {
                    var p = profiles.FirstOrDefault(x => x.Name == activeProfile);
                    if (p != null)
                    {
                        foreach (var name in modNames)
                        {
                            string norm = name.Replace("\\", "/");
                            p.ProfileMods.RemoveAll(x =>
                                x.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                                x.Equals(name, StringComparison.OrdinalIgnoreCase));
                            p.EnabledMods.RemoveAll(x =>
                                x.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                                x.Equals(name, StringComparison.OrdinalIgnoreCase));
                            deletedCount++;
                        }
                        SaveProfiles();
                    }
                }
                else
                {
                    deletedCount = _modManager.DeleteAllMods();
                }

                this.Invoke(() =>
                {
                    RefreshConflictCount();
                    SendDataToWeb();
                    SendStatusMessage(
                        "success",
                        $"Deleted {deletedCount} mod{(deletedCount == 1 ? "" : "s")}.",
                        "delete_all_mods_success",
                        new object[] { deletedCount });
                });
            }
            catch (Exception ex)
            {
                LogError($"[DELETE_ALL_FAIL] {ex.Message}");
                this.Invoke(() => SendStatusMessage(
                    "error",
                    $"Delete all mods failed: {ex.Message}",
                    "delete_all_mods_failed",
                    new object[] { ex.Message }));
            }
        });
    }

    private void HandleDeleteMod(string name) { 
        try {
            if (activeProfile != "Default Profile")
            {
                var p = profiles.FirstOrDefault(x => x.Name == activeProfile);
                if (p != null)
                {
                    string norm = name.Replace("\\", "/");
                    
                    var entry = p.ProfileMods.FirstOrDefault(x => x.Equals(norm, StringComparison.OrdinalIgnoreCase) || x.Equals(name, StringComparison.OrdinalIgnoreCase));
                    
                    if (entry != null) {
                        p.ProfileMods.Remove(entry);
                        SaveProfiles();
                        
                        p.EnabledMods.RemoveAll(x => x.Equals(norm, StringComparison.OrdinalIgnoreCase) || x.Equals(name, StringComparison.OrdinalIgnoreCase));
                        
                        SendStatusMessage("success", $"Removed '{name}' from profile.", "removed_mod_from_profile", new object[] { name });
                        RefreshConflictCount(); 
                        SendDataToWeb();
                        return;
                    }
                }
            }

            LogActivity($"[HANDLER] Calling _modManager.DeleteMod('{name}')");
            _modManager.DeleteMod(name); 
            LogActivity($"[HANDLER] DeleteMod returned.");
            RefreshConflictCount(); 
            SendDataToWeb(); 
        } catch (Exception ex) {
            LogError($"[DELETE_FAIL] {ex.Message}");
            SendStatusMessage("error", $"Delete failed: {ex.Message}", "delete_mod_failed", new object[] { ex.Message });
        }
    }
    private void HandleUpdateModOrder(JsonElement order) {
        var newOrder = order.EnumerateArray().Select(s => s.GetString() ?? "").ToList();
        _modManager.UpdateModOrder(newOrder);
        SendDataToWeb();
    }
    private void HandleToggleMod(string name, bool enabled) {
        try {
            if (_modManager.ToggleModEnabled(name, enabled, out string? toggleError))
            {
                SyncProfileModsAfterToggle(name, enabled);
                LogActivity($"[MODS] Toggled mod '{name}' to {(enabled ? "enabled" : "disabled")}.");
            }
            else if (!string.IsNullOrEmpty(toggleError) &&
                     toggleError.Contains("not found on this platform", StringComparison.OrdinalIgnoreCase))
            {
                SendStatusMessage("warning",
                    "This mod's file isn't on the current platform yet — transfer or re-import it first.",
                    "mod_file_missing_platform");
            }
            else if (!string.IsNullOrEmpty(toggleError))
            {
                SendStatusMessage("error",
                    $"Failed to toggle mod: {toggleError}. Is the game running?",
                    "toggle_mod_failed");
            }
            SendDataToWeb();
        } catch (Exception ex) {
            LogError($"Toggle mod error: {ex.Message}");
        }
    }

    private void HandleBulkToggleMods(bool enabled, JsonElement namesElem)
    {
        try
        {
            int ok = 0;
            foreach (var el in namesElem.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                string name = el.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (_modManager.ToggleModEnabled(name, enabled, out string? toggleError))
                {
                    SyncProfileModsAfterToggle(name, enabled);
                    ok++;
                }
                else if (!string.IsNullOrEmpty(toggleError) &&
                         toggleError.Contains("not found on this platform", StringComparison.OrdinalIgnoreCase))
                {
                    SendStatusMessage("warning",
                        "This mod's file isn't on the current platform yet — transfer or re-import it first.",
                        "mod_file_missing_platform");
                }
            }
            if (ok > 0)
                LogActivity($"[MODS] Bulk toggled {ok} mod(s) to {(enabled ? "enabled" : "disabled")}.");
            SendDataToWeb();
        }
        catch (Exception ex)
        {
            LogError($"Bulk toggle mods error: {ex.Message}");
        }
    }

    private void HandleUpdateModGroups(JsonElement groups) { 
        modGroups = groups.GetRawText();
        SaveSettings();
        LogActivity("[MODS] Mod groups updated.");
        SendDataToWeb();
    }
    private void HandlePromoteBundleToMod(string bundleName)
    {
        try
        {
            if (_modManager.PromoteBundleToMod(bundleName, out string error, out string newModKey))
            {
                if (!string.IsNullOrWhiteSpace(newModKey))
                    SyncProfileAfterBundlePromote(bundleName, newModKey);

                RefreshConflictCount();
                string displayName = Path.GetFileName(newModKey);
                SendStatusMessage(
                    "success",
                    $"Moved {displayName} to Mods.",
                    "bundle_moved_to_mods",
                    new object[] { displayName });
                SendDataToWeb();
            }
            else
            {
                SendStatusMessage(
                    "error",
                    string.IsNullOrWhiteSpace(error) ? "Failed to move bundle to Mods." : error,
                    "bundle_move_to_mods_failed",
                    new object[] { error ?? "" });
            }
        }
        catch (Exception ex)
        {
            LogError($"Promote Bundle Error: {ex.Message}");
            SendStatusMessage("error", "Failed to move bundle to Mods.", "bundle_move_to_mods_failed");
        }
    }

    private void SyncProfileAfterBundlePromote(string oldBundleKey, string newModKey)
    {
        string fileName = Path.GetFileName((oldBundleKey ?? "").Replace('\\', '/'));
        var oldCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeProfileModPath(oldBundleKey ?? ""),
            NormalizeProfileModPath($"Bundles/{fileName}"),
            NormalizeProfileModPath($"Disabled/{fileName}"),
            NormalizeProfileModPath(fileName)
        };
        string newNorm = NormalizeProfileModPath(newModKey);

        foreach (var profile in profiles)
        {
            for (int i = 0; i < profile.ProfileMods.Count; i++)
            {
                if (oldCandidates.Contains(NormalizeProfileModPath(profile.ProfileMods[i])))
                    profile.ProfileMods[i] = newNorm;
            }

            bool wasEnabled = profile.EnabledMods.Any(entry =>
                oldCandidates.Contains(NormalizeProfileModPath(entry)));
            profile.EnabledMods.RemoveAll(entry =>
                oldCandidates.Contains(NormalizeProfileModPath(entry)));

            if (wasEnabled &&
                !profile.EnabledMods.Any(entry =>
                    string.Equals(NormalizeProfileModPath(entry), newNorm, StringComparison.OrdinalIgnoreCase)))
            {
                profile.EnabledMods.Add(newNorm);
            }
        }

        SaveProfiles();
    }

    private void HandleUpdateModMetadata(string originalName, JsonElement metadata) {
        try {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var meta = JsonSerializer.Deserialize<F76ManagerApp.Managers.ModManager.ModMetadata>(metadata.GetRawText(), options);
            if (meta != null) {
                bool hasNexusLinkPayload = rootTryGetNexusLink(
                    metadata,
                    out var parsedModId,
                    out var parsedFileId,
                    out var parsedVersion,
                    out var parsedUploaded,
                    out var parsedUrl,
                    out var clearNexusLink,
                    out var nexusRemove);
                if (hasNexusLinkPayload)
                {
                    _modManager.UpdateNexusLinkage(
                        originalName,
                        parsedModId,
                        parsedFileId,
                        parsedVersion,
                        parsedUploaded,
                        parsedUrl,
                        clearNexusLink,
                        nexusRemove);
                    if (clearNexusLink || (nexusRemove?.Any ?? false))
                    {
                        _modUpdateCache.Remove(originalName);
                    }
                }

                if (metadata.TryGetProperty("isEnabled", out _))
                {
                    _modManager.UpdateModMetadata(originalName, meta);
                    string friendlyStatus = meta.IsEnabled ? "Enabled" : "Disabled";
                    SendStatusMessage("success", $"{originalName} updated to {friendlyStatus}.", "mod_updated_status", new object[] { originalName, friendlyStatus });
                }
                else if (hasNexusLinkPayload)
                {
                    SendStatusMessage("success", "Nexus link saved for mod.", "mod_nexus_link_saved");
                }
                else
                {
                    _modManager.UpdateModMetadata(originalName, meta);
                }
                
                RefreshConflictCount();
            }
            SendDataToWeb();
        } catch (Exception ex) {
            LogError($"Update Metadata Error: {ex.Message}");
            SendStatusMessage("error", "Failed to update mod metadata.", "update_metadata_failed");
        }
    }

    private static bool rootTryGetNexusLink(
        JsonElement metadata,
        out long? modId,
        out long? fileId,
        out string? version,
        out long? uploaded,
        out string? url,
        out bool clearRequested,
        out ModManager.NexusRemoveFlags? removeFlags)
    {
        modId = null;
        fileId = null;
        version = null;
        uploaded = null;
        url = null;
        clearRequested = false;
        removeFlags = null;

        if (metadata.TryGetProperty("nexusModId", out var mid) && mid.ValueKind == JsonValueKind.Number)
            modId = mid.GetInt64();
        if (metadata.TryGetProperty("nexusFileId", out var fid) && fid.ValueKind == JsonValueKind.Number)
            fileId = fid.GetInt64();
        if (metadata.TryGetProperty("nexusFileVersion", out var ver) && ver.ValueKind == JsonValueKind.String)
            version = ver.GetString();
        if (metadata.TryGetProperty("nexusFileUploaded", out var up) && up.ValueKind == JsonValueKind.Number)
            uploaded = up.GetInt64();
        if (metadata.TryGetProperty("nexusUrl", out var u) && u.ValueKind == JsonValueKind.String)
            url = u.GetString();
        if (metadata.TryGetProperty("clearNexusLink", out var clr) &&
            (clr.ValueKind == JsonValueKind.True || clr.ValueKind == JsonValueKind.False))
            clearRequested = clr.GetBoolean();

        if (metadata.TryGetProperty("nexusRemove", out var rem) && rem.ValueKind == JsonValueKind.Object)
        {
            removeFlags = new ModManager.NexusRemoveFlags();
            if (rem.TryGetProperty("url", out var rUrl) && rUrl.ValueKind == JsonValueKind.True)
                removeFlags.Url = true;
            if (rem.TryGetProperty("modId", out var rMod) && rMod.ValueKind == JsonValueKind.True)
                removeFlags.ModId = true;
            if (rem.TryGetProperty("fileId", out var rFile) && rFile.ValueKind == JsonValueKind.True)
                removeFlags.FileId = true;
            if (rem.TryGetProperty("version", out var rVer) && rVer.ValueKind == JsonValueKind.True)
                removeFlags.Version = true;
            if (rem.TryGetProperty("uploaded", out var rUp) && rUp.ValueKind == JsonValueKind.True)
                removeFlags.Uploaded = true;
            if (!removeFlags.Any) removeFlags = null;
        }

        bool hasRemove = removeFlags?.Any ?? false;
        return clearRequested || hasRemove || modId.HasValue || fileId.HasValue ||
               !string.IsNullOrWhiteSpace(version) || !string.IsNullOrWhiteSpace(url);
    }

    private void HandleCheckModUpdates(JsonElement root)
    {
        if (_nexusManager == null || !nexusLoggedIn)
        {
            SendMessageToWeb(JsonSerializer.Serialize(new { type = "MOD_UPDATES_RESULT", updates = Array.Empty<object>(), error = "nexus_not_connected" }));
            return;
        }

        string? singleName = null;
        if (TryGetString(root, "originalName", out var oneName)) singleName = oneName;

        Task.Run(async () =>
        {
            var updates = new List<object>();
            var mods = SafeGetRealMods();

            foreach (dynamic mod in mods)
            {
                try
                {
                    string originalName = mod.originalName;
                    if (!string.IsNullOrEmpty(singleName) &&
                        !string.Equals(singleName, originalName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    long? nexusModId = mod.nexusModId;
                    if (!nexusModId.HasValue || nexusModId.Value <= 0) continue;

                    long? installedFileId = mod.nexusFileId;
                    string installedVersion = mod.nexusFileVersion ?? mod.version ?? "";
                    long? installedUploaded = mod.nexusFileUploaded;

                    var check = await _nexusManager.CheckModUpdateAsync(
                        originalName,
                        nexusModId.Value,
                        installedFileId,
                        installedVersion,
                        installedUploaded);

                    if (!string.IsNullOrEmpty(check.Error)) continue;

                    _modUpdateCache[originalName] = new CachedModUpdate
                    {
                        HasUpdate = check.HasUpdate,
                        IsUnverifiedLink = check.IsUnverifiedLink,
                        LatestFileId = check.LatestFileId,
                        LatestVersion = check.LatestVersion ?? "",
                        LatestFileName = check.LatestFileName ?? "",
                        LatestUploaded = check.LatestUploaded
                    };

                    updates.Add(new
                    {
                        originalName,
                        hasUpdate = check.HasUpdate,
                        isUnverifiedLink = check.IsUnverifiedLink,
                        nexusModId = nexusModId.Value,
                        latestFileId = check.LatestFileId,
                        latestVersion = check.LatestVersion,
                        latestFileName = check.LatestFileName,
                        latestUploaded = check.LatestUploaded
                    });
                }
                catch (Exception ex)
                {
                    LogError($"[NEXUS] Update check failed: {ex.Message}");
                }
            }

            this.Invoke(() =>
            {
                SendMessageToWeb(JsonSerializer.Serialize(new { type = "MOD_UPDATES_RESULT", updates }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                SendDataToWeb();
            });
        });
    }

    private void HandleCreateProfile(string name, string mode = "clone") 
    {
        if (string.IsNullOrEmpty(name)) return;
        if (profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) {
            SendStatusMessage("error", "Profile with this name already exists.", "profile_exists");
            return;
        }

        var currentP = profiles.FirstOrDefault(p => p.Name == activeProfile);
        if (currentP != null) {
            var captured = CaptureCurrentState(activeProfile);
            currentP.Settings = captured.Settings;
            currentP.EnabledMods = captured.EnabledMods;
        }

        Profile newProfile;
        
        if (mode == "empty") {
            newProfile = new Profile { Name = name };
            newProfile.Settings = SafeGetRealSettings();
            newProfile.EnabledMods = new List<string>();
            newProfile.ProfileMods = new List<string>();
        } else {
            newProfile = CaptureCurrentState(name);
        }

        profiles.Add(newProfile);
        
        activeProfile = name;
        
        ApplyProfileState(newProfile);
        
        SaveProfiles();
        SendStatusMessage("success", $"Profile '{name}' created ({mode}).", "profile_created", new object[] { name, mode });
        SendDataToWeb();
    }

    private void HandleDeleteProfile(string name) 
    {
        var p = profiles.FirstOrDefault(p => p.Name == name);
        if (p == null) return;
        
        if (name == "Default Profile") {
            SendStatusMessage("error", "Cannot delete Default Profile.", "delete_default_profile_error");
            return;
        }

        profiles.Remove(p);
        
        if (activeProfile == name) {
            activeProfile = "Default Profile";
            var def = profiles.FirstOrDefault(p => p.Name == "Default Profile");
            if (def != null) ApplyProfileState(def);
        }

        SaveProfiles();
        SendStatusMessage("success", "Profile deleted.", "profile_deleted");
        SendDataToWeb();
    }

    private void HandleSwitchProfile(string name) 
    {
        var target = profiles.FirstOrDefault(p => p.Name == name);
        if (target == null) return;

        var currentP = profiles.FirstOrDefault(p => p.Name == activeProfile);
        if (currentP != null) {
            var captured = CaptureCurrentState(activeProfile);
            currentP.Settings = captured.Settings;
            currentP.EnabledMods = captured.EnabledMods;
        }

        ApplyProfileState(target);
        activeProfile = name;

        SaveProfiles();
        SendStatusMessage("success", $"Switched to {name}.", "profile_switched", new object[] { name });
        SendDataToWeb();
    }

    private void HandleOpenInBrowser(string url)
    {
        LogActivity($"[RCV-OPEN] Request to open URL in browser: {url}");
        try {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        } catch (Exception ex) {
            LogError($"[RCV-OPEN] Failed to open URL: {url}. Error: {ex.Message}");
        }
    }

    private void HandleBundleBrowseFolder()
    {
        this.Invoke(() =>
        {
            using var fbd = new FolderBrowserDialog { Description = "Select folder to add to bundle workspace" };
            if (fbd.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(fbd.SelectedPath))
                return;

            SendMessageToWeb(JsonSerializer.Serialize(new
            {
                type = "FOLDER_SELECTED",
                path = fbd.SelectedPath
            }));
        });
    }

    private static string ResolveBundleBa2Path(string ba2Path)
    {
        if (string.IsNullOrWhiteSpace(ba2Path))
            throw new FileNotFoundException("BA2 path is empty.");
        if (File.Exists(ba2Path))
            return ba2Path;
        string resolved = Path.IsPathRooted(ba2Path)
            ? ba2Path
            : Path.Combine(AppPaths.DataPath, ba2Path);
        if (File.Exists(resolved))
            return resolved;
        throw new FileNotFoundException("BA2 file not found.", ba2Path);
    }

    private void HandleBundleExtract(string archive, List<string> paths)
    {
        this.Invoke(() =>
        {
            using var fbd = new FolderBrowserDialog { Description = "Select destination folder for extracted files" };
            if (fbd.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(fbd.SelectedPath))
                return;

            string destDir = fbd.SelectedPath;
            string archivePath;
            try
            {
                SyncAppPaths();
                archivePath = ResolveBundleBa2Path(archive);
            }
            catch (Exception ex)
            {
                LogError($"[BUNDLE] Extract resolve failed: {ex.Message}");
                SendStatusMessage("error", $"Extract failed: {ex.Message}", "bundle_extract_failed", new object[] { ex.Message });
                return;
            }

            HashSet<string>? includePaths = null;
            if (paths.Count > 0)
            {
                includePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    includePaths.Add(path.Replace('\\', '/').ToLowerInvariant());
                }
                if (includePaths.Count == 0)
                    includePaths = null;
            }

            string archiveName = Path.GetFileName(archivePath);
            int pathCount = includePaths?.Count ?? 0;
            SendStatusMessage(
                "info",
                pathCount > 0
                    ? $"Extracting {pathCount} file(s) from {archiveName}..."
                    : $"Extracting {archiveName}...",
                "bundle_extract_started",
                pathCount > 0 ? new object[] { archiveName, pathCount } : new object[] { archiveName });
            LogActivity($"[BUNDLE] Extract '{archivePath}' -> '{destDir}' ({(includePaths == null ? "all" : includePaths.Count.ToString())} path(s))");

            Task.Run(() =>
            {
                try
                {
                    BA2Utility.Extract(archivePath, destDir, LogActivity, includePaths);
                    this.Invoke(() => SendStatusMessage(
                        "success",
                        pathCount > 0
                            ? $"Extracted {pathCount} file(s) from {archiveName}."
                            : $"Extracted {archiveName} successfully.",
                        "bundle_extract_success",
                        pathCount > 0 ? new object[] { archiveName, pathCount } : new object[] { archiveName }));
                    LogActivity($"[BUNDLE] Extract complete: {archivePath} -> {destDir}");
                }
                catch (Exception ex)
                {
                    LogError($"[BUNDLE] Extract failed: {ex.Message}");
                    this.Invoke(() => SendStatusMessage(
                        "error",
                        $"Extract failed: {ex.Message}",
                        "bundle_extract_failed",
                        new object[] { ex.Message }));
                }
            });
        });
    }

    private void HandleBundleListBa2(string ba2Path)
    {
        Task.Run(() =>
        {
            try
            {
                ba2Path = ResolveBundleBa2Path(ba2Path);

                var result = BA2Utility.ListEntries(ba2Path, LogActivity);
                var entries = result.Entries.Select(e => new { path = e.Path, unpackedSize = e.UnpackedSize }).ToList();
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "BA2_CONTENTS",
                    path = ba2Path,
                    archiveType = result.ArchiveType,
                    fileCount = entries.Count,
                    entries
                }));
            }
            catch (Exception ex)
            {
                LogError($"[BUNDLE] List BA2 failed: {ex.Message}");
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "BA2_CONTENTS",
                    path = ba2Path,
                    error = ex.Message,
                    entries = Array.Empty<object>()
                }));
            }
        });
    }

    private void HandleBundleListFolder(string folderPath)
    {
        Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                    throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

                string normalizedRoot = Path.GetFullPath(folderPath);
                var entries = new List<object>();
                foreach (var file in Directory.GetFiles(normalizedRoot, "*.*", SearchOption.AllDirectories))
                {
                    string relPath = Path.GetRelativePath(normalizedRoot, file).Replace('\\', '/');
                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch { }
                    entries.Add(new { path = relPath, unpackedSize = size });
                }

                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "FOLDER_CONTENTS",
                    path = normalizedRoot,
                    fileCount = entries.Count,
                    entries
                }));
            }
            catch (Exception ex)
            {
                LogError($"[BUNDLE] List folder failed: {ex.Message}");
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "FOLDER_CONTENTS",
                    path = folderPath,
                    error = ex.Message,
                    entries = Array.Empty<object>()
                }));
            }
        });
    }

    private void HandleBundleListGameBa2s()
    {
        Task.Run(() =>
        {
            try
            {
                SyncAppPaths();
                string dataPath = AppPaths.DataPath;
                var files = new List<object>();
                if (!string.IsNullOrWhiteSpace(dataPath) && Directory.Exists(dataPath))
                {
                    var sorted = Directory.GetFiles(dataPath, "*.ba2", SearchOption.TopDirectoryOnly)
                        .Select(file =>
                        {
                            long size = 0;
                            try { size = new FileInfo(file).Length; } catch { }
                            return new { name = Path.GetFileName(file), path = file, size };
                        })
                        .OrderBy(f => f.name, StringComparer.OrdinalIgnoreCase);
                    foreach (var item in sorted)
                        files.Add(item);
                }

                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "GAME_BA2_LIST",
                    dataPath,
                    files
                }));
            }
            catch (Exception ex)
            {
                LogError($"[BUNDLE] List game BA2s failed: {ex.Message}");
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "GAME_BA2_LIST",
                    dataPath = AppPaths.DataPath,
                    files = Array.Empty<object>(),
                    error = ex.Message
                }));
            }
        });
    }

    private void HandleBrowseFolder(string target, string? requestedPath)
    {
        using var fbd = new FolderBrowserDialog();
        string candidate = !string.IsNullOrWhiteSpace(requestedPath) ? requestedPath.Trim() : ResolveFolderPathForTarget(target);
        if (!Directory.Exists(candidate))
        {
            candidate = ResolveFolderPathForTarget(target);
        }
        if (Directory.Exists(candidate))
        {
            fbd.SelectedPath = candidate;
        }

        if (fbd.ShowDialog() == DialogResult.OK) {
            if (target == "game") {
                gamePath = fbd.SelectedPath;
                if (_platformManager.CurrentPlatform == GamePlatform.Steam) steamGamePath = gamePath;
                else xboxGamePath = gamePath;
            }
            else if (target == "docs") {
                documentsPath = fbd.SelectedPath;
                if (_platformManager.CurrentPlatform == GamePlatform.Steam) steamDocsPath = documentsPath;
                else xboxDocsPath = documentsPath;
            }
            else if (target == "localAppData") {
                localAppDataPath = fbd.SelectedPath;
                if (_platformManager.CurrentPlatform == GamePlatform.Steam) steamLocalPath = localAppDataPath;
                else xboxLocalPath = localAppDataPath;
            }
            else if (target == "strings") {
                stringsPath = fbd.SelectedPath;
                if (_platformManager.CurrentPlatform == GamePlatform.Steam) steamStringsPath = stringsPath;
                else xboxStringsPath = stringsPath;
            }

            SaveSettings();
            SendDataToWeb();
        }
    }

    private void HandleImportUserTheme()
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "F76 Theme Package (*.f76theme)|*.f76theme",
            Title = "Import theme package",
        };
        if (ofd.ShowDialog() != DialogResult.OK) return;

        if (!_themePackageLoader.TryInstallPackage(ofd.FileName, out var theme, out var err) || theme == null)
        {
            SendMessageToWeb(System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "THEME_IMPORT_RESULT",
                ok = false,
                error = err ?? "Import failed.",
            }));
            LogError($"[THEMES] Import failed: {err}");
            return;
        }

        SendDataToWeb();
        SendMessageToWeb(System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "THEME_IMPORT_RESULT",
            ok = true,
            themeId = theme.Id,
            displayName = theme.DisplayName,
        }));
        LogActivity($"[THEMES] Imported theme '{theme.DisplayName}' ({theme.Id})");
    }

    private void HandleOpenThemesFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ThemesFolder);
            Process.Start(new ProcessStartInfo(AppPaths.ThemesFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogError($"[THEMES] Could not open Themes folder: {ex.Message}");
        }
    }

    private void HandleReloadUserThemes()
    {
        var result = _themePackageLoader.ReloadWithResult();
        SendDataToWeb();
        SendMessageToWeb(JsonSerializer.Serialize(new
        {
            type = "THEME_RELOAD_RESULT",
            themesFolder = result.ThemesFolder,
            loaded = result.Loaded.Select(t => new { t.Id, t.DisplayName, fileName = t.FileName }).ToArray(),
            rejected = result.Rejected.Select(t => new { fileName = t.FileName, error = t.Error }).ToArray(),
        }));
        LogActivity($"[THEMES] Reloaded {result.Loaded.Count} theme(s), {result.Rejected.Count} rejected.");
    }

    private void HandleBrowseExecutable(string target, string? requestedPath)
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            Title = target == "rarToolExe"
                ? "Select WinRAR (WinRAR.exe) or UnRAR.exe / Rar.exe / 7z.exe"
                : "Select 7-Zip (7z.exe)"
        };
        string? hint = !string.IsNullOrWhiteSpace(requestedPath) ? requestedPath.Trim() : null;
        if (!string.IsNullOrEmpty(hint))
        {
            try
            {
                if (File.Exists(hint))
                {
                    ofd.InitialDirectory = Path.GetDirectoryName(hint);
                    ofd.FileName = Path.GetFileName(hint);
                }
                else if (Directory.Exists(hint))
                {
                    ofd.InitialDirectory = hint;
                }
            }
            catch { }
        }

        if (ofd.ShowDialog() != DialogResult.OK) return;

        string selected = ofd.FileName;
        if (target == "sevenZipExe")
            sevenZipPath = selected;
        else if (target == "rarToolExe")
            rarExtractorPath = selected;
        else
            return;

        SaveSettings();
        SyncAppPaths();
        SendDataToWeb();
    }

    private void RefreshConflictCount(bool showModal = false, bool sendDataUpdate = true)
    {
        Task.Run(() => {
            try {
                SyncAppPaths();
                
                var enabledModPaths = _modManager.GetEnabledModPaths();
                LogActivity($"[UI] Enabled Mod Paths for Scan: {string.Join(", ", enabledModPaths.Select(Path.GetFileName))}");
                var searchPaths = new List<string> { AppPaths.DataPath };
                
                LogActivity($"[UI] Refreshing conflict count for {enabledModPaths.Count} enabled mods.");
                
                var conflicts = _conflictManager.DetectConflicts(enabledModPaths, searchPaths);
                if (showModal && conflicts != null && conflicts.Count > 0) {
                    var enabledMods = _modManager.GetModsList()
                        .Where(m => (string)((dynamic)m).status == "enabled")
                        .Select(m => (string)((dynamic)m).originalName)
                        .ToList();
                    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                    SendMessageToWeb(JsonSerializer.Serialize(new { type = "conflicts_found", conflicts = conflicts, requestedMods = enabledMods, isFromDeploy = false }, options));
                }
                
                if (sendDataUpdate) SendDataToWeb();
            } catch (Exception ex) {
                LogError($"RefreshConflictCount Error: {ex.Message}");
            }
        });
    }

    private void HandleNexusLogin()
    {
        if (!NexusManager.IsSsoAvailable)
        {
            SendStatusMessage(
                "error",
                "Nexus Mods sign-in is not available until this application is registered with Nexus Mods.",
                "nexus_auth_unavailable");
            return;
        }

        if (_nexusManager != null)
            Task.Run(async () => await _nexusManager.ConnectSSO());
    }

    private void HandleNexusLogout()
    {
        nexusLoggedIn = false;
        InitializeNexusManager(null);
        SaveSettings();
        SanitizeProfileCredentialSettings();
        SendStatusMessage("success", "Logged out of Nexus Mods.", "nexus_logout_success");
        SendDataToWeb();
    }

    private void HandleBackupIni() 
    {
        try {
            string backupsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
            if (!Directory.Exists(backupsDir)) Directory.CreateDirectory(backupsDir);

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string customSrc = AppPaths.CustomIniPath;
            string prefsSrc = AppPaths.PrefsIniPath;

            bool anyBackedUp = false;

            if (File.Exists(customSrc)) {
                File.Copy(customSrc, Path.Combine(backupsDir, $"{Path.GetFileNameWithoutExtension(customSrc)}_{ts}.ini"));
                anyBackedUp = true;
            }
            if (File.Exists(prefsSrc)) {
                File.Copy(prefsSrc, Path.Combine(backupsDir, $"{Path.GetFileNameWithoutExtension(prefsSrc)}_{ts}.ini"));
                anyBackedUp = true;
            }

            if (anyBackedUp)
            {
                SendStatusMessage("success", "INIs backed up to /Backups folder.", "inis_backed_up");
                HandleListBackups();
            }
            else SendStatusMessage("error", "No INI files found to backup.", "no_ini_found");
        } catch (Exception ex) {
            LogError($"Backup INI Failed: {ex.Message}");
            SendStatusMessage("error", "Backup failed.", "backup_failed");
        }
    }

    private static string SanitizeConfigBackupEntryName(string relativeKey)
    {
        string s = (relativeKey ?? "").Replace('\\', '/').Trim().TrimStart('/');
        s = s.Replace("/", "_");
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(s)) return "unknown";
        return s.Length > 200 ? s.Substring(s.Length - 200) : s;
    }

    private void HandleBackupConfigs()
    {
        try
        {
            string platformLabel = _platformManager.GetPlatformLabel();
            if (string.IsNullOrWhiteSpace(platformLabel)) platformLabel = "Unknown";

            string backupsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups", "Configs", platformLabel);
            Directory.CreateDirectory(backupsRoot);

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string destDir = Path.Combine(backupsRoot, $"ConfigsBackup_{ts}");
            Directory.CreateDirectory(destDir);

            var mods = SafeGetRealMods();
            int backedUp = 0;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in mods)
            {
                string originalName = (string)((dynamic)m).originalName;
                if (string.IsNullOrWhiteSpace(originalName)) continue;

                string lower = originalName.ToLowerInvariant();
                if (!lower.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
                    !lower.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                    !lower.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    continue;

                string src = _modManager.ResolveListKeyToFullPath(originalName);
                if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) continue;

                string baseName = SanitizeConfigBackupEntryName(originalName);
                string ext = Path.GetExtension(baseName);
                string stem = string.IsNullOrEmpty(ext) ? baseName : baseName[..^ext.Length];
                string destFile = baseName;
                int n = 1;
                while (usedNames.Contains(destFile))
                {
                    destFile = string.IsNullOrEmpty(ext) ? $"{stem}_{n}" : $"{stem}_{n}{ext}";
                    n++;
                }
                usedNames.Add(destFile);

                File.Copy(src, Path.Combine(destDir, destFile), overwrite: false);
                backedUp++;
            }

            if (backedUp == 0)
            {
                try { if (Directory.Exists(destDir)) Directory.Delete(destDir, true); } catch { }
                SendStatusMessage("error", "No config files found to backup.", "no_config_files_to_backup");
                return;
            }

            string manifest = $"Platform: {platformLabel}{Environment.NewLine}Created (local): {DateTime.Now:u}{Environment.NewLine}";
            File.WriteAllText(Path.Combine(destDir, "README.txt"), manifest);

            SendStatusMessage("success",
                $"Backed up {backedUp} config file(s) to Backups/Configs/{platformLabel}/.",
                "configs_backed_up");
            HandleListBackups();
        }
        catch (Exception ex)
        {
            LogError($"Backup Configs failed: {ex.Message}");
            SendStatusMessage("error", "Config backup failed.", "configs_backup_failed");
        }
    }

    private static bool IsConfigListKey(string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName)) return false;
        string lower = originalName.ToLowerInvariant();
        return lower.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
               lower.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
               lower.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProtectedCoreConfigKey(string originalName) =>
        AppPaths.IsProtectedCoreIniListKey(originalName);

    private void HandleDeleteAllConfigs()
    {
        Task.Run(() =>
        {
            try
            {
                SyncAppPaths();
                var configNames = _modManager.GetModsList()
                    .Select(m => (string)((dynamic)m).originalName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Where(IsConfigListKey)
                    .Where(n => !IsProtectedCoreConfigKey(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (configNames.Count == 0)
                {
                    this.Invoke(() => SendStatusMessage("info", "No config files to delete.", "delete_all_configs_none"));
                    return;
                }

                LogActivity($"[RCV-DELETE-ALL-CONFIGS] Request to delete {configNames.Count} config file(s).");

                int deletedCount = 0;
                if (activeProfile != "Default Profile")
                {
                    var p = profiles.FirstOrDefault(x => x.Name == activeProfile);
                    if (p != null)
                    {
                        foreach (var name in configNames)
                        {
                            string norm = name.Replace("\\", "/");
                            p.ProfileMods.RemoveAll(x =>
                                x.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                                x.Equals(name, StringComparison.OrdinalIgnoreCase));
                            p.EnabledMods.RemoveAll(x =>
                                x.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                                x.Equals(name, StringComparison.OrdinalIgnoreCase));
                            deletedCount++;
                        }
                        SaveProfiles();
                    }
                }
                else
                {
                    foreach (var name in configNames)
                    {
                        _modManager.DeleteMod(name);
                        deletedCount++;
                    }
                }

                this.Invoke(() =>
                {
                    RefreshConflictCount();
                    SendDataToWeb();
                    SendStatusMessage(
                        "success",
                        $"Deleted {deletedCount} config file{(deletedCount == 1 ? "" : "s")}.",
                        "delete_all_configs_success",
                        new object[] { deletedCount });
                });
            }
            catch (Exception ex)
            {
                LogError($"[DELETE_ALL_CONFIGS_FAIL] {ex.Message}");
                this.Invoke(() => SendStatusMessage(
                    "error",
                    $"Delete all configs failed: {ex.Message}",
                    "delete_all_configs_failed",
                    new object[] { ex.Message }));
            }
        });
    }

    private void HandleBackupMods()
    {
        Task.Run(() =>
        {
            string? zipPath = null;
            try
            {
                SyncAppPaths();

                string backupsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
                Directory.CreateDirectory(backupsDir);

                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                zipPath = Path.Combine(backupsDir, $"ModsBackup_{ts}.zip");
                int backedUpCount = 0;
                int skippedCount = 0;

                var modEntries = _modManager.CollectModsBackupEntries();
                var addedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    foreach (var (sourcePath, entryName) in modEntries)
                    {
                        if (!addedEntryNames.Add(entryName))
                        {
                            skippedCount++;
                            continue;
                        }

                        try
                        {
                            archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
                            backedUpCount++;
                        }
                        catch (Exception fileEx)
                        {
                            skippedCount++;
                            LogError($"[BACKUP] Skipped '{sourcePath}': {fileEx.Message}");
                        }
                    }

                    backedUpCount += AddOptionalFileToArchive(archive, AppPaths.ModsMetadataFile, "Settings/mods.json");
                    backedUpCount += AddOptionalFileToArchive(archive, AppPaths.ProfilesFile, "Profiles/profiles.json");
                }

                if (backedUpCount > 0)
                {
                    if (skippedCount > 0)
                        LogActivity($"[BACKUP] Mods backup completed with {skippedCount} skipped file(s).");
                    SendStatusMessage("success", "Mods backed up to /Backups folder.", "mods_backed_up");
                    try { HandleListBackups(); } catch (Exception listEx) { LogError($"[BACKUP] List refresh failed: {listEx.Message}"); }
                }
                else
                {
                    if (zipPath != null && File.Exists(zipPath)) File.Delete(zipPath);
                    SendStatusMessage("error", "No mod files found to backup.", "no_mod_files_found");
                }
            }
            catch (Exception ex)
            {
                LogError($"Backup Mods Failed: {ex.Message}");
                try
                {
                    if (zipPath != null && File.Exists(zipPath)) File.Delete(zipPath);
                }
                catch { }
                SendStatusMessage("error", "Mods backup failed.", "mods_backup_failed");
            }
        });
    }

    private int AddFilteredFilesToArchive(ZipArchive archive, string sourceDirectory, string archiveDirectory, HashSet<string> allowedExtensions, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory)) return 0;

        int added = 0;
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var filePath in Directory.GetFiles(sourceDirectory, "*.*", searchOption))
        {
            if (!allowedExtensions.Contains(Path.GetExtension(filePath))) continue;

            string relativePath = Path.GetRelativePath(sourceDirectory, filePath).Replace("\\", "/");
            string entryName = $"{archiveDirectory}/{relativePath}";
            archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
            added++;
        }

        return added;
    }

    private int AddOptionalFileToArchive(ZipArchive archive, string sourceFilePath, string archiveEntryName)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath)) return 0;
        archive.CreateEntryFromFile(sourceFilePath, archiveEntryName, CompressionLevel.Optimal);
        return 1;
    }

    private void HandleResetConfig() 
    {
        try {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            LoadSettings();
            SyncAppPaths();
            SendStatusMessage("success", "Configuration reset to defaults.", "config_reset");
            SendDataToWeb(); 
        } catch (Exception ex) {
            SendStatusMessage("error", $"Reset failed: {ex.Message}", "reset_failed", new object[] { ex.Message });
        }
    }

    private void HandleClearErrorLog()
    {
        if (this.InvokeRequired)
        {
            this.Invoke(HandleClearErrorLog);
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(logErrorPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(logErrorPath, string.Empty);
            LogActivity("[LOGS] Error log cleared by user.");
            SendDataToWeb();
        }
        catch (Exception ex)
        {
            LogError($"[LOGS] Failed to clear error log: {ex.Message}");
        }
    }

    private async Task HandleClearCacheTask() 
    { 
        if (this.InvokeRequired) { this.Invoke(async () => await HandleClearCacheTask()); return; }
        if (webView?.CoreWebView2 != null) {
            await webView.CoreWebView2.Profile.ClearBrowsingDataAsync(); 
            SendStatusMessage("success", "Application cache cleared.", "cache_cleared");
        }
    }
    
    private static string ResolveDataDirFromGamePath(string gamePathValue)
    {
        if (string.IsNullOrWhiteSpace(gamePathValue)) return "";
        return gamePathValue.EndsWith("Data", StringComparison.OrdinalIgnoreCase)
            ? gamePathValue
            : Path.Combine(gamePathValue, "Data");
    }

    private static string ResolveGameRootFromGamePath(string gamePathValue)
    {
        if (string.IsNullOrWhiteSpace(gamePathValue)) return "";
        try
        {
            return gamePathValue.EndsWith("Data", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(Path.Combine(gamePathValue, ".."))
                : Path.GetFullPath(gamePathValue.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch { return ""; }
    }

    /// <summary>Resolves a platform's mod storage locations (Data, Strings, GameRoot, Documents, INI prefix)
    /// for the active mode (Virtual Mod Mode staging vs. the live game folders).</summary>
    private (string data, string strings, string gameRoot, string docs, string prefix) ResolvePlatformModLocation(GamePlatform platform)
    {
        bool isXboxPlat = platform == GamePlatform.Xbox;
        string prefix = isXboxPlat ? "Project76" : "Fallout76";
        string folder = isXboxPlat ? "Xbox" : "Steam";
        string docs = isXboxPlat
            ? (string.IsNullOrWhiteSpace(xboxDocsPath) ? documentsPath : xboxDocsPath)
            : (string.IsNullOrWhiteSpace(steamDocsPath) ? documentsPath : steamDocsPath);

        if (virtualModMode)
        {
            string baseStaging = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Managed Staging", folder);
            string vData = Path.Combine(baseStaging, "Data");
            return (vData, Path.Combine(vData, "Strings"), Path.Combine(baseStaging, "GameRoot"), docs, prefix);
        }

        bool isCurrent = _platformManager.CurrentPlatform == platform;
        string gp = isCurrent ? gamePath : (isXboxPlat ? xboxGamePath : steamGamePath);
        string data = ResolveDataDirFromGamePath(gp);
        string gameRoot = ResolveGameRootFromGamePath(gp);
        string sp = isCurrent ? stringsPath : (isXboxPlat ? xboxStringsPath : steamStringsPath);
        if (string.IsNullOrWhiteSpace(sp) && !string.IsNullOrEmpty(data)) sp = Path.Combine(data, "Strings");
        return (data, sp, gameRoot, docs, prefix);
    }

    /// <summary>Merges the source platform's [Archive] load lists into the destination platform's Custom.ini
    /// (dedup, excluding vanilla). Preserves manual entries that loose-file mods like UniMap/TZMap require.</summary>
    private int MergeArchiveIniEntries(string srcDocs, string srcPrefix, string dstDocs, string dstPrefix)
    {
        if (string.IsNullOrWhiteSpace(srcDocs) || string.IsNullOrWhiteSpace(dstDocs)) return 0;
        string srcCustom = Path.Combine(srcDocs, $"{srcPrefix}Custom.ini");
        string dstCustom = Path.Combine(dstDocs, $"{dstPrefix}Custom.ini");
        if (!File.Exists(srcCustom)) return 0;

        int added = 0;
        foreach (var key in new[] { "sResourceArchive2List", "sResourceIndexFileList" })
        {
            string srcVal = _configManager.ReadIniValue(srcCustom, "Archive", key) ?? "";
            if (string.IsNullOrWhiteSpace(srcVal)) continue;
            string dstVal = _configManager.ReadIniValue(dstCustom, "Archive", key) ?? "";

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in dstVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (seen.Add(entry)) result.Add(entry);

            int addedThisKey = 0;
            foreach (var entry in srcVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (_modManager.IsVanillaModFile(entry)) continue;
                if (seen.Add(entry)) { result.Add(entry); added++; addedThisKey++; }
            }

            if (addedThisKey > 0)
            {
                // Fallout 76 requires no spaces after commas in the archive list.
                _configManager.UpdateBothInis("Archive", key, string.Join(",", result),
                    overrideDocsPath: dstDocs, onlyCustom: true, overridePrefix: dstPrefix);
            }
        }
        return added;
    }

    private static bool IsFallout76Running()
    {
        return Process.GetProcessesByName("Fallout76").Length > 0
            || Process.GetProcessesByName("Project76_GamePass").Length > 0;
    }

    private void HandleTransferModsAcrossPlatforms(string direction, bool overwrite)
    {
        try
        {
            if (IsFallout76Running())
            {
                SendStatusMessage("warning",
                    "Close Fallout 76 before transferring mods — its files are locked while the game is running.",
                    "transfer_mods_game_running");
                return;
            }

            var current = _platformManager.CurrentPlatform;
            var other = current == GamePlatform.Steam ? GamePlatform.Xbox : GamePlatform.Steam;

            bool toOther = !string.Equals(direction, "from-other", StringComparison.OrdinalIgnoreCase);
            var srcPlatform = toOther ? current : other;
            var dstPlatform = toOther ? other : current;

            var src = ResolvePlatformModLocation(srcPlatform);
            var dst = ResolvePlatformModLocation(dstPlatform);

            if (string.IsNullOrWhiteSpace(src.data) || !Directory.Exists(src.data))
            {
                SendStatusMessage("error",
                    $"No {srcPlatform} mod folder found to copy from. Set the {srcPlatform} game path in Settings (or import some mods there) first.",
                    "transfer_mods_no_source", new object[] { srcPlatform.ToString() });
                return;
            }
            if (string.IsNullOrWhiteSpace(dst.data))
            {
                SendStatusMessage("error",
                    $"The {dstPlatform} install path isn't set. Configure it in Settings, then try again.",
                    "transfer_mods_no_path", new object[] { dstPlatform.ToString() });
                return;
            }

            int copied;
            try
            {
                copied = _modManager.TransferModsToPlatform(
                    src.data, src.strings, src.gameRoot,
                    dst.data, dst.strings, dst.gameRoot, overwrite);
            }
            catch (UnauthorizedAccessException)
            {
                SendStatusMessage("error",
                    $"Couldn't write to the {dstPlatform} folder (access denied). Xbox/Game Pass installs are protected — run Fallout 76 Manager as administrator, or use Virtual Mod Mode.",
                    "transfer_mods_denied", new object[] { dstPlatform.ToString() });
                return;
            }
            catch (IOException ioEx)
            {
                LogError($"[TRANSFER] Mod transfer aborted (file in use): {ioEx.Message}");
                SendStatusMessage("warning",
                    "A mod file is in use, so the transfer was cancelled. Close Fallout 76 (and any tools using your mods), then try again.",
                    "transfer_mods_locked");
                return;
            }

            int iniAdded = MergeArchiveIniEntries(src.docs, src.prefix, dst.docs, dst.prefix);

            LogActivity($"[TRANSFER] {srcPlatform} -> {dstPlatform}: copied {copied} file(s), {iniAdded} INI archive entr(y/ies) (mode={(virtualModMode ? "VMM" : "direct")}).");
            SendStatusMessage("success",
                $"Copied {copied} mod file(s) from {srcPlatform} to {dstPlatform}. Your Nexus links and load order are shared, so they carried over automatically.",
                "transfer_mods_done", new object[] { copied, srcPlatform.ToString(), dstPlatform.ToString() });

            RefreshConflictCount(false, sendDataUpdate: false);
            SendDataToWeb();
        }
        catch (Exception ex)
        {
            LogError($"[TRANSFER] Mod transfer failed: {ex.Message}");
            SendStatusMessage("error", $"Mod transfer failed: {ex.Message}", "transfer_mods_failed");
        }
    }

    private void HandleSwitchPlatform()
    {
        if (_platformManager.CurrentPlatform == GamePlatform.Steam) {
            steamGamePath = gamePath; steamDocsPath = documentsPath; steamLocalPath = localAppDataPath;
            steamStringsPath = stringsPath;
        } else {
            xboxGamePath = gamePath; xboxDocsPath = documentsPath; xboxLocalPath = localAppDataPath;
            xboxStringsPath = stringsPath;
        }

        var newPlatform = _platformManager.CurrentPlatform == GamePlatform.Steam ? GamePlatform.Xbox : GamePlatform.Steam;
        _platformManager.SetPlatform(newPlatform);

        if (newPlatform == GamePlatform.Steam) {
            if (!string.IsNullOrEmpty(steamGamePath) &&
                (steamGamePath.Contains("XboxGames", StringComparison.OrdinalIgnoreCase) ||
                 steamGamePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                 steamGamePath.Contains("ModifiableWindowsApps", StringComparison.OrdinalIgnoreCase)))
            {
                steamGamePath = "";
            }

            gamePath = !string.IsNullOrEmpty(steamGamePath) ? steamGamePath : _platformManager.GetDefaultGamePath();
            documentsPath = !string.IsNullOrEmpty(steamDocsPath) ? steamDocsPath : _platformManager.GetDefaultDocumentsPath();
            localAppDataPath = !string.IsNullOrEmpty(steamLocalPath) ? steamLocalPath : _platformManager.GetDefaultLocalAppDataPath();
            stringsPath = !string.IsNullOrEmpty(steamStringsPath) ? steamStringsPath : GetDefaultStringsPathFromGamePath(gamePath);
        } else {
            if (!string.IsNullOrEmpty(xboxGamePath) && xboxGamePath.Contains("Steam", StringComparison.OrdinalIgnoreCase))
            {
                xboxGamePath = ""; 
            }
            
            gamePath = !string.IsNullOrEmpty(xboxGamePath) ? xboxGamePath : _platformManager.GetDefaultGamePath();
            
            if (gamePath.Contains("Steam", StringComparison.OrdinalIgnoreCase))
            {
                gamePath = _platformManager.GetDefaultGamePath();
            }

            documentsPath = !string.IsNullOrEmpty(xboxDocsPath) ? xboxDocsPath : _platformManager.GetDefaultDocumentsPath();
            localAppDataPath = !string.IsNullOrEmpty(xboxLocalPath) ? xboxLocalPath : _platformManager.GetDefaultLocalAppDataPath();
            stringsPath = !string.IsNullOrEmpty(xboxStringsPath) ? xboxStringsPath : GetDefaultStringsPathFromGamePath(gamePath);
        }
        
        SyncAppPaths();
        EnsureAllPrefsIniWritable();
        if (virtualModMode)
        {
            int hydrated = _modManager.EnsureManagedStagingHydrated();
            LogActivity($"[SETTINGS] Platform switched to {_platformManager.GetPlatformLabel()} with VirtualModMode enabled. Platform staging hydration copied {hydrated} files.");
        }
        SaveSettings();
        SendDataToWeb();
    }

    private void ApplyIndividualSettingObject(string key, JsonElement val, bool writeIni = false)
    {
        if (IsNexusCredentialSettingKey(key))
            return;

        switch (key)
        {
            case "gamePath": 
                gamePath = val.GetString() ?? ""; 
                if (gamePath.Contains("XboxGames", StringComparison.OrdinalIgnoreCase) || 
                    gamePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) || 
                    gamePath.Contains("ModifiableWindowsApps", StringComparison.OrdinalIgnoreCase))
                {
                    _platformManager.SetPlatform(GamePlatform.Xbox);
                }
                else
                {
                    _platformManager.SetPlatform(GamePlatform.Steam);
                }
                SyncAppPaths();
                break;
            case "documentsPath": documentsPath = val.GetString() ?? ""; SyncAppPaths(); break;
            case "localAppDataPath": localAppDataPath = val.GetString() ?? ""; SyncAppPaths(); break;
            case "stringsPath":
                stringsPath = val.GetString() ?? "";
                if (_platformManager.CurrentPlatform == GamePlatform.Steam) steamStringsPath = stringsPath;
                else xboxStringsPath = stringsPath;
                SyncAppPaths();
                break;
            case "minimizeToTray": minimizeToTray = val.GetBoolean(); break;
            case "uiAnimations": uiAnimations = val.GetBoolean(); break;
            case "platformBadgeGlow": platformBadgeGlow = val.GetBoolean(); break;
            case "configEditorSpellCheck": configEditorSpellCheck = val.GetBoolean(); break;
            case "confirmBeforeDeleteMod": confirmBeforeDeleteMod = val.GetBoolean(); break;
            case "confirmBeforeRemoveOldModOnUpdate": confirmBeforeRemoveOldModOnUpdate = val.GetBoolean(); break;
            case "virtualModMode":
            case "managedVanillaMode":
                virtualModMode = val.GetBoolean();
                SyncAppPaths();
                break;
            default:
                string sVal = "";
                if (val.ValueKind == JsonValueKind.String)
                {
                    sVal = val.GetString() ?? "";
                }
                else if (val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False) 
                {
                    sVal = val.GetBoolean().ToString().ToLower();
                }
                else 
                {
                    sVal = val.ToString();
                }
                ApplyIndividualSettingString(key, sVal, writeIni);
                break;
        }
    }

    private void ApplyIndividualSettingString(string key, string val, bool writeIni = false)
    {
        if (IsNexusCredentialSettingKey(key))
            return;

        LogActivity($"[SETTINGS] Apply: {key} = {val}");
        
        string customIniPath = Path.Combine(documentsPath, $"{AppPaths.IniPrefix}Custom.ini");
        void WritePrefsIni(string section, string iniKey, string iniVal)
        {
            if (writeIni) _configManager.UpdatePrefsIni(section, iniKey, iniVal);
        }
        void WriteBothInis(string section, string iniKey, string iniVal)
        {
            if (writeIni) _configManager.UpdateBothInis(section, iniKey, iniVal);
        }
        void WriteCustomIni(string section, string iniKey, string iniVal)
        {
            if (writeIni) _configManager.UpdateBothInis(section, iniKey, iniVal, onlyCustom: true);
        }
        void ScrubPipboyCrtFromPrefs()
        {
            if (!writeIni) return;
            string prefsIni = Path.Combine(documentsPath, $"{AppPaths.IniPrefix}Prefs.ini");
            foreach (string key in PipboyCrtIniKeys)
                RemoveIniKey(prefsIni, "Pipboy", key);
        }
        void RemoveIniKey(string iniFilePath, string section, string iniKey)
        {
            if (writeIni) _configManager.RemoveKey(iniFilePath, section, iniKey);
        }
        void RemoveBothIniKeys(string section, string iniKey)
        {
            if (!writeIni) return;
            string customIni = Path.Combine(documentsPath, $"{AppPaths.IniPrefix}Custom.ini");
            string prefsIni = Path.Combine(documentsPath, $"{AppPaths.IniPrefix}Prefs.ini");
            RemoveIniKey(customIni, section, iniKey);
            RemoveIniKey(prefsIni, section, iniKey);
        }

        switch (key.ToLower())
        {
            case "godrays":
                bool gr = (val == "true" || val == "1");
                WritePrefsIni("Display", "bVolumetricLightingEnable", gr ? "1" : "0");
                if (_platformManager.IsXbox()) xboxGodrays = gr; else steamGodrays = gr;
                break;
            case "grass":
                bool grass = (val == "true" || val == "1");
                WriteBothInis("Grass", "bAllowCreateGrass", grass ? "1" : "0");
                if (_platformManager.IsXbox()) xboxGrass = grass; else steamGrass = grass;
                break;
            case "motionblur":
                bool mb = (val == "true" || val == "1");
                    WriteBothInis("ImageSpace", "bMBEnable", mb ? "1" : "0");
                    WriteBothInis("ImageSpace", "bMBForce", mb ? "1" : "0");
                if (!mb)
                {
                        WriteBothInis("ImageSpace", "bDoDepthOfField", "0");
                        WriteBothInis("ImageSpace", "bDynamicDepthOfField", "0");
                        WriteBothInis("ImageSpace", "bLensFlare", "0");
                        WriteBothInis("ImageSpace", "bDoRadialBlur", "0");
                        WriteBothInis("ImageSpace", "bScreenSpaceBokeh", "0");
                        WriteBothInis("ImageSpace", "iRadialBlurLevel", "0");
                        WriteBothInis("Display", "fDOFMaxBlurRadius", "0.0000");
                        WriteBothInis("Display", "fDOFMinBlurRadius", "0.0000");
                        WriteBothInis("Display", "fDOFFarBlur", "0.0000");
                        WriteBothInis("Display", "fDOFNearBlur", "0.0000");
                }
                if (_platformManager.IsXbox()) xboxDof = mb; else steamDof = mb;
                break;
            case "fov":
                WriteBothInis("Display", "fDefaultWorldFOV", val);
                if (int.TryParse(val, out int fovVal)) {
                    if (_platformManager.IsXbox()) xboxFov = fovVal; else steamFov = fovVal;
                }
                break;
            case "fov1st":
                WriteBothInis("Display", "fDefault1stPersonFOV", val);
                if (int.TryParse(val, out int fov1Val)) {
                    if (_platformManager.IsXbox()) xboxFov1st = fov1Val; else steamFov1st = fov1Val;
                }
                break;
            case "fovpipboy":
                WriteBothInis("Display", "fPipboy1stFOV", val);
                if (int.TryParse(val, out int fovPbVal)) {
                    if (_platformManager.IsXbox()) xboxFovPipboy = fovPbVal; else steamFovPipboy = fovPbVal;
                }
                break;
            case "shadows":
                int shadowDist = 9000;
                int dirShadowDist = 9000;
                if (val == "Low") { shadowDist = 6000; dirShadowDist = 6000; }
                else if (val == "High") { shadowDist = 14000; dirShadowDist = 14000; }
                else if (val == "Ultra") { shadowDist = 20000; dirShadowDist = 20000; }
                    WritePrefsIni("Display", "fShadowDistance", shadowDist.ToString());
                    WritePrefsIni("Display", "fDirShadowDistance", dirShadowDist.ToString());
                if (_platformManager.IsXbox()) xboxShadows = val; else steamShadows = val;
                break;
            case "taa":
                WritePrefsIni("Display", "sAntiAliasing", val);
                if (_platformManager.IsXbox()) xboxTaa = val; else steamTaa = val;
                break;
            case "fastload":
                bool fl = (val == "true" || val == "1");
                if (fl) {
                        WriteBothInis("Interface", "fFadeToBlack time", "0.2");
                        WriteBothInis("Login", "fFirstLoginFadeOutTime", "0.0");
                        WriteBothInis("Login", "fLoginFadeOutTime", "0.0");
                        WriteBothInis("Interface", "fFadeToBlackFadeSeconds", "0.2000");
                        WriteBothInis("Interface", "fMinSecondsForLoadFadeIn", "0.0000");
                        WriteBothInis("Display", "fLoadingFadeOutTime", "0.0000");
                        WriteBothInis("Display", "fLoadingFadeInTime", "0.0000");
                        WriteBothInis("General", "bAlwaysAnimateTightLoop", "1");
                        WriteBothInis("General", "sIntroSequence", "0");
                        WriteBothInis("General", "uMainMenuDelayBeforeAllowSkip", "0");
                        WriteBothInis("General", "bSkipSplash", "1");
                } else {
                        RemoveBothIniKeys("Interface", "fFadeToBlack time");
                        RemoveBothIniKeys("Interface", "fFadeToBlackFadeSeconds");
                        RemoveBothIniKeys("Interface", "fMinSecondsForLoadFadeIn");
                        RemoveBothIniKeys("Login", "fFirstLoginFadeOutTime");
                        RemoveBothIniKeys("Login", "fLoginFadeOutTime");
                        RemoveBothIniKeys("Display", "fLoadingFadeOutTime");
                        RemoveBothIniKeys("Display", "fLoadingFadeInTime");
                        RemoveBothIniKeys("General", "bAlwaysAnimateTightLoop");
                        RemoveBothIniKeys("General", "sIntroSequence");
                        RemoveBothIniKeys("General", "uMainMenuDelayBeforeAllowSkip");
                        RemoveBothIniKeys("General", "bSkipSplash");
                }
                if (_platformManager.IsXbox()) {
                    xboxFastload = fl;
                    xboxSkipSplash = fl;
                } else {
                    steamFastload = fl;
                    steamSkipSplash = fl;
                }
                break;
            case "ping":
                bool ping = (val == "true" || val == "1");
                WriteBothInis("General", "bCheckPing", ping ? "0" : "1");
                if (_platformManager.IsXbox()) xboxPing = ping; else steamPing = ping;
                break;
            case "bandwidth":
                bool bw = (val == "true" || val == "1");
                if (bw) {
                        WriteBothInis("General", "fMaxProjectedBytesPerFrame", "30000000.0");
                        WriteBothInis("General", "fLoadingKVMBuSize", "4096");
                } else {
                        RemoveBothIniKeys("General", "fMaxProjectedBytesPerFrame");
                        RemoveBothIniKeys("General", "fLoadingKVMBuSize");
                }
                if (_platformManager.IsXbox()) xboxBandwidth = bw; else steamBandwidth = bw;
                break;
            case "vsync":
                bool vs = (val == "true" || val == "1");
                WriteBothInis("Display", "iPresentInterval", vs ? "1" : "0");
                if (_platformManager.IsXbox()) xboxVsync = vs; else steamVsync = vs;
                break;
            case "ao":
                bool ao = (val == "true" || val == "1");
                WriteBothInis("Display", "bSAOEnable", ao ? "1" : "0");
                if (_platformManager.IsXbox()) xboxAo = ao; else steamAo = ao;
                break;
            case "blood":
                bool blood = (val == "true" || val == "1");
                WriteBothInis("Decals", "bBloodSplatterEnabled", blood ? "1" : "0");
                if (_platformManager.IsXbox()) xboxBlood = blood; else steamBlood = blood;
                break;
            case "dof":
                bool dof = (val == "true" || val == "1");
                    WriteBothInis("ImageSpace", "bDynamicDepthOfField", dof ? "1" : "0");
                    WriteBothInis("ImageSpace", "bDoDepthOfField", dof ? "1" : "0");
                if (!dof) {
                        WriteBothInis("Display", "fDOFMaxBlurRadius", "0.0000");
                        WriteBothInis("Display", "fDOFMinBlurRadius", "0.0000");
                        WriteBothInis("Display", "fDOFFarBlur", "0.0000");
                        WriteBothInis("Display", "fDOFNearBlur", "0.0000");
                        WriteBothInis("ImageSpace", "bScreenSpaceBokeh", "0");
                        WriteBothInis("ImageSpace", "iRadialBlurLevel", "0");
                        WriteBothInis("Display", "fDOFBlendRatio", "0.0000");
                        WriteBothInis("Display", "fDOFMinFocalCoefDist", "999999.0");
                        WriteBothInis("Display", "fDOFMaxFocalCoefDist", "999999.0");
                        WriteBothInis("Display", "fDOFDynamicFarRange", "9999.0");
                        WriteBothInis("Display", "fDOFCenterWeightInt", "0.0");
                        WriteBothInis("Display", "fDOFFarDistance", "999999.0");
                } else {
                    string cIni = Path.Combine(documentsPath, $"{AppPaths.IniPrefix}Custom.ini");
                        RemoveIniKey(cIni, "Display", "fDOFMaxBlurRadius");
                        RemoveIniKey(cIni, "Display", "fDOFMinBlurRadius");
                        RemoveIniKey(cIni, "Display", "fDOFFarBlur");
                        RemoveIniKey(cIni, "Display", "fDOFNearBlur");
                        RemoveIniKey(cIni, "ImageSpace", "iRadialBlurLevel");
                        RemoveIniKey(cIni, "Display", "fDOFBlendRatio");
                        RemoveIniKey(cIni, "Display", "fDOFMinFocalCoefDist");
                        RemoveIniKey(cIni, "Display", "fDOFMaxFocalCoefDist");
                        RemoveIniKey(cIni, "Display", "fDOFDynamicFarRange");
                        RemoveIniKey(cIni, "Display", "fDOFCenterWeightInt");
                        RemoveIniKey(cIni, "Display", "fDOFFarDistance");
                }
                if (_platformManager.IsXbox()) xboxDofSpecific = dof; else steamDofSpecific = dof;
                break;
            case "lensflare":
                bool lf = (val == "true" || val == "1");
                WriteBothInis("ImageSpace", "bLensFlare", lf ? "1" : "0");
                if (_platformManager.IsXbox()) xboxLensFlare = lf; else steamLensFlare = lf;
                break;
            case "extrablur":
                bool xb = (val == "true" || val == "1");
                    WriteBothInis("ImageSpace", "bDoRadialBlur", xb ? "1" : "0");
                    WriteBothInis("ImageSpace", "bScreenSpaceBokeh", xb ? "1" : "0");
                if (_platformManager.IsXbox()) xboxExtraBlur = xb; else steamExtraBlur = xb;
                break;
            case "steamfpscap":
            case "xboxfpscap":
            case "fpscap":
                string cap = val;

                if (cap == "60")
                {
                        WriteBothInis("Display", "iPresentInterval", "1");
                        WriteBothInis("Display", "iFPSClamp", "60");
                    if (_platformManager.IsXbox()) xboxVsync = true; else steamVsync = true;
                }
                else if (cap == "Unlimited")
                {
                        WriteBothInis("Display", "iPresentInterval", "0");
                        WriteBothInis("Display", "iFPSClamp", "0");
                        WriteBothInis("Display", "bLockFramerate", "0");
                    if (_platformManager.IsXbox()) xboxVsync = false; else steamVsync = false;
                }
                else
                {
                        WriteBothInis("Display", "iPresentInterval", "0");
                        WriteBothInis("Display", "iFPSClamp", cap);
                        WriteBothInis("Display", "bLockFramerate", "0");
                    if (_platformManager.IsXbox()) xboxVsync = false; else steamVsync = false;
                }

                int parsedCap;
                int capValue = 0;
                if (cap == "Unlimited") capValue = 0;
                else if (int.TryParse(cap, out parsedCap)) capValue = parsedCap;

                if (key.ToLower() == "steamfpscap") steamFpsCap = capValue;
                else if (key.ToLower() == "xboxfpscap") xboxFpsCap = capValue;
                else { if (_platformManager.IsXbox()) xboxFpsCap = capValue; else steamFpsCap = capValue; }

                if (writeIni)
                {
                    string targetExe = _platformManager.IsXbox() ? "Project76_GamePass.exe" : "Fallout76.exe";
                    GpuFpsLimiter.SetLogger(LogActivity);
                    GpuFpsLimiter.SetFpsLimit(targetExe, capValue);
                }
                break;
            case "aniso":
                string anisoStr = val.Replace("x", "").Trim();
                if (anisoStr == "Off" || anisoStr == "None") anisoStr = "0";
                WritePrefsIni("Display", "iMaxAnisotropy", anisoStr);
                if (_platformManager.IsXbox()) xboxAniso = val; else steamAniso = val;
                break;
            case "water":
                if (val == "Low") {
                        WritePrefsIni("Water", "bUseWaterHiRes", "0");
                        WritePrefsIni("Water", "bUseWaterDisplacements", "0");
                        WritePrefsIni("Water", "bUseWaterRefractions", "0");
                        WritePrefsIni("Water", "bUseWaterReflections", "0");
                        WritePrefsIni("Water", "bUseWaterDepth", "0");
                } else if (val == "Medium") {
                        WritePrefsIni("Water", "bUseWaterHiRes", "0");
                        WritePrefsIni("Water", "bUseWaterDisplacements", "0");
                        WritePrefsIni("Water", "bUseWaterRefractions", "1");
                        WritePrefsIni("Water", "bUseWaterReflections", "1");
                        WritePrefsIni("Water", "bUseWaterDepth", "1");
                } else {
                        WritePrefsIni("Water", "bUseWaterHiRes", "1");
                        WritePrefsIni("Water", "bUseWaterDisplacements", "1");
                        WritePrefsIni("Water", "bUseWaterRefractions", "1");
                        WritePrefsIni("Water", "bUseWaterReflections", "1");
                        WritePrefsIni("Water", "bUseWaterDepth", "1");
                }
                if (_platformManager.IsXbox()) xboxWater = val; else steamWater = val;
                break;
            case "lod":
                if (int.TryParse(val, out int lodVal)) {
                    float lodMult = lodVal / 10.0f;
                    string lodStr = lodMult.ToString("0.0000", CultureInfo.InvariantCulture);
                        WritePrefsIni("LOD", "fLODFadeOutMultObjects", lodStr);
                        WritePrefsIni("LOD", "fLODFadeOutMultItems", lodStr);
                        WritePrefsIni("LOD", "fLODFadeOutMultActors", lodStr);
                    if (_platformManager.IsXbox()) xboxLod = lodVal; else steamLod = lodVal;
                }
                break;
            case "decals":
                if (val == "Off" || val == "None") {
                        WritePrefsIni("Decals", "bDecals", "0");
                        WritePrefsIni("Decals", "bSkinnedDecals", "0");
                        WritePrefsIni("Decals", "uMaxDecals", "0");
                        WritePrefsIni("Decals", "uMaxSkinDecals", "0");
                } else if (val == "Low") {
                        WritePrefsIni("Decals", "bDecals", "1");
                        WritePrefsIni("Decals", "bSkinnedDecals", "0");
                        WritePrefsIni("Decals", "uMaxDecals", "100");
                        WritePrefsIni("Decals", "uMaxSkinDecals", "0");
                } else if (val == "Medium") {
                        WritePrefsIni("Decals", "bDecals", "1");
                        WritePrefsIni("Decals", "bSkinnedDecals", "1");
                        WritePrefsIni("Decals", "uMaxDecals", "250");
                        WritePrefsIni("Decals", "uMaxSkinDecals", "35");
                } else if (val == "High") {
                        WritePrefsIni("Decals", "bDecals", "1");
                        WritePrefsIni("Decals", "bSkinnedDecals", "1");
                        WritePrefsIni("Decals", "uMaxDecals", "500");
                        WritePrefsIni("Decals", "uMaxSkinDecals", "50");
                } else {
                        WritePrefsIni("Decals", "bDecals", "1");
                        WritePrefsIni("Decals", "bSkinnedDecals", "1");
                        WritePrefsIni("Decals", "uMaxDecals", "1000");
                        WritePrefsIni("Decals", "uMaxSkinDecals", "100");
                }
                if (_platformManager.IsXbox()) xboxDecals = val; else steamDecals = val;
                break;
            case "pipboyfx":
                bool pbfx = (val == "true" || val == "1");
                if (!pbfx) {
                        WriteCustomIni("Pipboy", "bPipboyDisableFX", "1");
                        WriteCustomIni("Pipboy", "fPipboyScreenEmitIntensity", "1.25");
                        WriteCustomIni("Pipboy", "fPipboyScreenDiffuseIntensity", "0.15");
                        WriteCustomIni("Pipboy", "bPipboyEffectColorOnly", "1");
                } else {
                        WriteCustomIni("Pipboy", "bPipboyDisableFX", "0");
                        RemoveBothIniKeys("Pipboy", "fPipboyScreenEmitIntensity");
                        RemoveBothIniKeys("Pipboy", "fPipboyScreenDiffuseIntensity");
                        RemoveBothIniKeys("Pipboy", "bPipboyEffectColorOnly");
                }
                ScrubPipboyCrtFromPrefs();
                if (_platformManager.IsXbox()) xboxPipboyFx = pbfx; else steamPipboyFx = pbfx;
                if (writeIni) MarkPipboyCrtUserConfigured();
                break;
            case "volumquality":
                int vqLvl = val == "Low" ? 0 : val == "Medium" ? 1 : 2;
                int vqTex = vqLvl;
                WritePrefsIni("Display", "iVolumetricLightingQuality", vqLvl.ToString());
                WritePrefsIni("Display", "iVolumetricLightingTextureQuality", vqTex.ToString());
                if (_platformManager.IsXbox()) xboxVolumQuality = val; else steamVolumQuality = val;
                break;
            case "shadowres":
                WritePrefsIni("Display", "iShadowMapResolution", val);
                if (_platformManager.IsXbox()) xboxShadowRes = val; else steamShadowRes = val;
                break;
            case "shadowfilter":
                string sfVal = val == "Low" ? "0" : val == "Medium" ? "1" : "3";
                WritePrefsIni("Display", "uiShadowFilter", sfVal);
                WritePrefsIni("Display", "uiOrthoShadowFilter", sfVal);
                if (_platformManager.IsXbox()) xboxShadowFilter = val; else steamShadowFilter = val;
                break;
            case "focusshadows":
                bool dfs = (val == "true" || val == "1");
                int focusCnt = dfs ? 4 : 0;
                WritePrefsIni("Display", "iMaxFocusShadows", focusCnt.ToString());
                WritePrefsIni("Display", "iMaxFocusShadowsDialogue", focusCnt.ToString());
                if (_platformManager.IsXbox()) xboxFocusShadows = dfs; else steamFocusShadows = dfs;
                break;
            case "rendergrass":
                bool drg = (val == "true" || val == "1");
                WritePrefsIni("Grass", "bRenderGrass", drg ? "1" : "0");
                if (_platformManager.IsXbox()) xboxRenderGrass = drg; else steamRenderGrass = drg;
                break;
            case "grassfade":
                if (int.TryParse(val, out int gfVal)) {
                    string gfStr = gfVal.ToString("0.0000", CultureInfo.InvariantCulture);
                    WritePrefsIni("Grass", "fGrassStartFadeDistance", gfStr);
                    WritePrefsIni("Grass", "fGrassMaxStartFadeDistance", gfStr);
                    if (_platformManager.IsXbox()) xboxGrassFade = gfVal; else steamGrassFade = gfVal;
                }
                break;
            case "treedist":
                if (int.TryParse(val, out int tdVal)) {
                    string tdStr = tdVal.ToString("0.0000", CultureInfo.InvariantCulture);
                    WritePrefsIni("TerrainManager", "fTreeLoadDistance", tdStr);
                    WritePrefsIni("TerrainManager", "fBlockLevel0Distance", (tdVal * 2.4).ToString("0.0000", CultureInfo.InvariantCulture));
                    WritePrefsIni("TerrainManager", "fBlockLevel1Distance", (tdVal * 3.6).ToString("0.0000", CultureInfo.InvariantCulture));
                    WritePrefsIni("TerrainManager", "fBlockLevel2Distance", (tdVal * 4.4).ToString("0.0000", CultureInfo.InvariantCulture));
                    WritePrefsIni("TerrainManager", "fBlockMaximumDistance", (tdVal * 10.0).ToString("0.0000", CultureInfo.InvariantCulture));
                    if (_platformManager.IsXbox()) xboxTreeDist = tdVal; else steamTreeDist = tdVal;
                }
                break;
            case "texturequality":
                int tqLvl = val == "Low" ? 0 : val == "Medium" ? 1 : val == "High" ? 2 : 3;
                WritePrefsIni("Texture", "iTextureQualityLevel", tqLvl.ToString());
                if (val == "Low") {
                    WritePrefsIni("Texture", "iTextureMipSkipMinDimension", "512");
                    WritePrefsIni("Texture", "iLargeTextureArrayMipSkip", "1");
                } else {
                    string customIniTex = Path.Combine(documentsPath, $"{AppPaths.IniPrefix}Custom.ini");
                    RemoveIniKey(customIniTex, "Texture", "iTextureMipSkipMinDimension");
                    RemoveIniKey(Path.Combine(documentsPath, $"{AppPaths.IniPrefix}Prefs.ini"), "Texture", "iTextureMipSkipMinDimension");
                    WritePrefsIni("Texture", "iLargeTextureArrayMipSkip", "0");
                }
                if (_platformManager.IsXbox()) xboxTextureQuality = val; else steamTextureQuality = val;
                break;
            case "ssr":
                bool ssrOn = (val == "true" || val == "1");
                WritePrefsIni("LightingShader", "bScreenSpaceReflections", ssrOn ? "1" : "0");
                if (_platformManager.IsXbox()) xboxSsr = ssrOn; else steamSsr = ssrOn;
                break;
            case "rainocclusion":
                bool rainOn = (val == "true" || val == "1");
                WritePrefsIni("Weather", "iRainOcclusionMapResolution", rainOn ? "512" : "0");
                if (_platformManager.IsXbox()) xboxRainOcclusion = rainOn; else steamRainOcclusion = rainOn;
                break;
            case "npcshadowlights":
                bool npcSh = (val == "true" || val == "1");
                WritePrefsIni("Display", "bAllowShadowcasterNPCLights", npcSh ? "1" : "0");
                if (_platformManager.IsXbox()) xboxNpcShadowLights = npcSh; else steamNpcShadowLights = npcSh;
                break;
            case "decalsperframe":
                int maxDec = 100, maxSkin = 25;
                if (val == "Low") { maxDec = 50; maxSkin = 10; }
                else if (val == "Medium") { maxDec = 250; maxSkin = 35; }
                else if (val == "High") { maxDec = 500; maxSkin = 50; }
                WritePrefsIni("Display", "iMaxDecalsPerFrame", maxDec.ToString());
                WritePrefsIni("Display", "iMaxSkinDecalsPerFrame", maxSkin.ToString());
                if (_platformManager.IsXbox()) xboxDecalsPerFrame = val; else steamDecalsPerFrame = val;
                break;
            case "gridload":
                WriteBothInis("General", "uGridsToLoad", val);
                if (_platformManager.IsXbox()) xboxGridLoad = val; else steamGridLoad = val;
                break;
            case "cellloads":
                bool cellLd = (val == "true" || val == "1");
                WriteBothInis("General", "bBackgroundCellLoads", cellLd ? "1" : "0");
                if (_platformManager.IsXbox()) xboxCellLoads = cellLd; else steamCellLoads = cellLd;
                break;
            case "tiledlighting":
                bool tiled = (val == "true" || val == "1");
                WritePrefsIni("Display", "bComputeShaderDeferredTiledLighting", tiled ? "1" : "0");
                if (_platformManager.IsXbox()) xboxTiledLighting = tiled; else steamTiledLighting = tiled;
                break;
            case "skipsplash":
                bool skipLegacy = (val == "true" || val == "1");
                bool fastloadActive = _platformManager.IsXbox() ? xboxFastload : steamFastload;
                if (skipLegacy) {
                    WriteBothInis("General", "bSkipSplash", "1");
                    WriteBothInis("General", "sIntroSequence", "0");
                    WriteBothInis("General", "uMainMenuDelayBeforeAllowSkip", "0");
                    if (_platformManager.IsXbox()) { xboxSkipSplash = true; xboxFastload = true; }
                    else { steamSkipSplash = true; steamFastload = true; }
                } else if (!fastloadActive) {
                    RemoveBothIniKeys("General", "bSkipSplash");
                    RemoveBothIniKeys("General", "sIntroSequence");
                    RemoveBothIniKeys("General", "uMainMenuDelayBeforeAllowSkip");
                    if (_platformManager.IsXbox()) xboxSkipSplash = false; else steamSkipSplash = false;
                } else if (_platformManager.IsXbox()) xboxSkipSplash = false;
                else steamSkipSplash = false;
                break;
            case "gamma":
                double gammaVal;
                if (int.TryParse(val, out int gammaInt) && gammaInt >= 8 && gammaInt <= 14)
                    gammaVal = gammaInt / 10.0;
                else if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out gammaVal))
                    break;
                gammaVal = Math.Clamp(gammaVal, 0.8, 1.4);
                string gammaStr = gammaVal.ToString("0.0000", CultureInfo.InvariantCulture);
                WriteBothInis("Display", "fGamma", gammaStr);
                if (_platformManager.IsXbox()) xboxGamma = gammaVal; else steamGamma = gammaVal;
                break;
            case "glassshader":
                bool glass = (val == "true" || val == "1");
                WritePrefsIni("Display", "bEnableGlassShader", glass ? "1" : "0");
                if (_platformManager.IsXbox()) xboxGlassShader = glass; else steamGlassShader = glass;
                break;
            case "pbrshadows":
                bool pbr = (val == "true" || val == "1");
                WritePrefsIni("Display", "bEffectShaderAllowPBRShadows", pbr ? "1" : "0");
                if (_platformManager.IsXbox()) xboxPbrShadows = pbr; else steamPbrShadows = pbr;
                break;
            case "lodsky":
                if (int.TryParse(val, out int lodSkyUi)) {
                    float lodSkyMult = lodSkyUi / 10.0f;
                    string lsStr = lodSkyMult.ToString("0.0000", CultureInfo.InvariantCulture);
                    WritePrefsIni("LOD", "fLODFadeOutMultSkyCell", lsStr);
                    if (_platformManager.IsXbox()) xboxLodSky = lodSkyUi; else steamLodSky = lodSkyUi;
                }
                break;
            case "leafanim":
                if (int.TryParse(val, out int leafStart)) {
                    string ls = leafStart.ToString("0.0000", CultureInfo.InvariantCulture);
                    string le = (leafStart + 1000).ToString("0.0000", CultureInfo.InvariantCulture);
                    WritePrefsIni("Display", "fLeafAnimDampenDistStart", ls);
                    WritePrefsIni("Display", "fLeafAnimDampenDistEnd", le);
                    if (_platformManager.IsXbox()) xboxLeafAnim = leafStart; else steamLeafAnim = leafStart;
                }
                break;
            case "corpsehighlight":
                string chIni = val == "Off" ? "0" : val == "Low" ? "1" : "2";
                WritePrefsIni("Display", "uiShowCorpseHighlighting", chIni);
                if (_platformManager.IsXbox()) xboxCorpseHighlight = val; else steamCorpseHighlight = val;
                break;
            case "playernames":
                bool showNames = (val == "true" || val == "1");
                WritePrefsIni("Display", "bShowOtherPlayersNames", showNames ? "1" : "0");
                if (_platformManager.IsXbox()) xboxPlayerNames = showNames; else steamPlayerNames = showNames;
                break;
            case "playerpings":
                bool showPings = (val == "true" || val == "1");
                WritePrefsIni("Display", "bShowOtherPlayersPings", showPings ? "1" : "0");
                if (_platformManager.IsXbox()) xboxPlayerPings = showPings; else steamPlayerPings = showPings;
                break;
            case "conversationhistory":
                if (int.TryParse(val, out int convHist)) {
                    string chs = convHist.ToString("0.0000", CultureInfo.InvariantCulture);
                    WritePrefsIni("Display", "fConversationHistorySize", chs);
                    if (_platformManager.IsXbox()) xboxConversationHistory = convHist; else steamConversationHistory = convHist;
                }
                break;
            case "vatsblur":
                bool vatsBlur = (val == "true" || val == "1");
                WriteBothInis("VATS", "bVATSBlur", vatsBlur ? "1" : "0");
                if (_platformManager.IsXbox()) xboxVatsBlur = vatsBlur; else steamVatsBlur = vatsBlur;
                break;
        }

    }

    private void HandleRenameMod(string currentName, string newName, string details = "")
    {
        _modManager.RenameMod(currentName, newName, details);
        SendDataToWeb();
    }

    private void HandleSaveModDetails(string name, string details = "")
    {
        _modManager.SaveModDetails(name, details);
        SendDataToWeb();
    }

    private static bool IsEditableModConfigExtension(string relativePath)
    {
        string ext = Path.GetExtension(relativePath ?? "").ToLowerInvariant();
        return EditableModConfigExtensions.Contains(ext);
    }

    private bool TryResolveOwnedEditableFile(string modKey, string relativePath, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        try
        {
            if (string.IsNullOrWhiteSpace(modKey) || string.IsNullOrWhiteSpace(relativePath))
            {
                error = "Missing mod key or file path.";
                return false;
            }

            string relNorm = relativePath.Replace("\\", "/").Trim();
            if (relNorm.Contains("..", StringComparison.Ordinal))
            {
                error = "Invalid path.";
                return false;
            }

            if (!IsEditableModConfigExtension(relNorm))
            {
                error = "This file type is not editable.";
                return false;
            }

            var allMeta = _modManager.GetType()
                .GetMethod("LoadMetadata", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(_modManager, null) as Dictionary<string, ModManager.ModMetadata>;

            if (allMeta == null)
            {
                error = "Failed to read mod metadata.";
                return false;
            }

            string modKeyNorm = modKey.Replace("\\", "/").Trim();
            bool modKeyLooksLikeListKey =
                modKeyNorm.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase) ||
                modKeyNorm.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase) ||
                modKeyNorm.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase) ||
                modKeyNorm.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase) ||
                modKeyNorm.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase) ||
                modKeyNorm.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                modKeyNorm.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                modKeyNorm.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

            string resolvedKey = "";
            if (modKeyLooksLikeListKey && allMeta.ContainsKey(modKeyNorm))
            {
                resolvedKey = modKeyNorm;
            }
            else
            {
                resolvedKey = _modManager.GetType()
                    .GetMethod("ResolveMetadataKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(_modManager, new object[] { allMeta, modKeyNorm }) as string ?? "";
            }

            if (string.IsNullOrWhiteSpace(resolvedKey) || !allMeta.TryGetValue(resolvedKey, out var meta) || meta == null)
            {
                error = "Mod metadata record not found.";
                return false;
            }

            bool owned = (meta.Files ?? new List<string>())
                .Any(f => string.Equals((f ?? "").Replace("\\", "/").Trim(), relNorm, StringComparison.OrdinalIgnoreCase));
            if (!owned)
            {
                error = "This file is not part of that mod.";
                return false;
            }

            var getFullPath = _modManager.GetType()
                .GetMethod("GetFullPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (getFullPath == null)
            {
                error = "Path resolver not available.";
                return false;
            }

            string? resolvedFull = getFullPath.Invoke(_modManager, new object[] { relNorm }) as string;
            if (string.IsNullOrWhiteSpace(resolvedFull))
            {
                error = "Failed to resolve file path.";
                return false;
            }

            fullPath = Path.GetFullPath(resolvedFull);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void HandleReadModFile(string modKey, string relativePath)
    {
        try
        {
            if (!TryResolveOwnedEditableFile(modKey, relativePath, out var fullPath, out var error))
            {
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "MOD_FILE_CONTENT",
                    name = modKey,
                    relativePath = relativePath,
                    error = error,
                    content = ""
                }));
                return;
            }

            string content = File.Exists(fullPath)
                ? File.ReadAllText(fullPath, System.Text.Encoding.UTF8)
                : "";

            SendMessageToWeb(JsonSerializer.Serialize(new
            {
                type = "MOD_FILE_CONTENT",
                name = modKey,
                relativePath = relativePath,
                content = content
            }));
        }
        catch (Exception ex)
        {
            SendMessageToWeb(JsonSerializer.Serialize(new
            {
                type = "MOD_FILE_CONTENT",
                name = modKey,
                relativePath = relativePath,
                error = ex.Message,
                content = ""
            }));
        }
    }

    private void HandleWriteModFile(string modKey, string relativePath, string content)
    {
        try
        {
            if (!TryResolveOwnedEditableFile(modKey, relativePath, out var fullPath, out var error))
            {
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "MOD_FILE_WRITE_RESULT",
                    ok = false,
                    name = modKey,
                    relativePath = relativePath,
                    error = error
                }));
                return;
            }

            string ext = Path.GetExtension(relativePath ?? "").ToLowerInvariant();
            if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var _ = JsonDocument.Parse(content ?? "", new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                }
                catch (Exception jex)
                {
                    SendMessageToWeb(JsonSerializer.Serialize(new
                    {
                        type = "MOD_FILE_WRITE_RESULT",
                        ok = false,
                        name = modKey,
                        relativePath = relativePath,
                        error = $"Invalid JSON: {jex.Message}"
                    }));
                    return;
                }
            }

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, content ?? "", System.Text.Encoding.UTF8);
            SendMessageToWeb(JsonSerializer.Serialize(new
            {
                type = "MOD_FILE_WRITE_RESULT",
                ok = true,
                name = modKey,
                relativePath = relativePath
            }));

            RefreshConflictCount();
            SendDataToWeb();
        }
        catch (Exception ex)
        {
            SendMessageToWeb(JsonSerializer.Serialize(new
            {
                type = "MOD_FILE_WRITE_RESULT",
                ok = false,
                name = modKey,
                relativePath = relativePath,
                error = ex.Message
            }));
        }
    }

    private void HandleAdoptDataFile(string relativePath, string targetMod)
    {
        try
        {
            string relNorm = (relativePath ?? "").Replace("\\", "/").Trim();
            if (string.IsNullOrWhiteSpace(relNorm))
            {
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "MOD_FILE_ADOPT_RESULT",
                    ok = false,
                    relativePath = relativePath,
                    targetMod = targetMod,
                    error = "Missing relativePath."
                }));
                return;
            }

            if (!IsEditableModConfigExtension(relNorm))
            {
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "MOD_FILE_ADOPT_RESULT",
                    ok = false,
                    relativePath = relativePath,
                    targetMod = targetMod,
                    error = "This file type is not adoptable."
                }));
                return;
            }

            string dataRoot = AppPaths.DataPath;
            string sourceFull = Path.GetFullPath(Path.Combine(dataRoot, relNorm.Replace("/", "\\")));
            string rootFull = Path.GetFullPath(dataRoot);
            if (!sourceFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "MOD_FILE_ADOPT_RESULT",
                    ok = false,
                    relativePath = relativePath,
                    targetMod = targetMod,
                    error = "Invalid path."
                }));
                return;
            }
            if (!File.Exists(sourceFull))
            {
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "MOD_FILE_ADOPT_RESULT",
                    ok = false,
                    relativePath = relativePath,
                    targetMod = targetMod,
                    error = "File not found in Data."
                }));
                return;
            }

            var loadMeta = _modManager.GetType()
                .GetMethod("LoadMetadata", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var resolveKey = _modManager.GetType()
                .GetMethod("ResolveMetadataKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var saveMeta = _modManager.GetType()
                .GetMethod("SaveMetadata", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (loadMeta == null || resolveKey == null || saveMeta == null)
            {
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "MOD_FILE_ADOPT_RESULT",
                    ok = false,
                    relativePath = relativePath,
                    targetMod = targetMod,
                    error = "Metadata helpers not available."
                }));
                return;
            }

            var allMeta = loadMeta.Invoke(_modManager, Array.Empty<object>()) as Dictionary<string, ModManager.ModMetadata>
                          ?? new Dictionary<string, ModManager.ModMetadata>(StringComparer.OrdinalIgnoreCase);

            string resolvedTarget = resolveKey.Invoke(_modManager, new object[] { allMeta, targetMod }) as string ?? "";
            if (string.IsNullOrWhiteSpace(resolvedTarget) || !allMeta.TryGetValue(resolvedTarget, out var meta) || meta == null)
            {
                SendMessageToWeb(JsonSerializer.Serialize(new
                {
                    type = "MOD_FILE_ADOPT_RESULT",
                    ok = false,
                    relativePath = relativePath,
                    targetMod = targetMod,
                    error = "Target mod not found."
                }));
                return;
            }

            meta.Files ??= new List<string>();
            bool alreadyOwned = meta.Files.Any(f => string.Equals((f ?? "").Replace("\\", "/").Trim(), relNorm, StringComparison.OrdinalIgnoreCase));
            if (!alreadyOwned)
            {
                meta.Files.Add(relNorm);
            }

            string existingLooseKey = resolveKey.Invoke(_modManager, new object[] { allMeta, relNorm }) as string ?? "";
            if (!string.IsNullOrWhiteSpace(existingLooseKey) &&
                !string.Equals(existingLooseKey, resolvedTarget, StringComparison.OrdinalIgnoreCase) &&
                allMeta.ContainsKey(existingLooseKey))
            {
                allMeta.Remove(existingLooseKey);
            }

            allMeta[resolvedTarget] = meta;
            saveMeta.Invoke(_modManager, new object[] { allMeta });

            SendMessageToWeb(JsonSerializer.Serialize(new
            {
                type = "MOD_FILE_ADOPT_RESULT",
                ok = true,
                relativePath = relNorm,
                targetMod = resolvedTarget,
                alreadyOwned = alreadyOwned
            }));

            SendDataToWeb();
        }
        catch (Exception ex)
        {
            SendMessageToWeb(JsonSerializer.Serialize(new
            {
                type = "MOD_FILE_ADOPT_RESULT",
                ok = false,
                relativePath = relativePath,
                targetMod = targetMod,
                error = ex.Message
            }));
        }
    }

    private void HandleGetIniContent(string type)
    {
        try
        {
            string fileName = type == "prefs" ? $"{AppPaths.IniPrefix}Prefs.ini" : $"{AppPaths.IniPrefix}Custom.ini";
            string path = Path.Combine(documentsPath, fileName);
            string content = "";

            if (File.Exists(path))
            {
                content = File.ReadAllText(path);
            }

            string json = JsonSerializer.Serialize(new { 
                type = "INI_CONTENT", 
                iniType = type, 
                content = content 
            });
            SendMessageToWeb(json);
        }
        catch (Exception ex)
        {
            SendStatusMessage("error", $"Failed to load INI: {ex.Message}", "ini_load_error");
        }
    }

    private void HandleSaveIniContent(string type, string content)
    {
        try
        {
            string fileName = type == "prefs" ? $"{AppPaths.IniPrefix}Prefs.ini" : $"{AppPaths.IniPrefix}Custom.ini";
            string path = Path.Combine(documentsPath, fileName);

            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", true); } catch (Exception ex) { LogError($"[INI-SAVE] Failed to create backup for {fileName}: {ex.Message}"); }
            }

            File.WriteAllText(path, content, System.Text.Encoding.UTF8);
            
            SendStatusMessage("success", $"{fileName} saved successfully.", "ini_save_success");
            
            LogActivity($"[INI-SAVE] Saved {fileName} and created backup.");
        }
        catch (Exception ex)
        {
            SendStatusMessage("error", $"Failed to save INI: {ex.Message}", "ini_save_error");
        }
    }


    private static readonly string FallbackEnglish = """
{
  "install_paths": "Installation Paths",
  "manager_preferences": "Manager Preferences",
  "minimize_to_tray": "Minimize to Tray",
  "ui_animations": "UI Animations",
  "platform_badge_glow": "Platform Badge Glow (Steam/Xbox)",
  "platform_badge_glow_desc": "Adds a glow effect to the Steam/Xbox platform badge in the sidebar.",
  "sync_platforms": "Sync Platforms (Steam/Xbox)",
  "auto_conflict_override": "Auto Conflict Override",
  "virtual_mod_mode": "Virtual Mod Mode",
  "advanced_maintenance": "Advanced Maintenance",
  "save_changes": "Save Changes",
  "backup_inis": "Backup INIs",
  "backup_mods": "Backup Mods",
  "backup_configs": "Backup Configs",
  "configs_backed_up": "Config files backed up under Backups/Configs for this platform.",
  "no_config_files_to_backup": "No config files found to backup.",
  "configs_backup_failed": "Config backup failed.",
  "reset_config": "Reset Config",
  "clear_cache": "Clear Cache",
  "game_path": "Game Installation Path",
  "documents_path": "Documents / INI Path",
  "local_appdata_path": "Local AppData (plugins.txt)",
  "strings_path": "Strings Mod Path",
  "manager_settings_desc": "Configure your Fallout 76 Manager environment.",
  "settings_paths_desc": "Set game, documents, and mod paths.",
  "settings_advanced_desc": "Backup INI files, reset config, or clear cache.",
  "ui_language_label": "UI Language:",
  "ui_theme_label": "UI Theme:",
  "theme_fallout": "Fallout (default)",
  "theme_vault_tec": "Vault-Tec Blue",
  "theme_red_black": "Red & Black",
  "theme_black_white": "Enclave",
  "game_path_placeholder": "Game EXE folder",
  "docs_path_placeholder": "My Games folder",
  "local_appdata_path_placeholder": "Fallout76 AppData folder",
  "strings_path_placeholder": "Data/Strings folder",
  "created_for_wastelanders": "Created for the Wastelanders",
  "auto_detect": "Auto Detect",
  "welcome_back_hero": "Welcome back, {0}.",
  "hero_desc": "Appalachia is waiting. Your configuration is optimal.",
  "play_fallout_76": "Play Fallout 76",
  "launch_game_with_mods": "Launch the game with current mods",
  "apply_changes": "Apply Changes",
  "save_sync_ini": "Save and sync INI settings",
  "deploy_mods": "Deploy Mods",
  "update_load_order": "Update load order and BA2 files",
  "test_config": "Test Config",
  "run_integrity_check": "Run integrity check",
  "join_discord": "Join Discord",
  "join_discord_desc": "Get Help and Report Issues",
  "join_github": "GitHub",
  "join_github_desc": "View releases and report issues",
  "add_button": "Add Button",
  "add_button_desc": "Add a quick action",
  "remove_button": "Remove",
  "add_quick_action": "Add Quick Action",
  "nexus_tip_title": "Tip:",
  "nexus_tip_desc": "Sign in with Nexus Mods in Settings for one-click downloads.",
  "login_to_nexus": "Sign in with Nexus Mods",
  "dismiss": "Dismiss",
  "login_success": "Signed in to Nexus Mods.",
  "nexus_logout_success": "Signed out of Nexus Mods.",
  "manager_settings_saved": "Manager settings saved.",
  "nexus_connection": "Nexus Mods",
  "nexus_connected": "Signed in to Nexus Mods",
  "nexus_logout": "Sign out",
  "nexus_login": "Sign in with Nexus Mods",
  "nexus_connected_desc": "Your Nexus account is connected. Credentials are stored locally on this computer only.",
  "nexus_login_desc": "Sign in with your Nexus account to search and download mods in-app.",
  "nexus_sso_pending": "Nexus Mods sign-in will be available after this application is registered with Nexus Mods.",
  "nexus_sso_pending_title": "Nexus Mods integration pending",
  "nexus_sso_pending_desc": "In-app Nexus search and downloads require application registration with Nexus Mods. Use Mod Manager Download on the Nexus website — Fallout 76 Manager will import mods via NXM links.",
  "nexus_auth_unavailable": "Nexus Mods sign-in is not available until this application is registered with Nexus Mods.",
  "nexus_login_required_title": "Sign in with Nexus Mods",
  "nexus_login_sso_hint": "Opens your browser to authorize this application with your Nexus account.",
  "dashboard": "Dashboard",
  "mods": "Mods",
  "bundle": "Bundle",
  "tweaks": "Tweaks",
  "pip_boy": "Pip-Boy",
  "profiles": "Profiles",
  "logs": "Logs",
  "settings": "Settings",
  "cancel": "Cancel",
  "delete": "Delete",
  "apply": "Apply",
  "reset": "Reset",
  "refresh": "Refresh",
  "rename": "Rename",
  "delete_file": "Delete File",
  "all_mods": "All Mods",
  "uncategorized": "Uncategorized",
  "add_mod": "Add Mod",
  "deploy_n_mods": "Deploy {0} Mods",
  "no_mods_found": "No mods found",
  "no_mods_hint": "Drag and drop mods here or use the \"Add Mod\" button to get started.",
  "add_first_mod": "Add Your First Mod",
  "new_preset_prompt": "Enter new preset name:",
  "delete_preset_confirm": "Delete preset \"{0}\"? Mods will remain in your list.",
  "cat_performance": "Performance",
  "cat_graphics": "Graphics",
  "cat_network": "Network",
  "tweak_godrays": "Godrays",
  "tweak_godrays_desc": "Volumetric lighting from the sun. Turn off for a small FPS boost.",
  "tweak_grass": "Grass",
  "tweak_grass_desc": "Ground grass and foliage. Turn off to improve visibility and performance in forest areas.",
  "tweak_shadows": "Shadow Quality",
  "tweak_shadows_desc": "Adjust the distance and resolution of shadows.",
  "tweak_fastload": "Faster Loading",
  "tweak_fastload_desc": "Reduces fade-in times, skips intro splash, and loads into worlds faster.",
  "tweak_fastload_warning": "May freeze on startup. If that happens, close the game completely and launch again.",
  "version": "Version",
  "mod_update_available": "Update available on Nexus",
  "mod_nexus_link_unverified": "Nexus link not verified — add file ID in Edit",
  "rename_mod_nexus_file_id_label": "Nexus file ID",
  "rename_mod_nexus_file_id_placeholder": "e.g. 98765",
  "rename_mod_nexus_file_id_hint": "From the mod's Files tab on Nexus (file_id). Required for accurate update checks.",
  "rename_mod_nexus_uploaded_label": "File uploaded (Unix timestamp)",
  "rename_mod_nexus_uploaded_placeholder": "Optional",
  "mod_update_starting": "Downloading mod update from Nexus...",
  "mod_update_remove_confirm": "Remove the previous version of \"{0}\"? The new update is already installed.",
  "mod_update_remove_cancelled": "Kept the previous version of {0}. Both versions remain in your mod list.",
  "mod_update_unavailable": "This mod is not linked to Nexus or no update file is available.",
  "mod_update_replaced": "Mod update installed. Removed previous version of {0}.",
  "mod_nexus_link_saved": "Nexus link saved for this mod.",
  "rename_mod_tabs_label": "Mod editor sections",
  "rename_mod_tab_general": "Details",
  "rename_mod_tab_nexus": "Nexus",
  "rename_mod_nexus_heading": "Nexus tracking",
  "rename_mod_nexus_hint": "Link this mod to its Nexus page so the manager can check for updates.",
  "rename_mod_nexus_url_label": "Nexus URL",
  "rename_mod_nexus_url_placeholder": "https://www.nexusmods.com/fallout76/mods/12345",
  "rename_mod_nexus_mod_id_label": "Nexus Mod ID",
  "rename_mod_nexus_version_label": "Installed version",
  "tweak_vsync": "VSync",
  "tweak_vsync_desc": "Synchronizes frame rate with your monitor refresh rate to reduce screen tearing.",
  "tweak_vsync_warning": "When turning VSync off, set your FPS cap below via Frame Rate Cap, and match it in your GPU driver (NVIDIA Control Panel / AMD Adrenalin).",
  "tweak_ao": "Ambient Occlusion",
  "tweak_ao_desc": "Adds contact shadows where surfaces meet. Turn off for a performance gain at the cost of depth detail.",
  "tweak_blood": "Blood Splatter",
  "tweak_blood_desc": "Blood splatter decals on surfaces. Turn off for a small performance boost and clearer visuals.",
  "tweak_fpscap": "Frame Rate Cap",
  "tweak_fpscap_desc": "60 enables VSync. Higher values disable VSync and auto-apply on NVIDIA GPUs. AMD users: set the same limit in AMD Adrenalin.",
  "tweak_save_btn": "Save Changes",
  "tweak_edit_ini_btn": "Edit INI",
  "ini_editor_title": "INI Editor",
  "ini_tab_custom": "Custom.ini",
  "ini_tab_prefs": "Prefs.ini",
  "ini_save_success": "INI file saved successfully.",
  "ini_save_error": "Failed to save INI file.",
  "tweak_changes_pending": "You have unsaved changes.",
  "tweak_saved_msg": "Tweaks saved successfully.",
  "no_changes_to_save": "No changes to save.",
  "discard_changes": "Discard Changes",
  "tweak_fov": "World / Third-Person FOV",
  "tweak_fov_desc": "Fallout76Custom.ini [Display] fDefaultWorldFOV — third-person / world camera (default in-game often 70).",
  "tweak_motionblur": "Motion Blur",
  "tweak_motionblur_desc": "Enable or disable background blur, radial blur, lens flare, and bokeh effects.",
  "tweak_dof": "Depth of Field",
  "tweak_dof_desc": "Blurs distant objects for a cinematic look. Turn off to keep the image sharp and improve clarity.",
  "tweak_lensflare": "Lens Flares",
  "tweak_lensflare_desc": "Lens flares from bright light sources. Turn off to remove the effect.",
  "tweak_extrablur": "Extra Blur Effects",
  "tweak_extrablur_desc": "Radial blur and bokeh-style post effects. Turn off for a crisper image.",
  "tweak_vatsblur": "VATS Blur",
  "tweak_vatsblur_desc": "Enable or disable background blur while VATS targeting is active.",
  "tweak_taa": "Anti-Aliasing",
  "tweak_taa_desc": "Choose between TAA, FXAA, or None.",
  "tweak_aniso": "Anisotropic Filtering",
  "tweak_aniso_desc": "Improves clarity of textures viewed at an angle.",
  "tweak_water": "Water Quality",
  "tweak_water_desc": "Adjust reflections and displacement of water surfaces.",
  "tweak_lod": "Draw Distance (LOD)",
  "tweak_lod_desc": "Controls how far away items and characters appear before popping in.",
  "tweak_decals": "Decal Quantity",
  "tweak_decals_desc": "Controls the number of bullet holes, scorch marks, and blood splatters.",
  "tweak_pipboyfx": "Pip-Boy CRT Effect",
  "tweak_pipboyfx_desc": "Scanlines and screen curve on the Pip-Boy display. Turn off for a flat, crisp screen.",
  "tweak_pipboyfx_warning": "Turning the CRT effect off may make your Pip-Boy difficult or impossible to see and use. Turn this setting back on if that happens.",
  "tweak_ping": "Network Optimization",
  "tweak_ping_desc": "Attempts to reduce latency and jitter.",
  "tweak_bandwidth": "Bandwidth Uncap",
  "tweak_bandwidth_desc": "Allow the game to use more available bandwidth.",
  "opt_low": "Low",
  "opt_medium": "Medium",
  "opt_high": "High",
  "opt_ultra": "Ultra",
  "opt_60": "60",
  "opt_90": "90",
  "opt_120": "120",
  "opt_144": "144",
  "opt_unlimited": "Unlimited",
  "opt_none": "None",
  "opt_fxaa": "FXAA",
  "opt_taa": "TAA",
  "opt_4x": "4x",
  "opt_8x": "8x",
  "opt_16x": "16x",
  "quick_presets": "Quick Presets",
  "search_mods_placeholder": "Search mods...",
  "applied_preset_banner": "Applied {0} preset!",
  "pip_boy_desc": "Customize your Pip-Boy and Flashlight color.",
  "presets_label": "PRESETS",
  "custom_values_label": "CUSTOM VALUES",
  "preset_classic": "Classic",
  "preset_amber": "Amber",
  "preset_blue": "Blue",
  "preset_white": "White",
  "interface_tab_pipboy": "Pip-Boy",
  "interface_tab_quickboy": "Quick-Boy",
  "interface_tab_pa": "Power Armor",
  "interface_sync_label": "Sync All Colors",
  "interface_sync_desc": "Apply the same color to Pip-Boy, Quick-Boy, and Power Armor.",
  "interface_settings": "Settings",
  "interface_not_set": "Not yet configured in your INI. Set a color and click Apply to add it.",
  "quickboy_desc": "Customize your Quick-Boy overlay color.",
  "pa_desc": "Customize your Pip-Boy color while in Power Armor.",
  "configuration_profiles": "Configuration Profiles",
  "profiles_desc": "Create snapshots of your mods and settings for easy switching.",
  "profile_name_placeholder": "Profile Name (e.g. Ultra Graphics)",
  "create_from_current": "Create from Current",
  "tag_active": "Active",
  "tag_inactive": "Inactive",
  "switch": "Switch",
  "profiles_tip": "Each profile saves its own unique set of enabled mods, load order, and game settings. Switching profiles automatically applies these changes.",
  "delete_profile_confirm": "Are you sure you want to delete profile \"{0}\"?",
  "activity_log": "Activity Log",
  "error_log": "Error Log",
  "settings_saved": "Settings saved successfully.",
  "game_launched": "Game launched successfully!",
  "launching_game_via_platform": "Launching Fallout 76 through {0}...",
  "game_exe_not_found": "Game executable not found. Please check your path in Settings.",
  "changes_applied": "Changes applied successfully.",
  "config_test_success": "Configuration test successful.",
  "config_test_success_with_warnings": "Game install looks valid, but some optional paths or tools need attention. See the activity log for details.",
  "config_test_game_path_empty": "Game path is not set. Choose your Fallout 76 folder in Settings.",
  "config_test_game_path_missing": "Game folder not found: {0}",
  "config_test_data_missing": "Game Data folder not found under: {0}",
  "save_settings_failed": "Failed to save settings.",
  "interface_colors_updated": "Interface colors updated.",
  "interface_colors_synced": "All interface colors synced.",
  "import_no_new_files": "Import finished. No new mod files were added.",
  "nexus_download_complete": "Download complete. Importing {0}...",
  "nexus_download_premium": "Direct Nexus downloads require Premium. Opening the mod page — use Slow Download or Mod Manager Download.",
  "nexus_download_free_user": "Free Nexus account: opening Mod Manager Download in your browser. Confirm once — Fallout 76 Manager will import automatically.",
  "nexus_download_forbidden": "Nexus blocked the download request. Try reconnecting your Nexus account in Settings, or download from the opened mod page.",
  "nexus_collection_heading": "Import Nexus Collection",
  "nexus_collection_desc": "Paste a collection URL to download all mods via the Nexus API.",
  "nexus_collection_url_placeholder": "https://www.nexusmods.com/games/fallout76/collections/y0zdof/mods",
  "nexus_collection_import": "Import",
  "nexus_collection_fetching": "Fetching collection from Nexus...",
  "nexus_collection_queueing": "Queueing {0} mods from \"{1}\"...",
  "nexus_collection_started": "Starting collection import: {0} ({1} mods)...",
  "nexus_collection_download_progress": "Collection download {0}% ({1}/{2})",
  "nexus_collection_progress": "Collection download {0}/{1}: {2}",
  "nexus_collection_complete": "Collection \"{0}\" queued: {1} mod(s) downloading.",
  "nexus_collection_complete_partial": "Collection \"{0}\" finished with {1} queued and {2} skipped/failed.",
  "nexus_collection_login_required": "Log in to Nexus Mods in Settings before importing a collection.",
  "nexus_collection_invalid_link": "Unsupported collection link. Log in to Nexus Mods and try again, or paste the collection URL in Settings.",
  "nexus_collection_invalid_url": "Invalid Nexus collection URL.",
  "nexus_collection_missing_input": "Provide a collection URL or slug.",
  "nexus_collection_in_progress": "A collection import is already in progress.",
  "nexus_collection_fetch_failed": "Failed to fetch collection from Nexus.",
  "nexus_collection_empty": "Collection has no downloadable mods.",
  "nexus_collection_mod_skipped": "Skipped a collection mod (file {0}) — could not resolve mod info.",
  "nexus_collection_mod_failed": "Failed to queue mod file {0}: {1}",
  "removed_mod_from_profile": "Removed '{0}' from profile.",
  "delete_success": "Deleted bundle file: {0}",
  "delete_failed": "Failed to delete bundle file.",
  "delete_mod_failed": "Delete failed: {0}",
  "mod_updated_status": "{0} updated to {1}.",
  "update_metadata_failed": "Failed to update mod metadata.",
  "profile_created": "Profile '{0}' created ({1}).",
  "profile_exists": "Profile with this name already exists.",
  "profile_deleted": "Profile deleted.",
  "profile_switched": "Switched to {0}.",
  "inis_backed_up": "INIs backed up to /Backups folder.",
  "mods_backed_up": "Mods backed up to /Backups folder.",
  "no_ini_found": "No INI files found to backup.",
  "no_mod_files_found": "No mod files found to backup.",
  "backup_failed": "Backup failed.",
  "mods_backup_failed": "Mods backup failed.",
  "config_reset": "Configuration reset to defaults.",
  "reset_failed": "Reset failed: {0}",
  "cache_cleared": "Application cache cleared.",
  "deploy_success": "Successfully deployed {0} mods!",
  "deploy_failed": "Deployment failed: {0}",
  "delete_default_profile_error": "Cannot delete Default Profile.",
  "import_no_valid_files": "Import failed: No valid mod files found (or all were duplicates).",
  "config_health_header": "Configuration Health",
  "health_no_conflicts": "No conflicts detected in your current load order.",
  "health_status_ready": "Ready",
  "health_ini_verified": "INI integrity verified",
  "health_mod_files_present": "Mod files present",
  "health_profile_synced": "Profile synced",
  "stat_mods_active": "Mods Active",
  "stat_active_mod_storage": "Active Mod Storage",
  "stat_conflicts": "Conflicts",
  "never": "Never",
  "widget_conflict_detected": "Conflict Detected",
  "widget_conflict_count": "{0} Conflicts",
  "mod_bundles": "Mod Bundles",
  "create_new_bundle": "Create New Bundle",
  "no_bundles_found": "No Bundles Found",
  "no_bundles_desc": "Combine multiple mods into a single optimized BA2 archive to improve game stability and bypass the mod limit.",
  "create_first_bundle": "Create Your First Bundle",
  "in_mod_list": "In Mod List",
  "storage_only": "Storage Only",
  "bundling_workspace": "Bundling Workspace",
  "add_mod_from_list": "Add Mod from List",
  "add_external_ba2": "Add External BA2",
  "staged_items": "Staged Items ({0})",
  "workspace_empty": "Workspace is Empty",
  "workspace_empty_hint": "Workspace is empty.",
  "compression_default": "Default (Standard)",
  "create_bundle": "Create Bundle",
  "select_internal_mods": "Select Internal Mods",
  "n_selected_badge": "{0} Selected",
  "no_ba2_mods_found": "No BA2 mods found",
  "rename_bundle_prompt": "Enter new name for this bundle:",
  "added_to_workspace_banner": "Added to bundle workspace.",
  "delete_bundle_confirm": "Permanently delete \"{0}\"? This cannot be undone.",
  "select_external_ba2_title": "Select External BA2 Mods",
  "starting_bundle_banner": "Starting bundle process for \"{0}\"...",
  "remove_from_list": "Remove from Mod List",
  "add_to_list": "Add to Mod List",
  "finalize_bundle": "Finalize Bundle",
  "output_name": "Output Name",
  "created_in_bundles_dir": "Created in Bundles/ directory.",
  "compression_level": "Compression Level",
  "total_items": "Total Items",
  "bundle_format": "Format",
  "bundle_format_general": "General (non-texture)",
  "bundle_format_dds": "DDS (texture only)",
  "compression_level_1": "1 (Fast decompression)",
  "compression_level_2": "2",
  "compression_level_3": "3",
  "compression_level_4": "4",
  "compression_level_5": "5",
  "compression_level_6": "6 (Default by Bethesda)",
  "compression_level_7": "7",
  "compression_level_8": "8",
  "compression_level_9": "9 (High compression)",
  "compression_level_0": "0 (No compression)",
  "conflict_title": "Mod Conflicts Detected",
  "maybe_later": "Maybe Later",
  "endorse_on_nexus": "Endorse on Nexus",
  "no_log_entries": "No log entries found for this category.",
  "status": "Status",
  "initiating_sync": "Initiating system synchronization...",
  "conflict_desc": "The following mods are attempting to overwrite the same game files.",
  "conflict_auto_override": "Auto Conflict Override",
  "conflict_auto_override_desc": "Skip this warning in the future and always force deploy.",
  "conflict_cancel": "Cancel & Review",
  "conflict_force": "Force Deploy Anyway",
  "tweak_preset_vanilla": "Vanilla+",
  "tweak_preset_performance": "Performance",
  "tweak_preset_ultra": "Ultra",
  "tweak_preset_potato": "Potato PC",
  "archive_key_label": "Archive INI Key",
  "archive_key_desc": "Controls which INI key the manager uses for the mod load list. Use sResourceIndexFileList if your game language requires it (e.g. Spanish voice-over in newer areas).",
  "archive_key_auto": "Auto-Detect",
  "tweak_fov1st": "First-Person FOV",
  "tweak_fov1st_desc": "Fallout76Custom.ini [Display] fDefault1stPersonFOV — first-person view (default in-game often 70).",
  "tweak_fov_pipboy": "Pip-Boy FOV",
  "tweak_fov_pipboy_desc": "Fallout76Custom.ini [Display] fPipboy1stFOV — Pip-Boy screen; Creation Engine setting (may be ignored in some builds)."
}
""";

    private void HandleLoadLanguage(string lang)
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var allResources = assembly.GetManifestResourceNames();
            
            string searchKey = $".locales.{lang}.json";
            var resourceName = allResources.FirstOrDefault(r => r.EndsWith(searchKey, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null && lang.Contains("-"))
            {
                string altKey = $".locales.{lang.Replace("-", "_")}.json";
                resourceName = allResources.FirstOrDefault(r => r.EndsWith(altKey, StringComparison.OrdinalIgnoreCase));
            }

            if (resourceName != null)
            {
                var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string json = reader.ReadToEnd();
                        SendMessageToWeb(JsonSerializer.Serialize(new { 
                            type = "LANGUAGE_CONTENT", 
                            lang = lang, 
                            content = JsonDocument.Parse(json).RootElement 
                        }));
                        LogActivity($"[I18N] Served {lang} from Embedded: {resourceName}");
                        return;
                    }
                }
            }
            else 
            {
                if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                     LogActivity($"[I18N] Resource not found for {lang}. Using HARDCODED FALLBACK.");
                     SendMessageToWeb(JsonSerializer.Serialize(new { 
                            type = "LANGUAGE_CONTENT", 
                            lang = lang, 
                            content = JsonDocument.Parse(FallbackEnglish).RootElement 
                        }));
                     return;
                }

                LogActivity($"[I18N] Resource not found for {lang}. Available: {string.Join(", ", allResources.Where(r => r.Contains("locales")))}");
            }

            string localesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebSrc", "locales");
            
            if (!Directory.Exists(localesDir))
            {
                string candidate = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 5; i++)
                {
                    candidate = Directory.GetParent(candidate)?.FullName;
                    if (candidate == null) break;
                    string check = Path.Combine(candidate, "WebSrc", "locales");
                    if (Directory.Exists(check))
                    {
                        localesDir = check;
                        break;
                    }
                }
            }

            string filePath = Path.Combine(localesDir, $"{lang}.json");
            
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                SendMessageToWeb(JsonSerializer.Serialize(new { 
                    type = "LANGUAGE_CONTENT", 
                    lang = lang, 
                    content = JsonDocument.Parse(json).RootElement 
                }));
                 LogActivity($"[I18N] Served {lang}.json");
            }
            else
            {
                LogActivity($"[I18N] Language file not found: {filePath}");
                SendMessageToWeb(JsonSerializer.Serialize(new { type = "LANGUAGE_CONTENT", lang = lang, error = "not_found" }));
            }
        }
        catch (Exception ex)
        {
            LogActivity($"[I18N] Error loading language {lang}: {ex.Message}");
             SendMessageToWeb(JsonSerializer.Serialize(new { type = "LANGUAGE_CONTENT", lang = lang, error = ex.Message }));
        }
    }
}
