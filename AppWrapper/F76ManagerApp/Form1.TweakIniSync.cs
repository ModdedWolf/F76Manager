using System.Globalization;
using System.Collections.Generic;
using F76ManagerApp.Managers;

namespace F76ManagerApp;

public partial class Form1
{
    private static bool IniOn(string? v) =>
        v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    private void SyncTweakValuesFromIni()
    {
        if (string.IsNullOrEmpty(documentsPath) || !Directory.Exists(documentsPath))
            return;

        string prefsPath = Path.Combine(documentsPath, $"{AppPaths.IniPrefix}Prefs.ini");
        string customPath = Path.Combine(documentsPath, $"{AppPaths.IniPrefix}Custom.ini");
        if (!File.Exists(prefsPath) && !File.Exists(customPath))
            return;

        bool isXbox = _platformManager.IsXbox();

        string? ReadPrefs(string section, string key) =>
            _configManager.ReadIniValue(prefsPath, section, key);

        string? ReadMerged(string section, string key) =>
            _configManager.ReadMergedIniValue(section, key, documentsPath);

        string? vq = ReadPrefs("Display", "iVolumetricLightingQuality");
        if (vq == "0") SetVolumQuality(isXbox, "Low");
        else if (vq == "1") SetVolumQuality(isXbox, "Medium");
        else if (vq == "2") SetVolumQuality(isXbox, "High");

        string? godEnable = ReadPrefs("Display", "bVolumetricLightingEnable");
        if (godEnable != null)
            SetGodrays(isXbox, IniOn(godEnable));

        string? grassAllow = ReadMerged("Grass", "bAllowCreateGrass");
        if (grassAllow != null)
            SetGrass(isXbox, IniOn(grassAllow));

        string? renderGrass = ReadPrefs("Grass", "bRenderGrass");
        if (renderGrass != null)
            SetRenderGrass(isXbox, renderGrass == "1");

        string? smr = ReadPrefs("Display", "iShadowMapResolution");
        if (!string.IsNullOrEmpty(smr) && new[] { "512", "1024", "2048", "4096" }.Contains(smr))
            SetShadowRes(isXbox, smr);

        string? sf = ReadPrefs("Display", "uiShadowFilter");
        if (sf == "0") SetShadowFilter(isXbox, "Low");
        else if (sf == "1") SetShadowFilter(isXbox, "Medium");
        else if (sf == "2" || sf == "3") SetShadowFilter(isXbox, "High");

        string? shadowDist = ReadPrefs("Display", "fShadowDistance");
        if (float.TryParse(shadowDist, NumberStyles.Float, CultureInfo.InvariantCulture, out float sdVal))
        {
            string shadowLvl = sdVal switch
            {
                <= 6500f => "Low",
                <= 10000f => "Medium",
                <= 17000f => "High",
                _ => "Ultra"
            };
            SetShadows(isXbox, shadowLvl);
        }

        string? focusCnt = ReadPrefs("Display", "iMaxFocusShadows");
        if (focusCnt != null && int.TryParse(focusCnt, out int fc))
            SetFocusShadows(isXbox, fc != 0);

        string? tq = ReadPrefs("Texture", "iTextureQualityLevel");
        if (tq == "0") SetTextureQuality(isXbox, "Low");
        else if (tq == "1") SetTextureQuality(isXbox, "Medium");
        else if (tq == "2") SetTextureQuality(isXbox, "High");
        else if (tq == "3") SetTextureQuality(isXbox, "Ultra");

        string? mdf = ReadPrefs("Display", "iMaxDecalsPerFrame");
        if (mdf == "50" || mdf == "100") SetDecalsPerFrame(isXbox, "Low");
        else if (mdf == "250") SetDecalsPerFrame(isXbox, "Medium");
        else if (mdf == "500" || mdf == "1000") SetDecalsPerFrame(isXbox, "High");

        string? grids = ReadMerged("General", "uGridsToLoad");
        if (!string.IsNullOrEmpty(grids) && new[] { "3", "5", "7", "9" }.Contains(grids))
            SetGridLoad(isXbox, grids);

        string? ch = ReadPrefs("Display", "uiShowCorpseHighlighting");
        if (ch == "0") SetCorpseHighlight(isXbox, "Off");
        else if (ch == "1") SetCorpseHighlight(isXbox, "Low");
        else if (ch == "2") SetCorpseHighlight(isXbox, "High");

        string? gf = ReadPrefs("Grass", "fGrassStartFadeDistance");
        if (float.TryParse(gf, NumberStyles.Float, CultureInfo.InvariantCulture, out float gfVal))
            SetGrassFade(isXbox, (int)Math.Clamp(gfVal, 0, 15000));

        string? td = ReadPrefs("TerrainManager", "fTreeLoadDistance");
        if (float.TryParse(td, NumberStyles.Float, CultureInfo.InvariantCulture, out float tdVal))
            SetTreeDist(isXbox, (int)Math.Clamp(tdVal, 5000, 100000));

        string? lodMult = ReadPrefs("LOD", "fLODFadeOutMultObjects");
        if (float.TryParse(lodMult, NumberStyles.Float, CultureInfo.InvariantCulture, out float lodVal))
            SetLod(isXbox, (int)Math.Clamp(Math.Round(lodVal * 10), 10, 100));

        string? ls = ReadPrefs("LOD", "fLODFadeOutMultSkyCell");
        if (float.TryParse(ls, NumberStyles.Float, CultureInfo.InvariantCulture, out float lsVal))
            SetLodSky(isXbox, (int)Math.Clamp(Math.Round(lsVal * 10), 1, 20));

        string? la = ReadPrefs("Display", "fLeafAnimDampenDistStart");
        if (float.TryParse(la, NumberStyles.Float, CultureInfo.InvariantCulture, out float laVal))
            SetLeafAnim(isXbox, (int)Math.Clamp(laVal, 1000, 8000));

        string? gm = ReadMerged("Display", "fGamma");
        if (double.TryParse(gm, NumberStyles.Float, CultureInfo.InvariantCulture, out double gmVal))
            SetGamma(isXbox, Math.Clamp(gmVal, 0.8, 1.4));

        string? ssr = ReadPrefs("LightingShader", "bScreenSpaceReflections");
        if (ssr != null)
            SetSsr(isXbox, IniOn(ssr));

        string? rain = ReadPrefs("Weather", "iRainOcclusionMapResolution");
        if (rain != null)
            SetRainOcclusion(isXbox, rain != "0");

        string? npcSh = ReadPrefs("Display", "bAllowShadowcasterNPCLights");
        if (npcSh != null)
            SetNpcShadowLights(isXbox, IniOn(npcSh));

        string? tiled = ReadPrefs("Display", "bComputeShaderDeferredTiledLighting");
        if (tiled != null)
            SetTiledLighting(isXbox, IniOn(tiled));

        string? ao = ReadMerged("Display", "bSAOEnable");
        if (ao != null)
            SetAo(isXbox, IniOn(ao));

        string? blood = ReadMerged("Decals", "bBloodSplatterEnabled");
        if (blood != null)
            SetBlood(isXbox, blood != "0");

        string? cellLd = ReadMerged("General", "bBackgroundCellLoads");
        if (cellLd != null)
            SetCellLoads(isXbox, IniOn(cellLd));

        SyncDecalsQualityFromIni(isXbox, ReadPrefs);
        SyncWaterQualityFromIni(isXbox, ReadPrefs);
        SyncFastloadFromIni(isXbox, ReadMerged);
        SyncFpsAndVsyncFromIni(isXbox, ReadMerged, ReadPrefs);

        if (int.TryParse(ReadMerged("Display", "fDefaultWorldFOV"), out int fovW))
            SetFov(isXbox, Math.Clamp(fovW, 70, 120));

        if (int.TryParse(ReadMerged("Display", "fDefault1stPersonFOV"), out int fov1))
            SetFov1st(isXbox, Math.Clamp(fov1, 70, 120));

        if (int.TryParse(ReadMerged("Display", "fPipboy1stFOV"), out int fovPb))
            SetFovPipboy(isXbox, Math.Clamp(fovPb, 70, 120));

        string? mb = ReadMerged("ImageSpace", "bMBEnable");
        if (mb != null)
            SetMotionBlur(isXbox, IniOn(mb));

        string? dofDyn = ReadMerged("ImageSpace", "bDynamicDepthOfField");
        if (dofDyn != null)
            SetDofSpecific(isXbox, IniOn(dofDyn));

        string? lens = ReadMerged("ImageSpace", "bLensFlare");
        if (lens != null)
            SetLensFlare(isXbox, IniOn(lens));

        string? radial = ReadMerged("ImageSpace", "bDoRadialBlur");
        if (radial != null)
            SetExtraBlur(isXbox, IniOn(radial));

        string? vats = ReadMerged("VATS", "bVATSBlur");
        if (vats != null)
            SetVatsBlur(isXbox, IniOn(vats));

        string? taa = ReadPrefs("Display", "sAntiAliasing");
        if (!string.IsNullOrEmpty(taa))
            SetTaa(isXbox, taa);

        string? anisoRaw = ReadPrefs("Display", "iMaxAnisotropy");
        if (anisoRaw != null)
            SetAniso(isXbox, MapAnisotropyFromIni(anisoRaw));

        string? glass = ReadPrefs("Display", "bEnableGlassShader");
        if (glass != null)
            SetGlassShader(isXbox, IniOn(glass));

        string? pbr = ReadPrefs("Display", "bEffectShaderAllowPBRShadows");
        if (pbr != null)
            SetPbrShadows(isXbox, IniOn(pbr));

        string? names = ReadPrefs("Display", "bShowOtherPlayersNames");
        if (names != null)
            SetPlayerNames(isXbox, IniOn(names));

        string? pings = ReadPrefs("Display", "bShowOtherPlayersPings");
        if (pings != null)
            SetPlayerPings(isXbox, IniOn(pings));

        string? conv = ReadPrefs("Display", "fConversationHistorySize");
        if (float.TryParse(conv, NumberStyles.Float, CultureInfo.InvariantCulture, out float convVal))
            SetConversationHistory(isXbox, (int)Math.Clamp(Math.Round(convVal), 1, 10));

        string? pipFx = ReadMerged("Pipboy", "bPipboyDisableFX");
        if (pipFx != null)
            SetPipboyFx(isXbox, !IniOn(pipFx));

        string? ping = ReadMerged("General", "bCheckPing");
        if (ping != null)
            SetPing(isXbox, ping == "0");

        string? bw = ReadMerged("General", "fMaxProjectedBytesPerFrame");
        string? kvm = ReadMerged("General", "fLoadingKVMBuSize");
        SetBandwidth(isXbox, bw == "30000000.0" && kvm == "4096");
    }

    private void SyncDecalsQualityFromIni(bool isXbox, Func<string, string, string?> readPrefs)
    {
        string? bDecals = readPrefs("Decals", "bDecals");
        string? uMax = readPrefs("Decals", "uMaxDecals");
        if (bDecals == "0" || uMax == "0")
            SetDecals(isXbox, "Off");
        else if (uMax == "100")
            SetDecals(isXbox, "Low");
        else if (uMax == "250")
            SetDecals(isXbox, "Medium");
        else if (uMax == "500")
            SetDecals(isXbox, "High");
        else if (uMax == "1000")
            SetDecals(isXbox, "Ultra");
    }

    private void SyncWaterQualityFromIni(bool isXbox, Func<string, string, string?> readPrefs)
    {
        string? hiRes = readPrefs("Water", "bUseWaterHiRes");
        string? disp = readPrefs("Water", "bUseWaterDisplacements");
        if (hiRes == "0" && disp == "0")
            SetWater(isXbox, "Low");
        else if (hiRes == "0")
            SetWater(isXbox, "Medium");
        else if (hiRes == "1")
            SetWater(isXbox, "High");
    }

    private void SyncFastloadFromIni(bool isXbox, Func<string, string, string?> readMerged)
    {
        bool skip = IniOn(readMerged("General", "bSkipSplash"));
        bool fastFade = false;
        string? fade = readMerged("Interface", "fFadeToBlackFadeSeconds");
        if (float.TryParse(fade, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f <= 0.25f)
            fastFade = true;

        bool fl = skip || fastFade;
        SetFastload(isXbox, fl);
        SetSkipSplash(isXbox, skip);
    }

    private void SyncFpsAndVsyncFromIni(bool isXbox, Func<string, string, string?> readMerged, Func<string, string, string?> readPrefs)
    {
        string? clamp = readMerged("Display", "iFPSClamp");
        string? interval = readMerged("Display", "iPresentInterval");

        if (int.TryParse(clamp, out int capVal))
        {
            if (capVal == 0)
                SetFpsCap(isXbox, 0);
            else
                SetFpsCap(isXbox, capVal);
        }

        if (interval != null)
            SetVsync(isXbox, interval != "0");
        else if (clamp == "60")
            SetVsync(isXbox, true);
    }

    private static string MapAnisotropyFromIni(string raw)
    {
        if (raw == "0") return "None";
        if (raw == "4") return "4x";
        if (raw == "8") return "8x";
        if (raw == "16") return "16x";
        return $"{raw}x";
    }

    private void SetGodrays(bool xbox, bool v) { if (xbox) xboxGodrays = v; else steamGodrays = v; }
    private void SetGrass(bool xbox, bool v) { if (xbox) xboxGrass = v; else steamGrass = v; }
    private void SetRenderGrass(bool xbox, bool v) { if (xbox) xboxRenderGrass = v; else steamRenderGrass = v; }
    private void SetShadows(bool xbox, string v) { if (xbox) xboxShadows = v; else steamShadows = v; }
    private void SetMotionBlur(bool xbox, bool v) { if (xbox) xboxDof = v; else steamDof = v; }
    private void SetDofSpecific(bool xbox, bool v) { if (xbox) xboxDofSpecific = v; else steamDofSpecific = v; }
    private void SetLensFlare(bool xbox, bool v) { if (xbox) xboxLensFlare = v; else steamLensFlare = v; }
    private void SetExtraBlur(bool xbox, bool v) { if (xbox) xboxExtraBlur = v; else steamExtraBlur = v; }
    private void SetVatsBlur(bool xbox, bool v) { if (xbox) xboxVatsBlur = v; else steamVatsBlur = v; }
    private void SetTaa(bool xbox, string v) { if (xbox) xboxTaa = v; else steamTaa = v; }
    private void SetAniso(bool xbox, string v) { if (xbox) xboxAniso = v; else steamAniso = v; }
    private void SetFov(bool xbox, int v) { if (xbox) xboxFov = v; else steamFov = v; }
    private void SetFov1st(bool xbox, int v) { if (xbox) xboxFov1st = v; else steamFov1st = v; }
    private void SetFovPipboy(bool xbox, int v) { if (xbox) xboxFovPipboy = v; else steamFovPipboy = v; }
    private void SetFastload(bool xbox, bool v) { if (xbox) xboxFastload = v; else steamFastload = v; }
    private void SetSkipSplash(bool xbox, bool v) { if (xbox) xboxSkipSplash = v; else steamSkipSplash = v; }
    private void SetPing(bool xbox, bool v) { if (xbox) xboxPing = v; else steamPing = v; }
    private void SetBandwidth(bool xbox, bool v) { if (xbox) xboxBandwidth = v; else steamBandwidth = v; }
    private void SetVsync(bool xbox, bool v) { if (xbox) xboxVsync = v; else steamVsync = v; }
    private void SetFpsCap(bool xbox, int v) { if (xbox) xboxFpsCap = v; else steamFpsCap = v; }
    private void SetAo(bool xbox, bool v) { if (xbox) xboxAo = v; else steamAo = v; }
    private void SetBlood(bool xbox, bool v) { if (xbox) xboxBlood = v; else steamBlood = v; }
    private void SetLod(bool xbox, int v) { if (xbox) xboxLod = v; else steamLod = v; }
    private void SetDecals(bool xbox, string v) { if (xbox) xboxDecals = v; else steamDecals = v; }
    private void SetWater(bool xbox, string v) { if (xbox) xboxWater = v; else steamWater = v; }
    private void SetSsr(bool xbox, bool v) { if (xbox) xboxSsr = v; else steamSsr = v; }
    private void SetRainOcclusion(bool xbox, bool v) { if (xbox) xboxRainOcclusion = v; else steamRainOcclusion = v; }
    private void SetNpcShadowLights(bool xbox, bool v) { if (xbox) xboxNpcShadowLights = v; else steamNpcShadowLights = v; }
    private void SetCellLoads(bool xbox, bool v) { if (xbox) xboxCellLoads = v; else steamCellLoads = v; }
    private void SetTiledLighting(bool xbox, bool v) { if (xbox) xboxTiledLighting = v; else steamTiledLighting = v; }
    private void SetFocusShadows(bool xbox, bool v) { if (xbox) xboxFocusShadows = v; else steamFocusShadows = v; }
    private void SetGlassShader(bool xbox, bool v) { if (xbox) xboxGlassShader = v; else steamGlassShader = v; }
    private void SetPbrShadows(bool xbox, bool v) { if (xbox) xboxPbrShadows = v; else steamPbrShadows = v; }
    private void SetPlayerNames(bool xbox, bool v) { if (xbox) xboxPlayerNames = v; else steamPlayerNames = v; }
    private void SetPlayerPings(bool xbox, bool v) { if (xbox) xboxPlayerPings = v; else steamPlayerPings = v; }
    private void SetConversationHistory(bool xbox, int v) { if (xbox) xboxConversationHistory = v; else steamConversationHistory = v; }
    private void SetPipboyFx(bool xbox, bool v) { if (xbox) xboxPipboyFx = v; else steamPipboyFx = v; }

    private static readonly string[] PipboyCrtIniKeys =
    {
        "bPipboyDisableFX",
        "fPipboyScreenEmitIntensity",
        "fPipboyScreenDiffuseIntensity",
        "bPipboyEffectColorOnly"
    };

    private IEnumerable<(string docs, string prefix, bool crtEnabled)> EnumeratePlatformIniTargets()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(steamDocsPath))
        {
            string docs = steamDocsPath.Trim();
            string key = docs + "|Fallout76";
            if (seen.Add(key))
                yield return (docs, "Fallout76", steamPipboyFx);
        }
        if (!string.IsNullOrWhiteSpace(xboxDocsPath))
        {
            string docs = xboxDocsPath.Trim();
            string key = docs + "|Project76";
            if (seen.Add(key))
                yield return (docs, "Project76", xboxPipboyFx);
        }
    }

    private void ScrubPipboyCrtKeysFromPrefs(string docsPath, string iniPrefix)
    {
        if (string.IsNullOrWhiteSpace(docsPath)) return;
        string prefsPath = Path.Combine(docsPath, $"{iniPrefix}Prefs.ini");
        foreach (string key in PipboyCrtIniKeys)
            _configManager.RemoveKey(prefsPath, "Pipboy", key);
    }

    private void ApplyPipboyCrtToCustomIni(string docsPath, string iniPrefix, bool crtEffectEnabled)
    {
        if (string.IsNullOrWhiteSpace(docsPath)) return;
        if (!Directory.Exists(docsPath))
            Directory.CreateDirectory(docsPath);

        string customPath = Path.Combine(docsPath, $"{iniPrefix}Custom.ini");
        if (!crtEffectEnabled)
        {
            _configManager.UpdateBothInis("Pipboy", "bPipboyDisableFX", "1", docsPath, onlyCustom: true, overridePrefix: iniPrefix);
            _configManager.UpdateBothInis("Pipboy", "fPipboyScreenEmitIntensity", "1.25", docsPath, onlyCustom: true, overridePrefix: iniPrefix);
            _configManager.UpdateBothInis("Pipboy", "fPipboyScreenDiffuseIntensity", "0.15", docsPath, onlyCustom: true, overridePrefix: iniPrefix);
            _configManager.UpdateBothInis("Pipboy", "bPipboyEffectColorOnly", "1", docsPath, onlyCustom: true, overridePrefix: iniPrefix);
        }
        else
        {
            _configManager.UpdateBothInis("Pipboy", "bPipboyDisableFX", "0", docsPath, onlyCustom: true, overridePrefix: iniPrefix);
            foreach (string key in PipboyCrtIniKeys)
            {
                if (key == "bPipboyDisableFX") continue;
                _configManager.RemoveKey(customPath, "Pipboy", key);
            }
        }
    }

    private void EnsurePipboyPrefsIniScrubMigration()
    {
        if (pipboyPrefsIniScrubV1) return;

        int scrubbed = 0;
        foreach (var (docs, prefix, crtEnabled) in EnumeratePlatformIniTargets())
        {
            ScrubPipboyCrtKeysFromPrefs(docs, prefix);
            ApplyPipboyCrtToCustomIni(docs, prefix, crtEnabled);
            scrubbed++;
        }

        pipboyPrefsIniScrubV1 = true;
        SaveSettings();
        LogActivity($"[MIGRATION] Moved Pip-Boy CRT settings to Custom.ini only ({scrubbed} profile path(s); removed from Prefs.ini).");
    }

    private void EnsurePipboyCrtOnDefaultMigration()
    {
        if (pipboyCrtOnDefaultV2) return;

        if (pipboyCrtUserConfigured)
        {
            pipboyCrtOnDefaultV2 = true;
            SaveSettings();
            return;
        }

        steamPipboyFx = true;
        xboxPipboyFx = true;

        int applied = 0;
        foreach (var (docs, prefix, _) in EnumeratePlatformIniTargets())
        {
            ScrubPipboyCrtKeysFromPrefs(docs, prefix);
            ApplyPipboyCrtToCustomIni(docs, prefix, crtEffectEnabled: true);
            applied++;
        }

        pipboyCrtOnDefaultV2 = true;
        pipboyCrtDefaultMigrationV1 = true;
        SaveSettings();
        LogActivity($"[MIGRATION] Pip-Boy CRT Effect set to on by default ({applied} INI location(s) updated).");
    }

    private void MarkPipboyCrtUserConfigured()
    {
        if (pipboyCrtUserConfigured) return;
        pipboyCrtUserConfigured = true;
    }

    private static readonly string[] PrefsArchiveKeys =
    {
        "sResourceArchive2List",
        "sResourceIndexFileList"
    };

    private void EnsureGameIntegrityRepairMigration()
    {
        if (gameIntegrityRepairV1) return;

        int prefsScrubbed = 0;
        foreach (var (docs, prefix, _) in EnumeratePlatformIniTargets())
        {
            if (string.IsNullOrWhiteSpace(docs)) continue;
            string prefsPath = Path.Combine(docs, $"{prefix}Prefs.ini");
            foreach (string key in PrefsArchiveKeys)
                _configManager.RemoveKey(prefsPath, "Archive", key);
            prefsScrubbed++;
        }

        int dataFoldersRemoved = 0;
        foreach (string root in new[] { gamePath, steamGamePath, xboxGamePath })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            string coreIniDir = Path.Combine(root, "Data", "CoreIni");
            if (!Directory.Exists(coreIniDir)) continue;
            try
            {
                Directory.Delete(coreIniDir, recursive: true);
                dataFoldersRemoved++;
                LogActivity($"[MIGRATION] Removed invalid game Data folder: {coreIniDir}");
            }
            catch (Exception ex)
            {
                LogError($"[MIGRATION] Failed to remove {coreIniDir}: {ex.Message}");
            }
        }

        gameIntegrityRepairV1 = true;
        SaveSettings();
        LogActivity($"[MIGRATION] Game integrity repair complete (Prefs archive scrub: {prefsScrubbed}; Data/CoreIni removed: {dataFoldersRemoved}).");
    }
}
