using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace F76ManagerApp;

internal sealed class LogsPopoutForm : Form
{
    private readonly Form1 _owner;
    private readonly CoreWebView2Environment _environment;
    private readonly string _themeId;
    private readonly string _language;
    private WebView2? _webView;

    public CoreWebView2? CoreWebView => _webView?.CoreWebView2;

    public LogsPopoutForm(
        Form1 owner,
        CoreWebView2Environment environment,
        string themeId,
        string language,
        Rectangle? restoreBounds = null,
        bool maximized = false)
    {
        _owner = owner;
        _environment = environment;
        _themeId = themeId ?? "fallout";
        _language = string.IsNullOrWhiteSpace(language) ? "en-US" : language.Trim();

        Text = "F76 Manager — Logs";
        Width = 980;
        Height = 720;
        MinimumSize = new Size(640, 420);
        ShowIcon = true;
        ShowInTaskbar = true;

        if (restoreBounds is { } rb && IsBoundsOnScreen(rb))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = rb;
            if (maximized) WindowState = FormWindowState.Maximized;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }

        ApplyAppIcon();

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            AllowExternalDrop = false
        };
        Controls.Add(_webView);

        Load += async (_, _) => await InitializeAsync();
        ResizeEnd += (_, _) => PersistBounds();
        Move += (_, _) =>
        {
            if (WindowState == FormWindowState.Normal) PersistBounds();
        };
        FormClosing += (_, _) => PersistBounds();
        FormClosed += (_, _) =>
        {
            try { _webView?.Dispose(); } catch { }
            _webView = null;
        };
    }

    private void PersistBounds()
    {
        try { _owner.CaptureLogsPopoutBounds(this); } catch { }
    }

    internal static bool IsBoundsOnScreen(Rectangle bounds)
    {
        if (bounds.Width < 200 || bounds.Height < 150) return false;
        foreach (var screen in Screen.AllScreens)
        {
            if (screen.WorkingArea.IntersectsWith(bounds)) return true;
        }
        return false;
    }

    private void ApplyAppIcon()
    {
        try
        {
            if (_owner.Icon != null)
            {
                Icon = (Icon)_owner.Icon.Clone();
                return;
            }

            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Icon.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
                return;
            }

            var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (exeIcon != null)
                Icon = exeIcon;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LOGS-POPOUT] Failed to load icon: {ex.Message}");
        }
    }

    private async Task InitializeAsync()
    {
        if (_webView == null) return;
        try
        {
            if (Icon == null) ApplyAppIcon();
            else if (IsHandleCreated && _owner.Icon != null)
            {
                try { Icon = (Icon)_owner.Icon.Clone(); } catch { }
            }

            await _webView.EnsureCoreWebView2Async(_environment);
            var core = _webView.CoreWebView2;
            if (core == null) return;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => _owner.ServeAppResource(core, e, kickoffStartup: false);
            core.WebMessageReceived += (_, e) =>
            {
                try { _owner.HandlePopoutWebMessage(this, e.WebMessageAsJson); }
                catch (Exception ex) { Debug.WriteLine($"[LOGS-POPOUT] message: {ex.Message}"); }
            };

            if (IsHandleCreated)
                _owner.ApplyWindowChromeFromThemeToHandle(Handle, _themeId);

            string langQuery = Uri.EscapeDataString(_language);
            core.Navigate($"https://f76manager.app/logs-popout.html?lang={langQuery}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LOGS-POPOUT] init failed: {ex.Message}");
            MessageBox.Show(this, $"Could not open Logs window:\n{ex.Message}", "Logs", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }
}
