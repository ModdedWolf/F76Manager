using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using F76ManagerApp.Managers;
using System.Diagnostics;

namespace F76ManagerApp;

public partial class Form1
{
    private sealed class WebViewAttempt
    {
        public string Name { get; }
        public string UserDataFolder { get; }
        public string? BrowserArgs { get; }

        public WebViewAttempt(string name, string userDataFolder, string? browserArgs)
        {
            Name = name;
            UserDataFolder = userDataFolder;
            BrowserArgs = browserArgs;
        }
    }

    private List<WebViewAttempt> BuildWebViewAttempts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "F76Manager_WebView2");
        try { Directory.CreateDirectory(tempRoot); } catch { }

        return new List<WebViewAttempt>
        {
            new WebViewAttempt("default", Path.Combine(tempRoot, "profile_default"), null),

            new WebViewAttempt("disable_gpu", Path.Combine(tempRoot, "profile_disable_gpu"), "--disable-gpu --disable-gpu-compositing"),

            new WebViewAttempt("swiftshader", Path.Combine(tempRoot, "profile_swiftshader"), "--disable-gpu --use-gl=swiftshader --disable-gpu-compositing"),

            new WebViewAttempt("rc_integrity_off", Path.Combine(tempRoot, "profile_rc_integrity_off"), "--disable-features=RendererCodeIntegrity --disable-gpu --disable-gpu-compositing"),

            new WebViewAttempt("no_sandbox", Path.Combine(tempRoot, "profile_no_sandbox"), "--no-sandbox --disable-gpu --disable-gpu-compositing --disable-features=RendererCodeIntegrity"),
        };
    }

    private async Task InitializeWebViewWithAttempt(WebViewAttempt attempt)
    {
        webView = new WebView2();
        webView.Dock = DockStyle.Fill;
        this.Controls.Add(webView);

        this.Text = "Fallout 76 Manager";

        CoreWebView2EnvironmentOptions? options = null;
        if (!string.IsNullOrWhiteSpace(attempt.BrowserArgs))
        {
            options = new CoreWebView2EnvironmentOptions(attempt.BrowserArgs);
        }
        var env = await CoreWebView2Environment.CreateAsync(null, attempt.UserDataFolder, options);
        await webView.EnsureCoreWebView2Async(env);
        try { LogActivity("[WEBVIEW] CoreWebView2 initialized."); } catch { }
        try { LogActivity($"[WEBVIEW] Attempt={attempt.Name} UserDataFolder={attempt.UserDataFolder} Args={attempt.BrowserArgs}"); } catch { }

        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

        webView.CoreWebView2.SourceChanged += (s, e) =>
        {
            try { LogActivity($"[WEBVIEW] SourceChanged: {webView?.Source}"); } catch { }
        };
        webView.CoreWebView2.DOMContentLoaded += (s, e) =>
        {
            try { LogActivity("[WEBVIEW] DOMContentLoaded."); } catch { }
        };

        webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
        webView.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;
        webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
        webView.CoreWebView2.FrameNavigationStarting += CoreWebView2_FrameNavigationStarting;
        webView.CoreWebView2.ProcessFailed += (s, e) =>
        {
            try
            {
                LogActivity($"[WEBVIEW] ProcessFailed: Kind={e.ProcessFailedKind}");
                try
                {
                    var props = e.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    foreach (var p in props)
                    {
                        if (p == null) continue;
                        var n = p.Name ?? "";
                        if (n == nameof(e.ProcessFailedKind)) continue;
                        object? v = null;
                        try { v = p.GetValue(e); } catch { }
                        LogActivity($"[WEBVIEW] ProcessFailed.{n}={v}");
                    }
                }
                catch { }
            }
            catch
            {
                LogActivity("[WEBVIEW] ProcessFailed (exception while logging).");
            }

            if (!_webViewRecoveryAttempted && e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                _webViewRecoveryAttempted = true;
                try { LogActivity("[WEBVIEW] Attempting recovery with next startup profile/flags."); } catch { }
                try
                {
                    this.Invoke(() =>
                    {
                        try { if (webView != null) { this.Controls.Remove(webView); webView.Dispose(); } } catch { }
                        _ = InitializeWebViewFromAttemptList(startIndex: 1);
                    });
                }
                catch { }
                return;
            }

            if (_webViewRecoveryAttempted && e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                try
                {
                    this.Invoke(() =>
                    {
                        try
                        {
                            MessageBox.Show(
                                "WebView2 failed to start (browser process exited).\r\n\r\n" +
                                "Fixes to try:\r\n" +
                                "1) Install/repair Microsoft Edge WebView2 Runtime\r\n" +
                                "2) Update GPU drivers (or try a different GPU)\r\n" +
                                "3) Delete the app's WebView2 cache folder and relaunch\r\n\r\n" +
                                $"Cache folders:\r\n- {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_Cache_v2")}\r\n" +
                                $"- {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_Cache_v2_recover")}\r\n\r\n" +
                                "A detailed log was written to Release/Logs/activity.log.",
                                "WebView2 Startup Failure",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error
                            );
                        }
                        catch { }
                    });
                }
                catch { }
            }
        };

        webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        webView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;

        webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
        webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        webView.Source = new Uri("https://f76manager.app/index.html");
        LogActivity("[WEBVIEW] Navigating to https://f76manager.app/index.html");

        webView.AllowExternalDrop = true;
        ((Control)webView).AllowDrop = true;

        webView.DragEnter += WebView_DragEnter;
        webView.DragDrop += WebView_DragDrop;

        if (IsRunningAsAdmin()) ApplyDeepUIPI();
    }

    private async Task InitializeWebViewFromAttemptList(int startIndex = 0)
    {
        var attempts = BuildWebViewAttempts();
        for (int i = startIndex; i < attempts.Count; i++)
        {
            var attempt = attempts[i];
            try
            {
                try { Directory.CreateDirectory(attempt.UserDataFolder); } catch { }
                await InitializeWebViewWithAttempt(attempt);
                return;
            }
            catch (Exception ex)
            {
                try { LogActivity($"[WEBVIEW] Attempt {attempt.Name} failed to initialize: {ex.GetType().Name} {ex.Message}"); } catch { }
                try
                {
                    this.Invoke(() =>
                    {
                        try { if (webView != null) { this.Controls.Remove(webView); webView.Dispose(); } } catch { }
                    });
                }
                catch { }
            }
        }

        try
        {
            this.Invoke(() =>
            {
                try
                {
                    MessageBox.Show(
                        "WebView2 failed to start after multiple launch attempts.\r\n\r\n" +
                        "This is usually caused by a broken WebView2 Runtime install, GPU/driver issues, or security/injection software.\r\n\r\n" +
                        "Fixes to try:\r\n" +
                        "1) Repair/reinstall Microsoft Edge WebView2 Runtime\r\n" +
                        "2) Update GPU drivers\r\n" +
                        "3) Temporarily disable overlays / antivirus web protection\r\n\r\n" +
                        "A detailed log was written to Release/Logs/activity.log.",
                        "WebView2 Startup Failure",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
                catch { }
            });
        }
        catch { }
    }

    private async void InitializeWebView()
    {
        try 
        {
            _webViewRecoveryAttempted = false;
            await InitializeWebViewFromAttemptList(startIndex: 0);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 Error: {ex.Message}", "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LogActivity("[WEBVIEW] Navigation Completed. Sending initial data...");
            RefreshConflictCount();
            SendDataToWeb(); 
            ApplyDeepUIPI();
            return;
        }

        try
        {
            var status = e.WebErrorStatus.ToString();
            var src = webView?.Source?.ToString() ?? "(unknown)";
            LogActivity($"[WEBVIEW] Navigation FAILED. Status={status} Source={src}");
        }
        catch
        {
            LogActivity("[WEBVIEW] Navigation FAILED (exception while logging status).");
        }
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsModFile(e.Uri)) 
        {
             e.Cancel = true;
             LogActivity($"[DND] Intercepted Navigation: {e.Uri}");
             HandleModImportUri(e.Uri);
        }
        else if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) && 
                 !e.Uri.StartsWith("https://f76manager.app/", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            try {
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
            } catch (Exception ex) {
                LogActivity($"[ERROR] Failed to open external link from nav: {ex.Message}");
            }
        }
    }

    private void CoreWebView2_FrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsModFile(e.Uri)) 
        { 
            e.Cancel = true; 
            LogActivity($"[DND] Intercepted Frame Nav: {e.Uri}");
            HandleModImportUri(e.Uri); 
        }
    }

    private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (IsModFile(e.Uri)) 
        { 
            e.Handled = true; 
            LogActivity($"[DND] Intercepted NewWindow: {e.Uri}");
            HandleModImportUri(e.Uri); 
        }
        else if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            e.Handled = true;
            try {
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
            } catch (Exception ex) {
                LogActivity($"[ERROR] Failed to open external link: {ex.Message}");
            }
        }
    }

    private void CoreWebView2_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (IsModFile(e.DownloadOperation.Uri))
        {
            e.Cancel = true;
            e.Handled = true;
            LogActivity($"[DND] Intercepted Download: {e.DownloadOperation.Uri}");
            HandleModImportUri(e.DownloadOperation.Uri);
        }
    }

    private void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string uri = e.Request.Uri;
        bool shouldLog = false;
        try
        {
            if (uri.StartsWith("https://f76manager.app/", StringComparison.OrdinalIgnoreCase))
            {
                var p = uri.Substring("https://f76manager.app/".Length);
                var qi = p.IndexOf('?');
                if (qi >= 0) p = p.Substring(0, qi);
                p = string.IsNullOrWhiteSpace(p) ? "index.html" : p;
                shouldLog =
                    p.Equals("index.html", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("js/main.js", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("css/style.css", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("js/components/", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        
        if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (IsModFile(uri))
            {
                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(null, 204, "No Content", "");
                if (lastSection == "mods") HandleModImportUri(uri);
                return;
            }
        }
        if (uri.StartsWith("https://f76manager.app/", StringComparison.OrdinalIgnoreCase))
        {
            string path = uri.Substring("https://f76manager.app/".Length);
            if (string.IsNullOrEmpty(path)) path = "index.html";
            
            int queryIndex = path.IndexOf('?');
            if (queryIndex >= 0) path = path.Substring(0, queryIndex);

            if (path.Equals("js/user-themes-boot.js", StringComparison.OrdinalIgnoreCase))
            {
                var js = System.Text.Encoding.UTF8.GetBytes(BuildUserThemesBootScript());
                var bootMs = new MemoryStream(js);
                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    bootMs, 200, "OK", VirtualHostResponseHeaders("application/javascript"));
                return;
            }

            if (path.StartsWith("user-theme-logo/", StringComparison.OrdinalIgnoreCase))
            {
                var logoPath = _themePackageLoader.ResolveLogoPhysicalPath(path);
                if (logoPath != null && File.Exists(logoPath))
                {
                    var fs = File.OpenRead(logoPath);
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        fs, 200, "OK", VirtualHostResponseHeaders(GetContentType(logoPath)));
                    return;
                }
                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }

            string resourceName = "F76ManagerApp.www." + path.Replace('/', '.').Replace('\\', '.');
            
            if (path.Equals("favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                resourceName = "F76ManagerApp.www.assets.Icon.png";
            }

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var allResources = assembly.GetManifestResourceNames();
            var match = allResources.FirstOrDefault(r => r.Equals(resourceName, StringComparison.OrdinalIgnoreCase));

            string physicalPath = Path.Combine(AppPaths.SettingsFolder, "..", "www", path.Replace('/', Path.DirectorySeparatorChar));

            bool preferPhysicalEnv = string.Equals(
                Environment.GetEnvironmentVariable("F76MGR_USE_PHYSICAL_WWW"),
                "1",
                StringComparison.OrdinalIgnoreCase
            );
#if DEBUG
            // Debug builds copy WebSrc → output/www for fast iteration; prefer disk when present so UI/CSS changes apply without relying on embedded resources.
            bool preferPhysical = preferPhysicalEnv || File.Exists(physicalPath);
#else
            bool preferPhysical = preferPhysicalEnv;
#endif

            if (shouldLog)
            {
                LogActivity($"[WEBVIEW_RESOURCE] {path} preferPhysical={preferPhysical} embeddedMatch={(match != null)} physicalExists={File.Exists(physicalPath)}");
            }

            if (!preferPhysical && match != null)
            {
                using (var stream = assembly.GetManifestResourceStream(match))
                {
                    if (stream != null)
                    {
                        var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        ms.Position = 0;
                        string contentType = resourceName.EndsWith(".png") ? "image/png" : GetContentType(path);
                        e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(ms, 200, "OK", VirtualHostResponseHeaders(contentType));
                        return;
                    }
                }
            }

            bool canUsePhysical = (preferPhysical || match == null) && File.Exists(physicalPath);

            if (canUsePhysical)
            {
                var fs = File.OpenRead(physicalPath);
                string contentType = GetContentType(physicalPath);
                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(fs, 200, "OK", VirtualHostResponseHeaders(contentType));
                if (shouldLog) LogActivity($"[WEBVIEW_RESOURCE] Served physical: {path}");
            }
            else if (match != null)
            {
                using (var stream = assembly.GetManifestResourceStream(match))
                {
                    if (stream != null)
                    {
                        var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        ms.Position = 0;
                        string contentType = resourceName.EndsWith(".png") ? "image/png" : GetContentType(path);
                        e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(ms, 200, "OK", VirtualHostResponseHeaders(contentType));
                        if (shouldLog) LogActivity($"[WEBVIEW_RESOURCE] Served embedded: {path}");
                    }
                }
            }
            else
            {
                if (!path.EndsWith("favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    LogActivity($"[RESOURCE_FAIL] 404 Not Found: {uri}");
                }
                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            }
        }
    }

    private string GetContentType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }

    private static string VirtualHostResponseHeaders(string contentType)
    {
        return contentType switch
        {
            "text/css" or "application/javascript" or "text/html" or "image/png" or "image/jpeg" or "image/webp" =>
                $"Content-Type: {contentType}\r\nCache-Control: no-store\r\nPragma: no-cache",
            _ => $"Content-Type: {contentType}",
        };
    }
}
