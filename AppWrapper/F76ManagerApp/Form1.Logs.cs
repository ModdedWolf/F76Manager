using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace F76ManagerApp;

public partial class Form1
{
    private sealed class ThemeSyncState
    {
        public string ThemeId { get; init; } = "";
        public string Css { get; init; } = "";
        public string Caption { get; init; } = "";
        public string Text { get; init; } = "";
        public string Border { get; init; } = "";
    }

    private LogsPopoutForm? _logsPopout;
    private ThemeSyncState? _lastThemeSync;

    private void HandleShareLogs(JsonElement root, CoreWebView2? replyTo = null)
    {
        try
        {
            string tab = "activity";
            if (TryGetString(root, "tab", out var tabVal) && !string.IsNullOrWhiteSpace(tabVal))
                tab = tabVal.Trim().ToLowerInvariant();

            bool errors = tab is "errors" or "error";
            string path = errors ? logErrorPath : logActivityPath;
            string label = errors ? "Error Log" : "Activity Log";

            var lines = ReadLastLines(path, 400);
            string platformLabel = "Unknown";
            try { platformLabel = _platformManager?.CurrentPlatform.ToString() ?? "Unknown"; } catch { }

            var sb = new StringBuilder();
            sb.AppendLine($"F76 Manager v{CurrentVersion} | {platformLabel} | {label} | {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine("---");
            foreach (var line in lines)
                sb.AppendLine(line);

            string redacted = RedactLogText(sb.ToString());
            if (redacted.Length < 1900)
                redacted = "```\n" + redacted.TrimEnd() + "\n```";

            void Copy()
            {
                try
                {
                    Clipboard.SetText(redacted);
                    PostLogsStatus(
                        replyTo,
                        "success",
                        $"Copied {lines.Count} lines to clipboard",
                        "logs_copied",
                        new object[] { lines.Count });
                }
                catch (Exception ex)
                {
                    LogError($"[LOGS] Clipboard copy failed: {ex.Message}");
                    PostLogsStatus(replyTo, "error", $"Could not copy logs: {ex.Message}");
                }
            }

            if (InvokeRequired) Invoke(Copy);
            else Copy();
        }
        catch (Exception ex)
        {
            LogError($"[LOGS] Share failed: {ex.Message}");
            PostLogsStatus(replyTo, "error", $"Could not share logs: {ex.Message}");
        }
    }

    private void HandleClearActivityLog(CoreWebView2? replyTo = null)
    {
        void Work()
        {
            try
            {
                var dir = Path.GetDirectoryName(logActivityPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(logActivityPath, string.Empty);
                LogActivity("[LOGS] Activity log cleared by user.");
                SendDataToWeb();
                NotifyLogsPopoutData();
                PostLogsStatus(replyTo, "success", "Activity log cleared.", "logs_activity_cleared");
            }
            catch (Exception ex)
            {
                LogError($"[LOGS] Failed to clear activity log: {ex.Message}");
                PostLogsStatus(replyTo, "error", $"Failed to clear activity log: {ex.Message}");
            }
        }

        if (InvokeRequired) Invoke(Work);
        else Work();
    }

    private void HandleOpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(logFolderPath);
            if (File.Exists(logActivityPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{logActivityPath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(logFolderPath) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            LogError($"[LOGS] Could not open Logs folder: {ex.Message}");
            SendStatusMessage("error", $"Could not open Logs folder: {ex.Message}");
        }
    }

    private void HandleGetLogs(CoreWebView2 replyTo)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "LOGS_DATA",
                logs = SafeGetRealLogs()
            });
            replyTo.PostWebMessageAsJson(payload);
        }
        catch (Exception ex)
        {
            LogError($"[LOGS] GET_LOGS failed: {ex.Message}");
        }
    }

    private void HandleOpenLogsPopout()
    {
        void Work()
        {
            try
            {
                if (_logsPopout != null && !_logsPopout.IsDisposed)
                {
                    if (_logsPopout.WindowState == FormWindowState.Minimized)
                        _logsPopout.WindowState = FormWindowState.Normal;
                    _logsPopout.BringToFront();
                    _logsPopout.Activate();
                    return;
                }

                if (webView?.CoreWebView2?.Environment == null)
                {
                    SendStatusMessage("error", "WebView is not ready yet.", "logs_popout_not_ready");
                    return;
                }

                Rectangle? bounds = null;
                if (logsPopoutBoundsValid)
                    bounds = new Rectangle(logsPopoutX, logsPopoutY, logsPopoutW, logsPopoutH);

                _logsPopout = new LogsPopoutForm(
                    this,
                    webView.CoreWebView2.Environment,
                    uiTheme,
                    applicationLanguage,
                    bounds,
                    logsPopoutMaximized);
                _logsPopout.FormClosed += (_, _) =>
                {
                    _logsPopout = null;
                    if (_mainHiddenForLogsPopout && !isShuttingDown)
                    {
                        try { BeginInvoke(new Action(() => RunGracefulExit(0))); }
                        catch { RunGracefulExit(0); }
                        return;
                    }
                    if (!isShuttingDown)
                    {
                        logsPopoutOpen = false;
                        try { SaveSettings(); } catch { }
                    }
                };
                _logsPopout.Show();

                if (!logsPopoutOpen)
                {
                    logsPopoutOpen = true;
                    try { SaveSettings(); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogError($"[LOGS] Pop-out failed: {ex.Message}");
                SendStatusMessage("error", $"Could not open logs window: {ex.Message}");
            }
        }

        if (InvokeRequired) Invoke(Work);
        else Work();
    }

    internal void CaptureLogsPopoutBounds(LogsPopoutForm form)
    {
        try
        {
            if (form == null || form.IsDisposed) return;
            var bounds = form.WindowState == FormWindowState.Maximized
                ? form.RestoreBounds
                : form.Bounds;
            if (bounds.Width < 200 || bounds.Height < 150) return;
            logsPopoutX = bounds.X;
            logsPopoutY = bounds.Y;
            logsPopoutW = bounds.Width;
            logsPopoutH = bounds.Height;
            logsPopoutMaximized = form.WindowState == FormWindowState.Maximized;
            logsPopoutBoundsValid = true;
        }
        catch { }
    }

    private void TryRestoreLogsPopout()
    {
        try
        {
            if (!logsPopoutReopenOnLaunch) return;
            if (!logsPopoutOpen) return;
            if (_logsPopout != null && !_logsPopout.IsDisposed) return;
            if (webView?.CoreWebView2?.Environment == null) return;
            HandleOpenLogsPopout();
        }
        catch (Exception ex)
        {
            LogError($"[LOGS] Restore pop-out failed: {ex.Message}");
        }
    }

    internal void HandlePopoutWebMessage(LogsPopoutForm popout, string webMessageAsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(webMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            string type = typeEl.GetString() ?? "";
            var core = popout.CoreWebView;

            switch (type)
            {
                case "GET_LOGS":
                    if (core != null) HandleGetLogs(core);
                    break;
                case "GET_THEME_SYNC":
                    NotifyLogsPopoutTheme();
                    break;
                case "SHARE_LOGS":
                    HandleShareLogs(root, core);
                    break;
                case "CLEAR_ERROR_LOG":
                    HandleClearErrorLog();
                    if (core != null) HandleGetLogs(core);
                    break;
                case "CLEAR_ACTIVITY_LOG":
                    HandleClearActivityLog(core);
                    break;
                case "OPEN_LOGS_FOLDER":
                    HandleOpenLogsFolder();
                    break;
            }
        }
        catch (Exception ex)
        {
            LogError($"[LOGS] Pop-out message failed: {ex.Message}");
        }
    }

    internal void NotifyLogsPopoutData()
    {
        try
        {
            if (_logsPopout == null || _logsPopout.IsDisposed) return;
            var core = _logsPopout.CoreWebView;
            if (core != null) HandleGetLogs(core);
        }
        catch { }
    }

    private void NotifyLogsPopoutTheme()
    {
        try
        {
            if (_logsPopout == null || _logsPopout.IsDisposed) return;
            var sync = _lastThemeSync;
            if (sync == null) return;

            if (_logsPopout.IsHandleCreated
                && !string.IsNullOrWhiteSpace(sync.Caption))
            {
                ApplyWindowChromeFromColorsToHandle(
                    _logsPopout.Handle, sync.Caption, sync.Text, sync.Border);
            }

            PostThemeSyncToPopout(_logsPopout.CoreWebView);
        }
        catch { }
    }

    private void PostThemeSyncToPopout(CoreWebView2? core)
    {
        try
        {
            if (core == null) return;
            var sync = _lastThemeSync;
            if (sync == null) return;
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "THEME_SYNC",
                themeId = sync.ThemeId,
                css = sync.Css,
                uiAnimations,
            }));
        }
        catch { }
    }

    private void CloseLogsPopout()
    {
        try
        {
            if (_logsPopout == null || _logsPopout.IsDisposed) return;
            _logsPopout.Close();
            _logsPopout = null;
        }
        catch { }
    }

    private void PostLogsStatus(CoreWebView2? replyTo, string type, string text, string? key = null, object[]? args = null)
    {
        if (replyTo != null)
        {
            try
            {
                replyTo.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "STATUS",
                    status = new { type, text, key, args }
                }));
                return;
            }
            catch { }
        }
        SendStatusMessage(type, text, key, args);
    }

    private static string RedactLogText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        string result = text;
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                result = Regex.Replace(result, Regex.Escape(profile), "%USERPROFILE%", RegexOptions.IgnoreCase);
                string usersRoot = Path.GetDirectoryName(profile) ?? "";
                string userName = Path.GetFileName(profile);
                if (!string.IsNullOrEmpty(usersRoot) && !string.IsNullOrEmpty(userName))
                {
                    string pattern = Regex.Escape(usersRoot.Replace('/', '\\')) + @"[\\/]+" + Regex.Escape(userName);
                    result = Regex.Replace(result, pattern, "%USERPROFILE%", RegexOptions.IgnoreCase);
                }
            }

            string user = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(user) && user.Length >= 2)
            {
                result = Regex.Replace(
                    result,
                    $@"(?<![A-Za-z0-9_]){Regex.Escape(user)}(?![A-Za-z0-9_])",
                    "<user>",
                    RegexOptions.IgnoreCase);
            }
        }
        catch { }

        result = Regex.Replace(
            result,
            @"(?i)(api[_-]?key|apikey|token|access[_-]?token|secret|password|nexus[_-]?key)\s*[:=]\s*[""']?[^\s""',;]+",
            "$1=[REDACTED]");

        result = Regex.Replace(
            result,
            @"\b[A-Za-z0-9+/]{32,}={0,2}\b",
            "[REDACTED]");

        result = Regex.Replace(
            result,
            @"\b[0-9a-fA-F]{32,}\b",
            "[REDACTED]");

        return result;
    }
}
