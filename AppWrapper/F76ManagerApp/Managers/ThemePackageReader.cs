using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace F76ManagerApp.Managers;

public static class ThemePackageReader
{
    public sealed class ThemePackageContent
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public Dictionary<string, string> Tokens { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, object> LogoLayout { get; init; } = new(StringComparer.Ordinal);
        public byte[] LogoBytes { get; init; } = Array.Empty<byte>();
        public string LogoExtension { get; init; } = ".png";
    }

    public static bool TryRead(string packagePath, out ThemePackageContent? content, out string? error)
    {
        content = null;
        error = null;

        if (!File.Exists(packagePath))
        {
            error = "File not found.";
            return false;
        }

        var fi = new FileInfo(packagePath);
        if (fi.Length > ThemePackageValidator.MaxZipBytes)
        {
            error = "Package too large.";
            return false;
        }

        using var zip = ZipFile.OpenRead(packagePath);
        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        if (entries.Count == 0 || entries.Count > 2)
        {
            error = "Invalid entry count.";
            return false;
        }

        ZipArchiveEntry? manifestEntry = null;
        ZipArchiveEntry? logoEntry = null;

        foreach (var e in entries)
        {
            var name = e.FullName.Replace('\\', '/').TrimStart('/');
            if (name.Contains("..", StringComparison.Ordinal)) { error = "Path traversal."; return false; }
            if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                manifestEntry = e;
            else if (name.StartsWith("logo.", StringComparison.OrdinalIgnoreCase))
                logoEntry = e;
            else
            {
                error = $"Unexpected entry: {name}";
                return false;
            }
        }

        if (manifestEntry == null || logoEntry == null)
        {
            error = "Missing manifest.json or logo.";
            return false;
        }

        if (manifestEntry.Length > ThemePackageValidator.MaxManifestBytes)
        {
            error = "manifest.json too large.";
            return false;
        }

        string manifestJson;
        using (var ms = manifestEntry.Open())
        using (var reader = new StreamReader(ms, Encoding.UTF8))
            manifestJson = reader.ReadToEnd();

        if (!ThemePackageValidator.TryValidateManifest(manifestJson, out var manifest, out error))
            return false;

        byte[] logoBytes;
        using (var ls = logoEntry.Open())
        using (var buf = new MemoryStream())
        {
            ls.CopyTo(buf);
            logoBytes = buf.ToArray();
        }

        if (logoBytes.Length > ThemePackageValidator.MaxLogoBytes)
        {
            error = "Logo too large.";
            return false;
        }

        if (!ThemePackageValidator.TryValidateLogoImage(logoBytes, out var ext, out error))
            return false;

        var logoLayout = ThemePackageValidator.NormalizeLogoLayout(
            JsonDocument.Parse(manifestJson).RootElement);

        content = new ThemePackageContent
        {
            Id = manifest!.Id,
            DisplayName = manifest.DisplayName,
            Tokens = new Dictionary<string, string>(manifest.Tokens, StringComparer.Ordinal),
            LogoLayout = logoLayout,
            LogoBytes = logoBytes,
            LogoExtension = ext,
        };
        return true;
    }
}
