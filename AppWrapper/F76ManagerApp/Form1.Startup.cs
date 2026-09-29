using System.Diagnostics;

using System.Text.Json;

using Microsoft.Web.WebView2.Core;

using Microsoft.Web.WebView2.WinForms;

using F76ManagerApp.Managers;



namespace F76ManagerApp;



public partial class Form1

{

    private Task<CoreWebView2Environment>? _prewarmedWebViewEnvironment;

    private bool _startupLoadHooked;

    private bool _applicationReady;

    private bool _pendingGetDataRequest;

    private bool _webViewHandlersAttached;

    private Task? _modListWarmTask;

    private bool _backgroundHydrationStarted;

    private bool _deferredStartupScheduled;

    private bool _essentialsLoadStarted;



    public Form1(Task<CoreWebView2Environment>? prewarmedWebViewEnvironment = null)

    {

        _prewarmedWebViewEnvironment = prewarmedWebViewEnvironment;

        InitializeComponent();

        TryApplySavedWindowBounds();

        _themePackageLoader = new ThemePackageLoader(LogActivity, LogError);
        _themesLoadTask = Task.Run(() =>
        {
            try
            {
                _themePackageLoader.Reload();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[THEMES] Early load failed: {ex.Message}");
            }
            finally
            {
                _themesReady = true;
            }
        });

        PrepareWebViewShell();



        this.Load += Form1_StartupLoad;

        this.Load += Form1_NxmStartupLoad;

        this.FormClosing += Form1_FormClosing;

        Application.ApplicationExit += OnApplicationExit;



        MainInstance = this;

        this.FormClosed += (_, _) => { if (ReferenceEquals(MainInstance, this)) MainInstance = null; };



        try

        {

            if (!Directory.Exists(logFolderPath)) Directory.CreateDirectory(logFolderPath);

            EnsureIpcServerStarted();

        }

        catch (Exception ex)

        {

            Debug.WriteLine($"[INIT] Minimal startup failed: {ex.Message}");

        }

    }



    private async void Form1_NxmStartupLoad(object? sender, EventArgs e)

    {

        if (string.IsNullOrEmpty(InitialNxmLink)) return;

        LogActivity("[NEXUS] App launched from an NXM link; waiting for startup to finish.");


        while (!_applicationReady && !IsDisposed)

            await Task.Delay(50).ConfigureAwait(true);



        if (IsDisposed) return;

        await Task.Delay(300).ConfigureAwait(true);

        _ = HandleNxmLinkAsync(InitialNxmLink);

    }



    private async void Form1_StartupLoad(object? sender, EventArgs e)

    {

        if (_startupLoadHooked) return;

        _startupLoadHooked = true;



        try

        {

            StartupTrace.Mark("Form1.Load fired (window handle created)");

            await Task.Yield();



            await InitializeWebViewFromAttemptList(startIndex: 0).ConfigureAwait(true);

            StartupTrace.Mark("WebView2 initialized + navigation started");



            _ = Task.Run(async () =>

            {

                await Task.Delay(15000).ConfigureAwait(false);

                RunOnUiThreadSafe(() => _ = StartStartupEssentialsOnceAsync());

            });

        }

        catch (Exception ex)

        {

            try

            {

                File.WriteAllText(

                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FATAL_CRASH.txt"),

                    ex.ToString());

            }

            catch (Exception writeEx)

            {

                Debug.WriteLine($"[CRASH] Failed to write crash log: {writeEx.Message}");

            }



            MessageBox.Show(

                $"Fallout 76 Manager failed to start:\r\n\r\n{ex.Message}",

                "Startup Error",

                MessageBoxButtons.OK,

                MessageBoxIcon.Error);

        }

    }



    private void MarkApplicationReady()

    {

        if (_applicationReady) return;



        _applicationReady = true;

        StartupTrace.Mark("Application ready (essentials loaded)");

        try
        {
            if (logsPopoutOpen)
            {
                BeginInvoke(new Action(() =>
                {
                    if (webView?.CoreWebView2 != null)
                        TryRestoreLogsPopout();
                    else
                    {
                        BeginInvoke(new Action(TryRestoreLogsPopout));
                    }
                }));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LOGS] Schedule restore failed: {ex.Message}");
        }

        _modListWarmTask = Task.Run(() =>

        {

            StartupTrace.Mark("GetModsList warm: start (background)");

            try { _modManager.GetModsList(); }

            catch (Exception ex) { LogError($"[INIT] Mod list warm failed: {ex.Message}"); }

            StartupTrace.Mark("GetModsList warm: done");

        });



        FlushPendingGetData();

        BeginBackgroundDataHydration();

    }



    internal void ScheduleDeferredStartup()

    {

        if (_deferredStartupScheduled) return;

        _deferredStartupScheduled = true;

        BeginInvoke(RunDeferredStartup);

    }



    private async Task StartStartupEssentialsOnceAsync()

    {

        if (_essentialsLoadStarted) return;

        _essentialsLoadStarted = true;



        try

        {

            await InitializeStartupEssentialsAsync().ConfigureAwait(true);

            MarkApplicationReady();

        }

        catch (Exception ex)

        {

            LogError($"[INIT] Startup essentials failed: {ex.Message}");

        }

    }



    private void PrepareWebViewShell()

    {

        if (webView != null) return;



        webView = new WebView2

        {

            Dock = DockStyle.Fill,

            DefaultBackgroundColor = Color.FromArgb(18, 18, 18),

        };

        Controls.Add(webView);

    }



    private void SendStartupDataToWeb()

    {

        SendDataToWeb(deferHeavyWork: true, deferModScan: true);

    }



    private void FlushPendingGetData()

    {

        if (!_pendingGetDataRequest) return;

        _pendingGetDataRequest = false;

        SendStartupDataToWeb();

    }



    private void BeginBackgroundDataHydration()

    {

        if (_backgroundHydrationStarted) return;

        _backgroundHydrationStarted = true;



        Task.Run(async () =>

        {

            try

            {

                if (_modListWarmTask != null)

                    await _modListWarmTask.ConfigureAwait(false);

            }

            catch (Exception ex)

            {

                LogError($"[INIT] Background mod scan failed: {ex.Message}");

            }



            RunOnUiThreadSafe(() =>

            {

                try { SendDataToWeb(deferHeavyWork: true, deferModScan: false); }

                catch (Exception ex) { LogError($"[INIT] Background dashboard refresh failed: {ex.Message}"); }

                StartupTrace.Mark("Hydration: full mod list UPDATE_DATA sent");

            });

        });

    }



    private async Task InitializeStartupEssentialsAsync()

    {

        StartupTrace.Mark("Essentials: start");

        _configManager = new GameConfigManager(LogActivity, (t, m) => this.Invoke(() => SendStatusMessage(t, m)));

        _modManager = new ModManager(_configManager, LogActivity, (t, m) => this.Invoke(() => SendStatusMessage(t, m)));

        _conflictManager = new ConflictManager(LogActivity);

        try
        {
            await _themesLoadTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogError($"[THEMES] Wait for early load failed: {ex.Message}");
        }
        _themesReady = true;

        _platformManager = new PlatformManager();

        _bundleManager = new BundleManager(

            LogActivity,

            (t, m) => this.Invoke(() => SendStatusMessage(t, m)),

            _modManager.UpdateModMetadata,

            _modManager.ToggleMods,

            LogError);



        _endorsementManager = new EndorsementManager(

            Path.Combine(settingsFolderPath, "endorsement.json"),

            LogActivity,

            () => SendMessageToWeb(JsonSerializer.Serialize(new { type = "SHOW_ENDORSEMENT" }))

        );



        var runtimeTimer = new System.Windows.Forms.Timer { Interval = 10000 };

        runtimeTimer.Tick += (s, e) => _endorsementManager.Tick(10);

        runtimeTimer.Start();



        try

        {

            Directory.CreateDirectory(logFolderPath);

            Directory.CreateDirectory(profilesFolderPath);

            Directory.CreateDirectory(settingsFolderPath);

        }

        catch (Exception ex)

        {

            Debug.WriteLine($"[INIT] Failed to create startup directories: {ex.Message}");

        }



        StartupTrace.Mark("Essentials: managers constructed");

        await LoadSettingsAsync().ConfigureAwait(true);

        StartupTrace.Mark("Essentials: settings loaded");

        ApplyDefaultPathsIfEmpty();

        StartupTrace.Mark("Essentials: default paths resolved");

        LoadProfiles();

        StartupTrace.Mark("Essentials: profiles loaded");

        SyncAppPaths(quick: true);

        StartupTrace.Mark("Essentials: SyncAppPaths(quick) done");

        await RunVirtualModModeStartupSelfHealAsync().ConfigureAwait(true);

        RestoreNexusSession();

        StartupTrace.Mark("Essentials: Nexus session restored");



        this.AllowDrop = false;



        try
        {
            ApplyWindowChromeFromTheme(uiTheme);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[INIT] Failed to apply window chrome: {ex.Message}");
        }

        Application.AddMessageFilter(this);

    }



    private async Task RunVirtualModModeStartupSelfHealAsync()
    {
        try
        {
            if (!virtualModMode && !_modManager.HasStagedFiles()) return;

            if (IsGameRunning() || (virtualModMode && _modManager.HasPendingManagedArtifacts()))
            {
                LogActivity("[STARTUP] VMM self-heal skipped (game running or runtime session pending).");
                return;
            }

            if (virtualModMode)
            {
                int moved = await Task.Run(() => _modManager.MoveLiveModsIntoStaging()).ConfigureAwait(true);
                if (moved > 0)
                    LogActivity($"[STARTUP] VMM self-heal moved {moved} leftover live file(s) into staging.");
            }
            else
            {
                int moved = await Task.Run(() => _modManager.MoveStagedModsToLive()).ConfigureAwait(true);
                if (moved > 0)
                {
                    await Task.Run(() => _modManager.RedeployDirect()).ConfigureAwait(true);
                    LogActivity($"[STARTUP] Direct-mode self-heal moved {moved} staged file(s) back to the game and redeployed.");
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"[STARTUP] VMM self-heal failed: {ex.Message}");
        }
    }

    private void InitializeStartupEssentials()

    {

        InitializeStartupEssentialsAsync().GetAwaiter().GetResult();

    }



    private void RunDeferredStartup()

    {

        Task.Run(() =>

        {

            try

            {

                RunOnUiThreadSafe(() =>

                {

                    try { EnableDragDropMessages(this.Handle); }

                    catch (Exception ex) { Debug.WriteLine($"[INIT] Failed to enable drag/drop message filter: {ex.Message}"); }



                    try { InitializeTrayIcon(); }

                    catch (Exception ex) { Debug.WriteLine($"[INIT] Failed to initialize tray icon: {ex.Message}"); }

                });



                try

                {

                    CleanupLogs();

                    EnsurePipboyCrtOnDefaultMigration();

                    EnsurePipboyPrefsIniScrubMigration();

                    EnsureGameIntegrityRepairMigration();

                    EnsureLooseConfigDeployRepairMigration();

                    EnsureAllPrefsIniWritable();

                    SyncAppPaths();

                }

                catch (Exception ex)

                {

                    LogError($"[INIT] Background migrations failed: {ex.Message}");

                }



                try

                {

                    Security.Init(LogActivity, RequestGracefulExit);

                    Security.StartMonitoring();

                    RunOnUiThreadSafe(() => LogActivity("Security monitor started."));

                }

                catch (Exception ex)

                {

                    RunOnUiThreadSafe(() => LogActivity($"[CRITICAL] Failed to start Security monitor: {ex.Message}"));

                }



                Task.Delay(5000).ContinueWith(_ =>

                {

                    try { ReconcileActiveProfileEnabledMods(); }

                    catch (Exception ex) { LogError($"[INIT] Profile reconcile failed: {ex.Message}"); }



                    RunOnUiThreadSafe(() =>

                    {

                        LogActivity("===========================================");

                        LogActivity($"Fallout 76 Manager v{CurrentVersion} Initialized");

                        LogActivity($"• Game Path: {gamePath}");

                        LogActivity($"• INI Path:  {documentsPath}");

                        LogActivity($"• Admin Mode: {IsRunningAsAdmin()}");

                        LogActivity($"• UI Language: {applicationLanguage}");

                        LogActivity("===========================================");

                    });

                });



                Task.Delay(10000).ContinueWith(_ =>

                {

                    try { RefreshConflictCount(); }

                    catch (Exception ex) { LogError($"[INIT] Deferred conflict scan failed: {ex.Message}"); }

                });

            }

            catch (Exception ex)

            {

                RunOnUiThreadSafe(() => LogError($"[INIT] Deferred startup worker failed: {ex.Message}"));

            }

        });

    }



    private void TryApplySavedWindowBounds()

    {

        try

        {

            if (File.Exists(settingsPath))

            {

                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));

                var root = doc.RootElement;

                if (root.TryGetProperty("windowWidth", out var ww)) windowWidth = ww.GetInt32();

                if (root.TryGetProperty("windowHeight", out var wh)) windowHeight = wh.GetInt32();

                if (root.TryGetProperty("windowTop", out var wt)) windowTop = wt.GetInt32();

                if (root.TryGetProperty("windowLeft", out var wl)) windowLeft = wl.GetInt32();

                if (root.TryGetProperty("windowMaximized", out var wm)) windowMaximized = wm.GetBoolean();

            }

        }

        catch (Exception ex)

        {

            Debug.WriteLine($"[INIT] Could not read saved window bounds: {ex.Message}");

        }



        ApplyWindowLayout();

    }



    private void ApplyWindowLayout()

    {

        this.Text = "Fallout 76 Manager";

        this.BackColor = Color.FromArgb(18, 18, 18);



        if (windowWidth > 0 && windowHeight > 0) this.Size = new Size(windowWidth, windowHeight);

        if (windowTop != -1 && windowLeft != -1) this.Location = new Point(windowLeft, windowTop);

        if (windowMaximized) this.WindowState = FormWindowState.Maximized;

        else this.StartPosition = FormStartPosition.CenterScreen;



        try

        {

            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Icon.ico");

            if (File.Exists(iconPath)) this.Icon = new Icon(iconPath);

            else

            {

                var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

                if (exeIcon != null) this.Icon = exeIcon;

            }

        }

        catch (Exception ex)

        {

            Debug.WriteLine($"[INIT] Failed to load app icon: {ex.Message}");

        }

    }



    internal static string GetDefaultWebViewUserDataFolder() =>

        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_Cache_v2", "profile_default");

}


