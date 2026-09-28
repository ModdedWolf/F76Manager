using System.Text.Json;
using F76ManagerApp.Managers;
using Microsoft.Web.WebView2.Core;

namespace F76ManagerApp;

public partial class Form1
{
    private ThemeCreatorForm? _themeCreator;
    private bool _themeCreatorAppliedPending;

    private void HandleOpenThemeCreator(string? editThemeId = null)
    {
        void Work()
        {
            try
            {
                editThemeId = string.IsNullOrWhiteSpace(editThemeId) ? null : editThemeId.Trim();

                if (_themeCreator != null && !_themeCreator.IsDisposed)
                {
                    if (_themeCreator.WindowState == FormWindowState.Minimized)
                        _themeCreator.WindowState = FormWindowState.Normal;
                    _themeCreator.BringToFront();
                    _themeCreator.Activate();
                    if (!string.IsNullOrEmpty(editThemeId) && _themeCreator.CoreWebView != null)
                    {
                        try
                        {
                            _themeCreator.CoreWebView.PostWebMessageAsJson(JsonSerializer.Serialize(new
                            {
                                type = "THEME_CREATOR_LOAD",
                                editThemeId,
                            }));
                        }
                        catch { }
                    }
                    return;
                }

                if (webView?.CoreWebView2?.Environment == null)
                {
                    SendStatusMessage("error", "WebView is not ready yet.", "theme_creator_not_ready");
                    return;
                }

                _themeCreatorAppliedPending = false;
                _themeCreator = new ThemeCreatorForm(
                    this,
                    webView.CoreWebView2.Environment,
                    uiTheme,
                    applicationLanguage,
                    editThemeId);
                _themeCreator.FormClosed += (_, _) =>
                {
                    try
                    {
                        if (!_themeCreatorAppliedPending)
                            PostThemePreviewEndToMain(restore: true);
                    }
                    catch { }
                    _themeCreatorAppliedPending = false;
                    _themeCreator = null;
                };
                _themeCreator.Show(this);
            }
            catch (Exception ex)
            {
                LogError($"[THEMES] Theme Creator pop-out failed: {ex.Message}");
                SendStatusMessage("error", $"Could not open Theme Creator: {ex.Message}");
            }
        }

        if (InvokeRequired) Invoke(Work);
        else Work();
    }

    internal void HandleThemeCreatorWebMessage(ThemeCreatorForm popout, string webMessageAsJson)
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
                case "THEME_PREVIEW_UPDATE":
                    ForwardThemePreviewToMain(root);
                    break;
                case "THEME_PREVIEW_END":
                {
                    bool restore = true;
                    if (root.TryGetProperty("restore", out var restoreEl))
                    {
                        if (restoreEl.ValueKind == JsonValueKind.False) restore = false;
                        else if (restoreEl.ValueKind == JsonValueKind.True) restore = true;
                    }
                    PostThemePreviewEndToMain(restore);
                    break;
                }
                case "SAVE_THEME_PACKAGE":
                    HandleSaveThemePackage(root, core);
                    break;
                case "GET_THEME_EDIT_DATA":
                    if (TryGetString(root, "id", out var editId, required: true))
                        HandleGetThemeEditData(editId, core);
                    break;
                case "SET_WINDOW_CHROME":
                    if (popout.IsHandleCreated)
                    {
                        string caption = root.TryGetProperty("caption", out var c) ? (c.GetString() ?? "") : "";
                        string text = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
                        string border = root.TryGetProperty("border", out var b) ? (b.GetString() ?? "") : "";
                        ApplyWindowChromeFromColorsToHandle(popout.Handle, caption, text, border);
                    }
                    break;
                case "CLOSE_THEME_CREATOR":
                    CloseThemeCreator();
                    break;
            }
        }
        catch (Exception ex)
        {
            LogError($"[THEMES] Theme Creator message failed: {ex.Message}");
        }
    }

    private void HandleGetThemeEditData(string themeId, CoreWebView2? replyTo)
    {
        void Post(object payload)
        {
            if (replyTo == null) return;
            try { replyTo.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); } catch { }
        }

        try
        {
            themeId = (themeId ?? "").Trim();
            var theme = _themePackageLoader.GetTheme(themeId);
            if (theme == null || string.IsNullOrWhiteSpace(theme.SourceFile) || !File.Exists(theme.SourceFile))
            {
                Post(new { type = "THEME_EDIT_DATA", ok = false, error = "Theme not found." });
                return;
            }

            if (!ThemePackageReader.TryRead(theme.SourceFile, out var content, out var err) || content == null)
            {
                Post(new { type = "THEME_EDIT_DATA", ok = false, error = err ?? "Could not read theme package." });
                return;
            }

            string mime = content.LogoExtension.ToLowerInvariant() switch
            {
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "image/png",
            };
            string logoDataUrl = $"data:{mime};base64,{Convert.ToBase64String(content.LogoBytes)}";

            Post(new
            {
                type = "THEME_EDIT_DATA",
                ok = true,
                id = content.Id,
                displayName = content.DisplayName,
                tokens = content.Tokens,
                logoLayout = content.LogoLayout,
                logoDataUrl,
            });
        }
        catch (Exception ex)
        {
            LogError($"[THEMES] GET_THEME_EDIT_DATA failed: {ex.Message}");
            Post(new { type = "THEME_EDIT_DATA", ok = false, error = ex.Message });
        }
    }

    private void ForwardThemePreviewToMain(JsonElement root)
    {
        try
        {
            if (webView?.CoreWebView2 == null) return;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "THEME_PREVIEW");
                if (root.TryGetProperty("css", out var css))
                    writer.WriteString("css", css.GetString() ?? "");
                if (root.TryGetProperty("logoUrl", out var logo))
                    writer.WriteString("logoUrl", logo.GetString() ?? "");
                if (root.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("layout");
                    layout.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            webView.CoreWebView2.PostWebMessageAsJson(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch (Exception ex)
        {
            LogError($"[THEMES] Forward preview failed: {ex.Message}");
        }
    }

    private void PostThemePreviewEndToMain(bool restore)
    {
        try
        {
            if (webView?.CoreWebView2 == null) return;
            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "THEME_PREVIEW_END",
                restore,
            }));
        }
        catch { }
    }

    private void PostThemeCreatorAppliedToMain(string themeId, string? displayName)
    {
        try
        {
            if (webView?.CoreWebView2 == null) return;
            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "THEME_CREATOR_APPLIED",
                themeId,
                displayName,
            }));
        }
        catch { }
    }

    private void CloseThemeCreator(bool applied = false)
    {
        void Work()
        {
            try
            {
                if (_themeCreator == null || _themeCreator.IsDisposed) return;
                if (applied) _themeCreatorAppliedPending = true;
                _themeCreator.Close();
                _themeCreator = null;
            }
            catch { }
        }

        if (InvokeRequired) Invoke(Work);
        else Work();
    }

    private void OnThemeCreatorInstallSuccess(string themeId, string displayName, CoreWebView2? replyTo)
    {
        PostThemeSaveResult(true, "install", themeId, displayName, null, replyTo);
        PostThemeCreatorAppliedToMain(themeId, displayName);
        CloseThemeCreator(applied: true);
    }
}
