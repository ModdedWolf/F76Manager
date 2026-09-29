using System.IO.Compression;
using System.Text;
using F76ManagerApp;

namespace F76ManagerApp.Managers;

public sealed class LoadedUserTheme
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string LogoVirtualPath { get; init; } = "";
    public string CssBlock { get; init; } = "";
    public string SourceFile { get; init; } = "";
}

public sealed class ThemeLoadEntry
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string FileName { get; init; } = "";
}

public sealed class ThemeRejectEntry
{
    public string FileName { get; init; } = "";
    public string Error { get; init; } = "";
}

public sealed class ThemeReloadResult
{
    public string ThemesFolder { get; init; } = "";
    public IReadOnlyList<ThemeLoadEntry> Loaded { get; init; } = Array.Empty<ThemeLoadEntry>();
    public IReadOnlyList<ThemeRejectEntry> Rejected { get; init; } = Array.Empty<ThemeRejectEntry>();
}

public sealed class ThemePackageLoader
{
    private readonly Action<string, string> _log;
    private readonly List<LoadedUserTheme> _themes = new();
    private readonly object _lock = new();

    public ThemePackageLoader(Action<string> logActivity, Action<string> logError)
    {
        _log = (a, e) =>
        {
            try { if (!string.IsNullOrEmpty(a)) logActivity(a); } catch { }
            if (!string.IsNullOrEmpty(e)) try { logError(e); } catch { }
        };
    }

    public IReadOnlyList<LoadedUserTheme> Themes
    {
        get { lock (_lock) return _themes.ToList(); }
    }

    public bool IsUserTheme(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_lock) return _themes.Any(t => t.Id == id);
    }

    public LoadedUserTheme? GetTheme(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock) return _themes.FirstOrDefault(t => t.Id == id);
    }

    public void Reload() => ReloadWithResult();

    public ThemeReloadResult ReloadWithResult()
    {
        var loaded = new List<ThemeLoadEntry>();
        var rejected = new List<ThemeRejectEntry>();
        var nextThemes = new List<LoadedUserTheme>();
        var themesFolder = AppPaths.ThemesFolder;

        try
        {
            Directory.CreateDirectory(AppPaths.ThemesFolder);
            Directory.CreateDirectory(AppPaths.ThemesCacheFolder);
        }
        catch (Exception ex)
        {
            _log("", $"[THEMES] Failed to create Themes folder: {ex.Message}");
            rejected.Add(new ThemeRejectEntry { FileName = "(folder)", Error = ex.Message });
            lock (_lock) _themes.Clear();
            return new ThemeReloadResult
            {
                ThemesFolder = themesFolder,
                Loaded = loaded,
                Rejected = rejected,
            };
        }

        void Warn(string message) => _log("", message);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(AppPaths.ThemesFolder, "*.f76theme", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            try
            {
                if (TryLoadPackage(path, out var theme, out var err, Warn))
                {
                    int dup = nextThemes.FindIndex(t => t.Id == theme!.Id);
                    if (dup >= 0)
                    {
                        var keep = File.GetLastWriteTimeUtc(nextThemes[dup].SourceFile) >= File.GetLastWriteTimeUtc(path)
                            ? nextThemes[dup]
                            : theme!;
                        var skipped = ReferenceEquals(keep, theme) ? nextThemes[dup] : theme!;
                        nextThemes[dup] = keep;
                        duplicateIds.Add(keep.Id);
                        int loadedIdx = loaded.FindIndex(l => l.Id == keep.Id);
                        if (loadedIdx >= 0)
                        {
                            loaded[loadedIdx] = new ThemeLoadEntry
                            {
                                Id = keep.Id,
                                DisplayName = keep.DisplayName,
                                FileName = Path.GetFileName(keep.SourceFile),
                            };
                        }
                        _log("", $"[THEMES] Duplicate theme id '{theme!.Id}': using {Path.GetFileName(keep.SourceFile)}, ignoring {Path.GetFileName(skipped.SourceFile)}");
                        continue;
                    }
                    nextThemes.Add(theme!);
                    loaded.Add(new ThemeLoadEntry
                    {
                        Id = theme!.Id,
                        DisplayName = theme.DisplayName,
                        FileName = fileName,
                    });
                    _log($"[THEMES] Loaded user theme '{theme.Id}' from {fileName}", "");
                }
                else
                {
                    var message = err ?? "Unknown error.";
                    rejected.Add(new ThemeRejectEntry { FileName = fileName, Error = message });
                    _log("", $"[THEMES] Rejected {fileName}: {message}");
                }
            }
            catch (Exception ex)
            {
                rejected.Add(new ThemeRejectEntry { FileName = fileName, Error = ex.Message });
                _log("", $"[THEMES] Error loading {fileName}: {ex.Message}");
            }
        }

        for (int i = 0; i < nextThemes.Count; i++)
        {
            if (!duplicateIds.Contains(nextThemes[i].Id)) continue;
            try
            {
                if (TryLoadPackage(nextThemes[i].SourceFile, out var reloaded, out _, Warn) && reloaded != null)
                    nextThemes[i] = reloaded;
            }
            catch (Exception ex)
            {
                _log("", $"[THEMES] Could not refresh logo for duplicate id '{nextThemes[i].Id}': {ex.Message}");
            }
        }

        lock (_lock)
        {
            _themes.Clear();
            _themes.AddRange(nextThemes);
        }

        return new ThemeReloadResult
        {
            ThemesFolder = themesFolder,
            Loaded = loaded,
            Rejected = rejected,
        };
    }

    public static bool TryLoadPackage(string packagePath, out LoadedUserTheme? theme, out string? error)
        => TryLoadPackage(packagePath, out theme, out error, warn: null);

    public static bool TryLoadPackage(string packagePath, out LoadedUserTheme? theme, out string? error, Action<string>? warn)
    {
        theme = null;
        error = null;

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

        if (ThemeIds.IsBuiltIn(manifest!.Id))
        {
            error =
                $"Theme id '{manifest.Id}' is reserved for a built-in theme. Change \"id\" in manifest.json to a unique name (e.g. my-vault-theme).";
            return false;
        }

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

        var cacheDir = Path.Combine(AppPaths.ThemesCacheFolder, manifest!.Id);
        var logoDest = Path.Combine(cacheDir, $"logo{ext}");
        try
        {
            Directory.CreateDirectory(cacheDir);

            bool needsWrite = true;
            if (File.Exists(logoDest))
            {
                try
                {
                    var existing = File.ReadAllBytes(logoDest);
                    if (existing.AsSpan().SequenceEqual(logoBytes))
                        needsWrite = false;
                }
                catch
                {
                }
            }

            if (needsWrite)
            {
                var tmp = logoDest + ".tmp";
                File.WriteAllBytes(tmp, logoBytes);
                File.Move(tmp, logoDest, overwrite: true);

                foreach (var stale in Directory.EnumerateFiles(cacheDir, "logo.*"))
                {
                    if (string.Equals(stale, logoDest, StringComparison.OrdinalIgnoreCase)) continue;
                    if (stale.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(stale); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            warn?.Invoke($"[THEMES] Could not refresh logo cache for '{manifest.Id}': {ex.Message}");
        }

        var logoLayout = ThemePackageValidator.NormalizeLogoLayout(
            System.Text.Json.JsonDocument.Parse(manifestJson).RootElement);
        var css = ThemePackageValidator.BuildThemeCssBlock(manifest.Id, manifest.Tokens, logoLayout);

        var revision = fi.LastWriteTimeUtc.Ticks.ToString("x");

        theme = new LoadedUserTheme
        {
            Id = manifest.Id,
            DisplayName = manifest.DisplayName,
            LogoVirtualPath = $"user-theme-logo/{manifest.Id}?r={revision}",
            CssBlock = css,
            SourceFile = packagePath,
        };
        return true;
    }

    public bool TryInstallPackage(string sourcePath, out LoadedUserTheme? theme, out string? error)
    {
        theme = null;
        error = null;

        if (!File.Exists(sourcePath))
        {
            error = "File not found.";
            return false;
        }

        if (!TryLoadPackage(sourcePath, out var preview, out error, msg => _log("", msg)))
            return false;

        try
        {
            Directory.CreateDirectory(AppPaths.ThemesFolder);

            foreach (var existing in Directory.EnumerateFiles(AppPaths.ThemesFolder, "*.f76theme", SearchOption.TopDirectoryOnly))
            {
                if (ThemePackageReader.TryRead(existing, out var content, out _)
                    && content != null
                    && content.Id == preview!.Id
                    && !string.Equals(existing, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(existing); } catch { }
                }
            }

            var fileName = Path.GetFileName(sourcePath);
            if (!fileName.EndsWith(".f76theme", StringComparison.OrdinalIgnoreCase))
                fileName = $"{ThemePackageValidator.SanitizeExportFileName(preview.DisplayName)}.f76theme";

            var destPath = Path.Combine(AppPaths.ThemesFolder, fileName);
            File.Copy(sourcePath, destPath, overwrite: true);

            ReloadWithResult();
            theme = GetTheme(preview.Id);
            if (theme == null)
            {
                error = "Theme installed but failed to reload.";
                return false;
            }

            _log($"[THEMES] Installed user theme '{theme.Id}' from {fileName}", "");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public string? ResolveLogoPhysicalPath(string virtualPath)
    {
        const string prefix = "user-theme-logo/";
        if (!virtualPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var id = virtualPath[prefix.Length..].Split('?')[0].Trim('/');
        if (!ThemePackageValidator.IsValidThemeId(id)) return null;

        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" })
        {
            var p = Path.Combine(AppPaths.ThemesCacheFolder, id, $"logo{ext}");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
