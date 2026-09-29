using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace F76ManagerApp.Managers
{
    public class GameConfigManager
    {
        private Action<string> _logger;
        private Action<string, string> _statusReporter;

        public GameConfigManager(Action<string> logger = null, Action<string, string> statusReporter = null)
        {
            _logger = logger ?? ((s) => { });
            _statusReporter = statusReporter ?? ((t, m) => { });
        }

        public void UpdateBothInis(string section, string key, string value, string overrideDocsPath = null, bool onlyCustom = false, string overridePrefix = null)
        {
            string targetDocs = !string.IsNullOrEmpty(overrideDocsPath) ? overrideDocsPath : AppPaths.DocumentsPath;
            string prefix = overridePrefix ?? AppPaths.IniPrefix;
            string customPath = Path.Combine(targetDocs, $"{prefix}Custom.ini");
            string prefsPath = Path.Combine(targetDocs, $"{prefix}Prefs.ini");

            Log($"[INI] Applying changes to {prefix}Custom.ini...");

            try {
                if (!Directory.Exists(targetDocs)) {
                    Log($"[INI-WRITE] Target directory not found. Creating: {targetDocs}");
                    Directory.CreateDirectory(targetDocs);
                }
            } catch (Exception ex) {
                LogError($"Failed to create Documents directory: {ex.Message}");
            }

            if (Directory.Exists(targetDocs))
            {
                UpdateSingleIni(customPath, section, key, value);
                
                if (!onlyCustom)
                {
                    UpdateSingleIni(prefsPath, section, key, value);
                }
            }
            else
            {
                LogError($"Cannot update INIs: Documents directory not found at {targetDocs}");
            }
        }

        public bool UpdateCustomIniKeyIfExists(string section, string key, string value, string overrideDocsPath = null, string overridePrefix = null)
        {
            string targetDocs = !string.IsNullOrEmpty(overrideDocsPath) ? overrideDocsPath : AppPaths.DocumentsPath;
            string prefix = overridePrefix ?? AppPaths.IniPrefix;
            string customPath = Path.Combine(targetDocs, $"{prefix}Custom.ini");
            if (!File.Exists(customPath)) return false;

            try
            {
                var lines = File.ReadAllLines(customPath).ToList();
                if (!TryUpdateExistingIniKey(lines, section, key, value))
                    return false;
                ClearReadOnly(customPath);
                File.WriteAllLines(customPath, lines, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Failed to update existing INI key {key} in {Path.GetFileName(customPath)}: {ex.Message}");
                return false;
            }
        }

        public static void WriteIniTextNoBom(string path, string content)
        {
            string text = (content ?? "").TrimStart('\uFEFF');
            text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            if (!text.EndsWith("\r\n", StringComparison.Ordinal))
                text += "\r\n";
            ClearReadOnlyAttribute(path);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        public void StripIniBomIfPresent(string overrideDocsPath = null, string overridePrefix = null)
        {
            string targetDocs = !string.IsNullOrEmpty(overrideDocsPath) ? overrideDocsPath : AppPaths.DocumentsPath;
            string prefix = overridePrefix ?? AppPaths.IniPrefix;
            StripBomFromFile(Path.Combine(targetDocs, $"{prefix}Custom.ini"));
            StripBomFromFile(Path.Combine(targetDocs, $"{prefix}Prefs.ini"));
        }

        private void StripBomFromFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;

                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF)
                    return;

                ClearReadOnly(path);
                var rest = new byte[bytes.Length - 3];
                Buffer.BlockCopy(bytes, 3, rest, 0, rest.Length);
                File.WriteAllBytes(path, rest);
                Log($"[INI] Stripped UTF-8 BOM from {Path.GetFileName(path)} so the game can read the first section.");
            }
            catch (Exception ex)
            {
                LogError($"Failed to strip INI BOM from {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        public void EnsureCustomFovStable(string preferredValue = null, string overrideDocsPath = null, string overridePrefix = null)
        {
            string targetDocs = !string.IsNullOrEmpty(overrideDocsPath) ? overrideDocsPath : AppPaths.DocumentsPath;
            string prefix = overridePrefix ?? AppPaths.IniPrefix;
            StripIniBomIfPresent(targetDocs, prefix);

            string customPath = Path.Combine(targetDocs, $"{prefix}Custom.ini");
            if (!File.Exists(customPath)) return;

            try
            {
                string world = ReadIniValue(customPath, "Display", "fDefaultWorldFOV");
                string first = ReadIniValue(customPath, "Display", "fDefault1stPersonFOV");
                string defaultFov = ReadIniValue(customPath, "Display", "fDefaultFOV");
                bool hasWorld = !string.IsNullOrWhiteSpace(world);
                bool hasFirst = !string.IsNullOrWhiteSpace(first);
                bool hasDefaultFov = !string.IsNullOrWhiteSpace(defaultFov);
                if (!hasWorld && !hasFirst && !hasDefaultFov) return;

                string tpSource = hasWorld ? world! : (hasDefaultFov ? defaultFov! : null);
                string fpSource = hasFirst ? first! : (hasDefaultFov ? defaultFov! : null);

                float ceiling = 0f;
                if (TryParseFovFloat(tpSource, out float tpF)) ceiling = Math.Max(ceiling, tpF);
                if (TryParseFovFloat(fpSource, out float fpF)) ceiling = Math.Max(ceiling, fpF);
                if (ceiling > 120f)
                    EnsureWorldFovMaxAtLeast(customPath, ceiling);

                if (!string.IsNullOrWhiteSpace(tpSource))
                    WritePrefsCameraFovIfDifferent(targetDocs, prefix, "fTPWorldFOV", tpSource);
                if (!string.IsNullOrWhiteSpace(fpSource))
                    WritePrefsCameraFovIfDifferent(targetDocs, prefix, "fFPWorldFOV", fpSource);
            }
            catch (Exception ex)
            {
                LogError($"Failed to align Prefs Camera FOV with Custom.ini: {ex.Message}");
            }
        }

        private void EnsureWorldFovMaxAtLeast(string customPath, float requiredMax)
        {
            string formatted = requiredMax.ToString("0.0000", CultureInfo.InvariantCulture);
            string existing = ReadIniValue(customPath, "Display", "fWorldFOVMax");
            if (!string.IsNullOrWhiteSpace(existing)
                && float.TryParse(existing, NumberStyles.Float, CultureInfo.InvariantCulture, out float existingMax)
                && existingMax + 0.01f >= requiredMax)
            {
                return;
            }

            UpdateSingleIni(customPath, "Display", "fWorldFOVMax", formatted);
            Log($"[FOV] Custom.ini [Display] fWorldFOVMax={formatted} (raised for FOV above 120).");
        }

        private void WritePrefsCameraFovIfDifferent(string docsPath, string prefix, string cameraKey, string rawValue)
        {
            string prefsPath = Path.Combine(docsPath, $"{prefix}Prefs.ini");
            string formatted = FormatPrefsFov(rawValue);
            string existing = ReadIniValue(prefsPath, "Camera", cameraKey);
            if (!string.IsNullOrWhiteSpace(existing)
                && float.TryParse(existing, NumberStyles.Float, CultureInfo.InvariantCulture, out float existingF)
                && float.TryParse(formatted, NumberStyles.Float, CultureInfo.InvariantCulture, out float nextF)
                && Math.Abs(existingF - nextF) < 0.01f)
            {
                return;
            }

            UpdateSingleIni(prefsPath, "Camera", cameraKey, formatted);
            Log($"[FOV] Prefs [Camera] {cameraKey}={formatted} (aligned to Custom.ini override).");
        }

        private static string FormatPrefsFov(string raw)
        {
            string t = (raw ?? "").Trim();
            if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                return f.ToString("0.0000", CultureInfo.InvariantCulture);
            return t;
        }

        private static bool TryParseFovFloat(string raw, out float fov)
        {
            fov = 0f;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out fov);
        }

        private bool TryUpdateExistingIniKey(List<string> lines, string section, string key, string value)
        {
            string sectionHeader = $"[{section}]";
            int sectionStartIndex = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
                {
                    sectionStartIndex = i;
                    break;
                }
            }

            if (sectionStartIndex == -1) return false;

            for (int i = sectionStartIndex + 1; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    break;

                var parts = trimmed.Split('=', 2);
                if (parts.Length >= 1 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{key}={value}";
                    _logger?.Invoke($"[INI-WRITE] Updated existing: [{section}] {key} = {value}");
                    return true;
                }
            }

            return false;
        }

        public void UpdatePrefsIni(string section, string key, string value, string overrideDocsPath = null, string overridePrefix = null)
        {
            string targetDocs = !string.IsNullOrEmpty(overrideDocsPath) ? overrideDocsPath : AppPaths.DocumentsPath;
            string prefix = overridePrefix ?? AppPaths.IniPrefix;
            string prefsPath = Path.Combine(targetDocs, $"{prefix}Prefs.ini");

            Log($"[INI] Updating Preferences ({prefix}Prefs.ini)...");

            if (Directory.Exists(targetDocs))
            {
                UpdateSingleIni(prefsPath, section, key, value);
            }
            else
            {
                LogError($"Cannot update Prefs INI: Documents directory not found at {targetDocs}");
            }
        }

        private void UpdateSingleIni(string path, string section, string key, string value)
        {
            try
            {
                var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
                UpdateIniKey(lines, section, key, value);
                if (lines.Count > 0)
                {
                    ClearReadOnly(path);
                    File.WriteAllLines(path, lines, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                LogError($"Failed to update INI {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        public void UpdateIniKey(List<string> lines, string section, string key, string value)
        {
            string sectionHeader = $"[{section}]";
            int sectionStartIndex = -1;
            int sectionEndIndex = -1;

            for (int i = 0; i < lines.Count; i++) {
                if (lines[i].Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase)) {
                    sectionStartIndex = i;
                    break;
                }
            }

            if (sectionStartIndex != -1) {
                for (int i = sectionStartIndex + 1; i < lines.Count; i++) {
                    string trimmed = lines[i].Trim();
                    
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]")) {
                        sectionEndIndex = i;
                        break;
                    }

                    var parts = trimmed.Split('=', 2);
                    if (parts.Length >= 1 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                        lines[i] = $"{key}={value}";
                        _logger?.Invoke($"[INI-WRITE] Updated: [{section}] {key} = {value}");
                        return;
                    }
                }

                if (sectionEndIndex != -1) {
                    lines.Insert(sectionEndIndex, $"{key}={value}");
                } else {
                    lines.Add($"{key}={value}");
                }
                _logger?.Invoke($"[INI-WRITE] Added: [{section}] {key} = {value}");
            } else {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines.Last())) lines.Add("");
                lines.Add(sectionHeader);
                lines.Add($"{key}={value}");
                _logger?.Invoke($"[INI] Settings updated: [{section}] {key}");
            }
        }

        public string ReadIniValue(string path, string section, string key)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path);
                string sectionHeader = $"[{section}]";
                bool inSection = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].Trim();

                    if (trimmed.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        inSection = true;
                        continue;
                    }

                    if (inSection)
                    {
                        if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                            break;

                        var parts = trimmed.Split('=', 2);
                        if (parts.Length == 2 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                        {
                            return parts[1].Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[INI-READ] Failed to read {key} from [{section}] in {Path.GetFileName(path)}: {ex.Message}");
            }
            return null;
        }

        public string? ReadMergedIniValue(string section, string key, string? docsPath = null, string? prefix = null)
        {
            string targetDocs = !string.IsNullOrEmpty(docsPath) ? docsPath : AppPaths.DocumentsPath;
            string iniPrefix = prefix ?? AppPaths.IniPrefix;
            string customPath = Path.Combine(targetDocs, $"{iniPrefix}Custom.ini");
            string prefsPath = Path.Combine(targetDocs, $"{iniPrefix}Prefs.ini");

            string? customVal = ReadIniValue(customPath, section, key);
            if (!string.IsNullOrEmpty(customVal))
                return customVal;

            return ReadIniValue(prefsPath, section, key);
        }

        public void RemoveKey(string path, string section, string key)
        {
            try
            {
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path).ToList();
                string sectionHeader = $"[{section}]";
                int sectionStartIndex = -1;
                bool modified = false;

                for (int i = 0; i < lines.Count; i++) {
                    if (lines[i].Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase)) {
                        sectionStartIndex = i;
                        break;
                    }
                }

                if (sectionStartIndex != -1) {
                    for (int i = sectionStartIndex + 1; i < lines.Count; i++) {
                        string trimmed = lines[i].Trim();
                        if (trimmed.StartsWith("[") && trimmed.EndsWith("]")) break;

                        var parts = trimmed.Split('=', 2);
                        if (parts.Length >= 1 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                            lines.RemoveAt(i);
                            i--;
                            modified = true;
                            _logger?.Invoke($"[INI-SCRUB] Removed key '{key}' from [{section}] in {Path.GetFileName(path)}");
                        }
                    }
                }

                if (modified) {
                    ClearReadOnly(path);
                    File.WriteAllLines(path, lines, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                LogError($"Failed to remove key '{key}' from [{section}] in {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        public void EnsurePrefsIniWritable(string? documentsFolder = null, string? overridePrefix = null)
        {
            string targetDocs = !string.IsNullOrEmpty(documentsFolder) ? documentsFolder : AppPaths.DocumentsPath;
            if (string.IsNullOrWhiteSpace(targetDocs) || !Directory.Exists(targetDocs))
                return;

            string prefix = overridePrefix ?? AppPaths.IniPrefix;
            string prefsPath = Path.Combine(targetDocs, $"{prefix}Prefs.ini");
            ClearReadOnly(prefsPath, logWhenCleared: true);
        }

        public static void EnsureF76AddToOverlayStubs()
        {
            try
            {
                if (!Directory.Exists(AppPaths.SettingsFolder))
                    Directory.CreateDirectory(AppPaths.SettingsFolder);
                if (!Directory.Exists(AppPaths.IniOverlaysFolder))
                    Directory.CreateDirectory(AppPaths.IniOverlaysFolder);

                AppPaths.MigrateLegacyF76AddToOverlayFiles();

                EnsureOverlayStub(
                    AppPaths.F76AddToCustomFileName,
                    "Custom.ini",
                    "[Display]\r\n; fDefaultWorldFOV=135");
                EnsureOverlayStub(
                    AppPaths.F76AddToPrefsFileName,
                    "Prefs.ini",
                    null);
                EnsureOverlayStub(
                    AppPaths.F76AddToFallout76FileName,
                    "Fallout76.ini / Project76.ini",
                    null);
            }
            catch
            {
            }
        }

        private static void EnsureOverlayStub(string fileName, string targetLabel, string? exampleBlock)
        {
            string path = AppPaths.GetF76AddToOverlayPath(fileName);
            string header = $"; F76 Manager overlay — keys here are applied to {targetLabel} when you Save (Ctrl+S).";
            if (File.Exists(path))
            {
                RefreshLegacyOverlayHeader(path, header);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(header);
            sb.AppendLine("; Blank lines and ; / # comments are ignored.");
            sb.AppendLine("; Archive load-list keys (sResource*List) are skipped so Deploy keeps BA2 ownership.");
            if (!string.IsNullOrWhiteSpace(exampleBlock))
            {
                sb.AppendLine(";");
                sb.AppendLine("; Example:");
                foreach (var line in exampleBlock.Replace("\r\n", "\n").Split('\n'))
                    sb.AppendLine($"; {line.TrimStart(';').Trim()}".TrimEnd());
            }
            sb.AppendLine();
            WriteIniTextNoBom(path, sb.ToString());
        }

        private static void RefreshLegacyOverlayHeader(string path, string header)
        {
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length == 0) return;
                string first = lines[0];
                if (!first.StartsWith("; F76 Manager overlay", StringComparison.Ordinal)) return;
                if (!first.Contains("on Deploy", StringComparison.Ordinal)) return;
                lines[0] = header;
                WriteIniTextNoBom(path, string.Join("\n", lines) + "\n");
            }
            catch
            {
            }
        }

        public int ApplyF76AddToOverlay(string overlayFileName, out string? targetFileName)
        {
            targetFileName = null;
            try
            {
                if (string.IsNullOrWhiteSpace(AppPaths.DocumentsPath))
                {
                    Log("[F76-Manager-AddTo] Skipped: Documents path not set.");
                    return 0;
                }

                if (!Directory.Exists(AppPaths.DocumentsPath))
                {
                    try { Directory.CreateDirectory(AppPaths.DocumentsPath); }
                    catch (Exception ex)
                    {
                        LogError($"[F76-Manager-AddTo] Cannot create Documents folder: {ex.Message}");
                        return 0;
                    }
                }

                string name = Path.GetFileName(overlayFileName ?? "");
                string? targetPath = AppPaths.GetF76AddToTargetPath(name);
                if (string.IsNullOrWhiteSpace(targetPath))
                    return 0;

                targetFileName = Path.GetFileName(targetPath);
                string overlayPath = AppPaths.GetF76AddToOverlayPath(name);
                return ApplyOverlayFile(overlayPath, targetPath);
            }
            catch (Exception ex)
            {
                LogError($"[F76-Manager-AddTo] Failed: {ex.Message}");
                return 0;
            }
        }

        private int ApplyOverlayFile(string overlayPath, string targetPath)
        {
            if (!File.Exists(overlayPath)) return 0;

            string text;
            try { text = File.ReadAllText(overlayPath); }
            catch { return 0; }

            var entries = ParseOverlayEntries(text);
            if (entries.Count == 0) return 0;

            var lines = File.Exists(targetPath)
                ? File.ReadAllLines(targetPath).ToList()
                : new List<string>();

            int applied = 0;
            foreach (var (section, key, value) in entries)
            {
                if (IsManagedArchiveLoadListKey(section, key))
                    continue;
                UpdateIniKey(lines, section, key, value);
                applied++;
            }

            if (applied == 0) return 0;

            try
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                ClearReadOnly(targetPath);
                WriteIniTextNoBom(targetPath, string.Join("\n", lines));
            }
            catch (Exception ex)
            {
                LogError($"[F76-Manager-AddTo] Failed writing {Path.GetFileName(targetPath)}: {ex.Message}");
                return 0;
            }

            return applied;
        }

        private static List<(string Section, string Key, string Value)> ParseOverlayEntries(string text)
        {
            var result = new List<(string, string, string)>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            string? section = null;
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;

                if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
                {
                    section = trimmed[1..^1].Trim();
                    continue;
                }

                if (section == null) continue;
                var parts = trimmed.Split('=', 2);
                if (parts.Length < 2) continue;
                string key = parts[0].Trim();
                if (key.Length == 0) continue;
                result.Add((section, key, parts[1]));
            }

            return result;
        }

        private static bool IsManagedArchiveLoadListKey(string section, string key)
        {
            if (!section.Equals("Archive", StringComparison.OrdinalIgnoreCase))
                return false;
            string k = key.Trim();
            return k.StartsWith("sResource", StringComparison.OrdinalIgnoreCase)
                && k.EndsWith("List", StringComparison.OrdinalIgnoreCase);
        }

        private static void ClearReadOnlyAttribute(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
            }
            catch
            {
            }
        }

        private void ClearReadOnly(string path, bool logWhenCleared = false)
        {
            try { 
                if (File.Exists(path)) { 
                    var attr = File.GetAttributes(path); 
                    if ((attr & FileAttributes.ReadOnly) != 0) {
                        File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
                        if (logWhenCleared)
                            Log($"[INI] Cleared read-only on {Path.GetFileName(path)}");
                    }
                } 
            } catch (Exception ex) {
                Log($"[INI] Failed to clear read-only attribute on {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        private void Log(string msg) => _logger?.Invoke(msg);
        private void LogError(string msg) => _statusReporter?.Invoke("error", msg);
    }
}
