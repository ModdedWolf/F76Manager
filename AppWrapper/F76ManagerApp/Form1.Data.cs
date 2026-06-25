using System.Text.Json;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using F76ManagerApp.Managers;

namespace F76ManagerApp;

public partial class Form1
{
    private string NormalizeUiTheme(string? id) =>
        ThemeIds.NormalizeUiTheme(id, uid => _themePackageLoader.IsUserTheme(uid));

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
        return
            $"window.__F76_BOOT_UI_THEME={themeJson};" +
            $"window.__F76_BOOT_LOGO={logoJson};" +
            $"window.__F76_ASSET_VERSION={assetVerJson};" +
            $"window.__F76_BUILTIN_THEME_IDS={builtinIdsJson};" +
            $"window.__F76_USER_THEME_IDS={idsJson};" +
            $"window.__F76_USER_THEME_CSS={cssJson};";
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

    private void SendDataToWeb(object? status = null)
    {
        if (this.InvokeRequired)
        {
            this.Invoke(() => SendDataToWeb(status));
            return;
        }

        if (webView == null || webView.CoreWebView2 == null) return;

        try 
        {
            SyncTweakValuesFromIni();
            var data = new {
                type = "UPDATE_DATA",
                mods = SafeGetRealMods(),
                settings = SafeGetRealSettings(),
                stats = SafeGetRealStats(),
                managerSettings = new {
                    gamePath = gamePath,
                    documentsPath = documentsPath,
                    localAppDataPath = localAppDataPath,
                    stringsPath = AppPaths.StringsPath,
                    minimizeToTray = minimizeToTray,
                    uiAnimations = uiAnimations,
                    platformBadgeGlow = platformBadgeGlow,
                    syncPlatforms = syncPlatforms,
                    autoForceDeploy = autoForceDeploy,
                    virtualModMode = virtualModMode,
                    configEditorSpellCheck = configEditorSpellCheck,
                    confirmBeforeDeleteMod = confirmBeforeDeleteMod,
                    confirmBeforeRemoveOldModOnUpdate = confirmBeforeRemoveOldModOnUpdate,
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
                lastSection = lastSection,
                status = status,
                appVersion = CurrentVersion,
                modGroups = modGroups,
                platform = _platformManager.GetPlatformLabel(),
                conflictsCount = _conflictManager != null ? _conflictManager.LastConflictCount : 0,
                configHealth = BuildConfigHealth(),
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

            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            webView.CoreWebView2.PostWebMessageAsJson(json);
        } catch (Exception ex) { LogError($"Failed to send data to web: {ex.Message}"); }
    }

    private object SafeGetRealStats()
    {
        try {
            long dataSize = GetDirectorySize(Path.Combine(gamePath, "Data"));
            var mods = _modManager.GetModsList();
            int enabledCount = mods.Count(m => (string)((dynamic)m).status == "enabled");
            long activeModsBytes = 0;
            foreach (var path in _modManager.GetEnabledModPaths())
            {
                try
                {
                    if (File.Exists(path))
                    {
                        activeModsBytes += new FileInfo(path).Length;
                    }
                }
                catch
                {
                }
            }
            return new {
                totalDataSize = FormatSize(dataSize),
                modsActive = enabledCount,
                activeModsSize = FormatSize(activeModsBytes),
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

        return s;
    }

    private static string NormalizeProfileModPath(string? raw) =>
        (raw ?? "").Replace('\\', '/').Trim();

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
                || modNorm.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase);
            bool eSpecial = eNorm.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase)
                || eNorm.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase);
            bool modGameRoot = modNorm.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase)
                || modNorm.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase);
            bool eGameRoot = eNorm.StartsWith("GameRoot/", StringComparison.OrdinalIgnoreCase)
                || eNorm.StartsWith("Disabled/GameRoot/", StringComparison.OrdinalIgnoreCase);
            if (modSpecial || eSpecial)
            {
                if (string.Equals(modNorm, eNorm, StringComparison.OrdinalIgnoreCase)) return true;
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

        string oldNorm = NormalizeProfileModPath(oldOriginalName);
        if (string.IsNullOrEmpty(oldNorm)) return;

        bool wasEnabled = p.EnabledMods.Any(e =>
            string.Equals(NormalizeProfileModPath(e), oldNorm, StringComparison.OrdinalIgnoreCase) ||
            ProfileAllowsMod(new HashSet<string> { e }, oldNorm));

        p.ProfileMods.RemoveAll(entry =>
        {
            var e = NormalizeProfileModPath(entry);
            return string.Equals(e, oldNorm, StringComparison.OrdinalIgnoreCase) ||
                   ProfileAllowsMod(new HashSet<string> { entry }, oldNorm);
        });
        p.EnabledMods.RemoveAll(entry =>
        {
            var e = NormalizeProfileModPath(entry);
            return string.Equals(e, oldNorm, StringComparison.OrdinalIgnoreCase) ||
                   ProfileAllowsMod(new HashSet<string> { entry }, oldNorm);
        });

        var allowed = new HashSet<string>(p.ProfileMods, StringComparer.OrdinalIgnoreCase);
        foreach (var rawKey in newKeys ?? Array.Empty<string>())
        {
            string key = NormalizeProfileModPath(rawKey);
            if (string.IsNullOrEmpty(key)) continue;
            if (!ProfileAllowsMod(allowed, key))
            {
                p.ProfileMods.Add(key);
                allowed.Add(key);
            }
            if (wasEnabled && !p.EnabledMods.Contains(key, StringComparer.OrdinalIgnoreCase))
                p.EnabledMods.Add(key);
        }

        SaveProfiles();
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
            return new {
                activity = ReadLastLines(logActivityPath, 100),
                errors = ReadLastLines(logErrorPath, 100),
                errorCount = CountLinesInFile(logErrorPath)
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

    private List<string> ReadLastLines(string path, int count)
    {
        try {
            if (!File.Exists(path)) return new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs)) {
                var lines = new List<string>();
                string line;
                while ((line = sr.ReadLine()) != null) lines.Add(line);
                if (lines.Count <= count) return lines;
                return lines.Skip(lines.Count - count).ToList();
            }
        } catch (Exception ex) {
            LogError($"[DATA] Failed to read log file '{path}': {ex.Message}");
            return new List<string>();
        }
    }

    private List<string> GetProfileList() => profiles.Select(p => p.Name).ToList();

    private void LoadSettings()
    {
        try {
            if (File.Exists(settingsPath)) {
                string json = File.ReadAllText(settingsPath);
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
                if (root.TryGetProperty("platformBadgeGlow", out var pbg)) platformBadgeGlow = pbg.GetBoolean();
                if (root.TryGetProperty("syncPlatforms", out var spr)) syncPlatforms = spr.GetBoolean();
                if (root.TryGetProperty("autoForceDeploy", out var afd)) autoForceDeploy = afd.GetBoolean();
                if (root.TryGetProperty("virtualModMode", out var vmm)) virtualModMode = vmm.GetBoolean();
                else if (root.TryGetProperty("managedVanillaMode", out var mvm)) virtualModMode = mvm.GetBoolean();
                if (root.TryGetProperty("configEditorSpellCheck", out var cesc)) configEditorSpellCheck = cesc.GetBoolean();
                if (root.TryGetProperty("confirmBeforeDeleteMod", out var cbddm)) confirmBeforeDeleteMod = cbddm.GetBoolean();
                if (root.TryGetProperty("confirmBeforeRemoveOldModOnUpdate", out var cbroou)) confirmBeforeRemoveOldModOnUpdate = cbroou.GetBoolean();
                if (root.TryGetProperty("language", out var lang)) applicationLanguage = lang.GetString() ?? applicationLanguage;
                else {
                    applicationLanguage = CultureInfo.CurrentUICulture.Name;
                }
                if (root.TryGetProperty("uiTheme", out var ut)) uiTheme = ut.GetString() ?? uiTheme;
                uiTheme = NormalizeUiTheme(uiTheme);

                if (root.TryGetProperty("archiveKeyName", out var akn)) archiveKeyName = akn.GetString() ?? archiveKeyName;
                if (root.TryGetProperty("keybinds", out var kb) && kb.ValueKind == JsonValueKind.Object)
                    keybindsJson = kb.GetRawText();
                if (root.TryGetProperty("sevenZipPath", out var szp)) sevenZipPath = szp.GetString() ?? sevenZipPath;
                if (root.TryGetProperty("rarExtractorPath", out var rep)) rarExtractorPath = rep.GetString() ?? rarExtractorPath;

                if (root.TryGetProperty("modGroups", out var mg)) modGroups = mg.GetRawText();
                if (root.TryGetProperty("activeTweaksPreset", out var atp)) activeTweaksPreset = atp.GetString() ?? activeTweaksPreset;
                if (root.TryGetProperty("pipboyCrtDefaultMigrationV1", out var pcdm)) pipboyCrtDefaultMigrationV1 = pcdm.GetBoolean();
                if (root.TryGetProperty("pipboyCrtOnDefaultV2", out var pcdv2)) pipboyCrtOnDefaultV2 = pcdv2.GetBoolean();
                if (root.TryGetProperty("pipboyCrtUserConfigured", out var pcuc)) pipboyCrtUserConfigured = pcuc.GetBoolean();
                if (root.TryGetProperty("pipboyPrefsIniScrubV1", out var pps)) pipboyPrefsIniScrubV1 = pps.GetBoolean();
                if (root.TryGetProperty("gameIntegrityRepairV1", out var gir)) gameIntegrityRepairV1 = gir.GetBoolean();
                bool purgeLegacyNexusCreds =
                    root.TryGetProperty("nexusApiKey", out _) ||
                    root.TryGetProperty("nexusApiKeyProtected", out _) ||
                    root.TryGetProperty("nexusAuthCredentialProtected", out _);
                nexusLoggedIn = false;
                if (purgeLegacyNexusCreds)
                {
                    LogActivity("[NEXUS] Removed legacy credential fields from settings.");
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
                    if (gamePath.Contains("XboxGames", StringComparison.OrdinalIgnoreCase) || 
                        gamePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) || 
                        gamePath.Contains("ModifiableWindowsApps", StringComparison.OrdinalIgnoreCase))
                    {
                        _platformManager.SetPlatform(GamePlatform.Xbox);
                    }
                    else if (gamePath.Contains("Steam", StringComparison.OrdinalIgnoreCase))
                    {
                        _platformManager.SetPlatform(GamePlatform.Steam);
                    }
                    else
                    {
                        _platformManager.SetPlatform(GamePlatform.Steam);
                    }
                }
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

            SyncAppPaths();
            RefreshConflictCount();
        } catch (Exception ex) { LogError($"LoadSettings Error: {ex.Message}"); }
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
            s["minimizeToTray"] = minimizeToTray;
            s["uiAnimations"] = uiAnimations;
            s["platformBadgeGlow"] = platformBadgeGlow;
            s["syncPlatforms"] = syncPlatforms;
            s["autoForceDeploy"] = autoForceDeploy;
            s["virtualModMode"] = virtualModMode;
            s["configEditorSpellCheck"] = configEditorSpellCheck;
            s["confirmBeforeDeleteMod"] = confirmBeforeDeleteMod;
            s["confirmBeforeRemoveOldModOnUpdate"] = confirmBeforeRemoveOldModOnUpdate;
            s["language"] = applicationLanguage;
            s["uiTheme"] = uiTheme;
            s["sevenZipPath"] = sevenZipPath;
            s["rarExtractorPath"] = rarExtractorPath;
            try { s["modGroups"] = JsonDocument.Parse(modGroups).RootElement; } catch { s["modGroups"] = new object(); }

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
    }

    private void ReconcileActiveProfileEnabledMods()
    {
        var p = profiles.FirstOrDefault(x => x.Name == activeProfile);
        if (p == null) return;

        var currentEnabled = _modManager.GetModsList()
            .Where(m => (string)((dynamic)m).status == "enabled")
            .Select(m => (string)((dynamic)m).originalName)
            .ToList();

        var currentSet = new HashSet<string>(currentEnabled, StringComparer.OrdinalIgnoreCase);
        var profileSet = new HashSet<string>(p.EnabledMods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        if (currentSet.SetEquals(profileSet)) return;

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

        SyncTweakValuesFromIni();
        
        _modManager.BulkUpdateModStatus(p.EnabledMods);
        
        SaveSettings(); 
        RefreshConflictCount();
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
                    nexusLoggedIn = true;
                    InitializeNexusManager(msg);
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
                string originalName = mod.originalName;
                string full = _modManager.ResolveListKeyToFullPath(originalName);
                if (string.IsNullOrWhiteSpace(full) || !File.Exists(full))
                    missingMods.Add(originalName);
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
                var currentEnabled = _modManager.GetModsList()
                    .Where(m => (string)((dynamic)m).status == "enabled")
                    .Select(m => (string)((dynamic)m).originalName)
                    .ToList();

                var currentSet = new HashSet<string>(currentEnabled, StringComparer.OrdinalIgnoreCase);
                var profileSet = new HashSet<string>(profile.EnabledMods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                profileSynced = currentSet.SetEquals(profileSet);
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
        bool overallReady = iniStatus == "pass" &&
                            modFilesStatus == "pass" &&
                            profileSynced &&
                            (deployStatus == "pass" || deployState == "virtual") &&
                            conflictCount == 0;

        return new
        {
            overallReady,
            iniVerified = new { status = iniStatus, detail = iniDetail },
            modFilesPresent = new { status = modFilesStatus, missingCount = missingMods.Count, missingMods = missingMods.Take(5).ToList() },
            profileSynced = new { status = profileSynced ? "pass" : "warn", detail = profileDetail },
            deployState = new { status = deployStatus, state = deployState, detail = deployDetail ?? "" },
            conflictCount
        };
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
}
