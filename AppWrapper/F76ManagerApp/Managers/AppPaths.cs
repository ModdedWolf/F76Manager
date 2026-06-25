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

        public static bool IsProtectedCoreIniFileName(string? fileName) =>
            !string.IsNullOrWhiteSpace(fileName) && (
                fileName.Equals("Fallout76Custom.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Fallout76Prefs.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Project76Custom.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Project76Prefs.ini", StringComparison.OrdinalIgnoreCase));

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
