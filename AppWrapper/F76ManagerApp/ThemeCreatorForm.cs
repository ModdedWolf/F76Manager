using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace F76ManagerApp;

internal sealed class ThemeCreatorForm : Form
{
    private readonly Form1 _owner;
    private readonly CoreWebView2Environment _environment;
    private readonly string _themeId;
    private readonly string _language;
    private readonly string? _editThemeId;
    private WebView2? _webView;

    public CoreWebView2? CoreWebView => _webView?.CoreWebView2;

    public ThemeCreatorForm(
        Form1 owner,
        CoreWebView2Environment environment,
        string themeId,
        string language,
        string? editThemeId = null)
    {
        _owner = owner;
        _environment = environment;
        _themeId = themeId ?? "fallout";
        _language = string.IsNullOrWhiteSpace(language) ? "en-US" : language.Trim();
        _editThemeId = string.IsNullOrWhiteSpace(editThemeId) ? null : editThemeId.Trim();

        Text = "F76 Manager — Theme Creator";
        Width = 1100;
        Height = 780;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 620);
        ShowIcon = true;
        ShowInTaskbar = true;

        ApplyAppIcon();

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            AllowExternalDrop = false
        };
        Controls.Add(_webView);

        Load += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) =>
        {
            try { _webView?.Dispose(); } catch { }
            _webView = null;
        };
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
            Debug.WriteLine($"[THEME-CREATOR] Failed to load icon: {ex.Message}");
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
                try { _owner.HandleThemeCreatorWebMessage(this, e.WebMessageAsJson); }
                catch (Exception ex) { Debug.WriteLine($"[THEME-CREATOR] message: {ex.Message}"); }
            };

            if (IsHandleCreated)
                _owner.ApplyWindowChromeFromThemeToHandle(Handle, _themeId);

            string langQuery = Uri.EscapeDataString(_language);
            string url = $"https://f76manager.app/theme-creator.html?lang={langQuery}";
            if (!string.IsNullOrEmpty(_editThemeId))
                url += $"&edit={Uri.EscapeDataString(_editThemeId)}";
            core.Navigate(url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[THEME-CREATOR] init failed: {ex.Message}");
            MessageBox.Show(this, $"Could not open Theme Creator:\n{ex.Message}", "Theme Creator", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }
}
