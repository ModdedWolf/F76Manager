using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace F76ManagerApp.Managers
{
    public enum GamePlatform
    {
        Steam,
        Xbox
    }

    public class PlatformManager
    {
        public const int SteamLiveAppId = 1151340;
        public const int SteamPtsAppId = 1836200;
        public const string DefaultPtsInstallDir = "Fallout 76 Playtest";

        public GamePlatform CurrentPlatform { get; private set; } = GamePlatform.Steam;
        public bool IsPts { get; private set; } = false;
        private Action<string> _logger;

        public PlatformManager(Action<string> logger = null)
        {
            _logger = logger;
        }

        public void SetPlatform(GamePlatform platform)
        {
            CurrentPlatform = platform;
            if (platform != GamePlatform.Steam)
                IsPts = false;
        }

        public void SetPts(bool isPts)
        {
            if (CurrentPlatform != GamePlatform.Steam)
            {
                IsPts = false;
                return;
            }
            IsPts = isPts;
        }

        public static GamePlatform DetectPlatformFromGamePath(string path, GamePlatform fallback)
        {
            if (string.IsNullOrWhiteSpace(path))
                return fallback;

            if (path.Contains("XboxGames", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("ModifiableWindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                return GamePlatform.Xbox;
            }

            try
            {
                string dir = path;
                if (File.Exists(path) && !Directory.Exists(path))
                    dir = Path.GetDirectoryName(path) ?? path;

                if (Directory.Exists(dir))
                {
                    if (File.Exists(Path.Combine(dir, "Project76_GamePass.exe")))
                        return GamePlatform.Xbox;
                    if (File.Exists(Path.Combine(dir, "Fallout76.exe")))
                        return GamePlatform.Steam;
                }
            }
            catch
            {
            }

            if (path.Contains("Steam", StringComparison.OrdinalIgnoreCase))
                return GamePlatform.Steam;

            return fallback;
        }

        public static bool LooksLikePtsGamePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.Contains("Playtest", StringComparison.OrdinalIgnoreCase)
                || path.Contains("Public Test", StringComparison.OrdinalIgnoreCase)
                || path.Contains("PTS", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsXbox() => CurrentPlatform == GamePlatform.Xbox;
        public string GetPlatformName() => GetPlatformLabel();
        public string GetGameExeName() => GetExecutableName();

        public string GetIniPrefix()
        {
            return CurrentPlatform == GamePlatform.Xbox ? "Project76" : "Fallout76";
        }

        public string GetDefaultDocumentsPath()
        {
            string baseDocs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
            return Path.Combine(baseDocs, "Fallout 76");
        }

        public string GetDefaultLocalAppDataPath()
        {
            string baseLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseLocal, "Fallout76");
        }

        public string GetPlatformLabel()
        {
            return CurrentPlatform == GamePlatform.Steam ? "Steam" : "Xbox";
        }

        public string GetGameLabel()
        {
            if (CurrentPlatform == GamePlatform.Xbox) return "Xbox";
            return IsPts ? "Steam PTS" : "Steam";
        }

        public string GetExecutableName()
        {
            return CurrentPlatform == GamePlatform.Xbox ? "Project76_GamePass.exe" : "Fallout76.exe";
        }

        public string GetDefaultGamePath(Action<string> promptCallback = null)
        {
            if (CurrentPlatform == GamePlatform.Steam)
            {
                if (IsPts)
                    return GetDefaultPtsGamePath();

                try
                {
                    foreach (string libraryPath in EnumerateSteamLibraryPaths())
                    {
                        string gamePath = Path.Combine(libraryPath, "steamapps", "common", "Fallout76");
                        if (Directory.Exists(gamePath)) return gamePath;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"Steam path detection failed: {ex.Message}");
                }
                return FallbackPathSearch("Steam");
            }
            else
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\GamingServices\GameConfig");
                    if (key != null)
                    {
                        foreach (var subkeyName in key.GetSubKeyNames())
                        {
                            using var subkey = key.OpenSubKey(subkeyName);
                            if (subkey?.GetValue("PackageFullName")?.ToString().Contains("BethesdaSoftworks.Fallout76") == true)
                            {
                                string installPath = subkey.GetValue("InstallPath")?.ToString();
                                if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                                    return installPath;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"Xbox path detection failed: {ex.Message}");
                }

                string fallback = FallbackPathSearch("Xbox");
                if (!string.IsNullOrEmpty(fallback)) return fallback;

                if (promptCallback != null)
                {
                    promptCallback("Please select Fallout 76 install folder.");
                }

                return @"C:\XboxGames\Fallout 76\Content";
            }
        }

        public string GetDefaultPtsGamePath()
        {
            try
            {
                foreach (string libraryPath in EnumerateSteamLibraryPaths())
                {
                    string installDir = ReadSteamAppInstallDir(libraryPath, SteamPtsAppId) ?? DefaultPtsInstallDir;
                    string gamePath = Path.Combine(libraryPath, "steamapps", "common", installDir);
                    if (Directory.Exists(gamePath) && File.Exists(Path.Combine(gamePath, "Fallout76.exe")))
                        return gamePath;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"PTS path detection failed: {ex.Message}");
            }

            return FallbackPathSearch("PTS");
        }

        public bool IsPtsInstalled()
        {
            string path = GetDefaultPtsGamePath();
            return !string.IsNullOrEmpty(path) && Directory.Exists(path);
        }

        public int? GetPtsManifestState()
        {
            try
            {
                foreach (string libraryPath in EnumerateSteamLibraryPaths())
                {
                    string acfPath = Path.Combine(libraryPath, "steamapps", $"appmanifest_{SteamPtsAppId}.acf");
                    if (!File.Exists(acfPath)) continue;
                    string content = File.ReadAllText(acfPath);
                    var match = Regex.Match(content, @"\""StateFlags\""\s+\""(\d+)\""", RegexOptions.IgnoreCase);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int flags))
                        return flags;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"PTS manifest state read failed: {ex.Message}");
            }
            return null;
        }

        public static string? TryGetSteamInstallPath()
        {
            try
            {
                string? steamPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
                if (!string.IsNullOrEmpty(steamPath) && Directory.Exists(steamPath))
                    return steamPath;

                steamPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;
                if (!string.IsNullOrEmpty(steamPath) && Directory.Exists(steamPath))
                    return steamPath;
            }
            catch
            {
            }
            return null;
        }

        private IEnumerable<string> EnumerateSteamLibraryPaths()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? steamRoot = TryGetSteamInstallPath();
            if (string.IsNullOrEmpty(steamRoot))
                yield break;

            if (seen.Add(steamRoot))
                yield return steamRoot;

            string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath))
                yield break;

            string vdfContent = File.ReadAllText(vdfPath);
            var matches = Regex.Matches(vdfContent, @"\""path\""\s+\""(.+?)\""", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                string libraryPath = match.Groups[1].Value.Replace(@"\\", @"\");
                if (string.IsNullOrWhiteSpace(libraryPath)) continue;
                if (!Directory.Exists(libraryPath)) continue;
                if (seen.Add(libraryPath))
                    yield return libraryPath;
            }
        }

        private static string? ReadSteamAppInstallDir(string libraryPath, int appId)
        {
            try
            {
                string acfPath = Path.Combine(libraryPath, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acfPath)) return null;
                string content = File.ReadAllText(acfPath);
                var match = Regex.Match(content, @"\""installdir\""\s+\""(.+?)\""", RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Groups[1].Value;
            }
            catch
            {
            }
            return null;
        }

        private string FallbackPathSearch(string platform)
        {
            string[] paths = platform switch
            {
                "PTS" => new[]
                {
                    @"D:\Steam\steamapps\common\Fallout 76 Playtest",
                    @"C:\Program Files (x86)\Steam\steamapps\common\Fallout 76 Playtest",
                },
                "Steam" => new[] { @"D:\Steam\steamapps\common\Fallout76", },
                _ => new[] { @"C:\XboxGames\Fallout 76\Content", },
            };

            foreach (var path in paths)
            {
                if (Directory.Exists(path)) return path;
            }
            return "";
        }
    }
}
