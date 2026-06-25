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

    private WebView2 webView;
    private bool _webViewRecoveryAttempted = false;
    private bool _ipcServerStarted;
    private bool _ipcHandleCreatedHooked;
    public const string CurrentVersion = "X.X.X";

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
    
    private GameConfigManager _configManager;
    private ModManager _modManager;
    private ConflictManager _conflictManager;
    private ThemePackageLoader _themePackageLoader;
    private PlatformManager _platformManager;
    private BundleManager _bundleManager;
    private NexusManager _nexusManager;
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

    private EndorsementManager _endorsementManager; 
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
    private bool syncPlatforms = false, autoForceDeploy = false, virtualModMode = false;
    private bool configEditorSpellCheck = false;
    private bool confirmBeforeDeleteMod = false;
    private bool confirmBeforeRemoveOldModOnUpdate = false;
    private string modGroups = "{}";
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

    private void SyncAppPaths()
    {
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
        _modManager.EnsureManagedStagingHydrated();
        EnsureTweakIniWatcher();
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

    public string InitialNxmLink { get; set; }

    public Form1()
    {
        InitializeComponent();
        this.Load += async (s, e) => {
             if (!string.IsNullOrEmpty(InitialNxmLink))
             {
                 await Task.Delay(2000);
                 _ = HandleNxmLinkAsync(InitialNxmLink);
             }
        };
        try {
            if (!Directory.Exists(logFolderPath)) Directory.CreateDirectory(logFolderPath);
            
            _configManager = new GameConfigManager(LogActivity, (t, m) => this.Invoke(() => SendStatusMessage(t, m)));
            _modManager = new ModManager(_configManager, LogActivity, (t, m) => this.Invoke(() => SendStatusMessage(t, m)));
            _conflictManager = new ConflictManager(LogActivity);
            _themePackageLoader = new ThemePackageLoader(LogActivity, LogError);
            _themePackageLoader.Reload();
            _platformManager = new PlatformManager();
            _bundleManager = new BundleManager(LogActivity, (t, m) => this.Invoke(() => SendStatusMessage(t, m)), _modManager.UpdateModMetadata, _modManager.ToggleMods, LogError);
            
            InitializeNexusManager(null);
            
            Task.Run(() => _nexusManager.RegisterNxmProtocol());

            EnsureIpcServerStarted();

            gamePath = _platformManager.GetDefaultGamePath() ?? "";
            documentsPath = _platformManager.GetDefaultDocumentsPath() ?? "";
            localAppDataPath = _platformManager.GetDefaultLocalAppDataPath() ?? "";
            stringsPath = Path.Combine(gamePath, "Data", "Strings");

            xboxGamePath =  @"C:\XboxGames\Fallout 76\Content";
            xboxDocsPath = documentsPath;
            xboxLocalPath = localAppDataPath;
            steamStringsPath = stringsPath;
            xboxStringsPath = GetDefaultStringsPathFromGamePath(xboxGamePath);

            _endorsementManager = new EndorsementManager(
                Path.Combine(settingsFolderPath, "endorsement.json"),
                LogActivity,
                () => SendMessageToWeb(JsonSerializer.Serialize(new { type = "SHOW_ENDORSEMENT" }))
            );

            System.Windows.Forms.Timer runtimeTimer = new System.Windows.Forms.Timer { Interval = 10000 };
            runtimeTimer.Tick += (s, e) => _endorsementManager.Tick(10);
            runtimeTimer.Start();

            
            try {
                Directory.CreateDirectory(logFolderPath);
                Directory.CreateDirectory(profilesFolderPath);
                Directory.CreateDirectory(settingsFolderPath);
            } catch (Exception ex) {
                Debug.WriteLine($"[INIT] Failed to create startup directories: {ex.Message}");
            }
            
            CleanupLogs();
            LoadSettings();
            SyncAppPaths();
            EnsurePipboyCrtOnDefaultMigration();
            EnsurePipboyPrefsIniScrubMigration();
            EnsureGameIntegrityRepairMigration();
            EnsureAllPrefsIniWritable();
            LoadProfiles();
            ReconcileActiveProfileEnabledMods();

            LogActivity("===========================================");
            LogActivity($"Fallout 76 Manager v{CurrentVersion} Initialized");
            LogActivity($"• Game Path: {gamePath}");
            LogActivity($"• INI Path:  {documentsPath}");
            LogActivity($"• Admin Mode: {IsRunningAsAdmin()}");
            LogActivity($"• UI Language: {applicationLanguage}");
            LogActivity("===========================================");

            this.Text = "Fallout 76 Manager";
            this.BackColor = Color.FromArgb(18, 18, 18);
            
            if (windowWidth > 0 && windowHeight > 0) this.Size = new Size(windowWidth, windowHeight);
            if (windowTop != -1 && windowLeft != -1) this.Location = new Point(windowLeft, windowTop);
            if (windowMaximized) this.WindowState = FormWindowState.Maximized;
            else this.StartPosition = FormStartPosition.CenterScreen;
            
            try {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Icon.ico");
                if (File.Exists(iconPath)) this.Icon = new Icon(iconPath);
                else {
                    var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    if (exeIcon != null) this.Icon = exeIcon;
                }
            } catch (Exception ex) {
                Debug.WriteLine($"[INIT] Failed to load app icon: {ex.Message}");
            }

            try { InitializeTrayIcon(); } catch (Exception ex) { Debug.WriteLine($"[INIT] Failed to initialize tray icon: {ex.Message}"); }
            
            this.AllowDrop = false; 
            try { EnableDragDropMessages(this.Handle); } catch (Exception ex) { Debug.WriteLine($"[INIT] Failed to enable drag/drop message filter: {ex.Message}"); }

            try {
                int darkMode = 1;
                DwmSetWindowAttribute(this.Handle, 20, ref darkMode, sizeof(int));
            } catch (Exception ex) {
                Debug.WriteLine($"[INIT] Failed to apply dark mode window attribute: {ex.Message}");
            }

            Application.AddMessageFilter(this);
            try { InitializeWebView(); } catch (Exception ex) { Debug.WriteLine($"[INIT] Failed to initialize WebView: {ex.Message}"); }

            MainInstance = this;
            this.FormClosed += (_, _) => { if (ReferenceEquals(MainInstance, this)) MainInstance = null; };

            this.FormClosing += Form1_FormClosing;
            this.Load += Form1_Load;
            Application.ApplicationExit += OnApplicationExit;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FATAL_CRASH.txt"), ex.ToString()); } catch (Exception writeEx) { Debug.WriteLine($"[CRASH] Failed to write crash log: {writeEx.Message}"); }
            throw; 
        }
    }

    private void Form1_Load(object? sender, EventArgs e)
    {
        try
        {
            Security.Init(LogActivity, RequestGracefulExit);
            Security.StartMonitoring();
            LogActivity("Security monitor started.");
        }
        catch (Exception ex)
        {
            LogActivity($"[CRITICAL] Failed to start Security monitor: {ex.Message}");
        }
    }

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!forceExitRequested && minimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
             e.Cancel = true;
             this.Hide();
             trayIcon?.ShowBalloonTip(3000, "Fallout 76 Manager", "The application is still running in the system tray.", ToolTipIcon.Info);
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
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int darkMode, int size);

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
        try { File.AppendAllText(logActivityPath, $"[{DateTime.Now:T}] {msg}\n"); } catch (Exception ex) { Debug.WriteLine($"[LOG] Failed to write activity log: {ex.Message}"); }
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
                                        SendStatusMessage("info", "Received NXM Link from browser...");
                                        _ = HandleNxmLinkAsync(args);
                                        this.Show();
                                        this.WindowState = FormWindowState.Normal;
                                        this.Activate();
                                    }
                                    else if (args == "SHOW")
                                    {
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

