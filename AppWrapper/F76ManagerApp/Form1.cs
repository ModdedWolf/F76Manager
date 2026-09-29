using System.Text.Json;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Globalization;
using System.Runtime.InteropServices;
using System.IO.Pipes;
using System.Threading;

using F76ManagerApp.Managers;

namespace F76ManagerApp;

public partial class Form1 : Form, IMessageFilter
{
    internal static Form1? MainInstance { get; private set; }

    internal static void RequestGracefulExit(int exitCode)
    {
        var main = MainInstance;
        if (main != null && !main.IsDisposed)
        {
            try
            {
                if (!main.IsHandleCreated)
                    main.CreateControl();
                if (main.InvokeRequired)
                {
                    main.BeginInvoke(() => main.RunGracefulExit(exitCode));
                    return;
                }
                main.RunGracefulExit(exitCode);
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EXIT] RequestGracefulExit marshal failed: {ex.Message}");
            }
        }
        Environment.Exit(exitCode);
    }

    private void RunGracefulExit(int exitCode)
    {
        forceExitRequested = true;
        Environment.ExitCode = exitCode;
        if (!isShuttingDown)
            CleanupResources();
        Application.Exit();
    }

    private WebView2? webView;
    private bool _webViewRecoveryAttempted = false;
    private bool _ipcServerStarted;
    private bool _ipcHandleCreatedHooked;
    public const string CurrentVersion = "1.1.0";

    public static string GetRunningProductVersion()
    {
        try
        {
            string? path = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                string? product = info.ProductVersion?.Trim();
                if (!string.IsNullOrWhiteSpace(product))
                {
                    int plus = product.IndexOf('+');
                    if (plus >= 0) product = product[..plus];
                    int dash = product.IndexOf('-');
                    if (dash >= 0) product = product[..dash];
                    return product.Trim();
                }
            }
        }
        catch { }
        return CurrentVersion;
    }
    
    private GameConfigManager _configManager = null!;
    private ModManager _modManager = null!;
    private ConflictManager _conflictManager = null!;
    private ThemePackageLoader _themePackageLoader = null!;
    private Task _themesLoadTask = Task.CompletedTask;
    private volatile bool _themesReady;
    private PlatformManager _platformManager = null!;
    private BundleManager _bundleManager = null!;
    private NexusManager _nexusManager = null!;
    private bool nexusLoggedIn = false;

    private sealed class PendingNexusImport
    {
        public long ModId { get; set; }
        public long FileId { get; set; }
        public string FileVersion { get; set; } = "";
        public long? FileUploaded { get; set; }
        public string ModName { get; set; } = "";
        public string Author { get; set; } = "";
        public string Details { get; set; } = "";
        public string Category { get; set; } = "";
        public string ExpectedArchivePath { get; set; } = "";
        public string ReplaceOriginalName { get; set; } = "";
        public bool IsBulkUpdate { get; set; }
    }

    private PendingNexusImport? _pendingNexusImport;

    private sealed class CachedModUpdate
    {
        public bool HasUpdate { get; set; }
        public bool IsUnverifiedLink { get; set; }
        public long? LatestFileId { get; set; }
        public string LatestVersion { get; set; } = "";
        public string LatestFileName { get; set; } = "";
        public long? LatestUploaded { get; set; }
    }

    private readonly Dictionary<string, CachedModUpdate> _modUpdateCache =
        new Dictionary<string, CachedModUpdate>(StringComparer.OrdinalIgnoreCase);

    private int _fullModUpdateCheckRunning;

    private sealed class ImportedCollectionModEntry
    {
        public long ModId { get; set; }
        public long FileId { get; set; }
        public string FileName { get; set; } = "";
        public string FileVersion { get; set; } = "";
    }

    private sealed class ImportedCollectionRecord
    {
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public int Revision { get; set; }
        public List<ImportedCollectionModEntry> Mods { get; set; } = new();
    }

    private ImportedCollectionRecord? _importedCollection;

    private sealed class BulkUpdateQueueItem
    {
        public string OriginalName { get; set; } = "";
        public long ModId { get; set; }
        public long FileId { get; set; }
        public string FileName { get; set; } = "";
        public string FileVersion { get; set; } = "";
        public long? FileUploaded { get; set; }
    }

    private readonly Queue<BulkUpdateQueueItem> _bulkUpdateQueue = new();
    private bool _bulkUpdateInProgress;
    private bool _bulkUpdatePreflightInProgress;
    private bool _bulkUpdateSuppressReplaceConfirm;
    private int _bulkUpdateTotal;
    private int _bulkUpdateCompleted;
    private int _bulkUpdateFailed;

    private EndorsementManager _endorsementManager = null!;
    private string gamePath = "";
    private string documentsPath = "";
    private string localAppDataPath = "";
    private string stringsPath = "";

    private string steamGamePath = "", steamDocsPath = "", steamLocalPath = "", steamStringsPath = "";
    private string xboxGamePath = "", xboxDocsPath = "", xboxLocalPath = "", xboxStringsPath = "";
    
    private int steamPipboyRed = 26, steamPipboyGreen = 255, steamPipboyBlue = 128;
    private int steamQuickboyRed = -1, steamQuickboyGreen = -1, steamQuickboyBlue = -1;
    private int steamPaRed = -1, steamPaGreen = -1, steamPaBlue = -1;
    private int steamHudRed = 26, steamHudGreen = 255, steamHudBlue = 128;
    private bool steamGodrays = true, steamDof = true, steamPing = false, steamBandwidth = false, steamFastload = false, steamGrass = true, steamVsync = true;
    private bool steamAo = true, steamBlood = true, steamDofSpecific = true, steamLensFlare = true, steamExtraBlur = true, steamVatsBlur = true;
    private int steamFov = 90;
    private int steamFov1st = 90;
    private int steamFovPipboy = 90;
    private int steamFpsCap = 144;
    private string steamShadows = "Medium", steamTaa = "TAA";
    private string steamAniso = "16x", steamWater = "High", steamDecals = "High";
    private int steamLod = 50;
    private bool steamPipboyFx = true;
    private bool pipboyCrtDefaultMigrationV1 = false;
    private bool pipboyCrtOnDefaultV2 = false;
    private bool pipboyCrtUserConfigured = false;
    private bool pipboyPrefsIniScrubV1 = false;
    private bool gameIntegrityRepairV1 = false;
    private bool looseConfigDeployRepairV1 = false;
    private string steamVolumQuality = "High", steamShadowRes = "2048", steamShadowFilter = "High";
    private string steamTextureQuality = "High", steamDecalsPerFrame = "High", steamGridLoad = "5", steamCorpseHighlight = "Low";
    private bool steamFocusShadows = true, steamRenderGrass = true, steamSsr = true, steamRainOcclusion = true;
    private bool steamNpcShadowLights = true, steamCellLoads = true, steamTiledLighting = true, steamSkipSplash = false;
    private bool steamGlassShader = true, steamPbrShadows = true, steamPlayerNames = true, steamPlayerPings = true;
    private int steamGrassFade = 7000, steamTreeDist = 25000, steamLodSky = 10, steamLeafAnim = 3600, steamConversationHistory = 4;
    private double steamGamma = 1.0;

    private int xboxPipboyRed = 26, xboxPipboyGreen = 255, xboxPipboyBlue = 128;
    private int xboxQuickboyRed = -1, xboxQuickboyGreen = -1, xboxQuickboyBlue = -1;
    private int xboxPaRed = -1, xboxPaGreen = -1, xboxPaBlue = -1;
    private int xboxHudRed = 26, xboxHudGreen = 255, xboxHudBlue = 128;
    private bool xboxGodrays = true, xboxDof = true, xboxPing = false, xboxBandwidth = false, xboxFastload = false, xboxGrass = true, xboxVsync = true;
    private bool xboxAo = true, xboxBlood = true, xboxDofSpecific = true, xboxLensFlare = true, xboxExtraBlur = true, xboxVatsBlur = true;
    private int xboxFov = 90;
    private int xboxFov1st = 90;
    private int xboxFovPipboy = 90;
    private int xboxFpsCap = 144;
    private string xboxShadows = "Medium", xboxTaa = "TAA";
    private string xboxAniso = "16x", xboxWater = "High", xboxDecals = "High";
    private int xboxLod = 50;
    private bool xboxPipboyFx = true;
    private string xboxVolumQuality = "High", xboxShadowRes = "2048", xboxShadowFilter = "High";
    private string xboxTextureQuality = "High", xboxDecalsPerFrame = "High", xboxGridLoad = "5", xboxCorpseHighlight = "Low";
    private bool xboxFocusShadows = true, xboxRenderGrass = true, xboxSsr = true, xboxRainOcclusion = true;
    private bool xboxNpcShadowLights = true, xboxCellLoads = true, xboxTiledLighting = true, xboxSkipSplash = false;
    private bool xboxGlassShader = true, xboxPbrShadows = true, xboxPlayerNames = true, xboxPlayerPings = true;
    private int xboxGrassFade = 7000, xboxTreeDist = 25000, xboxLodSky = 10, xboxLeafAnim = 3600, xboxConversationHistory = 4;
    private double xboxGamma = 1.0;

    private string settingsFolderPath = AppPaths.SettingsFolder;
    private string settingsPath => AppPaths.SettingsFile;
    private string logFolderPath = AppPaths.LogFolder;
    private string logActivityPath => Path.Combine(logFolderPath, "activity.log");
    private string logErrorPath => Path.Combine(logFolderPath, "error.log");
    private string activeTweaksPreset = "";
    private string lastSection = "dashboard"; 
    private string activeProfile = "Default Profile";
    private string profilesFolderPath = AppPaths.ProfilesFolder;

    private bool minimizeToTray = false, uiAnimations = true, platformBadgeGlow = true;
    private bool hideKofi = false;
    private bool logsPopoutOpen = false;
    private bool logsPopoutReopenOnLaunch = true;
    private bool logsPopoutKeepOpen = false;
    private bool _mainHiddenForLogsPopout = false;
    private int logsPopoutX, logsPopoutY, logsPopoutW, logsPopoutH;
    private bool logsPopoutMaximized = false;
    private bool logsPopoutBoundsValid = false;
    private bool syncPlatforms = false, autoForceDeploy = false, virtualModMode = false;
    private bool configEditorSpellCheck = false;
    private bool confirmBeforeDeleteMod = false;
    private bool confirmBeforeRemoveOldModOnUpdate = false;
    private bool updateModsInAllPresets = false;
    private string modGroups = "{}";
    private string modPresetsJson = "";
    private string activeModPreset = "Default";
    private string applicationLanguage = "en-US";
    private string uiTheme = "fallout";
    private string archiveKeyName = "auto";
    private string keybindsJson = "";
    private string sevenZipPath = "";
    private string rarExtractorPath = "";

    private int windowWidth = 1280;
    private int windowHeight = 800;
    private int windowTop = -1;
    private int windowLeft = -1;
    private bool windowMaximized = false;

    private static string GetDefaultStringsPathFromGamePath(string gp)
    {
        if (string.IsNullOrEmpty(gp)) return "";
        string dataPath = gp.EndsWith("Data", StringComparison.OrdinalIgnoreCase)
            ? gp
            : Path.Combine(gp, "Data");
        return Path.Combine(dataPath, "Strings");
    }

    private void SyncAppPaths(bool quick = false)
    {
        if (!quick)
            TryAutoPopulateArchiveExecutablePaths();
        AppPaths.GamePath = gamePath;
        AppPaths.DocumentsPath = documentsPath;
        AppPaths.LocalAppDataPath = localAppDataPath;
        AppPaths.StringsPath = stringsPath;
        AppPaths.SetPlatform(_platformManager.IsXbox());
        _modManager.ArchiveKeyPreference = archiveKeyName;
        _modManager.VirtualModMode = virtualModMode;
        _modManager.SevenZipPath = sevenZipPath;
        _modManager.RarExtractorPath = rarExtractorPath;
        if (!quick)
            _modManager.EnsureManagedStagingHydrated();
        EnsureTweakIniWatcher();
    }

    private void ApplyDefaultPathsIfEmpty()
    {
        if (string.IsNullOrWhiteSpace(documentsPath))
            documentsPath = _platformManager.GetDefaultDocumentsPath() ?? "";
        if (string.IsNullOrWhiteSpace(localAppDataPath))
            localAppDataPath = _platformManager.GetDefaultLocalAppDataPath() ?? "";

        if (string.IsNullOrWhiteSpace(gamePath))
            gamePath = _platformManager.GetDefaultGamePath() ?? "";

        if (string.IsNullOrWhiteSpace(stringsPath) && !string.IsNullOrWhiteSpace(gamePath))
            stringsPath = Path.Combine(gamePath, "Data", "Strings");

        if (string.IsNullOrWhiteSpace(xboxGamePath))
            xboxGamePath = @"C:\XboxGames\Fallout 76\Content";
        if (string.IsNullOrWhiteSpace(xboxDocsPath) && !string.IsNullOrWhiteSpace(documentsPath))
            xboxDocsPath = documentsPath;
        if (string.IsNullOrWhiteSpace(xboxLocalPath) && !string.IsNullOrWhiteSpace(localAppDataPath))
            xboxLocalPath = localAppDataPath;
        if (string.IsNullOrWhiteSpace(steamStringsPath) && !string.IsNullOrWhiteSpace(stringsPath))
            steamStringsPath = stringsPath;
        if (string.IsNullOrWhiteSpace(xboxStringsPath))
            xboxStringsPath = GetDefaultStringsPathFromGamePath(xboxGamePath);
    }

    private void EnsureAllPrefsIniWritable()
    {
        _configManager.EnsurePrefsIniWritable();

        if (!string.IsNullOrWhiteSpace(steamDocsPath) && Directory.Exists(steamDocsPath))
            _configManager.EnsurePrefsIniWritable(steamDocsPath, "Fallout76");

        if (!string.IsNullOrWhiteSpace(xboxDocsPath) && Directory.Exists(xboxDocsPath))
            _configManager.EnsurePrefsIniWritable(xboxDocsPath, "Project76");
    }

    private void TryAutoPopulateArchiveExecutablePaths()
    {
        bool changed = false;
        try
        {
            if (string.IsNullOrWhiteSpace(sevenZipPath) || !File.Exists(sevenZipPath.Trim()))
            {
                string? d = ModManager.AutoDetectSevenZipExecutable();
                if (!string.IsNullOrEmpty(d))
                {
                    sevenZipPath = d;
                    changed = true;
                }
                else if (!string.IsNullOrWhiteSpace(sevenZipPath))
                {
                    sevenZipPath = "";
                    changed = true;
                }
            }
            if (string.IsNullOrWhiteSpace(rarExtractorPath) || !File.Exists(rarExtractorPath.Trim()))
            {
                string? d = ModManager.AutoDetectRarExtractorExecutable();
                if (!string.IsNullOrEmpty(d))
                {
                    rarExtractorPath = d;
                    changed = true;
                }
                else if (!string.IsNullOrWhiteSpace(rarExtractorPath))
                {
                    rarExtractorPath = "";
                    changed = true;
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"[PATHS] Archive executable auto-detect failed: {ex.Message}");
        }
        if (changed) SaveSettings();
    }
    private NotifyIcon? trayIcon;
    private ContextMenuStrip? trayContextMenu;
    private bool isShuttingDown = false;
    private bool forceExitRequested = false;
    private string lastGameLaunch = "Never";

    [StructLayout(LayoutKind.Sequential)]
    struct CHANGEFILTERSTRUCT { public uint cbSize; public uint ExtStatus; }

    public string InitialNxmLink { get; set; } = "";

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!forceExitRequested && minimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
             e.Cancel = true;
             this.Hide();
             trayIcon?.ShowBalloonTip(3000, "Fallout 76 Manager", "The application is still running in the system tray.", ToolTipIcon.Info);
             return;
        }

        if (!forceExitRequested
            && logsPopoutKeepOpen
            && e.CloseReason == CloseReason.UserClosing
            && _logsPopout != null
            && !_logsPopout.IsDisposed)
        {
            e.Cancel = true;
            _mainHiddenForLogsPopout = true;
            try { SaveSettings(); } catch { }
            this.Hide();
            return;
        }

        CleanupResources();
    }

    private void OnApplicationExit(object? sender, EventArgs e)
    {
        CleanupResources();
    }

    private void CleanupResources()
    {
        if (isShuttingDown) return;
        isShuttingDown = true;

        try {
            var currentP = profiles.FirstOrDefault(p => p.Name == activeProfile);
            if (currentP != null) {
                var captured = CaptureCurrentState(activeProfile);
                currentP.Settings = captured.Settings;
                currentP.EnabledMods = captured.EnabledMods;
            }
            SaveProfiles();
        } catch (Exception ex) {
            LogError($"[SHUTDOWN] Failed to persist profile state before exit: {ex.Message}");
        }

        LogActivity("Shutting down...");
        try
        {
            if (_logsPopout != null && !_logsPopout.IsDisposed)
                CaptureLogsPopoutBounds(_logsPopout);
        }
        catch { }
        CloseLogsPopout();
        CloseThemeCreator();
        
        if (this.WindowState == FormWindowState.Maximized)
        {
            windowMaximized = true;
            windowWidth = this.RestoreBounds.Width;
            windowHeight = this.RestoreBounds.Height;
            windowTop = this.RestoreBounds.Top;
            windowLeft = this.RestoreBounds.Left;
        }
        else if (this.WindowState == FormWindowState.Normal)
        {
            windowMaximized = false;
            windowWidth = this.Width;
            windowHeight = this.Height;
            windowTop = this.Top;
            windowLeft = this.Left;
        }

        SaveSettings();

        Security.StopMonitoring();
        DisposeTrayIcon();

        Application.ApplicationExit -= OnApplicationExit;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    private void ApplyWindowChrome(int captionColorRef, int textColorRef, int borderColorRef)
        => ApplyWindowChromeToHandle(this.Handle, captionColorRef, textColorRef, borderColorRef);

    internal void ApplyWindowChromeToHandle(IntPtr hwnd, int captionColorRef, int textColorRef, int borderColorRef)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColorRef, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColorRef, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColorRef, sizeof(int));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CHROME] Failed to apply window chrome: {ex.Message}");
        }
    }

    private static int HexToColorRef(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return 0;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length < 6) return 0;
        if (!byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)) return 0;
        if (!byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)) return 0;
        if (!byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b)) return 0;
        return (b << 16) | (g << 8) | r;
    }

    private void ApplyWindowChromeFromTheme(string themeId)
        => ApplyWindowChromeFromThemeToHandle(this.Handle, themeId);

    internal void ApplyWindowChromeFromThemeToHandle(IntPtr hwnd, string themeId)
    {
        string caption = "#121212", text = "#e0e0e0", border = "#333333";
        switch ((themeId ?? "").Trim().ToLowerInvariant())
        {
            case "vault-tec":
                caption = "#081220"; text = "#e8f1fa"; border = "#243a5c"; break;
            case "red-black":
                caption = "#0b0908"; text = "#e6ddd2"; border = "#3d1f18"; break;
            case "black-white":
                caption = "#000000"; text = "#cacaca"; border = "#383838"; break;
            default:
                caption = "#121212"; text = "#e0e0e0"; border = "#333333"; break;
        }
        ApplyWindowChromeToHandle(hwnd, HexToColorRef(caption), HexToColorRef(text), HexToColorRef(border));
    }

    internal void ApplyWindowChromeFromColorsToHandle(IntPtr hwnd, string caption, string text, string border)
    {
        if (string.IsNullOrWhiteSpace(caption)) return;
        ApplyWindowChromeToHandle(hwnd, HexToColorRef(caption), HexToColorRef(text), HexToColorRef(border));
    }

    private void HandleSetWindowChrome(System.Text.Json.JsonElement root)
    {
        string caption = root.TryGetProperty("caption", out var c) ? (c.GetString() ?? "") : "";
        string text = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
        string border = root.TryGetProperty("border", out var b) ? (b.GetString() ?? "") : "";
        if (string.IsNullOrWhiteSpace(caption)) return;
        ApplyWindowChrome(HexToColorRef(caption), HexToColorRef(text), HexToColorRef(border));

        string themeId = root.TryGetProperty("themeId", out var tid) ? (tid.GetString() ?? "") : "";
        string css = root.TryGetProperty("css", out var cssEl) ? (cssEl.GetString() ?? "") : "";
        _lastThemeSync = new ThemeSyncState
        {
            ThemeId = themeId,
            Css = css,
            Caption = caption,
            Text = text,
            Border = border,
        };
        NotifyLogsPopoutTheme();
    }

    private void InitializeTrayIcon()
    {
        if (isShuttingDown) return;
        DisposeTrayIcon();

        trayContextMenu = new ContextMenuStrip();
        trayContextMenu.Items.Add("Open", null, TrayOpenClicked);
        trayContextMenu.Items.Add("Exit", null, TrayExitClicked);

        Icon? trayImage = null;
        try
        {
            if (this.Icon != null)
                trayImage = (Icon)this.Icon.Clone();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TRAY] Icon clone failed, tray without icon: {ex.Message}");
        }

        trayIcon = new NotifyIcon
        {
            Icon = trayImage,
            Visible = true,
            Text = "Fallout 76 Manager",
            ContextMenuStrip = trayContextMenu
        };
        trayIcon.DoubleClick += TrayIcon_DoubleClick;
    }

    private void DisposeTrayIcon()
    {
        if (trayIcon != null)
        {
            try
            {
                trayIcon.DoubleClick -= TrayIcon_DoubleClick;
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TRAY] Failed to dispose tray icon: {ex.Message}");
            }
            finally
            {
                trayIcon = null;
            }
        }

        if (trayContextMenu != null)
        {
            try { trayContextMenu.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[TRAY] Failed to dispose tray context menu: {ex.Message}"); }
            finally { trayContextMenu = null; }
        }
    }

    private void TrayOpenClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void TrayIcon_DoubleClick(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void TrayExitClicked(object? sender, EventArgs e)
    {
        forceExitRequested = true;
        this.Close();
    }

    private void ShowMainWindow()
    {
        if (isShuttingDown) return;
        _mainHiddenForLogsPopout = false;
        this.Show();
        this.WindowState = FormWindowState.Normal;
        this.Activate();
    }

    private void SendMessageToWeb(string json)
    {
        if (this.InvokeRequired)
        {
            this.Invoke(() => SendMessageToWeb(json));
            return;
        }

        if (webView != null && webView.CoreWebView2 != null)
            webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void SendStatusMessage(string type, string text, string key = null, object[] args = null)
    {
        SendMessageToWeb(JsonSerializer.Serialize(new { type = "STATUS", status = new { type, text, key, args } }));
    }

    private void LogActivity(string msg)
    {
        string path = logActivityPath;
        string line = $"[{DateTime.Now:T}] {msg}\n";
        Task.Run(() =>
        {
            try { File.AppendAllText(path, line); }
            catch (Exception ex) { Debug.WriteLine($"[LOG] Failed to write activity log: {ex.Message}"); }
        });
    }

    private void LogError(string msg)
    {
        LogActivity($"[ERROR] {msg}");
        try { File.AppendAllText(logErrorPath, $"[{DateTime.Now:T}] [ERROR] {msg}\n"); } catch (Exception ex) { Debug.WriteLine($"[LOG] Failed to write error log: {ex.Message}"); }
    }

    private void LogSuccess(string msg) => LogActivity($"[SUCCESS] {msg}");
    private void LogWarning(string msg) => LogActivity($"[WARN] {msg}");

    private void CleanupLogs() { }

    private bool IsRunningAsAdmin()
    {
        try {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        } catch (Exception ex) {
            Debug.WriteLine($"[SECURITY] Failed admin check: {ex.Message}");
            return false;
        }
    }

    private bool IsGameRunning()
    {
        string exeName = _platformManager.GetGameExeName().Replace(".exe", "");
        return Process.GetProcessesByName(exeName).Length > 0;
    }

    private void EnsureIpcServerStarted()
    {
        if (_ipcServerStarted) return;

        if (IsHandleCreated)
        {
            _ipcServerStarted = true;
            StartIpcServer();
            return;
        }

        if (_ipcHandleCreatedHooked) return;
        _ipcHandleCreatedHooked = true;

        void OnHandleCreated(object? sender, EventArgs e)
        {
            this.HandleCreated -= OnHandleCreated;
            if (_ipcServerStarted) return;
            _ipcServerStarted = true;
            StartIpcServer();
        }

        this.HandleCreated += OnHandleCreated;
    }

    private void RunOnUiThreadSafe(Action action)
    {
        if (action == null) return;
        if (IsDisposed || Disposing) return;

        try
        {
            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void StartIpcServer()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using (var server = new NamedPipeServerStream("F76ManagerPipe", PipeDirection.In))
                    {
                        await server.WaitForConnectionAsync();
                        using (var reader = new StreamReader(server))
                        {
                            string? args = await reader.ReadToEndAsync();
                            if (!string.IsNullOrEmpty(args))
                            {
                                RunOnUiThreadSafe(() =>
                                {
                                    if (args.StartsWith("nxm://"))
                                    {
                                        _ = HandleNxmLinkAsync(args);
                                        _mainHiddenForLogsPopout = false;
                                        this.Show();
                                        this.WindowState = FormWindowState.Normal;
                                        this.Activate();
                                    }
                                    else if (args == "SHOW")
                                    {
                                        _mainHiddenForLogsPopout = false;
                                        this.Show();
                                        this.WindowState = FormWindowState.Normal;
                                        this.Activate();
                                    }
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogError($"IPC Server Error: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        });
    }
    public class Profile 
    { 
        public string Name { get; set; } = "New Profile";
        public Dictionary<string, object> Settings { get; set; } = new();
        public List<string> EnabledMods { get; set; } = new();
        public List<string> ProfileMods { get; set; } = new();
    }
}

