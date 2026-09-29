using System;
using System.IO;

namespace F76ManagerApp.Managers
{
    public static class AppPaths
    {
        public static string GamePath { get; set; } = "";
        public static string DocumentsPath { get; set; } = "";
        public static string LocalAppDataPath { get; set; } = "";
        
        public static string SettingsFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings");
        public static string SettingsFile => Path.Combine(SettingsFolder, "settings.json");
        public static string ThemesFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
        public static string ThemesCacheFolder => Path.Combine(ThemesFolder, ".cache");
        public static string BundledThemesFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BundledThemes");
        public static string LogFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        public static string ProfilesFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles");
        public static string ProfilesFile => Path.Combine(ProfilesFolder, "profiles.json");
        public static string ModsMetadataFile => Path.Combine(SettingsFolder, "mods.json");
        public static string ManagedArtifactsFile => Path.Combine(SettingsFolder, "managed-artifacts.json");
        public static string IniOverlaysFolder => Path.Combine(SettingsFolder, "IniOverlays");

        public const string F76AddToCustomFileName = "F76-Manager-AddToCustom.ini";
        public const string F76AddToPrefsFileName = "F76-Manager-AddToPrefs.ini";
        public const string F76AddToFallout76FileName = "F76-Manager-AddToFallout76.ini";

        private static readonly string[] LegacyF76AddToOverlayFileNames =
        {
            "F76AddToCustom.ini",
            "F76AddToPrefs.ini",
            "F76AddToFallout76.ini"
        };

        public static readonly string[] F76AddToOverlayFileNames =
        {
            F76AddToCustomFileName,
            F76AddToPrefsFileName,
            F76AddToFallout76FileName
        };

        public static bool IsXbox { get; private set; } = false;
        public static string PlatformFolderName => IsXbox ? "Xbox" : "Steam";

        public static void SetPlatform(bool isXbox)
        {
            IsXbox = isXbox;
            if (string.IsNullOrEmpty(StringsPath)) StringsPath = Path.Combine(DataPath, "Strings");
        }

        public static string IniPrefix => IsXbox ? "Project76" : "Fallout76";
        public static string CustomIniPath => Path.Combine(DocumentsPath, $"{IniPrefix}Custom.ini");
        public static string PrefsIniPath => Path.Combine(DocumentsPath, $"{IniPrefix}Prefs.ini");
        public static string BaseGameIniPath => Path.Combine(DocumentsPath, $"{IniPrefix}.ini");

        public static string GetF76AddToOverlayPath(string fileName) =>
            Path.Combine(IniOverlaysFolder, Path.GetFileName(fileName));

        public static bool IsF76AddToOverlayFileName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            string name = Path.GetFileName(fileName);
            foreach (var known in F76AddToOverlayFileNames)
            {
                if (name.Equals(known, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            foreach (var legacy in LegacyF76AddToOverlayFileNames)
            {
                if (name.Equals(legacy, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static string? GetF76AddToTargetPath(string overlayFileName)
        {
            string name = Path.GetFileName(overlayFileName ?? "");
            if (name.Equals(F76AddToCustomFileName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("F76AddToCustom.ini", StringComparison.OrdinalIgnoreCase))
                return CustomIniPath;
            if (name.Equals(F76AddToPrefsFileName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("F76AddToPrefs.ini", StringComparison.OrdinalIgnoreCase))
                return PrefsIniPath;
            if (name.Equals(F76AddToFallout76FileName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("F76AddToFallout76.ini", StringComparison.OrdinalIgnoreCase))
                return BaseGameIniPath;
            return null;
        }

        public static void MigrateLegacyF76AddToOverlayFiles()
        {
            try
            {
                if (!Directory.Exists(IniOverlaysFolder)) return;
                for (int i = 0; i < LegacyF76AddToOverlayFileNames.Length; i++)
                {
                    string legacyPath = Path.Combine(IniOverlaysFolder, LegacyF76AddToOverlayFileNames[i]);
                    string modernPath = Path.Combine(IniOverlaysFolder, F76AddToOverlayFileNames[i]);
                    if (!File.Exists(legacyPath)) continue;
                    if (File.Exists(modernPath))
                    {
                        File.Delete(legacyPath);
                        continue;
                    }
                    File.Move(legacyPath, modernPath);
                }
            }
            catch
            {
            }
        }

        public static bool IsProtectedCoreIniFileName(string? fileName) =>
            !string.IsNullOrWhiteSpace(fileName) && (
                fileName.Equals("Fallout76Custom.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Fallout76Prefs.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Project76Custom.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Project76Prefs.ini", StringComparison.OrdinalIgnoreCase) ||
                IsF76AddToOverlayFileName(fileName));

        public static bool IsProtectedCoreIniListKey(string? originalName)
        {
            if (string.IsNullOrWhiteSpace(originalName)) return false;
            string norm = originalName.Replace('\\', '/');
            if (norm.StartsWith("CoreIni/", StringComparison.OrdinalIgnoreCase)) return true;
            return IsProtectedCoreIniFileName(Path.GetFileName(norm));
        }
        
        public static string DataPath => string.IsNullOrEmpty(GamePath) ? "" :
            (GamePath.EndsWith("Data", StringComparison.OrdinalIgnoreCase) 
            ? GamePath 
            : Path.Combine(GamePath, "Data"));

        public static string GameInstallRoot
        {
            get
            {
                try
                {
                    if (string.IsNullOrEmpty(GamePath)) return "";
                    return GamePath.EndsWith("Data", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFullPath(Path.Combine(GamePath, ".."))
                        : Path.GetFullPath(GamePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
                catch
                {
                    return "";
                }
            }
        }
            
        public static string StringsPath { get; set; } = "";
        public static string BundlesPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Bundles");
        public static string DisabledModsPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Disabled Mods");
        public static string ManagedStagingPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Managed Staging", PlatformFolderName);
        public static string ManagedStagingDataPath => Path.Combine(ManagedStagingPath, "Data");
        public static string ManagedStagingStringsPath => Path.Combine(ManagedStagingDataPath, "Strings");
        public static string ManagedStagingGameRootPath => Path.Combine(ManagedStagingPath, "GameRoot");
        public static string LegacyManagedStagingPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Managed Staging", "Data");

        public static string PluginsFilePath => Path.Combine(LocalAppDataPath, "plugins.txt");
        public static string GlobalStatsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F76Manager", "stats.json");
    }
}
