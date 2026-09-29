using System.Net.Http.Headers;
using System.Text.Json;
using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Threading;

namespace F76ManagerApp.Managers;

public class NexusManager : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _authCredential;
    private readonly Action<string> _logger;
    private readonly Action<string, string> _statusReporter;
    private bool _disposed;
    
    private const string BASE_URL = "https://api.nexusmods.com/v1";
    private const string GRAPHQL_URL = "https://api.nexusmods.com/v2/graphql";
    private const string GAME_DOMAIN = "fallout76";
    private const int GAME_ID = 2590;
    private const string APP_NAME = "F76Manager";

    public const string ApplicationSlug = "moddedwolfx-f76manager";

    public const bool SsoEnabled = true;

    public const string? SsoApplicationSlug = ApplicationSlug;

    public static bool IsSsoAvailable =>
        SsoEnabled && !string.IsNullOrWhiteSpace(SsoApplicationSlug);

    private const string CollectionRevisionQuery = """
        query CollectionRevisionMods($slug: String!, $revision: Int, $domainName: String!) {
          collectionRevision(slug: $slug, revision: $revision, domainName: $domainName) {
            revisionNumber
            modCount
            collection {
              name
              slug
            }
            modFiles {
              fileId
              optional
              version
              file {
                modId
                name
                version
                mod {
                  modId
                  name
                  author
                  summary
                  category
                }
              }
            }
          }
        }
        """;

    public NexusManager(string? authCredential, string applicationVersion, Action<string> logger, Action<string, string> statusReporter)
    {
        _authCredential = authCredential ?? "";
        _logger = logger;
        _statusReporter = statusReporter;
        string version = string.IsNullOrWhiteSpace(applicationVersion) ? "unknown" : applicationVersion;

        _client = new HttpClient();
        if (!string.IsNullOrWhiteSpace(_authCredential))
            _client.DefaultRequestHeaders.Add("apikey", _authCredential);
        _client.DefaultRequestHeaders.Add("Application-Name", APP_NAME);
        _client.DefaultRequestHeaders.Add("Application-Version", version);
        _client.Timeout = TimeSpan.FromMinutes(5);
    }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_authCredential);

    private string AuthRequiredJsonError() =>
        JsonSerializer.Serialize(new { error = "Sign in to Nexus Mods in Settings to use this feature." });

    public async Task<string> SearchMods(string query)
    {
        if (!IsAuthenticated) return AuthRequiredJsonError();

        try
        {
            string encodedQuery = System.Web.HttpUtility.UrlEncode(query);
            string url = $"{BASE_URL}/mods.json?terms={encodedQuery}&game_id={GAME_ID}";

            _logger($"[NEXUS] Searching for: {query}");
            var response = await _client.GetAsync(url);
            
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                
                
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("results", out var results))
                {
                   if (results.GetArrayLength() == 0) _logger("[NEXUS] No mods found matching your search.");
                }

                return json; 
            }
            else
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                _logger($"[NEXUS] Search failed: {response.StatusCode} - {errorContent}");
                return JsonSerializer.Serialize(new { error = $"Search failed: {response.ReasonPhrase} ({response.StatusCode})" });
            }
        }
        catch (Exception ex)
        {
            _logger($"[NEXUS] Search error: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    public async Task<string> GetModDetails(long modId)
    {
        if (!IsAuthenticated) return AuthRequiredJsonError();

        try
        {
            string url = $"{BASE_URL}/games/{GAME_DOMAIN}/mods/{modId}.json";
            var response = await _client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            _logger($"[NEXUS_ERR] Details request for mod {modId} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return JsonSerializer.Serialize(new { error = "Mod not found" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    public async Task<string> GetModFiles(long modId)
    {
        if (!IsAuthenticated) return AuthRequiredJsonError();

        try
        {
            string url = $"{BASE_URL}/games/{GAME_DOMAIN}/mods/{modId}/files.json";
            var response = await _client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            _logger($"[NEXUS_ERR] Files request for mod {modId} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return JsonSerializer.Serialize(new { error = "Files not found" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static readonly TimeSpan ModFilesCacheTtl = TimeSpan.FromMinutes(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (DateTime FetchedUtc, string Json)> _modFilesCache = new();

    private async Task<string> GetModFilesCached(long modId)
    {
        if (_modFilesCache.TryGetValue(modId, out var cached) && DateTime.UtcNow - cached.FetchedUtc < ModFilesCacheTtl)
            return cached.Json;

        string json = await GetModFiles(modId);
        bool isError = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            isError = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out _);
        }
        catch { isError = true; }

        if (!isError)
            _modFilesCache[modId] = (DateTime.UtcNow, json);
        return json;
    }

    public sealed class DownloadProgressOptions
    {
        public bool SuppressInfoMessages { get; set; }
        public Action<long, long?>? OnBytesProgress { get; set; }
        public Action<long>? OnContentLengthDiscovered { get; set; }
    }

    public async Task DownloadMod(
        long modId,
        long fileId,
        string fileName,
        string? nxmKey = null,
        string? nxmExpires = null,
        DownloadProgressOptions? progressOptions = null,
        CancellationToken cancellationToken = default)
    {
        string? localPathForCleanup = null;
        try
        {
            bool suppressInfo = progressOptions?.SuppressInfoMessages == true;
            bool hasNxmAuth = !string.IsNullOrEmpty(nxmKey) && !string.IsNullOrEmpty(nxmExpires);
            if (!IsAuthenticated && !hasNxmAuth)
            {
                _statusReporter("error", "Log in to Nexus Mods in Settings to download mods.");
                return;
            }

            if (!suppressInfo)
                _statusReporter("info", $"Requesting download link for {fileName}...");
            _logger($"[NEXUS] Download request: mod={modId}, file={fileId}, name={fileName}");

            if (!hasNxmAuth && !await IsPremiumUserAsync())
            {
                OpenModInBrowser(modId, fileId, triggerModManagerDownload: true);
                _statusReporter("nexus_download_free_user", modId.ToString());
                return;
            }

            string linkUrl = $"{BASE_URL}/games/{GAME_DOMAIN}/mods/{modId}/files/{fileId}/download_link.json";
            
            if (!string.IsNullOrEmpty(nxmKey) && !string.IsNullOrEmpty(nxmExpires))
            {
                linkUrl += $"?key={Uri.EscapeDataString(nxmKey)}&expires={Uri.EscapeDataString(nxmExpires)}";
            }

            var linkResponse = await _client.GetAsync(linkUrl, cancellationToken);
            
            if (!linkResponse.IsSuccessStatusCode)
            {
                var errContent = await linkResponse.Content.ReadAsStringAsync();
                _logger($"[NEXUS_ERR] Link Request Failed ({linkResponse.StatusCode}): {errContent}");

                if (linkResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    bool premiumBlocked = errContent.Contains("premium", StringComparison.OrdinalIgnoreCase) ||
                                          errContent.Contains("permission", StringComparison.OrdinalIgnoreCase);
                    OpenModInBrowser(modId, fileId);
                    _statusReporter(
                        premiumBlocked ? "nexus_download_premium" : "nexus_download_forbidden",
                        modId.ToString());
                    return;
                }

                _statusReporter("error", $"Failed to retrieve download link. HTTP {linkResponse.StatusCode}: {linkResponse.ReasonPhrase}");
                return;
            }

            var linkJson = await linkResponse.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(linkJson);
            var root = doc.RootElement;
            string downloadUri = null;
            
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                downloadUri = root[0].GetProperty("URI").GetString();
            }

            if (string.IsNullOrEmpty(downloadUri))
            {
                _statusReporter("error", "No valid download URI found.");
                return;
            }

            string downloadsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Nexus", "downloads");
            Directory.CreateDirectory(downloadsDir);
            
            string localPath = Path.Combine(downloadsDir, $"{modId}_{fileName}");
            localPathForCleanup = localPath;
            
            if (!suppressInfo)
                _statusReporter("info", $"Downloading {fileName}...");
            _logger($"[NEXUS] Downloading mod file: {fileName}");

            using (var response = await _client.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                var contentLength = response.Content.Headers.ContentLength;
                bool contentLengthReported = false;
                using (var s = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    int lastReported = -1;

                    while ((read = await s.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read, cancellationToken);
                        totalRead += read;

                        if (contentLength.HasValue && contentLength.Value > 0 && !contentLengthReported)
                        {
                            contentLengthReported = true;
                            progressOptions?.OnContentLengthDiscovered?.Invoke(contentLength.Value);
                        }

                        if (progressOptions?.OnBytesProgress != null)
                        {
                            progressOptions.OnBytesProgress(totalRead, contentLength);
                        }
                        else if (!suppressInfo && contentLength.HasValue && contentLength.Value > 0)
                        {
                            int percent = (int)((totalRead * 100L) / contentLength.Value);
                            if (percent != lastReported && percent % 5 == 0)
                            {
                                lastReported = percent;
                                _statusReporter("info", $"Downloading {fileName}... {percent}%");
                            }
                        }
                    }

                    if (progressOptions?.OnBytesProgress != null)
                        progressOptions.OnBytesProgress(totalRead, contentLength);
                }
            }

            _logger($"[IMPORT] [NEXUS] Successfully downloaded and prepared: {fileName}");
            _statusReporter("download_complete", localPath);
        }
        catch (OperationCanceledException)
        {
            _logger($"[NEXUS] Download cancelled by user: {fileName}");
            TryDeletePartialDownload(localPathForCleanup);
            _statusReporter("nexus_download_cancelled", fileName);
        }
        catch (Exception ex)
        {
            _logger($"[NEXUS] Download failed: {ex.Message}");
            TryDeletePartialDownload(localPathForCleanup);
            _statusReporter("error", $"Download failed: {ex.Message}");
        }
    }

    private void TryDeletePartialDownload(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger($"[NEXUS] Could not remove partial download '{path}': {ex.Message}");
        }
    }

    private const string SSO_URI = "wss://sso.nexusmods.com";
    
    public async Task ConnectSSO()
    {
        if (!IsSsoAvailable)
        {
            _statusReporter("error", "Nexus Mods sign-in is not available until this application is registered.");
            return;
        }

        try
        {
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            await ws.ConnectAsync(new Uri(SSO_URI), CancellationToken.None);
            
            var connectionId = Guid.NewGuid().ToString();
            _logger($"[SSO] Connected. Requesting token with ID: {connectionId}");

            var req = new { id = connectionId, token = (string)null, protocol = 2 };
            string msg = JsonSerializer.Serialize(req);
            var buffer = System.Text.Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(new ArraySegment<byte>(buffer), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);

            var rcvBuffer = new byte[1024 * 4];
            
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(rcvBuffer), CancellationToken.None);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;

                string receivedMsg = System.Text.Encoding.UTF8.GetString(rcvBuffer, 0, result.Count);
                if (string.IsNullOrEmpty(receivedMsg)) continue;
                
                using var doc = JsonDocument.Parse(receivedMsg);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("connection_token", out var ct))
                    {
                        _logger("[SSO] Received connection token. Opening browser...");
                        
                        var url = $"https://www.nexusmods.com/sso?id={connectionId}&application={Uri.EscapeDataString(SsoApplicationSlug)}";
                        
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                        _statusReporter("info", "Please authorize in the opened browser window...");
                    }
                    
                    if (data.TryGetProperty("api_key", out var ak))
                    {
                        string credential = ak.GetString();
                        if (!string.IsNullOrEmpty(credential))
                        {
                            _logger("[SSO] Nexus authorization credential received.");
                            _statusReporter("sso_success", credential);
                            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                            return;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[SSO] Error: {ex.Message}");
            _statusReporter("error", $"SSO Failed: {ex.Message}");
        }
    }

    public void RegisterNxmProtocol()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\nxm"))
            {
                key.SetValue("", "URL:F76Manager NXM Protocol");
                key.SetValue("URL Protocol", "");
                
                using (var icon = key.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue("", System.Windows.Forms.Application.ExecutablePath + ",1");
                }

                using (var command = key.CreateSubKey(@"shell\open\command"))
                {
                    command.SetValue("", "\"" + System.Windows.Forms.Application.ExecutablePath + "\" \"%1\"");
                }
            }
            _logger("[NEXUS] NXM Protocol Registered (User Mode).");
        }
        catch (UnauthorizedAccessException)
        {
            _logger("[NEXUS] Failed to register NXM protocol (HKCU): Access Denied.");
        }
        catch (Exception ex)
        {
            _logger($"[NEXUS] Protocol Registration Error: {ex.Message}");
        }
    }

    private static long ParseFileSizeBytes(JsonElement file)
    {
        if (file.TryGetProperty("size_kb", out var sizeKb) && sizeKb.ValueKind == JsonValueKind.Number)
            return sizeKb.GetInt64() * 1024L;
        if (file.TryGetProperty("size_in_bytes", out var sizeBytes) && sizeBytes.ValueKind == JsonValueKind.Number)
            return sizeBytes.GetInt64();
        if (file.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number)
            return size.GetInt64();
        return 0;
    }

    private sealed record ModFileCandidate(long FileId, string Version, long Uploaded, string FileName, int CategoryId, string Name = "", string CategoryName = "");

    private static List<ModFileCandidate> ParseModFileCandidates(JsonElement filesArray)
    {
        var candidates = new List<ModFileCandidate>();
        foreach (var file in filesArray.EnumerateArray())
        {
            long fileId = 0;
            if (file.TryGetProperty("file_id", out var fid)) fileId = fid.GetInt64();
            else if (file.TryGetProperty("id", out var idProp)) fileId = idProp.GetInt64();
            if (fileId <= 0) continue;

            string version = "";
            if (file.TryGetProperty("version", out var ver)) version = ver.GetString() ?? "";
            long uploaded = 0;
            if (file.TryGetProperty("uploaded_timestamp", out var ts)) uploaded = ts.GetInt64();
            string fileName = "";
            if (file.TryGetProperty("file_name", out var fn)) fileName = fn.GetString() ?? "";
            else if (file.TryGetProperty("name", out var n)) fileName = n.GetString() ?? "";
            int categoryId = 0;
            if (file.TryGetProperty("category_id", out var cat) && cat.ValueKind == JsonValueKind.Number) categoryId = cat.GetInt32();
            string displayName = "";
            if (file.TryGetProperty("name", out var dn) && dn.ValueKind == JsonValueKind.String) displayName = dn.GetString() ?? "";
            string categoryName = "";
            if (file.TryGetProperty("category_name", out var cn) && cn.ValueKind == JsonValueKind.String) categoryName = cn.GetString() ?? "";

            candidates.Add(new ModFileCandidate(fileId, version, uploaded, fileName, categoryId, displayName, categoryName));
        }
        return candidates;
    }

    private static ModFileCandidate? SelectPreferredModFile(IReadOnlyList<ModFileCandidate> candidates, long? preferredFileId = null)
    {
        if (candidates == null || candidates.Count == 0) return null;

        if (preferredFileId.HasValue && preferredFileId.Value > 0)
        {
            var exact = candidates.FirstOrDefault(c => c.FileId == preferredFileId.Value);
            if (exact != null) return exact;
        }

        var mainFiles = candidates.Where(c => c.CategoryId == 1).ToList();
        var pool = mainFiles.Count > 0 ? mainFiles : candidates;
        return pool.OrderByDescending(c => c.Uploaded).ThenByDescending(c => c.FileId).First();
    }

    private sealed record ModFileUpdateLink(long OldFileId, long NewFileId, long Uploaded);

    private static Dictionary<long, List<ModFileUpdateLink>> ParseFileUpdates(JsonElement root)
    {
        var map = new Dictionary<long, List<ModFileUpdateLink>>();
        if (root.ValueKind != JsonValueKind.Object) return map;
        if (!root.TryGetProperty("file_updates", out var updates) || updates.ValueKind != JsonValueKind.Array) return map;

        foreach (var u in updates.EnumerateArray())
        {
            if (!u.TryGetProperty("old_file_id", out var o) || o.ValueKind != JsonValueKind.Number) continue;
            if (!u.TryGetProperty("new_file_id", out var n) || n.ValueKind != JsonValueKind.Number) continue;
            long oldId = o.GetInt64();
            long newId = n.GetInt64();
            if (oldId <= 0 || newId <= 0 || oldId == newId) continue;
            long uploaded = 0;
            if (u.TryGetProperty("uploaded_timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number) uploaded = ts.GetInt64();

            if (!map.TryGetValue(oldId, out var list))
            {
                list = new List<ModFileUpdateLink>();
                map[oldId] = list;
            }
            list.Add(new ModFileUpdateLink(oldId, newId, uploaded));
        }
        return map;
    }

    private static bool IsInactiveFileCategory(ModFileCandidate file)
    {
        if (file.CategoryId == 4 || file.CategoryId == 6 || file.CategoryId == 7) return true;
        string name = (file.CategoryName ?? "").Trim();
        return name.Equals("OLD_VERSION", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ARCHIVED", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("DELETED", StringComparison.OrdinalIgnoreCase);
    }

    private static ModFileCandidate? ResolveUpdateTarget(
        IReadOnlyList<ModFileCandidate> candidates,
        Dictionary<long, List<ModFileUpdateLink>> fileUpdates,
        long installedFileId,
        out bool ambiguous)
    {
        ambiguous = false;
        var byId = new Dictionary<long, ModFileCandidate>();
        foreach (var c in candidates) byId[c.FileId] = c;

        long current = installedFileId;
        long lastListed = byId.ContainsKey(installedFileId) ? installedFileId : 0;
        var visited = new HashSet<long> { current };
        while (fileUpdates.TryGetValue(current, out var links) && links.Count > 0)
        {
            var next = links.OrderByDescending(l => l.Uploaded).ThenByDescending(l => l.NewFileId).First();
            if (!visited.Add(next.NewFileId)) break;
            current = next.NewFileId;
            if (byId.ContainsKey(current)) lastListed = current;
        }

        if (lastListed > 0 && lastListed != installedFileId)
            return byId[lastListed];

        byId.TryGetValue(installedFileId, out var installed);
        if (installed != null && !IsInactiveFileCategory(installed))
            return installed;

        var active = candidates.Where(c => !IsInactiveFileCategory(c)).ToList();

        if (installed != null && !string.IsNullOrWhiteSpace(installed.Name))
        {
            var sameName = active
                .Where(c => string.Equals(c.Name.Trim(), installed.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Uploaded).ThenByDescending(c => c.FileId)
                .FirstOrDefault();
            if (sameName != null) return sameName;
        }

        var mainFiles = active.Where(c => c.CategoryId == 1).ToList();
        if (mainFiles.Count == 1) return mainFiles[0];

        ambiguous = true;
        return null;
    }

    private static (ModFileCandidate? Target, bool HasUpdate, bool Ambiguous) EvaluateFileUpdate(
        IReadOnlyList<ModFileCandidate> candidates,
        Dictionary<long, List<ModFileUpdateLink>> fileUpdates,
        long installedFileId,
        string? installedVersion)
    {
        var target = ResolveUpdateTarget(candidates, fileUpdates, installedFileId, out bool ambiguous);
        if (target == null) return (null, false, ambiguous);
        if (target.FileId != installedFileId) return (target, true, false);
        return (target, IsNewerVersion(target.Version, installedVersion), false);
    }

    public static void OpenModInBrowser(long modId, long fileId = 0, bool triggerModManagerDownload = true)
    {
        string url;
        if (fileId > 0)
        {
            url = triggerModManagerDownload
                ? $"https://www.nexusmods.com/{GAME_DOMAIN}/mods/{modId}?tab=files&file_id={fileId}&nmm=1"
                : $"https://www.nexusmods.com/{GAME_DOMAIN}/mods/{modId}?tab=files&file_id={fileId}";
        }
        else
        {
            url = $"https://www.nexusmods.com/{GAME_DOMAIN}/mods/{modId}?tab=files";
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private bool? _cachedIsPremium;
    private DateTime _premiumCheckedUtc = DateTime.MinValue;

    public async Task<bool> IsPremiumUserAsync(bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(_authCredential)) return false;

        if (!forceRefresh && _cachedIsPremium.HasValue &&
            DateTime.UtcNow - _premiumCheckedUtc < TimeSpan.FromHours(1))
        {
            return _cachedIsPremium.Value;
        }

        try
        {
            var response = await _client.GetAsync($"{BASE_URL}/users/validate.json");
            if (!response.IsSuccessStatusCode)
            {
                _logger($"[NEXUS] validate.json failed: HTTP {response.StatusCode}");
                return _cachedIsPremium ?? false;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger("[NEXUS] Session is no longer authorized.");
                return false;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            bool premium = ReadPremiumFlag(root);
            _cachedIsPremium = premium;
            _premiumCheckedUtc = DateTime.UtcNow;
            _logger($"[NEXUS] User premium status: {premium}");
            return premium;
        }
        catch (Exception ex)
        {
            _logger($"[NEXUS] Premium check failed: {ex.Message}");
            return _cachedIsPremium ?? false;
        }
    }

    private static bool ReadPremiumFlag(JsonElement root)
    {
        foreach (var name in new[] { "is_premium", "is_premium?" })
        {
            if (root.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True) return true;
                if (prop.ValueKind == JsonValueKind.False) return false;
            }
        }
        return false;
    }

    public async Task<bool> ValidateSessionAsync()
    {
        if (!IsAuthenticated) return false;

        try
        {
            var response = await _client.GetAsync($"{BASE_URL}/users/validate.json");
            if (response.IsSuccessStatusCode) return true;
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return false;
            return true;
        }
        catch (Exception ex)
        {
            _logger($"[NEXUS] Session validation skipped (network): {ex.Message}");
            return true;
        }
    }

    public async void HandleNxmLink(string url)
    {
        try
        {
            if (!TryParseNxmLink(url, out var modId, out var fileId, out var key, out var expires))
                return;

            _logger($"[NEXUS] Downloading Mod ID: {modId}, File ID: {fileId}");
            _statusReporter("info", $"Processing NXM Link: Mod {modId}, File {fileId}...");
            await DownloadMod(modId, fileId, $"Mod-{modId}-File-{fileId}", key, expires);
        }
        catch (Exception ex)
        {
            _statusReporter("error", $"Invalid NXM Link: {ex.Message}");
            _logger($"[NEXUS] NXM Parse Error: {ex.Message}");
        }
    }

    public sealed class NexusImportInfo
    {
        public long ModId { get; set; }
        public long FileId { get; set; }
        public string FileName { get; set; } = "";
        public string FileVersion { get; set; } = "";
        public long? FileUploaded { get; set; }
        public string ModName { get; set; } = "";
        public string Author { get; set; } = "";
        public string Details { get; set; } = "";
        public string Category { get; set; } = "";
        public string? NxmKey { get; set; }
        public string? NxmExpires { get; set; }
        public long FileSizeBytes { get; set; }
    }

    public sealed class CollectionImportResult
    {
        public string CollectionName { get; set; } = "";
        public string Slug { get; set; } = "";
        public int RevisionNumber { get; set; }
        public List<CollectionModEntry> Mods { get; set; } = new();
        public string? Error { get; set; }
    }

    public sealed class CollectionModEntry
    {
        public long ModId { get; set; }
        public long FileId { get; set; }
        public string FileName { get; set; } = "";
        public string FileVersion { get; set; } = "";
        public bool Optional { get; set; }
    }

    public static bool TryParseCollectionUrl(string url, out string slug, out int? revision)
    {
        slug = "";
        revision = null;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            var uri = new Uri(url.Trim());
            if (!uri.Host.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase))
                return false;

            string path = uri.AbsolutePath;
            if (!path.Contains($"/{GAME_DOMAIN}/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"/games/{GAME_DOMAIN}/", StringComparison.OrdinalIgnoreCase))
                return false;

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int collectionsIdx = Array.FindIndex(
                segments,
                s => s.Equals("collections", StringComparison.OrdinalIgnoreCase));
            if (collectionsIdx < 0 || collectionsIdx + 1 >= segments.Length)
                return false;

            slug = segments[collectionsIdx + 1];
            if (string.IsNullOrWhiteSpace(slug) ||
                slug.Equals("mods", StringComparison.OrdinalIgnoreCase))
                return false;

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            if (query["revision"] != null &&
                int.TryParse(query["revision"], out int queryRevision) &&
                queryRevision > 0)
            {
                revision = queryRevision;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryParseNxmCollectionLink(string url, out string slug, out int? revision)
    {
        slug = "";
        revision = null;

        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var uri = new Uri(url);
            if (!uri.Host.Equals(GAME_DOMAIN, StringComparison.OrdinalIgnoreCase))
                return false;

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int collectionsIdx = Array.FindIndex(
                segments,
                s => s.Equals("collections", StringComparison.OrdinalIgnoreCase));
            if (collectionsIdx < 0 || collectionsIdx + 1 >= segments.Length)
                return false;

            slug = segments[collectionsIdx + 1];
            if (string.IsNullOrWhiteSpace(slug))
                return false;

            int revisionsIdx = Array.FindIndex(
                segments,
                s => s.Equals("revisions", StringComparison.OrdinalIgnoreCase));
            if (revisionsIdx >= 0 &&
                revisionsIdx + 1 < segments.Length &&
                int.TryParse(segments[revisionsIdx + 1], out int revisionNumber) &&
                revisionNumber > 0)
            {
                revision = revisionNumber;
            }

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            if (query["revision"] != null &&
                int.TryParse(query["revision"], out int queryRevision) &&
                queryRevision > 0)
            {
                revision = queryRevision;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<CollectionImportResult> FetchCollectionModsAsync(string slug, int? revision = null)
    {
        var result = new CollectionImportResult { Slug = slug };

        if (string.IsNullOrWhiteSpace(_authCredential))
        {
            result.Error = "Nexus login required.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            result.Error = "Collection slug is missing.";
            return result;
        }

        try
        {
            var payload = new
            {
                query = CollectionRevisionQuery,
                variables = new
                {
                    slug,
                    revision,
                    domainName = GAME_DOMAIN
                }
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");
            var response = await _client.PostAsync(GRAPHQL_URL, content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger($"[NEXUS] Collection GraphQL failed: HTTP {response.StatusCode} - {body}");
                result.Error = $"Collection lookup failed (HTTP {(int)response.StatusCode}).";
                return result;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array &&
                errors.GetArrayLength() > 0)
            {
                string message = errors[0].TryGetProperty("message", out var msgProp)
                    ? msgProp.GetString() ?? "GraphQL error"
                    : "GraphQL error";
                _logger($"[NEXUS] Collection GraphQL error: {message}");
                result.Error = message;
                return result;
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("collectionRevision", out var revisionNode) ||
                revisionNode.ValueKind == JsonValueKind.Null)
            {
                result.Error = "Collection not found.";
                return result;
            }

            if (revisionNode.TryGetProperty("revisionNumber", out var revProp) &&
                revProp.ValueKind == JsonValueKind.Number)
            {
                result.RevisionNumber = revProp.GetInt32();
            }

            if (revisionNode.TryGetProperty("collection", out var collectionNode) &&
                collectionNode.ValueKind == JsonValueKind.Object)
            {
                if (collectionNode.TryGetProperty("name", out var nameProp))
                    result.CollectionName = nameProp.GetString() ?? "";
                if (collectionNode.TryGetProperty("slug", out var slugProp))
                    result.Slug = slugProp.GetString() ?? slug;
            }

            if (!revisionNode.TryGetProperty("modFiles", out var modFilesNode) ||
                modFilesNode.ValueKind != JsonValueKind.Array)
            {
                result.Error = "Collection has no downloadable mods.";
                return result;
            }

            foreach (var modFile in modFilesNode.EnumerateArray())
            {
                long fileId = ReadJsonInt64(modFile, "fileId");
                if (fileId <= 0)
                    continue;

                long modId = 0;
                string fileName = "";
                string fileVersion = "";

                if (modFile.TryGetProperty("file", out var fileNode) &&
                    fileNode.ValueKind == JsonValueKind.Object)
                {
                    modId = ReadJsonInt64(fileNode, "modId");
                    if (fileNode.TryGetProperty("name", out var fileNameProp))
                        fileName = fileNameProp.GetString() ?? "";
                    if (fileNode.TryGetProperty("version", out var fileVersionProp))
                        fileVersion = fileVersionProp.GetString() ?? "";

                    if (fileNode.TryGetProperty("mod", out var modNode) &&
                        modNode.ValueKind == JsonValueKind.Object &&
                        modId <= 0)
                    {
                        modId = ReadJsonInt64(modNode, "modId");
                    }
                }

                if (string.IsNullOrWhiteSpace(fileVersion) &&
                    modFile.TryGetProperty("version", out var versionProp))
                {
                    fileVersion = versionProp.GetString() ?? "";
                }

                bool optional = modFile.TryGetProperty("optional", out var optionalProp) &&
                                optionalProp.ValueKind == JsonValueKind.True;

                if (modId <= 0)
                {
                    _logger($"[NEXUS] Collection mod missing modId for file {fileId}; will resolve via API.");
                }

                result.Mods.Add(new CollectionModEntry
                {
                    ModId = modId,
                    FileId = fileId,
                    FileName = fileName,
                    FileVersion = fileVersion,
                    Optional = optional
                });
            }

            if (result.Mods.Count == 0)
                result.Error = "Collection has no downloadable mods.";

            _logger($"[NEXUS] Collection '{result.CollectionName}' ({result.Slug}) revision {result.RevisionNumber}: {result.Mods.Count} mods.");
            return result;
        }
        catch (Exception ex)
        {
            _logger($"[NEXUS] Collection fetch failed: {ex.Message}");
            result.Error = ex.Message;
            return result;
        }
    }

    private static long ReadJsonInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return 0;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out long value))
            return value;

        if (prop.ValueKind == JsonValueKind.String &&
            long.TryParse(prop.GetString(), out long parsed))
            return parsed;

        return 0;
    }

    public static bool TryParseNxmLink(
        string url,
        out long modId,
        out long fileId,
        out string? nxmKey,
        out string? nxmExpires)
    {
        modId = 0;
        fileId = 0;
        nxmKey = null;
        nxmExpires = null;

        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            return false;

        if (TryParseNxmCollectionLink(url, out _, out _))
            return false;

        var uri = new Uri(url);
        if (!uri.Host.Equals(GAME_DOMAIN, StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.Segments;
        if (segments.Length < 5)
            return false;

        if (!long.TryParse(segments[2].Trim('/'), out modId) ||
            !long.TryParse(segments[4].Trim('/'), out fileId))
            return false;

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        nxmKey = query["key"];
        nxmExpires = query["expires"];
        return modId > 0 && fileId > 0;
    }

    public async Task<NexusImportInfo?> ResolveImportInfoAsync(long modId, long fileId)
    {
        var info = new NexusImportInfo { ModId = modId, FileId = fileId };

        try
        {
            string detailsJson = await GetModDetails(modId);
            using (var detailsDoc = JsonDocument.Parse(detailsJson))
            {
                var root = detailsDoc.RootElement;
                bool detailsFailed = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _);

                if (!detailsFailed && root.TryGetProperty("name", out var nameProp))
                    info.ModName = nameProp.GetString() ?? "";
                if (root.TryGetProperty("author", out var authorProp))
                    info.Author = authorProp.GetString() ?? "";
                if (root.TryGetProperty("summary", out var summaryProp))
                    info.Details = summaryProp.GetString() ?? "";
                else if (root.TryGetProperty("description", out var descProp))
                    info.Details = descProp.GetString() ?? "";
                if (root.TryGetProperty("category_name", out var catProp))
                    info.Category = catProp.GetString() ?? "";
                else if (root.TryGetProperty("category", out var catAlt) && catAlt.ValueKind == JsonValueKind.String)
                    info.Category = catAlt.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            _logger($"[NEXUS] ResolveImportInfo mod details failed: {ex.Message}");
        }

        try
        {
            string filesJson = await GetModFiles(modId);
            using var doc = JsonDocument.Parse(filesJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _))
                return info;

            JsonElement filesArray;
            if (root.ValueKind == JsonValueKind.Array)
                filesArray = root;
            else if (root.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
                filesArray = filesProp;
            else
                return info;

            foreach (var file in filesArray.EnumerateArray())
            {
                long id = 0;
                if (file.TryGetProperty("file_id", out var fid)) id = fid.GetInt64();
                else if (file.TryGetProperty("id", out var idProp)) id = idProp.GetInt64();
                if (id != fileId) continue;

                if (file.TryGetProperty("file_name", out var fn)) info.FileName = fn.GetString() ?? "";
                else if (file.TryGetProperty("name", out var n)) info.FileName = n.GetString() ?? "";
                if (file.TryGetProperty("version", out var ver)) info.FileVersion = ver.GetString() ?? "";
                if (file.TryGetProperty("uploaded_timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number)
                    info.FileUploaded = ts.GetInt64();
                info.FileSizeBytes = ParseFileSizeBytes(file);
                break;
            }

            var candidates = ParseModFileCandidates(filesArray);
            var preferred = SelectPreferredModFile(candidates, fileId);
            if (preferred != null)
            {
                if (string.IsNullOrWhiteSpace(info.FileName)) info.FileName = preferred.FileName;
                if (string.IsNullOrWhiteSpace(info.FileVersion)) info.FileVersion = preferred.Version;
                if (!info.FileUploaded.HasValue && preferred.Uploaded > 0) info.FileUploaded = preferred.Uploaded;
            }

            if (info.FileSizeBytes <= 0)
            {
                foreach (var file in filesArray.EnumerateArray())
                {
                    long id = 0;
                    if (file.TryGetProperty("file_id", out var fid)) id = fid.GetInt64();
                    else if (file.TryGetProperty("id", out var idProp)) id = idProp.GetInt64();
                    if (id != fileId) continue;
                    info.FileSizeBytes = ParseFileSizeBytes(file);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[NEXUS] ResolveImportInfo file list failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(info.FileName))
            info.FileName = $"Mod-{modId}-File-{fileId}";

        return info;
    }

    public sealed class ModLinkResolveResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public long NexusModId { get; set; }
        public long? NexusFileId { get; set; }
        public string NexusFileVersion { get; set; } = "";
        public string FileName { get; set; } = "";
        public long? NexusFileUploaded { get; set; }
        public string Url { get; set; } = "";
        public bool HasUpdate { get; set; }
    }

    public async Task<ModLinkResolveResult> ResolveModLinkAsync(long nexusModId, long? currentFileId = null, string? currentVersion = null)
    {
        var result = new ModLinkResolveResult
        {
            NexusModId = nexusModId,
            Url = $"https://www.nexusmods.com/{GAME_DOMAIN}/mods/{nexusModId}"
        };

        if (nexusModId <= 0)
        {
            result.Error = "Invalid Nexus mod ID";
            return result;
        }

        try
        {
            string filesJson = await GetModFiles(nexusModId);
            using var doc = JsonDocument.Parse(filesJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errProp))
            {
                result.Error = errProp.GetString() ?? "Files not found";
                return result;
            }

            JsonElement filesArray;
            if (root.ValueKind == JsonValueKind.Array)
                filesArray = root;
            else if (root.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
                filesArray = filesProp;
            else
            {
                result.Error = "Unexpected files response";
                return result;
            }

            var candidates = ParseModFileCandidates(filesArray);
            if (candidates.Count == 0)
            {
                result.Error = "No files on Nexus";
                return result;
            }

            ModFileCandidate? preferred;
            if (currentFileId.HasValue && currentFileId.Value > 0)
            {
                var (target, hasUpdate, _) = EvaluateFileUpdate(candidates, ParseFileUpdates(root), currentFileId.Value, currentVersion);
                if (target == null)
                {
                    result.Error = $"Couldn't match file ID {currentFileId.Value} to a current file on this mod. Check the file ID on the mod's Files tab.";
                    return result;
                }
                preferred = target;
                result.HasUpdate = hasUpdate;
            }
            else
            {
                var activeMains = candidates.Where(c => c.CategoryId == 1 && !IsInactiveFileCategory(c)).ToList();
                if (activeMains.Count > 1)
                {
                    result.Error = "This mod has several main files. Enter the file ID of the one you installed, then fetch again.";
                    return result;
                }
                preferred = SelectPreferredModFile(candidates);
                if (preferred == null)
                {
                    result.Error = "No files on Nexus";
                    return result;
                }
                result.HasUpdate = true;
            }

            result.Ok = true;
            result.NexusFileId = preferred.FileId;
            result.NexusFileVersion = preferred.Version ?? "";
            result.FileName = preferred.FileName ?? "";
            result.NexusFileUploaded = preferred.Uploaded > 0 ? preferred.Uploaded : null;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger($"[NEXUS] ResolveModLink failed for {nexusModId}: {ex.Message}");
            return result;
        }
    }

    public sealed class ModUpdateCheckResult
    {
        public string OriginalName { get; set; } = "";
        public long NexusModId { get; set; }
        public bool HasUpdate { get; set; }
        public bool IsUnverifiedLink { get; set; }
        public long? InstalledFileId { get; set; }
        public string InstalledVersion { get; set; } = "";
        public long? LatestFileId { get; set; }
        public string LatestVersion { get; set; } = "";
        public string LatestFileName { get; set; } = "";
        public long? LatestUploaded { get; set; }
        public string? Error { get; set; }
    }

    public async Task<ModUpdateCheckResult> CheckModUpdateAsync(
        string originalName,
        long nexusModId,
        long? installedFileId,
        string? installedVersion,
        long? installedUploaded)
    {
        var result = new ModUpdateCheckResult
        {
            OriginalName = originalName,
            NexusModId = nexusModId,
            InstalledFileId = installedFileId,
            InstalledVersion = installedVersion ?? ""
        };

        try
        {
            string filesJson = await GetModFilesCached(nexusModId);
            using var doc = JsonDocument.Parse(filesJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _))
            {
                result.Error = root.TryGetProperty("error", out var err) ? err.GetString() : "Files not found";
                return result;
            }

            JsonElement filesArray;
            if (root.ValueKind == JsonValueKind.Array)
                filesArray = root;
            else if (root.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
                filesArray = filesProp;
            else
            {
                result.Error = "Unexpected files response";
                return result;
            }

            var candidates = ParseModFileCandidates(filesArray);

            if (candidates.Count == 0)
            {
                result.Error = "No files on Nexus";
                return result;
            }

            if (!installedFileId.HasValue || installedFileId.Value <= 0)
            {
                result.IsUnverifiedLink = true;
                result.HasUpdate = false;
                return result;
            }

            result.IsUnverifiedLink = false;

            var fileUpdates = ParseFileUpdates(root);
            var (target, hasUpdate, ambiguous) = EvaluateFileUpdate(candidates, fileUpdates, installedFileId.Value, installedVersion);
            if (target == null)
            {
                if (ambiguous)
                    _logger($"[NEXUS] Update check for mod {nexusModId} ({originalName}): installed file {installedFileId.Value} has no update chain and multiple main files exist; skipping.");
                result.HasUpdate = false;
                return result;
            }

            result.LatestFileId = target.FileId;
            result.LatestVersion = target.Version;
            result.LatestFileName = target.FileName;
            result.LatestUploaded = target.Uploaded > 0 ? target.Uploaded : null;
            result.HasUpdate = hasUpdate;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

    public const long AppNexusModId = 3674;

    public sealed class AppUpdateInfo
    {
        public bool LoggedIn { get; set; }
        public bool Premium { get; set; }
        public bool UpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public long? LatestFileId { get; set; }
        public string LatestFileName { get; set; } = "";
        public string Changelog { get; set; } = "";
        public string? Error { get; set; }
    }

    public sealed class AppPackageDownloadResult
    {
        public bool Ok { get; set; }
        public bool NeedsBrowser { get; set; }
        public string? LocalPath { get; set; }
        public string? Error { get; set; }
    }

    public static bool IsNewerVersion(string? latest, string? current)
    {
        if (!TryParseAppVersion(latest, out var remote)) return false;
        if (!TryParseAppVersion(current, out var local)) return false;
        return remote > local;
    }

    private static bool TryParseAppVersion(string? raw, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var match = System.Text.RegularExpressions.Regex.Match(raw.Trim(), @"\d+\.\d+(?:\.\d+)?(?:\.\d+)?");
        if (!match.Success) return false;
        string text = match.Value;
        if (text.Count(c => c == '.') == 1) text += ".0";
        return Version.TryParse(text, out version!);
    }

    private static string ReadFileChangelog(JsonElement filesArray, long fileId)
    {
        foreach (var file in filesArray.EnumerateArray())
        {
            long id = 0;
            if (file.TryGetProperty("file_id", out var fid) && fid.ValueKind == JsonValueKind.Number)
                id = fid.GetInt64();
            else if (file.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                id = idProp.GetInt64();
            if (id != fileId) continue;

            foreach (var name in new[] { "changelog_html", "changelog" })
            {
                if (!file.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
                    continue;
                string text = prop.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            return "";
        }
        return "";
    }

    private async Task<string> ReadVersionChangelogAsync(long modId, string version)
    {
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(version)) return "";

        try
        {
            string url = $"{BASE_URL}/games/{GAME_DOMAIN}/mods/{modId}/changelogs.json";
            var response = await _client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return "";

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";

            string wanted = version.Trim().TrimStart('v', 'V');
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                string key = entry.Name.Trim().TrimStart('v', 'V');
                if (!string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                return FormatChangelogValue(entry.Value);
            }
        }
        catch (Exception ex)
        {
            _logger($"[APP-UPDATE] Changelog lookup failed: {ex.Message}");
        }

        return "";
    }

    private static string FormatChangelogValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return (value.GetString() ?? "").Trim();

        if (value.ValueKind != JsonValueKind.Array) return "";

        var lines = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            string line = (item.GetString() ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    public async Task<AppUpdateInfo> CheckAppUpdateAsync(string currentVersion)
    {
        var result = new AppUpdateInfo
        {
            LoggedIn = IsAuthenticated,
            CurrentVersion = currentVersion ?? ""
        };

        if (!IsAuthenticated)
        {
            result.Error = "not_logged_in";
            return result;
        }

        try
        {
            result.Premium = await IsPremiumUserAsync();
            string filesJson = await GetModFiles(AppNexusModId);
            using var doc = JsonDocument.Parse(filesJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errProp))
            {
                result.Error = errProp.GetString() ?? "Files not found";
                return result;
            }

            JsonElement filesArray;
            if (root.ValueKind == JsonValueKind.Array)
                filesArray = root;
            else if (root.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
                filesArray = filesProp;
            else
            {
                result.Error = "Unexpected files response";
                return result;
            }

            var latest = SelectPreferredModFile(ParseModFileCandidates(filesArray));
            if (latest == null)
            {
                result.Error = "No files on Nexus";
                return result;
            }

            result.LatestFileId = latest.FileId;
            result.LatestVersion = latest.Version ?? "";
            result.LatestFileName = latest.FileName ?? "";
            result.UpdateAvailable = IsNewerVersion(result.LatestVersion, result.CurrentVersion);
            result.Changelog = ReadFileChangelog(filesArray, latest.FileId);
            if (string.IsNullOrWhiteSpace(result.Changelog))
                result.Changelog = await ReadVersionChangelogAsync(AppNexusModId, result.LatestVersion);
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger($"[APP-UPDATE] Check failed: {ex.Message}");
            return result;
        }
    }

    public async Task<AppPackageDownloadResult> DownloadFileToAsync(
        long modId,
        long fileId,
        string destPath,
        Action<int>? onPercent = null,
        CancellationToken cancellationToken = default,
        string? nxmKey = null,
        string? nxmExpires = null)
    {
        var result = new AppPackageDownloadResult();
        bool hasNxmAuth = !string.IsNullOrEmpty(nxmKey) && !string.IsNullOrEmpty(nxmExpires);
        if (!IsAuthenticated && !hasNxmAuth)
        {
            result.Error = "not_logged_in";
            return result;
        }

        if (!hasNxmAuth && !await IsPremiumUserAsync())
        {
            result.NeedsBrowser = true;
            result.Error = "premium";
            return result;
        }

        try
        {
            string linkUrl = $"{BASE_URL}/games/{GAME_DOMAIN}/mods/{modId}/files/{fileId}/download_link.json";
            if (hasNxmAuth)
                linkUrl += $"?key={Uri.EscapeDataString(nxmKey!)}&expires={Uri.EscapeDataString(nxmExpires!)}";
            var linkResponse = await _client.GetAsync(linkUrl, cancellationToken);
            if (!linkResponse.IsSuccessStatusCode)
            {
                string errContent = await linkResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger($"[APP-UPDATE] Link request failed ({linkResponse.StatusCode}): {errContent}");
                if (linkResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    result.NeedsBrowser = true;
                    result.Error = "premium";
                    return result;
                }

                result.Error = $"Failed to retrieve download link. HTTP {linkResponse.StatusCode}";
                return result;
            }

            string linkJson = await linkResponse.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(linkJson);
            string? downloadUri = null;
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                downloadUri = doc.RootElement[0].GetProperty("URI").GetString();

            if (string.IsNullOrWhiteSpace(downloadUri))
            {
                result.Error = "No valid download URI found.";
                return result;
            }

            string? dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            using var response = await _client.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long? contentLength = response.Content.Headers.ContentLength;
            int lastReported = -1;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                if (onPercent == null || !contentLength.HasValue || contentLength.Value <= 0) continue;
                int percent = (int)Math.Min(100, (totalRead * 100L) / contentLength.Value);
                if (percent != lastReported && (percent == 100 || percent - lastReported >= 5))
                {
                    lastReported = percent;
                    onPercent(percent);
                }
            }

            onPercent?.Invoke(100);
            result.Ok = true;
            result.LocalPath = destPath;
            return result;
        }
        catch (Exception ex)
        {
            _logger($"[APP-UPDATE] Download failed: {ex.Message}");
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            result.Error = ex.Message;
            return result;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _client.Dispose();
        _disposed = true;
    }
}
