using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace F76ManagerApp.Managers;

public static class ThemePackageValidator
{
    public const int FormatVersion = 1;
    public const int MaxZipBytes = 600 * 1024;
    public const int MaxManifestBytes = 32 * 1024;
    public const int MaxLogoBytes = 512 * 1024;
    public const int MaxLogoDimension = 512;

    public static readonly IReadOnlyList<string> TokenKeys = new[]
    {
        "bg-dark", "bg-surface", "bg-surface-light", "primary-green", "primary-rgb",
        "accent-amber", "text-main", "text-muted", "border-color",
        "danger-red", "success-green", "warning-yellow", "on-primary",
    };

    private static readonly Regex IdRe = new(@"^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex HexRe = new(@"^#([0-9a-fA-F]{6})$", RegexOptions.Compiled);
    private static readonly Regex RgbRe = new(@"^\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*$", RegexOptions.Compiled);
    private static readonly HashSet<string> ObjectFits = new(StringComparer.OrdinalIgnoreCase)
        { "cover", "contain", "fill", "none", "scale-down" };

    public static bool IsValidThemeId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && IdRe.IsMatch(id) && id is not ("my-theme" or "default");

    public static bool ContainsDangerousText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var lower = value.ToLowerInvariant();
        return lower.Contains('<') || lower.Contains('>') || lower.Contains("javascript:") || lower.Contains("expression(");
    }

    public static bool TryValidateManifest(string json, out ValidatedThemeManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("formatVersion").GetInt32() != FormatVersion)
            {
                error = "Unsupported formatVersion.";
                return false;
            }

            var id = root.GetProperty("id").GetString() ?? "";
            var displayName = root.GetProperty("displayName").GetString() ?? "";
            if (!IsValidThemeId(id)) { error = "Invalid theme id."; return false; }
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80) { error = "Invalid displayName."; return false; }
            if (ContainsDangerousText(displayName)) { error = "displayName contains unsafe characters."; return false; }

            if (!root.TryGetProperty("tokens", out var tokensEl) || tokensEl.ValueKind != JsonValueKind.Object)
            {
                error = "Missing tokens object.";
                return false;
            }

            var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in TokenKeys)
            {
                if (!tokensEl.TryGetProperty(key, out var prop))
                {
                    error = $"Missing token: {key}";
                    return false;
                }
                var val = prop.GetString() ?? "";
                if (ContainsDangerousText(val)) { error = $"Unsafe token value: {key}"; return false; }
                if (key == "primary-rgb")
                {
                    if (!RgbRe.IsMatch(val)) { error = "Invalid primary-rgb."; return false; }
                }
                else if (!HexRe.IsMatch(val))
                {
                    error = $"Invalid color for {key}.";
                    return false;
                }
                tokens[key] = val.Trim();
            }

            foreach (var prop in tokensEl.EnumerateObject())
            {
                if (!TokenKeys.Contains(prop.Name))
                {
                    error = $"Unknown token key: {prop.Name}";
                    return false;
                }
            }

            var logoLayout = NormalizeLogoLayout(root);

            manifest = new ValidatedThemeManifest(id, displayName.Trim(), tokens, logoLayout);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static Dictionary<string, object> NormalizeLogoLayout(JsonElement root)
    {
        var layout = new Dictionary<string, object>
        {
            ["width"] = 44, ["height"] = 0, ["scale"] = 1.0,
            ["offsetX"] = 0, ["offsetY"] = 0,
            ["objectFit"] = "cover", ["objectPosition"] = "center bottom",
            ["opacity"] = 1.0, ["shadowY"] = 4, ["shadowBlur"] = 12, ["shadowOpacity"] = 0.5,
            ["collapsedScale"] = 1.0, ["collapsedOffsetX"] = 0,
        };
        if (!root.TryGetProperty("logoLayout", out var el) || el.ValueKind != JsonValueKind.Object)
            return layout;

        void N(string key, double lo, double hi)
        {
            if (!el.TryGetProperty(key, out var p)) return;
            if (p.TryGetDouble(out var d)) layout[key] = Math.Clamp(d, lo, hi);
        }

        N("width", 16, 120);
        N("height", 0, 120);
        N("scale", 0.25, 3);
        N("offsetX", -40, 40);
        N("offsetY", -40, 40);
        N("opacity", 0, 1);
        N("shadowY", 0, 24);
        N("shadowBlur", 0, 48);
        N("shadowOpacity", 0, 1);
        N("collapsedScale", 0.25, 3);
        N("collapsedOffsetX", -40, 40);

        if (el.TryGetProperty("objectFit", out var fit))
        {
            var s = fit.GetString() ?? "cover";
            if (ObjectFits.Contains(s)) layout["objectFit"] = s;
        }
        if (el.TryGetProperty("objectPosition", out var pos))
        {
            var s = (pos.GetString() ?? "").Trim();
            if (!string.IsNullOrEmpty(s) && !ContainsDangerousText(s)) layout["objectPosition"] = s;
        }

        layout["width"] = (int)Math.Round((double)layout["width"]);
        layout["height"] = (int)Math.Round((double)layout["height"]);
        foreach (var k in new[] { "offsetX", "offsetY", "shadowY", "shadowBlur", "collapsedOffsetX" })
            layout[k] = (int)Math.Round((double)layout[k]);

        return layout;
    }

    public static string BuildThemeCssBlock(string themeId, IReadOnlyDictionary<string, string> tokens, IReadOnlyDictionary<string, object> logoLayout)
    {
        var lines = new List<string> { $"[data-theme=\"{themeId}\"] {{" };
        foreach (var kv in tokens)
            lines.Add($"  --{kv.Key}: {kv.Value};");

        if (tokens.TryGetValue("primary-rgb", out var rgb))
            lines.Add($"  --primary-green-dim: rgba({rgb}, 0.7);");

        var width = (int)logoLayout["width"];
        var height = (int)logoLayout["height"];
        lines.Add($"  --logo-width: {width}px;");
        lines.Add($"  --logo-height: {(height > 0 ? $"{height}px" : "var(--topbar-height)")};");
        lines.Add($"  --logo-scale: {Convert.ToString(logoLayout["scale"], CultureInfo.InvariantCulture)};");
        lines.Add($"  --logo-offset-x: {(int)logoLayout["offsetX"]}px;");
        lines.Add($"  --logo-offset-y: {(int)logoLayout["offsetY"]}px;");
        lines.Add($"  --logo-object-fit: {logoLayout["objectFit"]};");
        lines.Add($"  --logo-object-position: {logoLayout["objectPosition"]};");
        lines.Add($"  --logo-opacity: {Convert.ToString(logoLayout["opacity"], CultureInfo.InvariantCulture)};");
        lines.Add($"  --logo-shadow-y: {(int)logoLayout["shadowY"]}px;");
        lines.Add($"  --logo-shadow-blur: {(int)logoLayout["shadowBlur"]}px;");
        lines.Add($"  --logo-shadow-opacity: {Convert.ToString(logoLayout["shadowOpacity"], CultureInfo.InvariantCulture)};");
        lines.Add($"  --logo-collapsed-scale: {Convert.ToString(logoLayout["collapsedScale"], CultureInfo.InvariantCulture)};");
        lines.Add($"  --logo-collapsed-offset-x: {(int)logoLayout["collapsedOffsetX"]}px;");
        lines.Add($"  --logo-collapsed-margin: {-width / 2}px;");
        lines.Add("}");
        return string.Join("\n", lines);
    }

    public static bool TryValidateLogoImage(byte[] data, out string ext, out string? error)
    {
        ext = ".png";
        error = null;
        if (data.Length < 12) { error = "Logo too small."; return false; }

        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            ext = ".png";
            return true;
        }
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            ext = ".jpg";
            return true;
        }
        if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            ext = ".webp";
            return true;
        }

        error = "Unsupported image format (use PNG, JPEG, or WebP).";
        return false;
    }

    public static string SanitizeExportFileName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "Theme";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = displayName.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ");
        return string.IsNullOrWhiteSpace(s) ? "Theme" : s.Length > 64 ? s[..64] : s;
    }
}

public sealed record ValidatedThemeManifest(
    string Id,
    string DisplayName,
    IReadOnlyDictionary<string, string> Tokens,
    IReadOnlyDictionary<string, object> LogoLayout);
