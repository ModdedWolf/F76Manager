using System;
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
        public GamePlatform CurrentPlatform { get; private set; } = GamePlatform.Steam;
        private Action<string> _logger;

        public PlatformManager(Action<string> logger = null)
        {
            _logger = logger;
        }

        public void SetPlatform(GamePlatform platform)
        {
            CurrentPlatform = platform;
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

        public string GetExecutableName()
        {
            return CurrentPlatform == GamePlatform.Xbox ? "Project76_GamePass.exe" : "Fallout76.exe";
        }

        public string GetDefaultGamePath(Action<string> promptCallback = null)
        {
            if (CurrentPlatform == GamePlatform.Steam)
            {
                try
                {
                    string steamPath = (string)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null);
                    if (steamPath == null) return FallbackPathSearch("Steam");

                    string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (!File.Exists(vdfPath)) return FallbackPathSearch("Steam");

                    string vdfContent = File.ReadAllText(vdfPath);
                    var matches = Regex.Matches(vdfContent, @"\""path\""\s+\""(.+?)\""", RegexOptions.IgnoreCase);
                    foreach (Match match in matches)
                    {
                        string libraryPath = match.Groups[1].Value.Replace(@"\\", @"\");
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

        private string FallbackPathSearch(string platform)
        {
            string[] paths = platform == "Steam" 
                ? new[] { @"D:\Steam\steamapps\common\Fallout76", }
                : new[] { @"C:\XboxGames\Fallout 76\Content", };

            foreach (var path in paths)
            {
                if (Directory.Exists(path)) return path;
            }
            return "";
        }
    }
}