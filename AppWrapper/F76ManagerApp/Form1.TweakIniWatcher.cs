using F76ManagerApp.Managers;

namespace F76ManagerApp;

public partial class Form1
{
    private FileSystemWatcher? _tweakIniWatcher;
    private System.Windows.Forms.Timer? _tweakIniDebounceTimer;

    private void EnsureTweakIniWatcher()
    {
        DisposeTweakIniWatcher();

        if (string.IsNullOrEmpty(documentsPath) || !Directory.Exists(documentsPath))
            return;

        string iniPrefix = AppPaths.IniPrefix;
        string prefsName = $"{iniPrefix}Prefs.ini";
        string customName = $"{iniPrefix}Custom.ini";

        _tweakIniDebounceTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _tweakIniDebounceTimer.Tick += (_, _) =>
        {
            _tweakIniDebounceTimer?.Stop();
            SyncTweakValuesFromIni();
            SendDataToWeb();
        };

        _tweakIniWatcher = new FileSystemWatcher(documentsPath)
        {
            Filter = $"{iniPrefix}*.ini",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };

        _tweakIniWatcher.Changed += (_, e) => OnTweakIniFileEvent(e.FullPath, prefsName, customName);
        _tweakIniWatcher.Created += (_, e) => OnTweakIniFileEvent(e.FullPath, prefsName, customName);
        _tweakIniWatcher.Renamed += (_, e) => OnTweakIniFileEvent(e.FullPath, prefsName, customName);
        _tweakIniWatcher.EnableRaisingEvents = true;
    }

    private void OnTweakIniFileEvent(string fullPath, string prefsName, string customName)
    {
        string fileName = Path.GetFileName(fullPath);
        if (!string.Equals(fileName, prefsName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fileName, customName, StringComparison.OrdinalIgnoreCase))
            return;

        if (InvokeRequired)
        {
            BeginInvoke(ScheduleTweakIniDebounce);
            return;
        }

        ScheduleTweakIniDebounce();
    }

    private void ScheduleTweakIniDebounce()
    {
        if (_tweakIniDebounceTimer == null) return;
        _tweakIniDebounceTimer.Stop();
        _tweakIniDebounceTimer.Start();
    }

    private void DisposeTweakIniWatcher()
    {
        if (_tweakIniWatcher != null)
        {
            _tweakIniWatcher.EnableRaisingEvents = false;
            _tweakIniWatcher.Dispose();
            _tweakIniWatcher = null;
        }

        if (_tweakIniDebounceTimer != null)
        {
            _tweakIniDebounceTimer.Stop();
            _tweakIniDebounceTimer.Dispose();
            _tweakIniDebounceTimer = null;
        }
    }
}
