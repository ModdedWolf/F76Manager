using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace F76ManagerApp;

internal sealed class PreviewPopoutForm : Form
{
    private readonly Form1 _owner;
    private readonly CoreWebView2Environment _environment;
    private readonly string _themeId;
    private readonly string _language;
    private WebView2? _webView;
    private bool _closeConfirmed;

    public CoreWebView2? CoreWebView => _webView?.CoreWebView2;
    public bool HasUnsavedEdits { get; set; }

    public PreviewPopoutForm(
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

        Text = "F76 Manager — Preview";
        Width = 1100;
        Height = 760;
        MinimumSize = new Size(520, 360);
        ShowIcon = true;
        ShowInTaskbar = true;

        if (restoreBounds is { } rb && LogsPopoutForm.IsBoundsOnScreen(rb))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = rb;
            if (maximized) WindowState = FormWindowState.Maximized;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }

        if (owner.Icon != null)
        {
            try { Icon = (Icon)owner.Icon.Clone(); } catch { }
        }

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            AllowExternalDrop = false
        };
        Controls.Add(_webView);

        Load += async (_, _) => await InitializeAsync();
        FormClosing += OnFormClosing;
        FormClosed += (_, _) =>
        {
            try { _webView?.Dispose(); } catch { }
            _webView = null;
        };
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (HasUnsavedEdits && e.CloseReason == CloseReason.UserClosing && !_closeConfirmed)
        {
            e.Cancel = true;
            try
            {
                CoreWebView?.PostWebMessageAsJson("{\"type\":\"PREVIEW_POPOUT_CLOSE_REQUEST\"}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PREVIEW-POPOUT] close request failed: {ex.Message}");
            }
            return;
        }
        try { _owner.CapturePreviewPopoutBounds(this); } catch { }
    }

    public void ConfirmClose()
    {
        _closeConfirmed = true;
        HasUnsavedEdits = false;
        if (IsHandleCreated && !IsDisposed)
            Close();
    }

    private async Task InitializeAsync()
    {
        if (_webView == null) return;
        try
        {
            await _webView.EnsureCoreWebView2Async(_environment);
            var core = _webView.CoreWebView2;
            if (core == null) return;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => _owner.ServeAppResource(core, e, kickoffStartup: false);
            core.WebMessageReceived += (_, e) =>
            {
                try { _owner.HandlePreviewPopoutWebMessage(this, e.WebMessageAsJson); }
                catch (Exception ex) { Debug.WriteLine($"[PREVIEW-POPOUT] message: {ex.Message}"); }
            };

            if (IsHandleCreated)
                _owner.ApplyWindowChromeFromThemeToHandle(Handle, _themeId);

            string langQuery = Uri.EscapeDataString(_language);
            core.Navigate($"https://f76manager.app/bundle-preview-popout.html?lang={langQuery}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PREVIEW-POPOUT] init failed: {ex.Message}");
            MessageBox.Show(this, $"Could not open the preview window:\n{ex.Message}", "Preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }
}
