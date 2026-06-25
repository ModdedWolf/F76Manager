using System;
using System.Collections.Generic;
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
