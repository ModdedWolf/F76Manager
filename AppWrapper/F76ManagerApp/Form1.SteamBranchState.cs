using System.Text.Json;
using F76ManagerApp.Managers;

namespace F76ManagerApp;

public partial class Form1
{
    private static readonly string[] SteamIniSnapshotFileNames =
    {
        "Fallout76Custom.ini",
        "Fallout76Prefs.ini",
        "Fallout76.ini",
    };

    private void SaveIniSnapshot(bool pts)
    {
        if (string.IsNullOrWhiteSpace(documentsPath) || !Directory.Exists(documentsPath))
            return;

        string snapDir = AppPaths.IniSnapshotFolder(pts);
        Directory.CreateDirectory(snapDir);

        foreach (string name in SteamIniSnapshotFileNames)
        {
            string src = Path.Combine(documentsPath, name);
            if (!File.Exists(src)) continue;
            string dest = Path.Combine(snapDir, name);
            try
            {
                ClearReadOnlyFlag(src);
                File.Copy(src, dest, overwrite: true);
            }
            catch (Exception ex)
            {
                LogError($"[BRANCH] Failed to snapshot {name} for {(pts ? "PTS" : "Live")}: {ex.Message}");
            }
        }
    }

    private void RestoreIniSnapshot(bool pts)
    {
        if (string.IsNullOrWhiteSpace(documentsPath))
            return;

        Directory.CreateDirectory(documentsPath);
        string snapDir = AppPaths.IniSnapshotFolder(pts);
        if (!Directory.Exists(snapDir))
            return;

        foreach (string name in SteamIniSnapshotFileNames)
        {
            string src = Path.Combine(snapDir, name);
            if (!File.Exists(src)) continue;
            string dest = Path.Combine(documentsPath, name);
            try
            {
                if (File.Exists(dest))
                    ClearReadOnlyFlag(dest);
                File.Copy(src, dest, overwrite: true);
                ClearReadOnlyFlag(dest);
            }
            catch (Exception ex)
            {
                LogError($"[BRANCH] Failed to restore {name} for {(pts ? "PTS" : "Live")}: {ex.Message}");
                throw;
            }
        }
    }

    private static void ClearReadOnlyFlag(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
        }
    }

    private bool SeedPtsFromLiveIfNeeded()
    {
        if (File.Exists(AppPaths.PtsModsMetadataFile))
            return false;

        LogActivity("[BRANCH] First PTS switch: seeding PTS state from Live (mods metadata, overlays, INIs, presets).");

        try
        {
            Directory.CreateDirectory(AppPaths.SettingsFolder);
            Directory.CreateDirectory(AppPaths.PtsRootFolder);
            Directory.CreateDirectory(AppPaths.PtsDisabledModsPath);
            Directory.CreateDirectory(AppPaths.PtsBundlesPath);
            Directory.CreateDirectory(AppPaths.PtsIniOverlaysFolder);
            Directory.CreateDirectory(AppPaths.IniSnapshotFolder(true));
            Directory.CreateDirectory(AppPaths.IniSnapshotFolder(false));

            SeedPtsModsMetadataFromLive();
            SeedPtsIniOverlaysFromLive();

            SaveIniSnapshot(false);
            SaveIniSnapshot(true);

            SeedPtsModPresetsFromLive();
            return true;
        }
        catch (Exception ex)
        {
            LogError($"[BRANCH] PTS seed from Live failed: {ex.Message}");
            throw;
        }
    }

    private void SeedPtsModsMetadataFromLive()
    {
        string livePath = AppPaths.LiveModsMetadataFile;
        if (!File.Exists(livePath))
        {
            File.WriteAllText(AppPaths.PtsModsMetadataFile, "{}");
            return;
        }

        try
        {
            string json = File.ReadAllText(livePath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ModManager.ModMetadata>>(json, options)
                         ?? new Dictionary<string, ModManager.ModMetadata>(StringComparer.OrdinalIgnoreCase);

            var pruned = new Dictionary<string, ModManager.ModMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in loaded)
            {
                string key = (kv.Key ?? "").Replace('\\', '/').Trim();
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (key.StartsWith("Bundles/", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.StartsWith("Loose/", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase)) continue;
                pruned[key] = kv.Value;
            }

            string outJson = JsonSerializer.Serialize(pruned, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.PtsModsMetadataFile, outJson);
            LogActivity($"[BRANCH] Seeded mods-pts.json with {pruned.Count} entr(y/ies) (pruned Bundles/Loose/Disabled).");
        }
        catch (Exception ex)
        {
            LogError($"[BRANCH] Failed seeding mods-pts.json: {ex.Message}");
            File.WriteAllText(AppPaths.PtsModsMetadataFile, "{}");
        }
    }

    private void SeedPtsIniOverlaysFromLive()
    {
        string liveOverlays = AppPaths.LiveIniOverlaysFolder;
        string ptsOverlays = AppPaths.PtsIniOverlaysFolder;
        Directory.CreateDirectory(ptsOverlays);
        if (!Directory.Exists(liveOverlays)) return;

        foreach (string src in Directory.GetFiles(liveOverlays))
        {
            string dest = Path.Combine(ptsOverlays, Path.GetFileName(src));
            try
            {
                File.Copy(src, dest, overwrite: true);
            }
            catch (Exception ex)
            {
                LogError($"[BRANCH] Failed copying overlay {Path.GetFileName(src)}: {ex.Message}");
            }
        }
    }

    private void SeedPtsModPresetsFromLive()
    {
        bool liveSlotActive = !ActivePresetSlotIsPts && !string.IsNullOrWhiteSpace(modPresetsJson);
        string livePresets = liveSlotActive
            ? modPresetsJson
            : (!string.IsNullOrWhiteSpace(liveModPresetsJson) ? liveModPresetsJson : modPresetsJson);
        string liveActive = liveSlotActive
            ? activeModPreset
            : (!string.IsNullOrWhiteSpace(liveActiveModPreset) ? liveActiveModPreset : activeModPreset);

        ptsModPresetsJson = string.IsNullOrWhiteSpace(livePresets)
            ? BuildDefaultModPresetsJson()
            : livePresets;
        ptsActiveModPreset = string.IsNullOrWhiteSpace(liveActive)
            ? DefaultModPresetName
            : liveActive;

        var ptsKeys = EnumeratePtsDataModKeys();
        if (ptsKeys.Count == 0) return;

        string previousPresets = modPresetsJson;
        string previousActive = activeModPreset;
        try
        {
            modPresetsJson = ptsModPresetsJson;
            activeModPreset = ptsActiveModPreset;
            AddImportedKeysToActivePreset(ptsKeys, markEnabled: true, persist: false);
            ptsModPresetsJson = modPresetsJson;
            ptsActiveModPreset = activeModPreset;
            LogActivity($"[BRANCH] Added {ptsKeys.Count} existing PTS Data mod(s) to the seeded PTS preset.");
        }
        finally
        {
            modPresetsJson = previousPresets;
            activeModPreset = previousActive;
        }
    }

    private List<string> EnumeratePtsDataModKeys()
    {
        var keys = new List<string>();
        try
        {
            string gp = !string.IsNullOrWhiteSpace(ptsGamePath) ? ptsGamePath : _platformManager.GetDefaultPtsGamePath();
            if (string.IsNullOrWhiteSpace(gp) || !Directory.Exists(gp)) return keys;

            string data = gp.EndsWith("Data", StringComparison.OrdinalIgnoreCase)
                ? gp
                : Path.Combine(gp, "Data");
            if (!Directory.Exists(data)) return keys;

            foreach (string file in Directory.GetFiles(data, "*.*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (_modManager != null && _modManager.IsVanillaModFile(name)) continue;
                string ext = Path.GetExtension(name);
                if (ext.Equals(".ba2", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".toml", StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(name);
                }
            }

            foreach (string file in Directory.GetFiles(data, "*.*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(data, file).Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(rel) || !rel.Contains('/')) continue;
                string top = rel.Split('/')[0];
                if (top.Equals("Strings", StringComparison.OrdinalIgnoreCase) ||
                    top.Equals("Terrain", StringComparison.OrdinalIgnoreCase) ||
                    top.Equals("Video", StringComparison.OrdinalIgnoreCase) ||
                    top.StartsWith("FO76Edit", StringComparison.OrdinalIgnoreCase))
                    continue;

                string ext = Path.GetExtension(rel);
                if (ext.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".toml", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".ba2", StringComparison.OrdinalIgnoreCase))
                {
                    if (!keys.Contains(rel, StringComparer.OrdinalIgnoreCase))
                        keys.Add(rel);
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"[BRANCH] EnumeratePtsDataModKeys failed: {ex.Message}");
        }
        return keys;
    }

    private bool ActivePresetSlotIsPts =>
        _platformManager.CurrentPlatform == GamePlatform.Steam && _platformManager.IsPts;

    private void AddKeysToPresetSlot(bool pts, IReadOnlyCollection<string> keys)
    {
        if (keys == null || keys.Count == 0) return;
        if (pts == ActivePresetSlotIsPts)
        {
            AddImportedKeysToActivePreset(keys, markEnabled: true);
            return;
        }

        string previousPresets = modPresetsJson;
        string previousActive = activeModPreset;
        try
        {
            string stashed = pts ? ptsModPresetsJson : liveModPresetsJson;
            string stashedActive = pts ? ptsActiveModPreset : liveActiveModPreset;
            modPresetsJson = string.IsNullOrWhiteSpace(stashed) ? BuildDefaultModPresetsJson() : stashed;
            activeModPreset = string.IsNullOrWhiteSpace(stashedActive) ? DefaultModPresetName : stashedActive;
            AddImportedKeysToActivePreset(keys, markEnabled: true, persist: false);
            if (pts)
            {
                ptsModPresetsJson = modPresetsJson;
                ptsActiveModPreset = activeModPreset;
            }
            else
            {
                liveModPresetsJson = modPresetsJson;
                liveActiveModPreset = activeModPreset;
            }
        }
        finally
        {
            modPresetsJson = previousPresets;
            activeModPreset = previousActive;
        }
        SaveSettings();
    }

    private void EnsurePtsSeededForTransfer()
    {
        if (File.Exists(AppPaths.PtsModsMetadataFile)) return;

        string prevDocs = documentsPath;
        try
        {
            if (_platformManager.CurrentPlatform != GamePlatform.Steam)
            {
                documentsPath = !string.IsNullOrWhiteSpace(steamDocsPath)
                    ? steamDocsPath
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Fallout 76");
            }
            SeedPtsFromLiveIfNeeded();
        }
        finally
        {
            documentsPath = prevDocs;
        }
    }

    private void StashActiveModPresetsForBranch(bool pts)
    {
        EnsureModPresetsInitialized();
        if (pts)
        {
            ptsModPresetsJson = modPresetsJson;
            ptsActiveModPreset = activeModPreset;
        }
        else
        {
            liveModPresetsJson = modPresetsJson;
            liveActiveModPreset = activeModPreset;
        }
    }

    private void LoadModPresetsForBranch(bool pts)
    {
        if (pts)
        {
            modPresetsJson = string.IsNullOrWhiteSpace(ptsModPresetsJson)
                ? BuildDefaultModPresetsJson()
                : ptsModPresetsJson;
            activeModPreset = string.IsNullOrWhiteSpace(ptsActiveModPreset)
                ? DefaultModPresetName
                : ptsActiveModPreset;
        }
        else
        {
            modPresetsJson = string.IsNullOrWhiteSpace(liveModPresetsJson)
                ? (string.IsNullOrWhiteSpace(modPresetsJson) ? BuildDefaultModPresetsJson() : modPresetsJson)
                : liveModPresetsJson;
            activeModPreset = string.IsNullOrWhiteSpace(liveActiveModPreset)
                ? DefaultModPresetName
                : liveActiveModPreset;
        }
        EnsureModPresetsInitialized();
    }

    private string BuildDefaultModPresetsJson()
    {
        try
        {
            return JsonSerializer.Serialize(BuildDefaultModPresetsObject());
        }
        catch
        {
            return "{\"Default\":{\"Mods\":[],\"Enabled\":[]}}";
        }
    }
}
