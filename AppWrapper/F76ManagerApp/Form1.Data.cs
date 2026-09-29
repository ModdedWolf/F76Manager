using System.Text.Json;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using F76ManagerApp.Managers;

namespace F76ManagerApp;

public partial class Form1
{
    private long? _cachedGameDataSizeBytes;
    private string? _cachedGameDataSizePath;
    private volatile bool _gameDataSizeScanRunning;
    private string? _cachedActiveModsSizeDisplay;
    private string? _cachedActiveModsSizeSignature;
    private volatile bool _activeModsSizeScanRunning;

    private string NormalizeUiTheme(string? id) =>
        ThemeIds.NormalizeUiTheme(id, uid => _themePackageLoader.IsUserTheme(uid));

    private volatile bool _settingsApplied;

    private void TryPeekUiThemeFromSettings()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("uiAnimations", out var ua)
                && (ua.ValueKind == JsonValueKind.True || ua.ValueKind == JsonValueKind.False))
            {
                uiAnimations = ua.GetBoolean();
            }
            if (root.TryGetProperty("language", out var bl) && bl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(bl.GetString()))
            {
                applicationLanguage = bl.GetString()!;
            }
            if (!root.TryGetProperty("uiTheme", out var ut)) return;
            var raw = ut.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return;
            if (_themesReady || ThemeIds.IsBuiltIn(raw))
                uiTheme = NormalizeUiTheme(raw);
            else if (Managers.ThemePackageValidator.IsValidThemeId(raw))
                uiTheme = raw;
        }
        catch {  }
    }

    private string BuildUserThemesBootScript()
    {
        var ids = _themePackageLoader.Themes.Select(t => t.Id).ToArray();
        var css = _themePackageLoader.Themes.ToDictionary(t => t.Id, t => t.CssBlock);
        var idsJson = JsonSerializer.Serialize(ids);
        var cssJson = JsonSerializer.Serialize(css);
        var themeId = NormalizeUiTheme(uiTheme);
        var logoPath = ResolveBootLogoPath(themeId);
        var builtinIdsJson = JsonSerializer.Serialize(ThemeBootLogos.BuiltInIds);
        var themeJson = JsonSerializer.Serialize(themeId);
        var logoJson = JsonSerializer.Serialize(logoPath);
        var assetVerJson = JsonSerializer.Serialize(CurrentVersion);
        var animJson = uiAnimations ? "true" : "false";
        var langJson = JsonSerializer.Serialize(applicationLanguage);
        return
            $"window.__F76_BOOT_UI_THEME={themeJson};" +
            $"window.__F76_BOOT_LOGO={logoJson};" +
            $"window.__F76_ASSET_VERSION={assetVerJson};" +
            $"window.__F76_BUILTIN_THEME_IDS={builtinIdsJson};" +
            $"window.__F76_USER_THEME_IDS={idsJson};" +
            $"window.__F76_USER_THEME_CSS={cssJson};" +
            $"window.__F76_BOOT_UI_ANIMATIONS={animJson};" +
            $"window.__F76_BOOT_LANGUAGE={langJson};";
    }

    private string ResolveBootLogoPath(string themeId)
    {
        if (ThemeBootLogos.LogoPaths.TryGetValue(themeId, out var builtInLogo))
            return builtInLogo;

        var user = _themePackageLoader.Themes.FirstOrDefault(t => t.Id == themeId);
        if (user != null)
            return user.LogoVirtualPath;

        return ThemeBootLogos.LogoPaths.TryGetValue(ThemeIds.Default, out var fallback)
            ? fallback
            : "assets/Icon-nobg.png";
    }

    private static bool IsNexusCredentialSettingKey(string key) =>
        string.Equals(key, "nexusApiKey", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "nexusApiKeyProtected", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "nexusAuthCredentialProtected", StringComparison.OrdinalIgnoreCase);

    private void SanitizeProfileCredentialSettings()
    {
        bool dirty = false;
        foreach (var profile in profiles)
        {
            if (profile.Settings == null) continue;
            foreach (var key in profile.Settings.Keys.ToList())
            {
                if (!IsNexusCredentialSettingKey(key)) continue;
                profile.Settings.Remove(key);
                dirty = true;
            }
        }
        if (dirty) SaveProfiles();
    }

    private void SendDataToWeb(object? status = null, bool deferHeavyWork = false, bool deferModScan = false)
    {
        if (this.InvokeRequired)
        {
            BeginInvoke(() => SendDataToWeb(status, deferHeavyWork, deferModScan));
            return;
        }

        if (webView == null || webView.CoreWebView2 == null) return;
        if (_modManager == null) return;

        try 
        {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            if (!deferHeavyWork)
                SyncTweakValuesFromIni();
            long tIni = swTotal.ElapsedMilliseconds;
            var modsPayload = deferModScan ? new List<object>() : SafeGetRealMods();
            long tMods = swTotal.ElapsedMilliseconds - tIni;
            var statsPayload = deferModScan ? BuildStatsQuick() : SafeGetRealStats(modsPayload);
            long tStats = swTotal.ElapsedMilliseconds - tIni - tMods;
            var data = new {
                type = "UPDATE_DATA",
                modsDeferred = deferModScan,
                mods = modsPayload,
                settings = SafeGetRealSettings(),
                stats = statsPayload,
                managerSettings = new {
                    gamePath = gamePath,
                    documentsPath = documentsPath,
                    localAppDataPath = localAppDataPath,
                    stringsPath = AppPaths.StringsPath,
                    minimizeToTray = minimizeToTray,
                    uiAnimations = uiAnimations,
                    hideKofi = hideKofi,
                    logsPopoutReopenOnLaunch = logsPopoutReopenOnLaunch,
                    logsPopoutKeepOpen = logsPopoutKeepOpen,
                    platformBadgeGlow = platformBadgeGlow,
                    syncPlatforms = syncPlatforms,
                    autoForceDeploy = autoForceDeploy,
                    virtualModMode = virtualModMode,
                    configEditorSpellCheck = configEditorSpellCheck,
                    confirmBeforeDeleteMod = confirmBeforeDeleteMod,
                    confirmBeforeRemoveOldModOnUpdate = confirmBeforeRemoveOldModOnUpdate,
                    updateModsInAllPresets = updateModsInAllPresets,
                    language = applicationLanguage,
                    uiTheme = NormalizeUiTheme(uiTheme),
                    nexusLoggedIn = nexusLoggedIn,
                    archiveKeyName = archiveKeyName,
                    keybinds = ParseKeybindsForWeb(),
                    sevenZipPath = sevenZipPath,
                    rarExtractorPath = rarExtractorPath
                },
                profiles = GetProfileList(),
                activeProfile = activeProfile,
                activeTweaksPreset = activeTweaksPreset,
                activeModPreset = activeModPreset,
                lastSection = lastSection,
                status = status,
                appVersion = CurrentVersion,
                modPresets = GetModPresetsObjectForWeb(),
                modGroups = GetModPresetsObjectForWeb(),
                platform = _platformManager.GetPlatformLabel(),
                conflictsCount = _conflictManager != null ? _conflictManager.LastConflictCount : 0,
                configHealth = deferHeavyWork ? BuildConfigHealthQuick() : BuildConfigHealth(),
                importedCollection = BuildImportedCollectionPayload(),
                logs = SafeGetRealLogs(),
                userThemes = _themePackageLoader.Themes.Select(t => new
                {
                    id = t.Id,
                    displayName = t.DisplayName,
                    logo = t.LogoVirtualPath,
                    css = t.CssBlock,
                }).ToArray(),
            };

            long tRest = swTotal.ElapsedMilliseconds - tIni - tMods - tStats;
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            webView.CoreWebView2.PostWebMessageAsJson(json);
            StartupTrace.Mark($"SendDataToWeb(deferHeavy={deferHeavyWork}, deferMods={deferModScan}) total={swTotal.ElapsedMilliseconds}ms ini={tIni} mods={tMods} stats={tStats} rest={tRest} json={json.Length / 1024}KB modCount={modsPayload.Count}");
            EnsureAppUpdateCheck();
        } catch (Exception ex) { LogError($"Failed to send data to web: {ex.Message}"); }
    }

    private object SafeGetRealStats(List<object>? modsAlreadyListed = null)
    {
        try {
            string dataPath = Path.Combine(gamePath, "Data");
            long dataSize = 0;
            if (!string.IsNullOrWhiteSpace(gamePath) && Directory.Exists(dataPath))
            {
                if (_cachedGameDataSizePath == dataPath && _cachedGameDataSizeBytes.HasValue)
                {
                    dataSize = _cachedGameDataSizeBytes.Value;
                }
                else if (!_gameDataSizeScanRunning)
                {
                    _gameDataSizeScanRunning = true;
                    Task.Run(() =>
                    {
                        try
                        {
                            long size = GetDirectorySize(dataPath);
                            _cachedGameDataSizeBytes = size;
                            _cachedGameDataSizePath = dataPath;
                            RunOnUiThreadSafe(() => SendDataToWeb(deferHeavyWork: true));
                        }
                        catch (Exception ex)
                        {
                            LogError($"[DATA] Background data-size scan failed: {ex.Message}");
                        }
                        finally
                        {
                            _gameDataSizeScanRunning = false;
                        }
                    });
                }
            }

            var mods = modsAlreadyListed ?? _modManager.GetModsList();
            var enabledNames = new List<string>();
            foreach (var m in mods)
            {
                try
                {
                    dynamic d = m;
                    if ((string)d.status != "enabled") continue;
                    string original = (string)d.originalName;
                    if (IsConfigListKey(original) || IsCoreIniHealthKey(original)) continue;
                    enabledNames.Add(original);
                }
                catch { }
            }
            int enabledCount = enabledNames.Count;
            enabledNames.Sort(StringComparer.OrdinalIgnoreCase);
            string signature = string.Join("\n", enabledNames);

            bool cacheValid = _cachedActiveModsSizeDisplay != null &&
                              string.Equals(_cachedActiveModsSizeSignature, signature, StringComparison.Ordinal);
            string activeModsSize = cacheValid ? _cachedActiveModsSizeDisplay! : (_cachedActiveModsSizeDisplay ?? "...");

            if (!cacheValid && !_activeModsSizeScanRunning)
            {
                _activeModsSizeScanRunning = true;
                Task.Run(() =>
                {
                    bool updated = false;
                    try
                    {
                        long activeModsBytes = 0;
                        foreach (var path in _modManager.GetEnabledModPaths())
                        {
                            try
                            {
                                if (File.Exists(path))
                                    activeModsBytes += new FileInfo(path).Length;
                            }
                            catch { }
                        }
                        _cachedActiveModsSizeDisplay = FormatSize(activeModsBytes);
                        _cachedActiveModsSizeSignature = signature;
                        updated = true;
                    }
                    catch (Exception ex)
                    {
                        LogError($"[DATA] Background active-mod size scan failed: {ex.Message}");
                    }
                    finally
                    {
                        _activeModsSizeScanRunning = false;
                    }
                    if (updated)
                        RunOnUiThreadSafe(() => SendDataToWeb(deferHeavyWork: true));
                });
            }

            return new {
                totalDataSize = FormatSize(dataSize),
                modsActive = enabledCount,
                activeModsSize = activeModsSize,
                lastLaunch = lastGameLaunch
            };
        } catch (Exception ex) {
            LogError($"[DATA] Failed to build stats payload: {ex.Message}");
            return new { totalDataSize = "0 B", modsActive = 0, activeModsSize = "0 B", lastLaunch = "Never" };
        }
    }

    private void SetVolumQuality(bool xbox, string v) { if (xbox) xboxVolumQuality = v; else steamVolumQuality = v; }
    private void SetShadowRes(bool xbox, string v) { if (xbox) xboxShadowRes = v; else steamShadowRes = v; }
    private void SetShadowFilter(bool xbox, string v) { if (xbox) xboxShadowFilter = v; else steamShadowFilter = v; }
    private void SetTextureQuality(bool xbox, string v) { if (xbox) xboxTextureQuality = v; else steamTextureQuality = v; }
    private void SetDecalsPerFrame(bool xbox, string v) { if (xbox) xboxDecalsPerFrame = v; else steamDecalsPerFrame = v; }
    private void SetGridLoad(bool xbox, string v) { if (xbox) xboxGridLoad = v; else steamGridLoad = v; }
    private void SetCorpseHighlight(bool xbox, string v) { if (xbox) xboxCorpseHighlight = v; else steamCorpseHighlight = v; }
    private void SetGrassFade(bool xbox, int v) { if (xbox) xboxGrassFade = v; else steamGrassFade = v; }
    private void SetTreeDist(bool xbox, int v) { if (xbox) xboxTreeDist = v; else steamTreeDist = v; }
    private void SetLodSky(bool xbox, int v) { if (xbox) xboxLodSky = v; else steamLodSky = v; }
    private void SetLeafAnim(bool xbox, int v) { if (xbox) xboxLeafAnim = v; else steamLeafAnim = v; }
    private void SetGamma(bool xbox, double v) { if (xbox) xboxGamma = v; else steamGamma = v; }

    private Dictionary<string, object> SafeGetRealSettings()
    {
        var s = new Dictionary<string, object>();
        s["steamPipboyRed"] = steamPipboyRed; s["steamPipboyGreen"] = steamPipboyGreen; s["steamPipboyBlue"] = steamPipboyBlue;
        s["steamQuickboyRed"] = steamQuickboyRed; s["steamQuickboyGreen"] = steamQuickboyGreen; s["steamQuickboyBlue"] = steamQuickboyBlue;
        s["steamPaRed"] = steamPaRed; s["steamPaGreen"] = steamPaGreen; s["steamPaBlue"] = steamPaBlue;
        s["steamHudRed"] = steamHudRed; s["steamHudGreen"] = steamHudGreen; s["steamHudBlue"] = steamHudBlue;
        s["steamFov"] = steamFov; s["steamFov1st"] = steamFov1st; s["steamFovPipboy"] = steamFovPipboy;
        s["steamShadows"] = steamShadows; s["steamTaa"] = steamTaa;
        s["steamGodrays"] = steamGodrays; s["steamDof"] = steamDof; s["steamGrass"] = steamGrass;
        s["steamPing"] = steamPing; s["steamBandwidth"] = steamBandwidth; s["steamFastload"] = steamFastload; s["steamVsync"] = steamVsync;
        s["steamFpsCap"] = steamFpsCap;
        s["steamAniso"] = steamAniso; s["steamWater"] = steamWater; s["steamLod"] = steamLod; s["steamDecals"] = steamDecals;
        s["steamPipboyFx"] = steamPipboyFx;
        s["steamVolumQuality"] = steamVolumQuality; s["steamShadowRes"] = steamShadowRes; s["steamShadowFilter"] = steamShadowFilter;
        s["steamTextureQuality"] = steamTextureQuality; s["steamDecalsPerFrame"] = steamDecalsPerFrame; s["steamGridLoad"] = steamGridLoad;
        s["steamCorpseHighlight"] = steamCorpseHighlight; s["steamFocusShadows"] = steamFocusShadows; s["steamRenderGrass"] = steamRenderGrass;
        s["steamSsr"] = steamSsr; s["steamRainOcclusion"] = steamRainOcclusion; s["steamNpcShadowLights"] = steamNpcShadowLights;
        s["steamCellLoads"] = steamCellLoads; s["steamTiledLighting"] = steamTiledLighting; s["steamSkipSplash"] = steamSkipSplash;
        s["steamGlassShader"] = steamGlassShader; s["steamPbrShadows"] = steamPbrShadows; s["steamPlayerNames"] = steamPlayerNames;
        s["steamPlayerPings"] = steamPlayerPings; s["steamGrassFade"] = steamGrassFade; s["steamTreeDist"] = steamTreeDist;
        s["steamLodSky"] = steamLodSky; s["steamLeafAnim"] = steamLeafAnim; s["steamConversationHistory"] = steamConversationHistory;
        s["steamGamma"] = steamGamma;

        s["xboxPipboyRed"] = xboxPipboyRed; s["xboxPipboyGreen"] = xboxPipboyGreen; s["xboxPipboyBlue"] = xboxPipboyBlue;
        s["xboxQuickboyRed"] = xboxQuickboyRed; s["xboxQuickboyGreen"] = xboxQuickboyGreen; s["xboxQuickboyBlue"] = xboxQuickboyBlue;
        s["xboxPaRed"] = xboxPaRed; s["xboxPaGreen"] = xboxPaGreen; s["xboxPaBlue"] = xboxPaBlue;
        s["xboxHudRed"] = xboxHudRed; s["xboxHudGreen"] = xboxHudGreen; s["xboxHudBlue"] = xboxHudBlue;
        s["xboxFov"] = xboxFov; s["xboxFov1st"] = xboxFov1st; s["xboxFovPipboy"] = xboxFovPipboy;
        s["xboxShadows"] = xboxShadows; s["xboxTaa"] = xboxTaa;
        s["xboxGodrays"] = xboxGodrays; s["xboxDof"] = xboxDof; s["xboxGrass"] = xboxGrass;
        s["xboxPing"] = xboxPing; s["xboxBandwidth"] = xboxBandwidth; s["xboxFastload"] = xboxFastload; s["xboxVsync"] = xboxVsync;
        s["xboxFpsCap"] = xboxFpsCap;
        s["xboxAniso"] = xboxAniso; s["xboxWater"] = xboxWater; s["xboxLod"] = xboxLod; s["xboxDecals"] = xboxDecals;
        s["xboxPipboyFx"] = xboxPipboyFx;
        s["xboxVolumQuality"] = xboxVolumQuality; s["xboxShadowRes"] = xboxShadowRes; s["xboxShadowFilter"] = xboxShadowFilter;
        s["xboxTextureQuality"] = xboxTextureQuality; s["xboxDecalsPerFrame"] = xboxDecalsPerFrame; s["xboxGridLoad"] = xboxGridLoad;
        s["xboxCorpseHighlight"] = xboxCorpseHighlight; s["xboxFocusShadows"] = xboxFocusShadows; s["xboxRenderGrass"] = xboxRenderGrass;
        s["xboxSsr"] = xboxSsr; s["xboxRainOcclusion"] = xboxRainOcclusion; s["xboxNpcShadowLights"] = xboxNpcShadowLights;
        s["xboxCellLoads"] = xboxCellLoads; s["xboxTiledLighting"] = xboxTiledLighting; s["xboxSkipSplash"] = xboxSkipSplash;
        s["xboxGlassShader"] = xboxGlassShader; s["xboxPbrShadows"] = xboxPbrShadows; s["xboxPlayerNames"] = xboxPlayerNames;
        s["xboxPlayerPings"] = xboxPlayerPings; s["xboxGrassFade"] = xboxGrassFade; s["xboxTreeDist"] = xboxTreeDist;
        s["xboxLodSky"] = xboxLodSky; s["xboxLeafAnim"] = xboxLeafAnim; s["xboxConversationHistory"] = xboxConversationHistory;
        s["xboxGamma"] = xboxGamma;

        bool isXbox = _platformManager.IsXbox();
        s["godrays"] = isXbox ? xboxGodrays : steamGodrays;
        s["grass"] = isXbox ? xboxGrass : steamGrass;
        s["shadows"] = isXbox ? xboxShadows : steamShadows;
        s["fov"] = isXbox ? xboxFov : steamFov;
        s["fov1st"] = isXbox ? xboxFov1st : steamFov1st;
        s["fovPipboy"] = isXbox ? xboxFovPipboy : steamFovPipboy;
        s["motionblur"] = isXbox ? xboxDof : steamDof;
        s["taa"] = isXbox ? xboxTaa : steamTaa;
        bool fastloadOn = isXbox ? xboxFastload : steamFastload;
        bool skipSplashOn = isXbox ? xboxSkipSplash : steamSkipSplash;
        s["fastload"] = fastloadOn || skipSplashOn;
        s["ping"] = isXbox ? xboxPing : steamPing;
        s["bandwidth"] = isXbox ? xboxBandwidth : steamBandwidth;
        s["vsync"] = isXbox ? xboxVsync : steamVsync;
        s["ao"] = isXbox ? xboxAo : steamAo;
        s["blood"] = isXbox ? xboxBlood : steamBlood;
        s["dof"] = isXbox ? xboxDofSpecific : steamDofSpecific;
        s["lensflare"] = isXbox ? xboxLensFlare : steamLensFlare;
        s["extrablur"] = isXbox ? xboxExtraBlur : steamExtraBlur;
        s["vatsblur"] = isXbox ? xboxVatsBlur : steamVatsBlur;
        s["aniso"] = isXbox ? xboxAniso : steamAniso;
        s["water"] = isXbox ? xboxWater : steamWater;
        s["lod"] = isXbox ? xboxLod : steamLod;
        s["decals"] = isXbox ? xboxDecals : steamDecals;
        s["pipboyfx"] = isXbox ? xboxPipboyFx : steamPipboyFx;
        int currentFpsCap = isXbox ? xboxFpsCap : steamFpsCap;
        s["fpscap"] = currentFpsCap == 0 ? "Unlimited" : currentFpsCap.ToString(CultureInfo.InvariantCulture);

        s["volumquality"] = isXbox ? xboxVolumQuality : steamVolumQuality;
        s["shadowres"] = isXbox ? xboxShadowRes : steamShadowRes;
        s["shadowfilter"] = isXbox ? xboxShadowFilter : steamShadowFilter;
        s["texturequality"] = isXbox ? xboxTextureQuality : steamTextureQuality;
        s["decalsperframe"] = isXbox ? xboxDecalsPerFrame : steamDecalsPerFrame;
        s["gridload"] = isXbox ? xboxGridLoad : steamGridLoad;
        s["corpsehighlight"] = isXbox ? xboxCorpseHighlight : steamCorpseHighlight;
        s["focusshadows"] = isXbox ? xboxFocusShadows : steamFocusShadows;
        s["rendergrass"] = isXbox ? xboxRenderGrass : steamRenderGrass;
        s["ssr"] = isXbox ? xboxSsr : steamSsr;
        s["rainocclusion"] = isXbox ? xboxRainOcclusion : steamRainOcclusion;
        s["npcshadowlights"] = isXbox ? xboxNpcShadowLights : steamNpcShadowLights;
        s["cellloads"] = isXbox ? xboxCellLoads : steamCellLoads;
        s["tiledlighting"] = isXbox ? xboxTiledLighting : steamTiledLighting;
        s["glassshader"] = isXbox ? xboxGlassShader : steamGlassShader;
        s["pbrshadows"] = isXbox ? xboxPbrShadows : steamPbrShadows;
        s["playernames"] = isXbox ? xboxPlayerNames : steamPlayerNames;
        s["playerpings"] = isXbox ? xboxPlayerPings : steamPlayerPings;
        s["grassfade"] = isXbox ? xboxGrassFade : steamGrassFade;
        s["treedist"] = isXbox ? xboxTreeDist : steamTreeDist;
        s["lodsky"] = isXbox ? xboxLodSky : steamLodSky;
        s["leafanim"] = isXbox ? xboxLeafAnim : steamLeafAnim;
        s["conversationhistory"] = isXbox ? xboxConversationHistory : steamConversationHistory;
        s["gamma"] = isXbox ? xboxGamma : steamGamma;
        
        s["pipboyRed"] = isXbox ? xboxPipboyRed : steamPipboyRed;
        s["pipboyGreen"] = isXbox ? xboxPipboyGreen : steamPipboyGreen;
        s["pipboyBlue"] = isXbox ? xboxPipboyBlue : steamPipboyBlue;
        
        s["quickboyRed"] = isXbox ? xboxQuickboyRed : steamQuickboyRed;
        s["quickboyGreen"] = isXbox ? xboxQuickboyGreen : steamQuickboyGreen;
        s["quickboyBlue"] = isXbox ? xboxQuickboyBlue : steamQuickboyBlue;

        s["paRed"] = isXbox ? xboxPaRed : steamPaRed;
        s["paGreen"] = isXbox ? xboxPaGreen : steamPaGreen;
        s["paBlue"] = isXbox ? xboxPaBlue : steamPaBlue;

        s["hudRed"] = isXbox ? xboxHudRed : steamHudRed;
        s["hudGreen"] = isXbox ? xboxHudGreen : steamHudGreen;
        s["hudBlue"] = isXbox ? xboxHudBlue : steamHudBlue;

        try
        {
            s["modPresets"] = GetModPresetsObjectForWeb();
            s["activeModPreset"] = activeModPreset;
        }
        catch { }

        return s;
    }

    private static string NormalizeProfileModPath(string? raw) =>
        (raw ?? "").Replace('\\', '/').Trim();

    private static bool IsStringModFileName(string? fileName)
    {
        string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext is ".strings" or ".dlstrings" or ".ilstrings";
    }

    private static bool ProfileAllowsMod(HashSet<string> allowed, string originalName)
    {
        if (allowed.Count == 0) return false;
        if (allowed.Contains(originalName)) return true;

        var modNorm = NormalizeProfileModPath(originalName);
        if (string.IsNullOrEmpty(modNorm)) return false;

        var modFile = Path.GetFileName(modNorm);
        foreach (var entry in allowed)
        {
            var eNorm = NormalizeProfileModPath(entry);
            if (string.IsNullOrEmpty(eNorm)) continue;
            if (string.Equals(modNorm, eNorm, StringComparison.OrdinalIgnoreCase)) return true;

            var eFile = Path.GetFileName(eNorm);
            if (string.IsNullOrEmpty(modFile) || string.IsNullOrEmpty(eFile)) continue;
            if (!string.Equals(modFile, eFile, StringComparison.OrdinalIgnoreCase)) continue;

            bool modSpecial = modNorm.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase)
                || modNorm.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase)
                || modNorm.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase);
            bool eSpecial = eNorm.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase)
                || eNorm.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase)
                || eNorm.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase);
            bool modGameRoot = modNorm.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase)
                || modNorm.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase);
            bool eGameRoot = eNorm.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase)
                || eNorm.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase);
            if (modSpecial || eSpecial)
            {
                if (string.Equals(modNorm, eNorm, StringComparison.OrdinalIgnoreCase)) return true;
                if (IsStringModFileName(modFile) && string.Equals(modFile, eFile, StringComparison.OrdinalIgnoreCase))
                    return true;
                string modLoose = modNorm.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase)
                    ? modNorm.Substring("Loose/".Length) : modNorm;
                string eLoose = eNorm.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase)
                    ? eNorm.Substring("Loose/".Length) : eNorm;
                if ((modNorm.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase) ||
                     eNorm.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(modLoose, eLoose, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            if (modGameRoot || eGameRoot)
            {
                if (string.Equals(modNorm, eNorm, StringComparison.OrdinalIgnoreCase)) return true;
                if (modGameRoot && eGameRoot && string.Equals(modFile, eFile, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            return true;
        }

        return false;
    }

    private static string ModOriginalNameAfterToggle(string normalizedCurrentPath, bool enabledAfterToggle)
    {
        if (string.IsNullOrEmpty(normalizedCurrentPath)) return normalizedCurrentPath;
        if (normalizedCurrentPath.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase)
            || normalizedCurrentPath.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase))
            return normalizedCurrentPath;

        if (normalizedCurrentPath.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(normalizedCurrentPath);
            return enabledAfterToggle ? $"GameRoot/{fn}" : $"Disabled/GameRoot/{fn}";
        }

        if (normalizedCurrentPath.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(normalizedCurrentPath);
            return enabledAfterToggle ? $"GameRoot/{fn}" : normalizedCurrentPath;
        }

        bool inDisabled = normalizedCurrentPath.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase);
        string fileOnly = inDisabled ? normalizedCurrentPath.Substring("Disabled/".Length) : Path.GetFileName(normalizedCurrentPath);
        if (string.IsNullOrEmpty(fileOnly)) return normalizedCurrentPath;

        return enabledAfterToggle ? fileOnly : $"Disabled/{fileOnly}";
    }

    private void SyncProfileModsAfterToggle(string currentOriginalName, bool enabledAfterToggle)
    {
        var p = profiles.FirstOrDefault(x => x.Name == activeProfile);
        if (p == null) return;

        string oldKey = NormalizeProfileModPath(currentOriginalName);
        if (string.IsNullOrEmpty(oldKey)) return;

        string newKey = ModOriginalNameAfterToggle(oldKey, enabledAfterToggle);

        p.EnabledMods.RemoveAll(entry =>
        {
            var e = NormalizeProfileModPath(entry);
            if (string.Equals(e, oldKey, StringComparison.OrdinalIgnoreCase)) return true;
            return ProfileAllowsMod(new HashSet<string> { entry }, oldKey);
        });

        if (enabledAfterToggle &&
            !p.EnabledMods.Any(e =>
                string.Equals(NormalizeProfileModPath(e), newKey, StringComparison.OrdinalIgnoreCase) ||
                ProfileAllowsMod(new HashSet<string> { e }, newKey)))
        {
            p.EnabledMods.Add(newKey);
        }

        if (activeProfile != "Default Profile" &&
            !string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase))
        {
            p.ProfileMods.RemoveAll(entry =>
            {
                var e = NormalizeProfileModPath(entry);
                if (string.Equals(e, oldKey, StringComparison.OrdinalIgnoreCase)) return true;
                return ProfileAllowsMod(new HashSet<string> { entry }, oldKey);
            });

            if (!ProfileAllowsMod(new HashSet<string>(p.ProfileMods, StringComparer.OrdinalIgnoreCase), newKey))
                p.ProfileMods.Add(newKey);
        }

        SaveProfiles();
    }

    private void ReplaceModInActiveProfile(string oldOriginalName, IEnumerable<string> newKeys)
    {
        if (activeProfile == "Default Profile") return;
        var p = profiles.FirstOrDefault(x => x.Name == activeProfile);
        if (p == null) return;
        if (ReplaceModKeysInProfile(p, oldOriginalName, newKeys))
            SaveProfiles();
    }

    private void ReplaceModInAllProfiles(string oldOriginalName, IEnumerable<string> newKeys)
    {
        bool any = false;
        foreach (var p in profiles)
        {
            if (p == null) continue;
            if (string.Equals(p.Name, "Default Profile", StringComparison.OrdinalIgnoreCase)) continue;
            if (ReplaceModKeysInProfile(p, oldOriginalName, newKeys))
                any = true;
        }
        if (any) SaveProfiles();
    }

    private bool ReplaceModKeysInProfile(Profile p, string oldOriginalName, IEnumerable<string> newKeys)
    {
        if (p == null) return false;

        string oldNorm = NormalizeProfileModPath(oldOriginalName);
        if (string.IsNullOrEmpty(oldNorm)) return false;

        p.ProfileMods ??= new List<string>();
        p.EnabledMods ??= new List<string>();

        bool hadOld = p.ProfileMods.Any(entry =>
            string.Equals(NormalizeProfileModPath(entry), oldNorm, StringComparison.OrdinalIgnoreCase) ||
            ProfileAllowsMod(new HashSet<string> { entry }, oldNorm));

        bool wasEnabled = p.EnabledMods.Any(e =>
            string.Equals(NormalizeProfileModPath(e), oldNorm, StringComparison.OrdinalIgnoreCase) ||
            ProfileAllowsMod(new HashSet<string> { e }, oldNorm));

        int removedMods = p.ProfileMods.RemoveAll(entry =>
        {
            var e = NormalizeProfileModPath(entry);
            return string.Equals(e, oldNorm, StringComparison.OrdinalIgnoreCase) ||
                   ProfileAllowsMod(new HashSet<string> { entry }, oldNorm);
        });
        int removedEnabled = p.EnabledMods.RemoveAll(entry =>
        {
            var e = NormalizeProfileModPath(entry);
            return string.Equals(e, oldNorm, StringComparison.OrdinalIgnoreCase) ||
                   ProfileAllowsMod(new HashSet<string> { entry }, oldNorm);
        });

        bool changed = removedMods > 0 || removedEnabled > 0;
        if (!hadOld && !wasEnabled)
            return changed;

        var allowed = new HashSet<string>(p.ProfileMods, StringComparer.OrdinalIgnoreCase);
        foreach (var rawKey in newKeys ?? Array.Empty<string>())
        {
            string key = NormalizeProfileModPath(rawKey);
            if (string.IsNullOrEmpty(key)) continue;
            if (!ProfileAllowsMod(allowed, key))
            {
                p.ProfileMods.Add(key);
                allowed.Add(key);
                changed = true;
            }
            if (wasEnabled && !p.EnabledMods.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                p.EnabledMods.Add(key);
                changed = true;
            }
        }

        return changed;
    }

    private List<object> SafeGetRealMods()
    {
        var allMods = _modManager.GetModsList();
        var enriched = allMods.Select(EnrichModWithUpdateCache).ToList();
        
        if (activeProfile != "Default Profile")
        {
            var p = profiles.FirstOrDefault(x => x.Name == activeProfile);
            if (p != null)
            {
                var allowed = new HashSet<string>(p.ProfileMods, StringComparer.OrdinalIgnoreCase);
                return enriched.Where(m =>
                {
                    string key = (string)((dynamic)m).originalName;
                    return key.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase) || ProfileAllowsMod(allowed, key);
                }).ToList();
            }
        }

        return enriched;
    }

    private object EnrichModWithUpdateCache(object mod)
    {
        try
        {
            dynamic d = mod;
            string key = d.originalName;
            if (string.IsNullOrEmpty(key) || !_modUpdateCache.TryGetValue(key, out var cache))
                return mod;

            return new
            {
                originalName = (string)d.originalName,
                name = (string)d.name,
                author = (string)d.author,
                version = (string)d.version,
                details = (string)d.details,
                status = (string)d.status,
                type = (string)d.type,
                isBundle = (bool)d.isBundle,
                loadOrder = (int)d.loadOrder,
                url = (string)(d.url ?? ""),
                nexusModId = d.nexusModId,
                nexusFileId = d.nexusFileId,
                nexusFileVersion = (string)(d.nexusFileVersion ?? ""),
                nexusFileUploaded = d.nexusFileUploaded,
                hasUpdate = cache.HasUpdate,
                isUnverifiedLink = cache.IsUnverifiedLink,
                latestFileId = cache.LatestFileId,
                latestVersion = cache.LatestVersion,
                latestFileName = cache.LatestFileName,
                latestUploaded = cache.LatestUploaded,
                files = d.files
            };
        }
        catch
        {
            return mod;
        }
    }

    private object SafeGetRealLogs()
    {
        try {
            var errors = ReadLastLines(logErrorPath, 100);
            return new {
                activity = ReadLastLines(logActivityPath, 100),
                errors = errors,
                errorCount = errors.Count < 100 ? errors.Count : CountLinesInFile(logErrorPath)
            };
        } catch (Exception ex) {
            LogError($"[DATA] Failed to collect logs payload: {ex.Message}");
            return new { activity = new List<string>(), errors = new List<string>(), errorCount = 0 };
        }
    }

    private static int CountLinesInFile(string path)
    {
        try {
            if (!File.Exists(path)) return 0;
            var n = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs)) {
                while (sr.ReadLine() != null) n++;
            }
            return n;
        } catch {
            return 0;
        }
    }

    private static List<string> ReadLastLines(string path, int count)
    {
        try {
            if (!File.Exists(path) || count <= 0) return new List<string>();

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return new List<string>();

            const int maxTailBytes = 512 * 1024;
            int bytesToRead = (int)Math.Min(fs.Length, maxTailBytes);
            fs.Seek(-bytesToRead, SeekOrigin.End);

            var buffer = new byte[bytesToRead];
            int read = fs.Read(buffer, 0, bytesToRead);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

            if (bytesToRead < fs.Length)
            {
                int firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0)
                    text = text.Substring(firstNewline + 1);
            }

            var lines = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (!string.IsNullOrEmpty(line))
                    lines.Add(line);
            }

            if (lines.Count <= count) return lines;
            return lines.Skip(lines.Count - count).ToList();
        } catch {
            return new List<string>();
        }
    }

    private List<string> GetProfileList() => profiles.Select(p => p.Name).ToList();

    private async Task LoadSettingsAsync()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            string json = await Task.Run(() => File.ReadAllText(settingsPath)).ConfigureAwait(true);
            ApplySettingsFromJson(json);
        }
        catch (Exception ex) { LogError($"LoadSettings Error: {ex.Message}"); }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            ApplySettingsFromJson(File.ReadAllText(settingsPath));
        }
        catch (Exception ex) { LogError($"LoadSettings Error: {ex.Message}"); }
    }

    private void ApplySettingsFromJson(string json)
    {
        try {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("gamePath", out var gp)) gamePath = gp.GetString() ?? gamePath;
                if (root.TryGetProperty("documentsPath", out var dcp)) documentsPath = dcp.GetString() ?? documentsPath;
                if (root.TryGetProperty("localAppDataPath", out var lap)) localAppDataPath = lap.GetString() ?? localAppDataPath;
                if (root.TryGetProperty("stringsPath", out var sp)) stringsPath = sp.GetString() ?? stringsPath;
                
                if (root.TryGetProperty("steamGamePath", out var sgp)) steamGamePath = sgp.GetString() ?? steamGamePath;
                if (root.TryGetProperty("steamDocsPath", out var sdp)) steamDocsPath = sdp.GetString() ?? steamDocsPath;
                if (root.TryGetProperty("steamLocalPath", out var slp)) steamLocalPath = slp.GetString() ?? steamLocalPath;
                if (root.TryGetProperty("steamStringsPath", out var ssp)) steamStringsPath = ssp.GetString() ?? steamStringsPath;

                if (root.TryGetProperty("xboxGamePath", out var xgp)) xboxGamePath = xgp.GetString() ?? xboxGamePath;
                if (root.TryGetProperty("xboxDocsPath", out var xdp)) xboxDocsPath = xdp.GetString() ?? xboxDocsPath;
                if (root.TryGetProperty("xboxLocalPath", out var xlp)) xboxLocalPath = xlp.GetString() ?? xboxLocalPath;
                if (root.TryGetProperty("xboxStringsPath", out var xsp)) xboxStringsPath = xsp.GetString() ?? xboxStringsPath;
                
                if (root.TryGetProperty("minimizeToTray", out var mt)) minimizeToTray = mt.GetBoolean();
                if (root.TryGetProperty("uiAnimations", out var ua)) uiAnimations = ua.GetBoolean();
                if (root.TryGetProperty("hideKofi", out var hk)) hideKofi = hk.GetBoolean();
                if (root.TryGetProperty("logsPopoutReopenOnLaunch", out var lpr)) logsPopoutReopenOnLaunch = lpr.GetBoolean();
                if (root.TryGetProperty("logsPopoutKeepOpen", out var lpk)) logsPopoutKeepOpen = lpk.GetBoolean();
                if (root.TryGetProperty("logsPopoutOpen", out var lpo)) logsPopoutOpen = lpo.GetBoolean();
                if (root.TryGetProperty("logsPopoutBounds", out var lpb) && lpb.ValueKind == JsonValueKind.Object)
                {
                    int x = lpb.TryGetProperty("x", out var bx) && bx.TryGetInt32(out var xi) ? xi : 0;
                    int y = lpb.TryGetProperty("y", out var by) && by.TryGetInt32(out var yi) ? yi : 0;
                    int w = lpb.TryGetProperty("w", out var bw) && bw.TryGetInt32(out var wi) ? wi : 0;
                    int h = lpb.TryGetProperty("h", out var bh) && bh.TryGetInt32(out var hi) ? hi : 0;
                    bool max = lpb.TryGetProperty("maximized", out var bm) && bm.ValueKind == JsonValueKind.True;
                    if (w >= 200 && h >= 150)
                    {
                        logsPopoutX = x; logsPopoutY = y; logsPopoutW = w; logsPopoutH = h;
                        logsPopoutMaximized = max;
                        logsPopoutBoundsValid = true;
                    }
                }
                if (root.TryGetProperty("platformBadgeGlow", out var pbg)) platformBadgeGlow = pbg.GetBoolean();
                if (root.TryGetProperty("syncPlatforms", out var spr)) syncPlatforms = spr.GetBoolean();
                if (root.TryGetProperty("autoForceDeploy", out var afd)) autoForceDeploy = afd.GetBoolean();
                if (root.TryGetProperty("virtualModMode", out var vmm)) virtualModMode = vmm.GetBoolean();
                else if (root.TryGetProperty("managedVanillaMode", out var mvm)) virtualModMode = mvm.GetBoolean();
                if (root.TryGetProperty("configEditorSpellCheck", out var cesc)) configEditorSpellCheck = cesc.GetBoolean();
                if (root.TryGetProperty("confirmBeforeDeleteMod", out var cbddm)) confirmBeforeDeleteMod = cbddm.GetBoolean();
                if (root.TryGetProperty("confirmBeforeRemoveOldModOnUpdate", out var cbroou)) confirmBeforeRemoveOldModOnUpdate = cbroou.GetBoolean();
                if (root.TryGetProperty("updateModsInAllPresets", out var umiap)) updateModsInAllPresets = umiap.GetBoolean();
                if (root.TryGetProperty("language", out var lang)) applicationLanguage = lang.GetString() ?? applicationLanguage;
                else {
                    applicationLanguage = CultureInfo.CurrentUICulture.Name;
                }
                if (root.TryGetProperty("uiTheme", out var ut)) uiTheme = ut.GetString() ?? uiTheme;
                if (_themesReady || ThemeIds.IsBuiltIn(uiTheme))
                    uiTheme = NormalizeUiTheme(uiTheme);
                else if (!Managers.ThemePackageValidator.IsValidThemeId(uiTheme))
                    uiTheme = ThemeIds.Default;

                if (root.TryGetProperty("archiveKeyName", out var akn)) archiveKeyName = akn.GetString() ?? archiveKeyName;
                if (root.TryGetProperty("keybinds", out var kb) && kb.ValueKind == JsonValueKind.Object)
                    keybindsJson = kb.GetRawText();
                if (root.TryGetProperty("sevenZipPath", out var szp)) sevenZipPath = szp.GetString() ?? sevenZipPath;
                if (root.TryGetProperty("rarExtractorPath", out var rep)) rarExtractorPath = rep.GetString() ?? rarExtractorPath;

                if (root.TryGetProperty("modPresets", out var mp))
                    modPresetsJson = mp.GetRawText();
                if (root.TryGetProperty("activeModPreset", out var amp))
                    activeModPreset = amp.GetString() ?? activeModPreset;
                if (root.TryGetProperty("activeProfile", out var apProf))
                {
                    string? savedProfile = apProf.GetString();
                    if (!string.IsNullOrWhiteSpace(savedProfile))
                        activeProfile = savedProfile;
                }
                if (root.TryGetProperty("modGroups", out var mg))
                    modGroups = mg.GetRawText();
                EnsureModPresetsInitialized();
                if (root.TryGetProperty("activeTweaksPreset", out var atp)) activeTweaksPreset = atp.GetString() ?? activeTweaksPreset;
                if (root.TryGetProperty("pipboyCrtDefaultMigrationV1", out var pcdm)) pipboyCrtDefaultMigrationV1 = pcdm.GetBoolean();
                if (root.TryGetProperty("pipboyCrtOnDefaultV2", out var pcdv2)) pipboyCrtOnDefaultV2 = pcdv2.GetBoolean();
                if (root.TryGetProperty("pipboyCrtUserConfigured", out var pcuc)) pipboyCrtUserConfigured = pcuc.GetBoolean();
                if (root.TryGetProperty("pipboyPrefsIniScrubV1", out var pps)) pipboyPrefsIniScrubV1 = pps.GetBoolean();
                if (root.TryGetProperty("gameIntegrityRepairV1", out var gir)) gameIntegrityRepairV1 = gir.GetBoolean();
                if (root.TryGetProperty("looseConfigDeployRepairV1", out var lcdr)) looseConfigDeployRepairV1 = lcdr.GetBoolean();

                bool purgeLegacyNexusFields = false;
                if (root.TryGetProperty("nexusApiKey", out var legacyPlain))
                {
                    string? plain = legacyPlain.GetString();
                    if (!string.IsNullOrWhiteSpace(plain))
                        NexusCredentialStore.Save(plain);
                    purgeLegacyNexusFields = true;
                }
                if (root.TryGetProperty("nexusApiKeyProtected", out var legacyProt))
                {
                    if (NexusCredentialStore.TryImportProtectedBase64(legacyProt.GetString()))
                        purgeLegacyNexusFields = true;
                    else
                        purgeLegacyNexusFields = true;
                }
                if (root.TryGetProperty("nexusAuthCredentialProtected", out var legacyAuth))
                {
                    if (NexusCredentialStore.TryImportProtectedBase64(legacyAuth.GetString()))
                        purgeLegacyNexusFields = true;
                    else
                        purgeLegacyNexusFields = true;
                }
                if (purgeLegacyNexusFields)
                {
                    LogActivity("[NEXUS] Migrated legacy credential fields out of settings.json.");
                    SaveSettings();
                }

                if (root.TryGetProperty("importedCollection", out var ic) && ic.ValueKind == JsonValueKind.Object)
                {
                    _importedCollection = new ImportedCollectionRecord();
                    if (ic.TryGetProperty("slug", out var icSlug)) _importedCollection.Slug = icSlug.GetString() ?? "";
                    if (ic.TryGetProperty("name", out var icName)) _importedCollection.Name = icName.GetString() ?? "";
                    if (ic.TryGetProperty("revision", out var icRev) && icRev.ValueKind == JsonValueKind.Number)
                        _importedCollection.Revision = icRev.GetInt32();
                    if (ic.TryGetProperty("mods", out var icMods) && icMods.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var modElem in icMods.EnumerateArray())
                        {
                            var entry = new ImportedCollectionModEntry();
                            if (modElem.TryGetProperty("modId", out var mid) && mid.ValueKind == JsonValueKind.Number)
                                entry.ModId = mid.GetInt64();
                            if (modElem.TryGetProperty("fileId", out var fid) && fid.ValueKind == JsonValueKind.Number)
                                entry.FileId = fid.GetInt64();
                            if (modElem.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String)
                                entry.FileName = fn.GetString() ?? "";
                            if (modElem.TryGetProperty("fileVersion", out var fv) && fv.ValueKind == JsonValueKind.String)
                                entry.FileVersion = fv.GetString() ?? "";
                            if (entry.ModId > 0 && entry.FileId > 0)
                                _importedCollection.Mods.Add(entry);
                        }
                    }
                }

                if (root.TryGetProperty("windowWidth", out var ww)) windowWidth = ww.GetInt32();
                if (root.TryGetProperty("windowHeight", out var wh)) windowHeight = wh.GetInt32();
                if (root.TryGetProperty("windowTop", out var wt)) windowTop = wt.GetInt32();
                if (root.TryGetProperty("windowLeft", out var wl)) windowLeft = wl.GetInt32();
                if (root.TryGetProperty("windowMaximized", out var wm)) windowMaximized = wm.GetBoolean();

                if (root.TryGetProperty("steamGodrays", out var sgr)) steamGodrays = sgr.GetBoolean();
                if (root.TryGetProperty("steamGrass", out var sgs)) steamGrass = sgs.GetBoolean();
                if (root.TryGetProperty("steamDof", out var sdf)) steamDof = sdf.GetBoolean();
                if (root.TryGetProperty("steamPing", out var spg)) steamPing = spg.GetBoolean();
                if (root.TryGetProperty("steamBandwidth", out var sbw)) steamBandwidth = sbw.GetBoolean();
                if (root.TryGetProperty("steamFastload", out var sfl)) steamFastload = sfl.GetBoolean();
                if (root.TryGetProperty("steamVsync", out var svs)) steamVsync = svs.GetBoolean();
                if (root.TryGetProperty("steamAo", out var sao)) steamAo = sao.GetBoolean();
                if (root.TryGetProperty("steamBlood", out var sbl)) steamBlood = sbl.GetBoolean();
                if (root.TryGetProperty("steamDofSpecific", out var sds)) steamDofSpecific = sds.GetBoolean();
                if (root.TryGetProperty("steamLensFlare", out var slf)) steamLensFlare = slf.GetBoolean();
                if (root.TryGetProperty("steamExtraBlur", out var seb)) steamExtraBlur = seb.GetBoolean();
                if (root.TryGetProperty("steamVatsBlur", out var svb)) steamVatsBlur = svb.GetBoolean();
                if (root.TryGetProperty("steamShadows", out var ssh)) steamShadows = ssh.GetString() ?? "Medium";
                if (root.TryGetProperty("steamTaa", out var sta)) steamTaa = sta.GetString() ?? "TAA";
                if (root.TryGetProperty("steamFov", out var sfv)) steamFov = sfv.GetInt32();
                if (root.TryGetProperty("steamFov1st", out var sf1)) steamFov1st = sf1.GetInt32();
                else steamFov1st = steamFov;
                if (root.TryGetProperty("steamFovPipboy", out var sfpb)) steamFovPipboy = sfpb.GetInt32();
                else steamFovPipboy = steamFov;
                if (root.TryGetProperty("steamFpsCap", out var sfc)) steamFpsCap = sfc.GetInt32();
                if (root.TryGetProperty("steamAniso", out var sani)) steamAniso = sani.GetString() ?? "16x";
                if (root.TryGetProperty("steamWater", out var swat)) steamWater = swat.GetString() ?? "High";
                if (root.TryGetProperty("steamLod", out var slod)) steamLod = slod.GetInt32();
                if (root.TryGetProperty("steamDecals", out var sdec)) steamDecals = sdec.GetString() ?? "High";
                if (root.TryGetProperty("steamPipboyFx", out var spbfx)) steamPipboyFx = spbfx.GetBoolean();
                if (root.TryGetProperty("steamVolumQuality", out var svq)) steamVolumQuality = svq.GetString() ?? steamVolumQuality;
                if (root.TryGetProperty("steamShadowRes", out var ssr2)) steamShadowRes = ssr2.GetString() ?? steamShadowRes;
                if (root.TryGetProperty("steamShadowFilter", out var ssf)) steamShadowFilter = ssf.GetString() ?? steamShadowFilter;
                if (root.TryGetProperty("steamTextureQuality", out var stq)) steamTextureQuality = stq.GetString() ?? steamTextureQuality;
                if (root.TryGetProperty("steamDecalsPerFrame", out var sdpf)) steamDecalsPerFrame = sdpf.GetString() ?? steamDecalsPerFrame;
                if (root.TryGetProperty("steamGridLoad", out var sgl)) steamGridLoad = sgl.GetString() ?? steamGridLoad;
                if (root.TryGetProperty("steamCorpseHighlight", out var sch)) steamCorpseHighlight = sch.GetString() ?? steamCorpseHighlight;
                if (root.TryGetProperty("steamFocusShadows", out var sfs)) steamFocusShadows = sfs.GetBoolean();
                if (root.TryGetProperty("steamRenderGrass", out var srg)) steamRenderGrass = srg.GetBoolean();
                if (root.TryGetProperty("steamSsr", out var sssr)) steamSsr = sssr.GetBoolean();
                if (root.TryGetProperty("steamRainOcclusion", out var sro)) steamRainOcclusion = sro.GetBoolean();
                if (root.TryGetProperty("steamNpcShadowLights", out var snpl)) steamNpcShadowLights = snpl.GetBoolean();
                if (root.TryGetProperty("steamCellLoads", out var scl)) steamCellLoads = scl.GetBoolean();
                if (root.TryGetProperty("steamTiledLighting", out var stl)) steamTiledLighting = stl.GetBoolean();
                if (root.TryGetProperty("steamSkipSplash", out var sss)) steamSkipSplash = sss.GetBoolean();
                if (root.TryGetProperty("steamGlassShader", out var sgs2)) steamGlassShader = sgs2.GetBoolean();
                if (root.TryGetProperty("steamPbrShadows", out var spbr)) steamPbrShadows = spbr.GetBoolean();
                if (root.TryGetProperty("steamPlayerNames", out var spn)) steamPlayerNames = spn.GetBoolean();
                if (root.TryGetProperty("steamPlayerPings", out var spp)) steamPlayerPings = spp.GetBoolean();
                if (root.TryGetProperty("steamGrassFade", out var sgf)) steamGrassFade = sgf.GetInt32();
                if (root.TryGetProperty("steamTreeDist", out var std)) steamTreeDist = std.GetInt32();
                if (root.TryGetProperty("steamLodSky", out var sls)) steamLodSky = sls.GetInt32();
                if (root.TryGetProperty("steamLeafAnim", out var sla)) steamLeafAnim = sla.GetInt32();
                if (root.TryGetProperty("steamConversationHistory", out var sch2)) steamConversationHistory = sch2.GetInt32();
                if (root.TryGetProperty("steamGamma", out var sgm)) steamGamma = sgm.GetDouble();

                if (root.TryGetProperty("steamPipboyRed", out var sP_R)) steamPipboyRed = sP_R.GetInt32();
                if (root.TryGetProperty("steamPipboyGreen", out var sP_G)) steamPipboyGreen = sP_G.GetInt32();
                if (root.TryGetProperty("steamPipboyBlue", out var sP_B)) steamPipboyBlue = sP_B.GetInt32();
                if (root.TryGetProperty("steamQuickboyRed", out var sQ_R)) steamQuickboyRed = sQ_R.GetInt32();
                if (root.TryGetProperty("steamQuickboyGreen", out var sQ_G)) steamQuickboyGreen = sQ_G.GetInt32();
                if (root.TryGetProperty("steamQuickboyBlue", out var sQ_B)) steamQuickboyBlue = sQ_B.GetInt32();
                if (root.TryGetProperty("steamPaRed", out var sPA_R)) steamPaRed = sPA_R.GetInt32();
                if (root.TryGetProperty("steamPaGreen", out var sPA_G)) steamPaGreen = sPA_G.GetInt32();
                if (root.TryGetProperty("steamPaBlue", out var sPA_B)) steamPaBlue = sPA_B.GetInt32();
                if (root.TryGetProperty("steamHudRed", out var sH_R)) steamHudRed = sH_R.GetInt32();
                if (root.TryGetProperty("steamHudGreen", out var sH_G)) steamHudGreen = sH_G.GetInt32();
                if (root.TryGetProperty("steamHudBlue", out var sH_B)) steamHudBlue = sH_B.GetInt32();
                
                if (root.TryGetProperty("xboxGodrays", out var xgr)) xboxGodrays = xgr.GetBoolean();
                if (root.TryGetProperty("xboxGrass", out var xgs)) xboxGrass = xgs.GetBoolean();
                if (root.TryGetProperty("xboxDof", out var xdf)) xboxDof = xdf.GetBoolean();
                if (root.TryGetProperty("xboxPing", out var xpg)) xboxPing = xpg.GetBoolean();
                if (root.TryGetProperty("xboxBandwidth", out var xbw)) xboxBandwidth = xbw.GetBoolean();
                if (root.TryGetProperty("xboxFastload", out var xfl)) xboxFastload = xfl.GetBoolean();
                if (root.TryGetProperty("xboxVsync", out var xvs)) xboxVsync = xvs.GetBoolean();
                if (root.TryGetProperty("xboxAo", out var xao)) xboxAo = xao.GetBoolean();
                if (root.TryGetProperty("xboxBlood", out var xbl)) xboxBlood = xbl.GetBoolean();
                if (root.TryGetProperty("xboxDofSpecific", out var xds)) xboxDofSpecific = xds.GetBoolean();
                if (root.TryGetProperty("xboxLensFlare", out var xlf)) xboxLensFlare = xlf.GetBoolean();
                if (root.TryGetProperty("xboxExtraBlur", out var xeb)) xboxExtraBlur = xeb.GetBoolean();
                if (root.TryGetProperty("xboxVatsBlur", out var xvb)) xboxVatsBlur = xvb.GetBoolean();
                if (root.TryGetProperty("xboxShadows", out var xsh)) xboxShadows = xsh.GetString() ?? "Medium";
                if (root.TryGetProperty("xboxTaa", out var xta)) xboxTaa = xta.GetString() ?? "TAA";
                if (root.TryGetProperty("xboxFov", out var xfv)) xboxFov = xfv.GetInt32();
                if (root.TryGetProperty("xboxFov1st", out var xf1)) xboxFov1st = xf1.GetInt32();
                else xboxFov1st = xboxFov;
                if (root.TryGetProperty("xboxFovPipboy", out var xfpb)) xboxFovPipboy = xfpb.GetInt32();
                else xboxFovPipboy = xboxFov;
                if (root.TryGetProperty("xboxFpsCap", out var xfc)) xboxFpsCap = xfc.GetInt32();
                if (root.TryGetProperty("xboxAniso", out var xani)) xboxAniso = xani.GetString() ?? "16x";
                if (root.TryGetProperty("xboxWater", out var xwat)) xboxWater = xwat.GetString() ?? "High";
                if (root.TryGetProperty("xboxLod", out var xlod)) xboxLod = xlod.GetInt32();
                if (root.TryGetProperty("xboxDecals", out var xdec)) xboxDecals = xdec.GetString() ?? "High";
                if (root.TryGetProperty("xboxPipboyFx", out var xpbfx)) xboxPipboyFx = xpbfx.GetBoolean();
                if (root.TryGetProperty("xboxVolumQuality", out var xvq)) xboxVolumQuality = xvq.GetString() ?? xboxVolumQuality;
                if (root.TryGetProperty("xboxShadowRes", out var xsr2)) xboxShadowRes = xsr2.GetString() ?? xboxShadowRes;
                if (root.TryGetProperty("xboxShadowFilter", out var xsf)) xboxShadowFilter = xsf.GetString() ?? xboxShadowFilter;
                if (root.TryGetProperty("xboxTextureQuality", out var xtq)) xboxTextureQuality = xtq.GetString() ?? xboxTextureQuality;
                if (root.TryGetProperty("xboxDecalsPerFrame", out var xdpf)) xboxDecalsPerFrame = xdpf.GetString() ?? xboxDecalsPerFrame;
                if (root.TryGetProperty("xboxGridLoad", out var xgl)) xboxGridLoad = xgl.GetString() ?? xboxGridLoad;
                if (root.TryGetProperty("xboxCorpseHighlight", out var xch)) xboxCorpseHighlight = xch.GetString() ?? xboxCorpseHighlight;
                if (root.TryGetProperty("xboxFocusShadows", out var xfs)) xboxFocusShadows = xfs.GetBoolean();
                if (root.TryGetProperty("xboxRenderGrass", out var xrg)) xboxRenderGrass = xrg.GetBoolean();
                if (root.TryGetProperty("xboxSsr", out var xssr)) xboxSsr = xssr.GetBoolean();
                if (root.TryGetProperty("xboxRainOcclusion", out var xro)) xboxRainOcclusion = xro.GetBoolean();
                if (root.TryGetProperty("xboxNpcShadowLights", out var xnpl)) xboxNpcShadowLights = xnpl.GetBoolean();
                if (root.TryGetProperty("xboxCellLoads", out var xcl)) xboxCellLoads = xcl.GetBoolean();
                if (root.TryGetProperty("xboxTiledLighting", out var xtl)) xboxTiledLighting = xtl.GetBoolean();
                if (root.TryGetProperty("xboxSkipSplash", out var xss)) xboxSkipSplash = xss.GetBoolean();
                if (root.TryGetProperty("xboxGlassShader", out var xgs2)) xboxGlassShader = xgs2.GetBoolean();
                if (root.TryGetProperty("xboxPbrShadows", out var xpbr)) xboxPbrShadows = xpbr.GetBoolean();
                if (root.TryGetProperty("xboxPlayerNames", out var xpn)) xboxPlayerNames = xpn.GetBoolean();
                if (root.TryGetProperty("xboxPlayerPings", out var xpp)) xboxPlayerPings = xpp.GetBoolean();
                if (root.TryGetProperty("xboxGrassFade", out var xgf)) xboxGrassFade = xgf.GetInt32();
                if (root.TryGetProperty("xboxTreeDist", out var xtd)) xboxTreeDist = xtd.GetInt32();
                if (root.TryGetProperty("xboxLodSky", out var xls)) xboxLodSky = xls.GetInt32();
                if (root.TryGetProperty("xboxLeafAnim", out var xla)) xboxLeafAnim = xla.GetInt32();
                if (root.TryGetProperty("xboxConversationHistory", out var xch2)) xboxConversationHistory = xch2.GetInt32();
                if (root.TryGetProperty("xboxGamma", out var xgm)) xboxGamma = xgm.GetDouble();
                if (root.TryGetProperty("xboxPipboyRed", out var xP_R)) xboxPipboyRed = xP_R.GetInt32();
                if (root.TryGetProperty("xboxPipboyGreen", out var xP_G)) xboxPipboyGreen = xP_G.GetInt32();
                if (root.TryGetProperty("xboxPipboyBlue", out var xP_B)) xboxPipboyBlue = xP_B.GetInt32();
                if (root.TryGetProperty("xboxQuickboyRed", out var xQ_R)) xboxQuickboyRed = xQ_R.GetInt32();
                if (root.TryGetProperty("xboxQuickboyGreen", out var xQ_G)) xboxQuickboyGreen = xQ_G.GetInt32();
                if (root.TryGetProperty("xboxQuickboyBlue", out var xQ_B)) xboxQuickboyBlue = xQ_B.GetInt32();
                if (root.TryGetProperty("xboxPaRed", out var xPA_R)) xboxPaRed = xPA_R.GetInt32();
                if (root.TryGetProperty("xboxPaGreen", out var xPA_G)) xboxPaGreen = xPA_G.GetInt32();
                if (root.TryGetProperty("xboxPaBlue", out var xPA_B)) xboxPaBlue = xPA_B.GetInt32();
                if (root.TryGetProperty("xboxHudRed", out var xH_R)) xboxHudRed = xH_R.GetInt32();
                if (root.TryGetProperty("xboxHudGreen", out var xH_G)) xboxHudGreen = xH_G.GetInt32();
                if (root.TryGetProperty("xboxHudBlue", out var xH_B)) xboxHudBlue = xH_B.GetInt32();
                
                if (root.TryGetProperty("lastPlatform", out var lp)) {
                    int platformId = lp.GetInt32();
                    if (platformId == 0) _platformManager.SetPlatform(GamePlatform.Steam);
                    else if (platformId == 1) _platformManager.SetPlatform(GamePlatform.Xbox);
                } else {
                    _platformManager.SetPlatform(
                        PlatformManager.DetectPlatformFromGamePath(gamePath, GamePlatform.Steam));
                }
            
            if (string.IsNullOrEmpty(steamGamePath) && !string.IsNullOrEmpty(gamePath) && 
                _platformManager.CurrentPlatform == GamePlatform.Steam && 
                !gamePath.Contains("XboxGames", StringComparison.OrdinalIgnoreCase)) 
            {
                steamGamePath = gamePath;
            }
            
            if (string.IsNullOrEmpty(xboxGamePath) && !string.IsNullOrEmpty(gamePath) && 
                (_platformManager.CurrentPlatform == GamePlatform.Xbox || gamePath.Contains("XboxGames", StringComparison.OrdinalIgnoreCase) || gamePath.Contains("WindowsApps")))
            {
                xboxGamePath = gamePath;
            }

            if (string.IsNullOrEmpty(steamDocsPath) && !string.IsNullOrEmpty(documentsPath)) steamDocsPath = documentsPath;
            if (string.IsNullOrEmpty(xboxDocsPath) && !string.IsNullOrEmpty(documentsPath)) xboxDocsPath = documentsPath;

            if (string.IsNullOrEmpty(steamLocalPath) && !string.IsNullOrEmpty(localAppDataPath)) steamLocalPath = localAppDataPath;
            if (string.IsNullOrEmpty(xboxLocalPath) && !string.IsNullOrEmpty(localAppDataPath)) xboxLocalPath = localAppDataPath;

            if (string.IsNullOrEmpty(steamStringsPath) && !string.IsNullOrEmpty(stringsPath)) steamStringsPath = stringsPath;
            if (string.IsNullOrEmpty(xboxStringsPath) && !string.IsNullOrEmpty(stringsPath)) xboxStringsPath = stringsPath;

            if (_platformManager.CurrentPlatform == GamePlatform.Steam && !string.IsNullOrEmpty(steamStringsPath))
                stringsPath = steamStringsPath;
            else if (_platformManager.CurrentPlatform == GamePlatform.Xbox && !string.IsNullOrEmpty(xboxStringsPath))
                stringsPath = xboxStringsPath;

            if (_platformManager.CurrentPlatform == GamePlatform.Steam && string.IsNullOrEmpty(gamePath)) 
                 gamePath = steamGamePath;
            else if (_platformManager.CurrentPlatform == GamePlatform.Xbox && string.IsNullOrEmpty(gamePath))
                 gamePath = xboxGamePath;

            _settingsApplied = true;
        } catch (Exception ex) { LogError($"ApplySettingsFromJson Error: {ex.Message}"); }
    }

    private void SaveSettings()
    {
        try {
            var s = new Dictionary<string, object>();
            s["gamePath"] = gamePath;
            s["documentsPath"] = documentsPath;
            s["localAppDataPath"] = localAppDataPath;
            s["stringsPath"] = stringsPath;
            
            s["steamGamePath"] = steamGamePath; s["steamDocsPath"] = steamDocsPath; s["steamLocalPath"] = steamLocalPath; s["steamStringsPath"] = steamStringsPath;
            s["xboxGamePath"] = xboxGamePath; s["xboxDocsPath"] = xboxDocsPath; s["xboxLocalPath"] = xboxLocalPath; s["xboxStringsPath"] = xboxStringsPath;
            s["lastPlatform"] = (int)_platformManager.CurrentPlatform;
            s["minimizeToTray"] = minimizeToTray;
            s["uiAnimations"] = uiAnimations;
            s["hideKofi"] = hideKofi;
            s["logsPopoutReopenOnLaunch"] = logsPopoutReopenOnLaunch;
            s["logsPopoutKeepOpen"] = logsPopoutKeepOpen;
            s["logsPopoutOpen"] = logsPopoutOpen;
            if (logsPopoutBoundsValid)
            {
                s["logsPopoutBounds"] = new Dictionary<string, object>
                {
                    ["x"] = logsPopoutX,
                    ["y"] = logsPopoutY,
                    ["w"] = logsPopoutW,
                    ["h"] = logsPopoutH,
                    ["maximized"] = logsPopoutMaximized,
                };
            }
            s["platformBadgeGlow"] = platformBadgeGlow;
            s["syncPlatforms"] = syncPlatforms;
            s["autoForceDeploy"] = autoForceDeploy;
            s["virtualModMode"] = virtualModMode;
            s["configEditorSpellCheck"] = configEditorSpellCheck;
            s["confirmBeforeDeleteMod"] = confirmBeforeDeleteMod;
            s["confirmBeforeRemoveOldModOnUpdate"] = confirmBeforeRemoveOldModOnUpdate;
            s["updateModsInAllPresets"] = updateModsInAllPresets;
            s["language"] = applicationLanguage;
            s["uiTheme"] = uiTheme;
            s["sevenZipPath"] = sevenZipPath;
            s["rarExtractorPath"] = rarExtractorPath;
            try { s["modPresets"] = JsonDocument.Parse(EnsureModPresetsInitialized()).RootElement; }
            catch { s["modPresets"] = BuildDefaultModPresetsObject(); }
            s["activeModPreset"] = activeModPreset;
            s["activeProfile"] = activeProfile;
            try { s["modGroups"] = JsonDocument.Parse(modPresetsJson).RootElement; } catch { s["modGroups"] = new object(); }

            s["steamPipboyRed"] = steamPipboyRed; s["steamPipboyGreen"] = steamPipboyGreen; s["steamPipboyBlue"] = steamPipboyBlue;
            s["steamQuickboyRed"] = steamQuickboyRed; s["steamQuickboyGreen"] = steamQuickboyGreen; s["steamQuickboyBlue"] = steamQuickboyBlue;
            s["steamPaRed"] = steamPaRed; s["steamPaGreen"] = steamPaGreen; s["steamPaBlue"] = steamPaBlue;
            s["steamHudRed"] = steamHudRed; s["steamHudGreen"] = steamHudGreen; s["steamHudBlue"] = steamHudBlue;
            s["steamFov"] = steamFov; s["steamFov1st"] = steamFov1st; s["steamFovPipboy"] = steamFovPipboy;
            s["steamShadows"] = steamShadows; s["steamTaa"] = steamTaa;
            s["steamGodrays"] = steamGodrays; s["steamDof"] = steamDof; s["steamGrass"] = steamGrass;
            s["steamPing"] = steamPing; s["steamBandwidth"] = steamBandwidth; s["steamFastload"] = steamFastload; s["steamVsync"] = steamVsync;
            s["steamFpsCap"] = steamFpsCap;
            s["steamAo"] = steamAo; s["steamBlood"] = steamBlood; s["steamDofSpecific"] = steamDofSpecific;
            s["steamLensFlare"] = steamLensFlare; s["steamExtraBlur"] = steamExtraBlur; s["steamVatsBlur"] = steamVatsBlur;
            s["steamAniso"] = steamAniso; s["steamWater"] = steamWater; s["steamLod"] = steamLod; s["steamDecals"] = steamDecals;
            s["steamPipboyFx"] = steamPipboyFx;
            s["steamVolumQuality"] = steamVolumQuality; s["steamShadowRes"] = steamShadowRes; s["steamShadowFilter"] = steamShadowFilter;
            s["steamTextureQuality"] = steamTextureQuality; s["steamDecalsPerFrame"] = steamDecalsPerFrame; s["steamGridLoad"] = steamGridLoad;
            s["steamCorpseHighlight"] = steamCorpseHighlight; s["steamFocusShadows"] = steamFocusShadows; s["steamRenderGrass"] = steamRenderGrass;
            s["steamSsr"] = steamSsr; s["steamRainOcclusion"] = steamRainOcclusion; s["steamNpcShadowLights"] = steamNpcShadowLights;
            s["steamCellLoads"] = steamCellLoads; s["steamTiledLighting"] = steamTiledLighting; s["steamSkipSplash"] = steamSkipSplash;
            s["steamGlassShader"] = steamGlassShader; s["steamPbrShadows"] = steamPbrShadows; s["steamPlayerNames"] = steamPlayerNames;
            s["steamPlayerPings"] = steamPlayerPings; s["steamGrassFade"] = steamGrassFade; s["steamTreeDist"] = steamTreeDist;
            s["steamLodSky"] = steamLodSky; s["steamLeafAnim"] = steamLeafAnim; s["steamConversationHistory"] = steamConversationHistory;
            s["steamGamma"] = steamGamma;

            s["xboxPipboyRed"] = xboxPipboyRed; s["xboxPipboyGreen"] = xboxPipboyGreen; s["xboxPipboyBlue"] = xboxPipboyBlue;
            s["xboxQuickboyRed"] = xboxQuickboyRed; s["xboxQuickboyGreen"] = xboxQuickboyGreen; s["xboxQuickboyBlue"] = xboxQuickboyBlue;
            s["xboxPaRed"] = xboxPaRed; s["xboxPaGreen"] = xboxPaGreen; s["xboxPaBlue"] = xboxPaBlue;
            s["xboxHudRed"] = xboxHudRed; s["xboxHudGreen"] = xboxHudGreen; s["xboxHudBlue"] = xboxHudBlue;
            s["xboxFov"] = xboxFov; s["xboxFov1st"] = xboxFov1st; s["xboxFovPipboy"] = xboxFovPipboy;
            s["xboxShadows"] = xboxShadows; s["xboxTaa"] = xboxTaa;
            s["xboxGodrays"] = xboxGodrays; s["xboxDof"] = xboxDof; s["xboxGrass"] = xboxGrass;
            s["xboxPing"] = xboxPing; s["xboxBandwidth"] = xboxBandwidth; s["xboxFastload"] = xboxFastload; s["xboxVsync"] = xboxVsync;
            s["xboxFpsCap"] = xboxFpsCap;
            s["xboxAo"] = xboxAo; s["xboxBlood"] = xboxBlood; s["xboxDofSpecific"] = xboxDofSpecific;
            s["xboxLensFlare"] = xboxLensFlare; s["xboxExtraBlur"] = xboxExtraBlur; s["xboxVatsBlur"] = xboxVatsBlur;
            s["xboxAniso"] = xboxAniso; s["xboxWater"] = xboxWater; s["xboxLod"] = xboxLod; s["xboxDecals"] = xboxDecals;
            s["xboxPipboyFx"] = xboxPipboyFx;
            s["xboxVolumQuality"] = xboxVolumQuality; s["xboxShadowRes"] = xboxShadowRes; s["xboxShadowFilter"] = xboxShadowFilter;
            s["xboxTextureQuality"] = xboxTextureQuality; s["xboxDecalsPerFrame"] = xboxDecalsPerFrame; s["xboxGridLoad"] = xboxGridLoad;
            s["xboxCorpseHighlight"] = xboxCorpseHighlight; s["xboxFocusShadows"] = xboxFocusShadows; s["xboxRenderGrass"] = xboxRenderGrass;
            s["xboxSsr"] = xboxSsr; s["xboxRainOcclusion"] = xboxRainOcclusion; s["xboxNpcShadowLights"] = xboxNpcShadowLights;
            s["xboxCellLoads"] = xboxCellLoads; s["xboxTiledLighting"] = xboxTiledLighting; s["xboxSkipSplash"] = xboxSkipSplash;
            s["xboxGlassShader"] = xboxGlassShader; s["xboxPbrShadows"] = xboxPbrShadows; s["xboxPlayerNames"] = xboxPlayerNames;
            s["xboxPlayerPings"] = xboxPlayerPings; s["xboxGrassFade"] = xboxGrassFade; s["xboxTreeDist"] = xboxTreeDist;
            s["xboxLodSky"] = xboxLodSky; s["xboxLeafAnim"] = xboxLeafAnim; s["xboxConversationHistory"] = xboxConversationHistory;
            s["xboxGamma"] = xboxGamma;
            
            s["activeTweaksPreset"] = activeTweaksPreset;
            s["pipboyCrtDefaultMigrationV1"] = pipboyCrtDefaultMigrationV1;
            s["pipboyCrtOnDefaultV2"] = pipboyCrtOnDefaultV2;
            s["pipboyCrtUserConfigured"] = pipboyCrtUserConfigured;
            s["pipboyPrefsIniScrubV1"] = pipboyPrefsIniScrubV1;
            s["gameIntegrityRepairV1"] = gameIntegrityRepairV1;
            s["looseConfigDeployRepairV1"] = looseConfigDeployRepairV1;
            s["archiveKeyName"] = archiveKeyName;

            if (!string.IsNullOrWhiteSpace(keybindsJson))
            {
                try { s["keybinds"] = JsonDocument.Parse(keybindsJson).RootElement; }
                catch { }
            }

            s["windowWidth"] = windowWidth;
            s["windowHeight"] = windowHeight;
            s["windowTop"] = windowTop;
            s["windowLeft"] = windowLeft;
            s["windowMaximized"] = windowMaximized;

            if (_importedCollection != null && !string.IsNullOrWhiteSpace(_importedCollection.Slug))
            {
                s["importedCollection"] = new
                {
                    slug = _importedCollection.Slug,
                    name = _importedCollection.Name,
                    revision = _importedCollection.Revision,
                    mods = _importedCollection.Mods.Select(m => new
                    {
                        modId = m.ModId,
                        fileId = m.FileId,
                        fileName = m.FileName,
                        fileVersion = m.FileVersion
                    }).ToList()
                };
            }

            string json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, json);
            SyncAppPaths();
        } catch (Exception ex) { LogError($"SaveSettings Error: {ex.Message}"); }
    }

    private List<Profile> profiles = new();
    private void LoadProfiles() 
    { 
        try {
            string path = Path.Combine(AppPaths.ProfilesFolder, "profiles.json");
            if (File.Exists(path)) {
                string json = File.ReadAllText(path);
                profiles = JsonSerializer.Deserialize<List<Profile>>(json) ?? new();
            }
            SanitizeProfileCredentialSettings();
        } catch (Exception ex) { LogError($"LoadProfiles Error: {ex.Message}"); }

        if (profiles.Count == 0 || !profiles.Any(p => p.Name == "Default Profile")) 
        {
            if (profiles.Count == 0) profiles.Add(new Profile { Name = "Default Profile" });
            else profiles.Insert(0, new Profile { Name = "Default Profile" });
        }

        if (!profiles.Any(p => p.Name == activeProfile))
            activeProfile = "Default Profile";
    }

    private static bool IsCoreIniHealthKey(string? key)
    {
        string n = (key ?? "").Replace('\\', '/').Trim();
        return n.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase);
    }

    private List<string> GetEnabledModKeysForProfile()
    {
        return _modManager.GetModsList()
            .Where(m => string.Equals((string)((dynamic)m).status, "enabled", StringComparison.OrdinalIgnoreCase))
            .Select(m => (string)((dynamic)m).originalName)
            .Where(k => !string.IsNullOrWhiteSpace(k) && !IsCoreIniHealthKey(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool EnabledSetsMatchProfile(IReadOnlyList<string> current, IReadOnlyList<string>? profile)
    {
        var currentList = current.Where(k => !IsCoreIniHealthKey(k)).ToList();
        var profileList = (profile ?? new List<string>()).Where(k => !string.IsNullOrWhiteSpace(k) && !IsCoreIniHealthKey(k)).ToList();
        if (currentList.Count == 0 && profileList.Count == 0) return true;

        var profileSet = new HashSet<string>(profileList, StringComparer.OrdinalIgnoreCase);
        var currentSet = new HashSet<string>(currentList, StringComparer.OrdinalIgnoreCase);
        foreach (var key in currentList)
        {
            if (!ProfileAllowsMod(profileSet, key)) return false;
        }
        foreach (var key in profileList)
        {
            if (!ProfileAllowsMod(currentSet, key)) return false;
        }
        return true;
    }

    private static string HealthModDisplayName(dynamic mod)
    {
        try
        {
            string name = (string)mod.name;
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, "Unknown Mod", StringComparison.OrdinalIgnoreCase))
                return name.Trim();
        }
        catch { }

        try
        {
            string key = (string)mod.originalName;
            if (!string.IsNullOrWhiteSpace(key)) return Path.GetFileName(key.Replace('\\', '/'));
        }
        catch { }

        return "Unknown mod";
    }

    private bool EnabledModFilesPresent(dynamic mod)
    {
        string originalName = "";
        try { originalName = (string)mod.originalName ?? ""; }
        catch { return false; }

        bool isLoose = originalName.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase);
        if (!isLoose)
        {
            try { isLoose = mod.isLoose is bool flag && flag; }
            catch {  }
        }

        var meta = _modManager.GetMetadataForMod(originalName);
        if (meta?.IsLoose == true) isLoose = true;

        if (isLoose)
        {
            var files = meta?.Files;
            if (files == null || files.Count == 0) return false;
            foreach (string relative in files)
            {
                if (string.IsNullOrWhiteSpace(relative)) continue;
                string owned = _modManager.ResolveListKeyToFullPath(relative);
                if (!string.IsNullOrWhiteSpace(owned) && File.Exists(owned))
                    return true;
            }
            return false;
        }

        string full = _modManager.ResolveListKeyToFullPath(originalName);
        return !string.IsNullOrWhiteSpace(full) && File.Exists(full);
    }

    private void ReconcileActiveProfileEnabledMods()
    {
        var p = profiles.FirstOrDefault(x => x.Name == activeProfile);
        if (p == null) return;

        var currentEnabled = GetEnabledModKeysForProfile();
        if (EnabledSetsMatchProfile(currentEnabled, p.EnabledMods)) return;

        p.EnabledMods = currentEnabled;
        SaveProfiles();
    }

    private void SaveProfiles() 
    { 
        try {
            string path = Path.Combine(AppPaths.ProfilesFolder, "profiles.json");
            string json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        } catch (Exception ex) { LogError($"SaveProfiles Error: {ex.Message}"); }
    }

    private Profile CaptureCurrentState(string name)
    {
        var p = new Profile { Name = name };
        p.Settings = SafeGetRealSettings();
        
        var allMods = _modManager.GetModsList();
        
        
        var currentP = profiles.FirstOrDefault(x => x.Name == activeProfile);
        
        if (currentP != null && name == activeProfile)
        {
            p.ProfileMods = new List<string>(currentP.ProfileMods);
        }
        else if (activeProfile == "Default Profile")
        {
             p.ProfileMods = allMods.Select(m => (string)((dynamic)m).originalName).ToList();
        }
        else
        {
            if (currentP != null) p.ProfileMods = new List<string>(currentP.ProfileMods);
        }
        
        p.EnabledMods = allMods
            .Where(m => (string)((dynamic)m).status == "enabled")
            .Select(m => (string)((dynamic)m).originalName)
            .ToList();
            
        return p;
    }

    private void ApplyProfileState(Profile p)
    {
        if (p == null) return;
        
        foreach(var kvp in p.Settings) 
        {
            if (IsNexusCredentialSettingKey(kvp.Key))
                continue;

            if (IsTweakSettingKey(kvp.Key))
                continue;

            try {
                var json = JsonSerializer.Serialize(kvp.Value);
                using var doc = JsonDocument.Parse(json);
                ApplyIndividualSettingObject(kvp.Key, doc.RootElement.Clone()); 
            } catch (Exception ex) {
                LogError($"[PROFILE] Failed to apply setting '{kvp.Key}' from profile '{p.Name}': {ex.Message}");
            }
        }

        if (p.Settings.ContainsKey("activeTweaksPreset"))
            activeTweaksPreset = p.Settings["activeTweaksPreset"].ToString() ?? "";

        if (p.Settings.TryGetValue("modPresets", out var mpObj) && mpObj != null)
        {
            try
            {
                string raw = mpObj is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(mpObj);
                modPresetsJson = raw;
                EnsureModPresetsInitialized();
            }
            catch (Exception ex) { LogError($"[PROFILE] Failed to restore modPresets: {ex.Message}"); }
        }
        if (p.Settings.TryGetValue("activeModPreset", out var ampObj) && ampObj != null)
            activeModPreset = ampObj.ToString() ?? activeModPreset;

        SyncTweakValuesFromIni();
        
        _modManager.BulkUpdateModStatus(p.EnabledMods);
        
        SaveSettings(); 
        RefreshConflictCount();
    }

    private void RestoreNexusSession()
    {
        string? credential = NexusCredentialStore.TryLoad();
        if (string.IsNullOrWhiteSpace(credential))
        {
            nexusLoggedIn = false;
            InitializeNexusManager(null);
            return;
        }

        nexusLoggedIn = true;
        InitializeNexusManager(credential);
        Task.Run(() => _nexusManager.RegisterNxmProtocol());
        LogActivity("[NEXUS] Restored saved Nexus session.");

        Task.Run(async () =>
        {
            try
            {
                if (_nexusManager == null || !await _nexusManager.ValidateSessionAsync().ConfigureAwait(false))
                {
                    RunOnUiThreadSafe(() =>
                    {
                        LogActivity("[NEXUS] Saved session expired; sign in again in Settings.");
                        NexusCredentialStore.Clear();
                        nexusLoggedIn = false;
                        InitializeNexusManager(null);
                        SendDataToWeb();
                    });
                }
            }
            catch (Exception ex)
            {
                LogError($"[NEXUS] Session validation failed: {ex.Message}");
            }
        });
    }

    private void InitializeNexusManager(string? authCredential)
    {
        _nexusManager?.Dispose();
        _nexusManager = new NexusManager(authCredential, CurrentVersion, LogActivity, (type, msg) => {
            this.Invoke(() => {
                if (type == "download_complete") {
                    LogActivity($"[NEXUS] Download complete: {msg}. Importing...");
                    HandleAddModFiles(new List<string> { msg }); 
                } 
                else if (type == "sso_success") {
                    LogActivity("[NEXUS] SSO login succeeded.");
                    try
                    {
                        NexusCredentialStore.Save(msg);
                    }
                    catch (Exception ex)
                    {
                        LogError($"[NEXUS] Failed to save session: {ex.Message}");
                        SendStatusMessage("error", "Logged in, but the session could not be saved. Downloads may not work after restart.", "nexus_session_save_failed");
                    }
                    nexusLoggedIn = true;
                    InitializeNexusManager(msg);
                    Task.Run(() => _nexusManager.RegisterNxmProtocol());
                    SendStatusMessage("success", "Logged in to Nexus Mods!", "login_success");
                    SendDataToWeb();
                }
                else if (type == "nexus_download_premium")
                {
                    SendStatusMessage(
                        "warning",
                        "Direct Nexus downloads require Premium. Opening the mod page — use Slow Download or Mod Manager Download.",
                        "nexus_download_premium");
                }
                else if (type == "nexus_download_free_user")
                {
                    SendStatusMessage(
                        "info",
                        "Free Nexus account: opening Mod Manager Download in your browser. Confirm once — Fallout 76 Manager will import automatically.",
                        "nexus_download_free_user");
                }
                else if (type == "nexus_download_forbidden")
                {
                    SendStatusMessage(
                        "error",
                        "Nexus blocked the download request. Try reconnecting your Nexus account in Settings, or download from the opened mod page.",
                        "nexus_download_forbidden");
                }
                else if (type == "nexus_download_cancelled")
                {
                    SendStatusMessage(
                        "warning",
                        $"Download cancelled: {msg}",
                        "nexus_download_cancelled",
                        new object[] { msg });
                }
                else if (_collectionImportInProgress && type == "info" &&
                         (msg.Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||
                          msg.Contains("Requesting download", StringComparison.OrdinalIgnoreCase)))
                {
                }
                else {
                    SendStatusMessage(type, msg);
                }
            });
        });
    }

    private string FormatSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = (decimal)bytes;
        while (Math.Round(number / 1024) >= 1) {
            number /= 1024;
            counter++;
        }
        return string.Format("{0:n1} {1}", number, suffixes[counter]);
    }

    private long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try {
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories).Sum(t => new FileInfo(t).Length);
        } catch (Exception ex) {
            LogError($"[DATA] Failed to compute directory size for '{path}': {ex.Message}");
            return 0;
        }
    }

    private object? BuildImportedCollectionPayload()
    {
        if (_importedCollection == null || string.IsNullOrWhiteSpace(_importedCollection.Slug))
            return null;

        return new
        {
            slug = _importedCollection.Slug,
            name = _importedCollection.Name,
            revision = _importedCollection.Revision,
            modCount = _importedCollection.Mods.Count
        };
    }

    private object BuildStatsQuick()
    {
        return new
        {
            totalDataSize = "...",
            modsActive = 0,
            activeModsSize = "...",
            lastLaunch = lastGameLaunch
        };
    }

    private object? _lastConfigHealth;

    private object BuildConfigHealthQuick()
    {
        if (_lastConfigHealth != null) return _lastConfigHealth;

        return new
        {
            overallReady = false,
            iniVerified = new { status = "pass", detail = "" },
            modFilesPresent = new { status = "pass", missingCount = 0, missingMods = Array.Empty<string>() },
            profileSynced = new { status = "pass", detail = "" },
            deployState = new { status = "pass", state = "pending", detail = "" },
            conflictCount = _conflictManager?.LastConflictCount ?? 0,
            duplicateCount = _conflictManager?.LastDuplicateCount ?? 0,
            conflictsAutoOverridden = autoForceDeploy && (_conflictManager?.LastConflictCount ?? 0) > 0
        };
    }

    private object BuildConfigHealth()
    {
        SyncAppPaths();

        string iniStatus = "pass";
        string iniDetail = "";

        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            iniStatus = "fail";
            iniDetail = "game_path_missing";
        }
        else
        {
            string dataDir = Path.Combine(gamePath, "Data");
            string exeFull = Path.Combine(gamePath, _platformManager.GetGameExeName());
            if (!Directory.Exists(dataDir))
            {
                iniStatus = "fail";
                iniDetail = "data_folder_missing";
            }
            else if (!File.Exists(exeFull))
            {
                iniStatus = "fail";
                iniDetail = "game_exe_missing";
            }
            else if (string.IsNullOrWhiteSpace(documentsPath) || !Directory.Exists(documentsPath))
            {
                iniStatus = "warn";
                iniDetail = "documents_path_issue";
            }
        }

        var missingMods = new List<string>();
        try
        {
            foreach (dynamic mod in SafeGetRealMods())
            {
                if ((string)mod.status != "enabled") continue;
                if (EnabledModFilesPresent(mod)) continue;
                missingMods.Add(HealthModDisplayName(mod));
            }
        }
        catch (Exception ex)
        {
            LogError($"[HEALTH] Mod file scan failed: {ex.Message}");
        }

        missingMods = missingMods.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string modFilesStatus = missingMods.Count == 0 ? "pass" : "fail";

        bool profileSynced = true;
        string profileDetail = "";
        try
        {
            var profile = profiles.FirstOrDefault(p => p.Name == activeProfile);
            if (profile == null)
            {
                profileSynced = false;
                profileDetail = "profile_not_found";
            }
            else
            {
                var currentEnabled = GetEnabledModKeysForProfile();
                profileSynced = EnabledSetsMatchProfile(currentEnabled, profile.EnabledMods ?? new List<string>());
                if (!profileSynced)
                    profileDetail = "profile_out_of_sync";
            }
        }
        catch (Exception ex)
        {
            profileSynced = false;
            profileDetail = ex.Message;
        }

        var (deployInSync, deployState, deployDetail) = _modManager.GetDeploySyncStatus();
        string deployStatus = deployState == "virtual" ? "pass" : (deployInSync ? "pass" : "warn");

        int conflictCount = _conflictManager != null ? _conflictManager.LastConflictCount : 0;
        int duplicateCount = _conflictManager != null ? _conflictManager.LastDuplicateCount : 0;
        bool conflictsBlockReady = conflictCount > 0 && !autoForceDeploy;
        bool overallReady = iniStatus == "pass" &&
                            modFilesStatus == "pass" &&
                            profileSynced &&
                            (deployStatus == "pass" || deployState == "virtual") &&
                            !conflictsBlockReady;

        var health = new
        {
            overallReady,
            iniVerified = new { status = iniStatus, detail = iniDetail },
            modFilesPresent = new { status = modFilesStatus, missingCount = missingMods.Count, missingMods = missingMods.Take(5).ToList() },
            profileSynced = new { status = profileSynced ? "pass" : "warn", detail = profileDetail },
            deployState = new { status = deployStatus, state = deployState, detail = deployDetail ?? "" },
            conflictCount,
            duplicateCount,
            conflictsAutoOverridden = autoForceDeploy && conflictCount > 0
        };
        _lastConfigHealth = health;
        return health;
    }

    private object? ParseKeybindsForWeb()
    {
        if (string.IsNullOrWhiteSpace(keybindsJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(keybindsJson);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ModPresetState
    {
        public List<string> Mods { get; set; } = new();
        public List<string> Enabled { get; set; } = new();
        public bool ReceiveUpdates { get; set; }
    }

    private const string DefaultModPresetName = "Default";

    private object BuildDefaultModPresetsObject()
    {
        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultModPresetName] = new { mods = Array.Empty<string>(), enabled = Array.Empty<string>(), receiveUpdates = false },
            ["Preset 1"] = new { mods = Array.Empty<string>(), enabled = Array.Empty<string>(), receiveUpdates = false },
            ["Preset 2"] = new { mods = Array.Empty<string>(), enabled = Array.Empty<string>(), receiveUpdates = false },
        };
    }

    private string EnsureModPresetsInitialized()
    {
        var presets = LoadModPresetsDictionary();
        if (presets.Count == 0)
        {
            presets = MigrateLegacyModGroups();
            if (presets.Count == 0)
            {
                presets[DefaultModPresetName] = new ModPresetState();
                presets["Preset 1"] = new ModPresetState();
                presets["Preset 2"] = new ModPresetState();
                SeedDefaultFromCurrentMods(presets[DefaultModPresetName]);
            }
        }

        MigrateLegacyStockPresetNames(presets);
        EnsureStockModPresetsExist(presets);

        if (string.IsNullOrWhiteSpace(activeModPreset) || !presets.ContainsKey(activeModPreset))
            activeModPreset = presets.ContainsKey(DefaultModPresetName)
                ? DefaultModPresetName
                : (presets.Keys.FirstOrDefault() ?? DefaultModPresetName);

        modPresetsJson = JsonSerializer.Serialize(presets);
        return modPresetsJson;
    }

    private void MigrateLegacyStockPresetNames(Dictionary<string, ModPresetState> presets)
    {
        if (presets == null || presets.Count == 0) return;
        if (presets.ContainsKey(DefaultModPresetName)) return;
        if (!presets.ContainsKey("Preset 1")) return;

        static void MoveKey(Dictionary<string, ModPresetState> dict, string from, string to)
        {
            if (!dict.TryGetValue(from, out var state)) return;
            dict.Remove(from);
            if (!dict.ContainsKey(to))
                dict[to] = state;
        }

        MoveKey(presets, "Preset 3", "__migrate_preset_2");
        MoveKey(presets, "Preset 2", "__migrate_preset_1");
        MoveKey(presets, "Preset 1", DefaultModPresetName);
        MoveKey(presets, "__migrate_preset_1", "Preset 1");
        MoveKey(presets, "__migrate_preset_2", "Preset 2");

        if (string.Equals(activeModPreset, "Preset 1", StringComparison.OrdinalIgnoreCase))
            activeModPreset = DefaultModPresetName;
        else if (string.Equals(activeModPreset, "Preset 2", StringComparison.OrdinalIgnoreCase))
            activeModPreset = "Preset 1";
        else if (string.Equals(activeModPreset, "Preset 3", StringComparison.OrdinalIgnoreCase))
            activeModPreset = "Preset 2";
    }

    private void EnsureStockModPresetsExist(Dictionary<string, ModPresetState> presets)
    {
        if (!presets.ContainsKey(DefaultModPresetName))
            presets[DefaultModPresetName] = new ModPresetState();
    }

    private Dictionary<string, ModPresetState> MigrateLegacyModGroups()
    {
        var result = new Dictionary<string, ModPresetState>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(modGroups) || modGroups.Trim() == "{}")
            return result;

        try
        {
            using var doc = JsonDocument.Parse(modGroups);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            bool looksLikeNewShape = false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    (prop.Value.TryGetProperty("mods", out _) || prop.Value.TryGetProperty("Mods", out _)))
                {
                    looksLikeNewShape = true;
                    break;
                }
            }

            if (looksLikeNewShape)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                    result[prop.Name] = ParsePresetElement(prop.Value);
                return result;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var state = new ModPresetState();
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        string? name = item.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                            state.Mods.Add(name);
                    }
                }
                result[prop.Name] = state;
            }

            if (!result.ContainsKey(DefaultModPresetName))
            {
                var def = new ModPresetState();
                SeedDefaultFromCurrentMods(def);
                result[DefaultModPresetName] = def;
            }
            if (!result.ContainsKey("Preset 1")) result["Preset 1"] = new ModPresetState();
            if (!result.ContainsKey("Preset 2")) result["Preset 2"] = new ModPresetState();
        }
        catch (Exception ex)
        {
            LogError($"[PRESETS] Failed to migrate modGroups: {ex.Message}");
        }
        return result;
    }

    private void SeedDefaultFromCurrentMods(ModPresetState preset)
    {
        try
        {
            if (_modManager == null) return;
            foreach (dynamic m in _modManager.GetModsList())
            {
                string name = (string)m.originalName;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (AppPaths.IsProtectedCoreIniListKey(name)) continue;
                preset.Mods.Add(name);
                if ((string)m.status == "enabled")
                    preset.Enabled.Add(name);
            }
        }
        catch (Exception ex)
        {
            LogError($"[PRESETS] Failed to seed Default preset: {ex.Message}");
        }
    }

    private ModPresetState ParsePresetElement(JsonElement el)
    {
        var state = new ModPresetState();
        if (el.ValueKind != JsonValueKind.Object) return state;
        if (el.TryGetProperty("mods", out var mods) || el.TryGetProperty("Mods", out mods))
        {
            if (mods.ValueKind == JsonValueKind.Array)
                foreach (var item in mods.EnumerateArray())
                {
                    string? n = item.GetString();
                    if (!string.IsNullOrWhiteSpace(n)) state.Mods.Add(n);
                }
        }
        if (el.TryGetProperty("enabled", out var en) || el.TryGetProperty("Enabled", out en))
        {
            if (en.ValueKind == JsonValueKind.Array)
                foreach (var item in en.EnumerateArray())
                {
                    string? n = item.GetString();
                    if (!string.IsNullOrWhiteSpace(n)) state.Enabled.Add(n);
                }
        }
        if (el.TryGetProperty("receiveUpdates", out var ru) || el.TryGetProperty("ReceiveUpdates", out ru))
        {
            if (ru.ValueKind == JsonValueKind.True) state.ReceiveUpdates = true;
            else if (ru.ValueKind == JsonValueKind.False) state.ReceiveUpdates = false;
        }
        return state;
    }

    private Dictionary<string, ModPresetState> LoadModPresetsDictionary()
    {
        var result = new Dictionary<string, ModPresetState>(StringComparer.OrdinalIgnoreCase);
        string raw = !string.IsNullOrWhiteSpace(modPresetsJson) ? modPresetsJson : "";
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "{}")
            return result;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in doc.RootElement.EnumerateObject())
                result[prop.Name] = ParsePresetElement(prop.Value);
        }
        catch (Exception ex)
        {
            LogError($"[PRESETS] Failed to parse modPresets: {ex.Message}");
        }
        return result;
    }

    private object GetModPresetsObjectForWeb()
    {
        EnsureModPresetsInitialized();
        TryLateSeedDefaultIfNeeded();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ModPresetState>>(modPresetsJson)
                   ?? BuildDefaultModPresetsObject();
        }
        catch
        {
            return BuildDefaultModPresetsObject();
        }
    }

    private bool _modPresetsInitialSeedAttempted;

    private void TryLateSeedDefaultIfNeeded()
    {
        if (_modPresetsInitialSeedAttempted) return;
        try
        {
            var presets = LoadModPresetsDictionary();
            if (!presets.TryGetValue(DefaultModPresetName, out var def) || def.Mods.Count > 0)
            {
                _modPresetsInitialSeedAttempted = true;
                return;
            }
            if (_modManager == null) return;
            var list = _modManager.GetModsList();
            if (list == null || list.Count == 0) return;

            SeedDefaultFromCurrentMods(def);
            _modPresetsInitialSeedAttempted = true;
            if (def.Mods.Count > 0)
                PersistModPresets(presets);
        }
        catch (Exception ex)
        {
            LogError($"[PRESETS] Late Default preset seed failed: {ex.Message}");
            _modPresetsInitialSeedAttempted = true;
        }
    }

    private void PersistModPresets(Dictionary<string, ModPresetState> presets)
    {
        modPresetsJson = JsonSerializer.Serialize(presets);
        SaveSettings();
    }

    private void AddImportedKeysToActivePreset(IEnumerable<string> importedKeys)
    {
        if (importedKeys == null) return;
        try
        {
            EnsureModPresetsInitialized();
            var presets = LoadModPresetsDictionary();
            string name = string.IsNullOrWhiteSpace(activeModPreset) ? DefaultModPresetName : activeModPreset;
            if (!presets.TryGetValue(name, out var preset))
            {
                preset = new ModPresetState();
                presets[name] = preset;
                activeModPreset = name;
            }

            var mods = new HashSet<string>(preset.Mods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var enabled = new HashSet<string>(preset.Enabled ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var raw in importedKeys)
            {
                string key = (raw ?? "").Replace('\\', '/').Trim();
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (AppPaths.IsProtectedCoreIniListKey(key)) continue;
                if (mods.Add(key)) added++;
                enabled.Add(key);
            }
            if (added == 0 && enabled.SetEquals(preset.Enabled ?? new List<string>()))
                return;

            preset.Mods = mods.ToList();
            preset.Enabled = enabled.ToList();
            PersistModPresets(presets);
            LogActivity($"[PRESETS] Added {added} imported mod(s) to '{name}'.");
        }
        catch (Exception ex)
        {
            LogError($"[PRESETS] Failed to attach imports to active preset: {ex.Message}");
        }
    }

    private string GetModPresetsBackupJson()
    {
        EnsureModPresetsInitialized();
        return JsonSerializer.Serialize(new
        {
            modPresets = JsonSerializer.Deserialize<object>(modPresetsJson),
            activeModPreset
        });
    }

    private void ApplyModPresetsBackupJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("modPresets", out var mp))
            modPresetsJson = mp.GetRawText();
        if (root.TryGetProperty("activeModPreset", out var amp))
            activeModPreset = amp.GetString() ?? activeModPreset;
        EnsureModPresetsInitialized();
        SaveSettings();
    }
}
