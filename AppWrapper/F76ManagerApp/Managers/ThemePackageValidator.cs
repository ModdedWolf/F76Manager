using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace F76ManagerApp.Managers;

public static class ThemePackageValidator
{
    public const int FormatVersion = 2;
    public const int MinFormatVersion = 1;
    public const int MaxZipBytes = 2200 * 1024;
    public const int MaxManifestBytes = 32 * 1024;
    public const int MaxLogoBytes = 2 * 1024 * 1024;
    public const int MaxLogoDimension = 512;

    public static readonly IReadOnlyList<string> TokenKeys = new[]
    {
        "bg-dark", "bg-surface", "bg-surface-light", "bg-elevated", "bg-inset",
        "primary-green", "primary-rgb", "primary-hover",
        "accent-amber", "text-main", "text-muted", "border-color",
        "danger-red", "danger-red-soft", "success-green", "warning-yellow", "on-primary",
    };

    public static readonly IReadOnlyList<string> V1TokenKeys = new[]
    {
        "bg-dark", "bg-surface", "bg-surface-light", "primary-green", "primary-rgb",
        "accent-amber", "text-main", "text-muted", "border-color",
        "danger-red", "success-green", "warning-yellow", "on-primary",
    };

    public static readonly IReadOnlyList<string> OptionalTokenKeys = new[]
    {
        "slider-track",
    };

    private static readonly HashSet<string> TokenKeySet = new(TokenKeys.Concat(OptionalTokenKeys), StringComparer.Ordinal);
    private static readonly HashSet<string> V1TokenKeySet = new(V1TokenKeys, StringComparer.Ordinal);

    private static readonly Regex IdRe = new(@"^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex Hex6Re = new(@"^#([0-9a-fA-F]{6})$", RegexOptions.Compiled);
    private static readonly Regex Hex8Re = new(@"^#([0-9a-fA-F]{8})$", RegexOptions.Compiled);
    private static readonly Regex RgbaRe = new(
        @"^rgba\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(0|1|0?\.\d+|1\.0+)\s*\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
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

    public static bool IsValidColorToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (Hex6Re.IsMatch(v) || Hex8Re.IsMatch(v)) return true;
        var m = RgbaRe.Match(v);
        if (!m.Success) return false;
        return int.TryParse(m.Groups[1].Value, out var r) && r <= 255
            && int.TryParse(m.Groups[2].Value, out var g) && g <= 255
            && int.TryParse(m.Groups[3].Value, out var b) && b <= 255;
    }

    public static bool TryValidateManifest(string json, out ValidatedThemeManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("formatVersion", out var fvEl) || !fvEl.TryGetInt32(out var formatVersion))
            {
                error = "Missing formatVersion.";
                return false;
            }
            if (formatVersion < MinFormatVersion || formatVersion > FormatVersion)
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
            var required = formatVersion >= 2 ? TokenKeys : V1TokenKeys;
            var allowed = formatVersion >= 2 ? TokenKeySet : V1TokenKeySet;

            foreach (var prop in tokensEl.EnumerateObject())
            {
                if (!allowed.Contains(prop.Name) && !TokenKeySet.Contains(prop.Name))
                {
                    error = $"Unknown token key: {prop.Name}";
                    return false;
                }
            }

            foreach (var key in required)
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
                else if (!IsValidColorToken(val))
                {
                    if (formatVersion < 2 && !Hex6Re.IsMatch(val.Trim()))
                    {
                        error = $"Invalid color for {key}.";
                        return false;
                    }
                    error = $"Invalid color for {key}.";
                    return false;
                }
                tokens[key] = val.Trim();
            }

            foreach (var key in TokenKeys.Concat(OptionalTokenKeys))
            {
                if (tokens.ContainsKey(key)) continue;
                if (!tokensEl.TryGetProperty(key, out var prop)) continue;
                var val = prop.GetString() ?? "";
                if (ContainsDangerousText(val)) { error = $"Unsafe token value: {key}"; return false; }
                if (key == "primary-rgb")
                {
                    if (!RgbRe.IsMatch(val)) { error = "Invalid primary-rgb."; return false; }
                }
                else if (!IsValidColorToken(val))
                {
                    error = $"Invalid color for {key}.";
                    return false;
                }
                tokens[key] = val.Trim();
            }

            DeriveMissingTokens(tokens);

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

    public static void DeriveMissingTokens(Dictionary<string, string> tokens)
    {
        if (!tokens.ContainsKey("primary-hover") && tokens.TryGetValue("primary-green", out var primary))
            tokens["primary-hover"] = LightenHexOrColor(primary, 0.12);

        if (!tokens.ContainsKey("bg-elevated")
            && tokens.TryGetValue("bg-surface", out var surface)
            && tokens.TryGetValue("bg-surface-light", out var surfaceLight))
            tokens["bg-elevated"] = MixColors(surface, surfaceLight, 0.5);

        if (!tokens.ContainsKey("bg-inset") && tokens.TryGetValue("bg-dark", out var dark))
            tokens["bg-inset"] = DarkenHexOrColor(dark, 0.04);

        if (!tokens.ContainsKey("danger-red-soft") && tokens.TryGetValue("danger-red", out var danger))
            tokens["danger-red-soft"] = ToRgba(danger, 0.18);

        if (!tokens.ContainsKey("slider-track")
            && tokens.TryGetValue("bg-surface-light", out var trackBase)
            && tokens.TryGetValue("primary-green", out var trackTint))
            tokens["slider-track"] = MixColors(trackBase, trackTint, 0.35);
    }

    private static bool TryParseColor(string value, out int r, out int g, out int b, out double a)
    {
        r = g = b = 0;
        a = 1;
        var v = value.Trim();
        if (Hex6Re.IsMatch(v))
        {
            r = Convert.ToInt32(v.Substring(1, 2), 16);
            g = Convert.ToInt32(v.Substring(3, 2), 16);
            b = Convert.ToInt32(v.Substring(5, 2), 16);
            return true;
        }
        if (Hex8Re.IsMatch(v))
        {
            r = Convert.ToInt32(v.Substring(1, 2), 16);
            g = Convert.ToInt32(v.Substring(3, 2), 16);
            b = Convert.ToInt32(v.Substring(5, 2), 16);
            a = Convert.ToInt32(v.Substring(7, 2), 16) / 255.0;
            return true;
        }
        var m = RgbaRe.Match(v);
        if (!m.Success) return false;
        r = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        g = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        b = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        a = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        return r <= 255 && g <= 255 && b <= 255;
    }

    private static string FormatHex(int r, int g, int b) =>
        $"#{ClampByte(r):x2}{ClampByte(g):x2}{ClampByte(b):x2}";

    private static int ClampByte(int n) => Math.Clamp(n, 0, 255);

    private static string LightenHexOrColor(string color, double amount)
    {
        if (!TryParseColor(color, out var r, out var g, out var b, out _))
            return color;
        r = (int)Math.Round(r + (255 - r) * amount);
        g = (int)Math.Round(g + (255 - g) * amount);
        b = (int)Math.Round(b + (255 - b) * amount);
        return FormatHex(r, g, b);
    }

    private static string DarkenHexOrColor(string color, double amount)
    {
        if (!TryParseColor(color, out var r, out var g, out var b, out _))
            return color;
        r = (int)Math.Round(r * (1 - amount));
        g = (int)Math.Round(g * (1 - amount));
        b = (int)Math.Round(b * (1 - amount));
        return FormatHex(r, g, b);
    }

    private static string MixColors(string a, string b, double t)
    {
        if (!TryParseColor(a, out var r1, out var g1, out var b1, out _))
            return a;
        if (!TryParseColor(b, out var r2, out var g2, out var b2, out _))
            return a;
        var r = (int)Math.Round(r1 + (r2 - r1) * t);
        var g = (int)Math.Round(g1 + (g2 - g1) * t);
        var bl = (int)Math.Round(b1 + (b2 - b1) * t);
        return FormatHex(r, g, bl);
    }

    private static string ToRgba(string color, double alpha)
    {
        if (!TryParseColor(color, out var r, out var g, out var b, out _))
            return $"rgba(224, 86, 76, {alpha.ToString(CultureInfo.InvariantCulture)})";
        return $"rgba({r}, {g}, {b}, {alpha.ToString(CultureInfo.InvariantCulture)})";
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
        var lines = new List<string> { $":root[data-theme=\"{themeId}\"] {{" };
        foreach (var key in TokenKeys.Concat(OptionalTokenKeys))
        {
            if (tokens.TryGetValue(key, out var val))
                lines.Add($"  --{key}: {val};");
        }

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
        if (data.Length >= 6
            && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F'
            && data[3] == (byte)'8' && (data[4] == (byte)'7' || data[4] == (byte)'9') && data[5] == (byte)'a')
        {
            ext = ".gif";
            return true;
        }

        error = "Unsupported image format (use PNG, JPEG, WebP, or GIF).";
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
