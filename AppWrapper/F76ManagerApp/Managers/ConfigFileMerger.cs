using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F76ManagerApp.Managers;

public static class ConfigFileMerger
{
    public static readonly HashSet<string> LooseConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json",
        ".txt",
        ".ini",
        ".toml"
    };

    public static readonly HashSet<string> KnownDataAssetFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "meshes", "textures", "materials", "scripts", "sound", "sounds", "music", "strings",
        "interface", "programs", "video", "lodsettings", "facegen", "misc", "shadersfx",
        "vis", "geo", "terrain", "grass"
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonDocumentOptions JsonReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    public static bool IsLooseConfigExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        if (!extension.StartsWith('.')) extension = "." + extension;
        return LooseConfigExtensions.Contains(extension);
    }

    public static bool IsRootLooseConfigPath(string? relativePath)
    {
        string rel = (relativePath ?? "").Replace("\\", "/").Trim().TrimStart('/');
        if (string.IsNullOrEmpty(rel)) return false;

        if (rel.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
            rel = rel.Substring("Disabled/".Length);
        if (rel.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase))
            return IsLooseConfigExtension(Path.GetExtension(rel));

        if (!IsLooseConfigExtension(Path.GetExtension(rel)))
            return false;

        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (KnownDataAssetFolders.Contains(parts[i]))
                return false;
        }

        return true;
    }

    private static readonly HashSet<string> ManagerReservedTopFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Strings", "Bundles", "GameRoot", "CoreIni", "Loose"
    };

    public static bool IsNonAssetDataFolderPath(string? relativePath)
    {
        var parts = SplitDataRelativePath(relativePath);
        return parts.Length >= 2 && IsNonAssetFolderChain(parts, parts.Length - 1);
    }

    public static bool IsNonAssetDataDirectory(string? relativeDirectory)
    {
        var parts = SplitDataRelativePath(relativeDirectory);
        return parts.Length >= 1 && IsNonAssetFolderChain(parts, parts.Length);
    }

    private static string[] SplitDataRelativePath(string? relativePath)
    {
        string rel = (relativePath ?? "").Replace("\\", "/").Trim().Trim('/');
        if (rel.StartsWith("Disabled/", StringComparison.OrdinalIgnoreCase))
            rel = rel.Substring("Disabled/".Length);
        return rel.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsNonAssetFolderChain(string[] parts, int folderCount)
    {
        if (ManagerReservedTopFolders.Contains(parts[0])) return false;
        for (int i = 0; i < folderCount; i++)
        {
            if (KnownDataAssetFolders.Contains(parts[i]))
                return false;
        }
        return true;
    }

    public static bool TryMergeAdditive(
        string existingPath,
        string incomingPath,
        out string mergedContent,
        out string summary)
    {
        mergedContent = "";
        summary = "";

        try
        {
            if (!File.Exists(existingPath) || !File.Exists(incomingPath))
                return false;

            string ext = Path.GetExtension(existingPath);
            if (!IsLooseConfigExtension(ext) ||
                !string.Equals(ext, Path.GetExtension(incomingPath), StringComparison.OrdinalIgnoreCase))
                return false;

            string existing = TextFileEncoding.Read(existingPath, out _);
            string incoming = TextFileEncoding.Read(incomingPath, out _);

            return ext.ToLowerInvariant() switch
            {
                ".ini" => TryMergeIni(existing, incoming, out mergedContent, out summary),
                ".txt" => TryMergeTxt(existing, incoming, out mergedContent, out summary),
                ".json" => TryMergeJson(existing, incoming, out mergedContent, out summary),
                ".toml" => KeepExisting(existing, out mergedContent, out summary),
                _ => false
            };
        }
        catch (Exception ex)
        {
            summary = ex.Message;
            return false;
        }
    }

    private static bool KeepExisting(string existing, out string mergedContent, out string summary)
    {
        mergedContent = existing;
        summary = "No new keys merged (kept existing file)";
        return true;
    }

    public static void WriteMergedFile(string destPath, string mergedContent)
    {
        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(destPath, mergedContent, Utf8NoBom);
    }

    public static bool MergeIntoFile(
        string destPath,
        string incomingPath,
        string backupRoot,
        out string summary,
        out bool changed) =>
        MergeIntoFile(destPath, incomingPath, backupRoot, out summary, out changed, out _);

    public static bool MergeIntoFile(
        string destPath,
        string incomingPath,
        string backupRoot,
        out string summary,
        out bool changed,
        out string? backupPath)
    {
        summary = "";
        changed = false;
        backupPath = null;

        try
        {
            if (!File.Exists(destPath) || !File.Exists(incomingPath))
                return false;

            string ext = Path.GetExtension(destPath);
            if (!IsLooseConfigExtension(ext) ||
                !string.Equals(ext, Path.GetExtension(incomingPath), StringComparison.OrdinalIgnoreCase))
                return false;

            string existing = TextFileEncoding.Read(destPath, out string token);
            string incoming = TextFileEncoding.Read(incomingPath, out _);

            return MergeIntoFileCore(destPath, existing, incoming, ext, token, backupRoot, out summary, out changed, out backupPath);
        }
        catch (Exception ex)
        {
            summary = ex.Message;
            changed = false;
            backupPath = null;
            return false;
        }
    }

    private static bool MergeIntoFileCore(
        string destPath,
        string existing,
        string incoming,
        string ext,
        string token,
        string backupRoot,
        out string summary,
        out bool changed,
        out string? backupPath)
    {
        summary = "";
        changed = false;
        backupPath = null;

        string merged = existing;
        bool ok;
        switch (ext.ToLowerInvariant())
        {
            case ".ini": ok = TryMergeIni(existing, incoming, out merged, out summary); break;
            case ".txt": ok = TryMergeTxt(existing, incoming, out merged, out summary); break;
            case ".json": ok = TryMergeJson(existing, incoming, out merged, out summary); break;
            case ".toml": ok = KeepExisting(existing, out merged, out summary); break;
            default: return false;
        }
        if (!ok) return false;

        if (string.Equals(merged, existing, StringComparison.Ordinal))
        {
            changed = false;
            return true;
        }

        // Back up the original before overwriting it.
        if (!string.IsNullOrWhiteSpace(backupRoot))
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backupDir = Path.Combine(backupRoot, stamp);
            Directory.CreateDirectory(backupDir);
            string fileName = Path.GetFileName(destPath);
            backupPath = Path.Combine(backupDir, fileName);
            if (File.Exists(backupPath))
            {
                string nameNoExt = Path.GetFileNameWithoutExtension(fileName);
                string fileExt = Path.GetExtension(fileName);
                int suffix = 1;
                do
                {
                    backupPath = Path.Combine(backupDir, $"{nameNoExt}_{suffix}{fileExt}");
                    suffix++;
                } while (File.Exists(backupPath));
            }
            File.Copy(destPath, backupPath);
        }

        string writeToken = TextFileEncoding.Normalize(token);
        // Match the config editor: INI files are always written UTF-8 without BOM.
        if (string.Equals(ext, ".ini", StringComparison.OrdinalIgnoreCase) &&
            writeToken == TextFileEncoding.Utf8Bom)
        {
            writeToken = TextFileEncoding.Utf8;
        }
        Encoding writeEncoding = TextFileEncoding.GetEncoding(writeToken, strictEncode: false);
        File.WriteAllText(destPath, merged, writeEncoding);
        changed = true;
        return true;
    }

    private static string DetectNewline(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            if (text.Contains("\r\n")) return "\r\n";
            if (text.Contains('\n')) return "\n";
            if (text.Contains('\r')) return "\r";
        }
        return Environment.NewLine;
    }

    /// <summary>
    /// Splits into lines without newline characters. A trailing newline is a
    /// terminator, not an extra empty line, so it does not produce a final "".
    /// Interior blank lines are preserved exactly.
    /// </summary>
    private static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = normalized.Split('\n').ToList();
        if (normalized.EndsWith('\n') && parts.Count > 0 && parts[^1].Length == 0)
            parts.RemoveAt(parts.Count - 1);
        return parts;
    }

    private static string JoinLines(List<string> lines, string newline, bool trailingNewline)
    {
        if (lines.Count == 0) return "";
        string joined = string.Join(newline, lines);
        return trailingNewline ? joined + newline : joined;
    }

    private static bool TryMergeIni(string existing, string incoming, out string mergedContent, out string summary)
    {
        if (!IniHasSections(existing) && !IniHasSections(incoming))
            return TryMergeIniLineList(existing, incoming, out mergedContent, out summary);

        mergedContent = "";
        summary = "";

        string newline = DetectNewline(existing);
        var lines = SplitLines(existing);

        var existingKeys = BuildIniKeySet(lines);
        int added = 0;

        string? currentSection = null;
        foreach (var rawLine in incoming.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed[1..^1].Trim();
                continue;
            }

            if (currentSection == null) continue;

            var parts = trimmed.Split('=', 2);
            if (parts.Length < 2) continue;

            string key = parts[0].Trim();
            string value = parts[1];
            if (key.Length == 0) continue;

            var pairKey = (Section: currentSection, Key: key);
            if (existingKeys.Contains(pairKey)) continue;

            AddIniKeyIfMissing(lines, currentSection, key, value);
            existingKeys.Add(pairKey);
            added++;
        }

        // Nothing new: hand the original text back untouched so callers can
        // tell the file does not need to be rewritten at all.
        mergedContent = added == 0 ? existing : JoinLines(lines, newline, trailingNewline: true);

        summary = added == 0 ? "No new INI keys" : $"Added {added} INI key{(added == 1 ? "" : "s")}";
        return true;
    }

    private static bool IniHasSections(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        foreach (var rawLine in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
                return true;
        }
        return false;
    }

    private static bool TryMergeIniLineList(string existing, string incoming, out string mergedContent, out string summary)
    {
        mergedContent = "";
        summary = "";

        string newline = DetectNewline(existing);
        // The user's existing lines are kept exactly as they are: no trimming,
        // no deduplication, no removal of blank lines or comments.
        var lines = SplitLines(existing);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) seen.Add(trimmed);
        }

        var toAppend = new List<string>();
        foreach (var rawLine in SplitLines(incoming))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            if (!seen.Add(trimmed)) continue;
            toAppend.Add(rawLine);
        }

        int added = toAppend.Count;
        if (added == 0)
        {
            mergedContent = existing;
        }
        else
        {
            var result = new List<string>(lines);
            result.AddRange(toAppend);
            mergedContent = JoinLines(result, newline, trailingNewline: true);
        }

        summary = added == 0 ? "No new INI list entries" : $"Added {added} INI list entr{(added == 1 ? "y" : "ies")}";
        return true;
    }

    private static HashSet<(string Section, string Key)> BuildIniKeySet(List<string> lines)
    {
        var set = new HashSet<(string, string)>(IniKeyComparer.Instance);
        string? section = null;

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                continue;
            }

            if (section == null) continue;

            var parts = trimmed.Split('=', 2);
            if (parts.Length >= 1 && parts[0].Trim().Length > 0)
                set.Add((section, parts[0].Trim()));
        }

        return set;
    }

    private static void AddIniKeyIfMissing(List<string> lines, string section, string key, string value)
    {
        string sectionHeader = $"[{section}]";
        int sectionStartIndex = -1;
        int sectionEndIndex = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                sectionStartIndex = i;
                break;
            }
        }

        string assignment = $"{key}={value}";

        if (sectionStartIndex != -1)
        {
            sectionEndIndex = lines.Count;
            for (int i = sectionStartIndex + 1; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    sectionEndIndex = i;
                    break;
                }
            }

            // Insert after the section's last non-blank line so blank separator
            // lines before the next [section] header stay where they were.
            int insertAt = sectionStartIndex + 1;
            for (int i = sectionStartIndex + 1; i < sectionEndIndex; i++)
            {
                if (lines[i].Trim().Length > 0) insertAt = i + 1;
            }
            lines.Insert(insertAt, assignment);
        }
        else
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add(sectionHeader);
            lines.Add(assignment);
        }
    }

    private sealed class IniKeyComparer : IEqualityComparer<(string Section, string Key)>
    {
        public static readonly IniKeyComparer Instance = new();

        public bool Equals((string Section, string Key) x, (string Section, string Key) y) =>
            string.Equals(x.Section, y.Section, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Section, string Key) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Section ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key ?? ""));
    }

    private static bool TryMergeTxt(string existing, string incoming, out string mergedContent, out string summary)
    {
        mergedContent = "";
        summary = "";

        var existingLines = SplitTxtLines(existing);
        var incomingLines = SplitTxtLines(incoming);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in existingLines)
            seen.Add(NormalizeTxtLine(line));

        int added = 0;
        var toAppend = new List<string>();

        foreach (var line in incomingLines)
        {
            string norm = NormalizeTxtLine(line);
            if (seen.Contains(norm)) continue;
            seen.Add(norm);
            toAppend.Add(line);
            added++;
        }

        if (added == 0)
        {
            mergedContent = existing;
        }
        else
        {
            string newline = DetectNewline(existing);
            var result = new List<string>(existingLines);
            if (result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1]))
                result.Add("");
            result.AddRange(toAppend);
            mergedContent = JoinLines(result, newline, trailingNewline: true);
        }

        summary = added == 0 ? "No new TXT lines" : $"Added {added} TXT line{(added == 1 ? "" : "s")}";
        return true;
    }

    private static List<string> SplitTxtLines(string text)
    {
        return SplitLines(text);
    }

    private static string NormalizeTxtLine(string line) => line.TrimEnd();

    private static bool TryMergeJson(string existing, string incoming, out string mergedContent, out string summary)
    {
        mergedContent = "";
        summary = "";

        JsonNode? existingNode = ParseJsonLenient(existing);
        JsonNode? incomingNode = ParseJsonLenient(incoming);

        if (existingNode == null || incomingNode == null)
            return false;

        var additions = new List<JsoncText.Addition>();
        CollectJsonAdditions(existingNode, incomingNode, new List<object>(), additions);
        int added = additions.Count;

        if (added == 0)
        {
            mergedContent = existing;
        }
        else
        {
            int mergeCount = 0;
            JsonNode merged = MergeJsonNodes(existingNode, incomingNode, ref mergeCount);
            string? patched = string.IsNullOrWhiteSpace(existing) ? null : JsoncText.ApplyAdditions(existing, additions);
            if (patched != null && JsonNodesDeepEqual(ParseJsonLenient(patched), merged))
            {
                mergedContent = patched;
            }
            else if (JsoncText.ContainsComments(existing))
            {
                mergedContent = existing;
                summary = $"Kept existing file to preserve comments ({added} JSON key{(added == 1 ? "" : "s")} not merged)";
                return true;
            }
            else
            {
                mergedContent = merged.ToJsonString(JsonWriteOptions);
            }
        }

        summary = added == 0 ? "No new JSON keys" : $"Added {added} JSON key{(added == 1 ? "" : "s")}";
        return true;
    }

    private static void CollectJsonAdditions(JsonNode existing, JsonNode incoming, List<object> path, List<JsoncText.Addition> additions)
    {
        if (existing is JsonObject exObj && incoming is JsonObject inObj)
        {
            foreach (var prop in inObj)
            {
                if (!exObj.TryGetPropertyValue(prop.Key, out var existingChild))
                {
                    additions.Add(new JsoncText.Addition(path.ToList(), prop.Key, prop.Value?.DeepClone()));
                }
                else if (existingChild == null)
                {
                    additions.Add(new JsoncText.Addition(path.ToList(), prop.Key, prop.Value?.DeepClone(), Replace: true));
                }
                else if (existingChild is JsonObject && prop.Value is JsonObject)
                {
                    CollectJsonAdditions(existingChild, prop.Value, new List<object>(path) { prop.Key }, additions);
                }
                else if (existingChild is JsonArray exChildArr && prop.Value is JsonArray inChildArr &&
                         IsStringArray(exChildArr) && IsStringArray(inChildArr))
                {
                    CollectJsonAdditions(exChildArr, inChildArr, new List<object>(path) { prop.Key }, additions);
                }
            }
            return;
        }

        if (existing is JsonArray exArr && incoming is JsonArray inArr)
        {
            var seen = exArr.ToList();
            foreach (var item in inArr)
            {
                if (seen.Any(s => JsonNodesDeepEqual(s, item))) continue;
                seen.Add(item);
                additions.Add(new JsoncText.Addition(path.ToList(), null, item?.DeepClone()));
            }
        }
    }

    private static JsonNode? ParseJsonLenient(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(text, documentOptions: JsonReadOptions);
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode MergeJsonNodes(JsonNode existing, JsonNode incoming, ref int addedCount)
    {
        if (existing is JsonObject exObj && incoming is JsonObject inObj)
        {
            var result = exObj.DeepClone().AsObject();
            foreach (var prop in inObj)
            {
                if (!result.TryGetPropertyValue(prop.Key, out var existingChild) || existingChild == null)
                {
                    result[prop.Key] = prop.Value?.DeepClone();
                    addedCount++;
                }
                else if (existingChild is JsonObject && prop.Value is JsonObject)
                {
                    result[prop.Key] = MergeJsonNodes(existingChild, prop.Value, ref addedCount);
                }
                else if (existingChild is JsonArray exChildArr && prop.Value is JsonArray inChildArr &&
                         IsStringArray(exChildArr) && IsStringArray(inChildArr))
                {
                    result[prop.Key] = MergeJsonNodes(exChildArr, inChildArr, ref addedCount);
                }
            }
            return result;
        }

        if (existing is JsonArray exArr && incoming is JsonArray inArr)
        {
            var result = exArr.DeepClone().AsArray();
            foreach (var item in inArr)
            {
                bool found = false;
                foreach (var existingItem in result)
                {
                    if (JsonNodesDeepEqual(existingItem, item))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    result.Add(item?.DeepClone());
                    addedCount++;
                }
            }
            return result;
        }

        return existing.DeepClone();
    }

    private static bool IsStringArray(JsonArray array) =>
        array.All(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.String);

    private static bool JsonNodesDeepEqual(JsonNode? a, JsonNode? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return string.Equals(a.ToJsonString(), b.ToJsonString(), StringComparison.Ordinal);
    }
}
