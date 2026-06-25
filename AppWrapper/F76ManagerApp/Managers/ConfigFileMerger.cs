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
        ".ini"
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

            string existing = File.ReadAllText(existingPath, Encoding.UTF8);
            string incoming = File.ReadAllText(incomingPath, Encoding.UTF8);

            return ext.ToLowerInvariant() switch
            {
                ".ini" => TryMergeIni(existing, incoming, out mergedContent, out summary),
                ".txt" => TryMergeTxt(existing, incoming, out mergedContent, out summary),
                ".json" => TryMergeJson(existing, incoming, out mergedContent, out summary),
                _ => false
            };
        }
        catch (Exception ex)
        {
            summary = ex.Message;
            return false;
        }
    }

    public static void WriteMergedFile(string destPath, string mergedContent)
    {
        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(destPath, mergedContent, Utf8NoBom);
    }

    private static bool TryMergeIni(string existing, string incoming, out string mergedContent, out string summary)
    {
        mergedContent = "";
        summary = "";

        var lines = string.IsNullOrEmpty(existing)
            ? new List<string>()
            : existing.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

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

        mergedContent = lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines);
        if (mergedContent.Length > 0 && !mergedContent.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            mergedContent += Environment.NewLine;

        summary = added == 0 ? "No new INI keys" : $"Added {added} INI key{(added == 1 ? "" : "s")}";
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
            for (int i = sectionStartIndex + 1; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    sectionEndIndex = i;
                    break;
                }
            }

            if (sectionEndIndex != -1)
                lines.Insert(sectionEndIndex, assignment);
            else
                lines.Add(assignment);
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

        var result = new List<string>(existingLines);
        if (toAppend.Count > 0)
        {
            if (result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1]))
                result.Add("");
            result.AddRange(toAppend);
        }

        mergedContent = result.Count == 0 ? "" : string.Join(Environment.NewLine, result);
        if (mergedContent.Length > 0 && !mergedContent.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            mergedContent += Environment.NewLine;

        summary = added == 0 ? "No new TXT lines" : $"Added {added} TXT line{(added == 1 ? "" : "s")}";
        return true;
    }

    private static List<string> SplitTxtLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
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

        int added = 0;
        JsonNode merged = MergeJsonNodes(existingNode, incomingNode, ref added);
        mergedContent = merged.ToJsonString(JsonWriteOptions);
        summary = added == 0 ? "No new JSON keys" : $"Added {added} JSON key{(added == 1 ? "" : "s")}";
        return true;
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

    private static bool JsonNodesDeepEqual(JsonNode? a, JsonNode? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return string.Equals(a.ToJsonString(), b.ToJsonString(), StringComparison.Ordinal);
    }
}
