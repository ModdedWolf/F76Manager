using System.Text.Json;

namespace F76ManagerApp;

public partial class Form1
{
    private PreviewPopoutForm? _previewPopout;
    private FileSystemWatcher? _previewFolderWatcher;
    private System.Windows.Forms.Timer? _previewFolderDebounce;
    private string? _watchedPreviewFullPath;
    private string? _watchedPreviewSource;
    private string? _watchedPreviewPath;
    private long _watchedPreviewStamp;
    private int _previewWatchGeneration;
    private string? _previewPopoutTargetJson;
    private Rectangle? _previewPopoutBounds;
    private bool _previewPopoutMaximized;

    private void HandleOpenBundlePreviewPopout(JsonElement root)
    {
        TryGetString(root, "source", out var source);
        TryGetString(root, "path", out var path);
        TryGetString(root, "mode", out var mode);
        TryGetString(root, "archive", out var archive);
        TryGetString(root, "entryPath", out var entryPath);
        TryGetString(root, "name", out var name);
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(path)) return;

        _previewPopoutTargetJson = JsonSerializer.Serialize(new
        {
            type = "PREVIEW_POPOUT_SHOW",
            target = new
            {
                source,
                path,
                mode = string.IsNullOrWhiteSpace(mode) ? "ba2" : mode,
                archive,
                entryPath = string.IsNullOrWhiteSpace(entryPath) ? path : entryPath,
                name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name
            },
            fontSize = configEditorFontSize,
            fontFamily = configEditorFontFamily
        });
        string title = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name;

        void Work()
        {
            try
            {
                if (_previewPopout != null && !_previewPopout.IsDisposed)
                {
                    _previewPopout.Text = $"F76 Manager — {title}";
                    if (_previewPopout.WindowState == FormWindowState.Minimized)
                        _previewPopout.WindowState = FormWindowState.Normal;
                    _previewPopout.BringToFront();
                    _previewPopout.Activate();
                    PostToPreviewPopout(_previewPopoutTargetJson);
                    return;
                }

                if (webView?.CoreWebView2?.Environment == null)
                {
                    SendStatusMessage("error", "WebView is not ready yet.", "theme_creator_not_ready");
                    return;
                }

                _previewPopout = new PreviewPopoutForm(
                    this,
                    webView.CoreWebView2.Environment,
                    uiTheme,
                    applicationLanguage,
                    _previewPopoutBounds,
                    _previewPopoutMaximized)
                {
                    Text = $"F76 Manager — {title}"
                };
                _previewPopout.FormClosed += (_, _) => _previewPopout = null;
                _previewPopout.Show();
            }
            catch (Exception ex)
            {
                LogError($"[PREVIEW] Pop-out failed: {ex.Message}");
                SendStatusMessage("error", $"Could not open the preview window: {ex.Message}", "preview_popout_open_failed", new object[] { ex.Message });
            }
        }

        if (InvokeRequired) Invoke(Work);
        else Work();
    }

    internal void CapturePreviewPopoutBounds(PreviewPopoutForm form)
    {
        if (form == null || form.IsDisposed) return;
        var bounds = form.WindowState == FormWindowState.Maximized ? form.RestoreBounds : form.Bounds;
        if (bounds.Width < 200 || bounds.Height < 150) return;
        _previewPopoutBounds = bounds;
        _previewPopoutMaximized = form.WindowState == FormWindowState.Maximized;
    }

    private void PostToPreviewPopout(string? json)
    {
        if (string.IsNullOrEmpty(json)) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => PostToPreviewPopout(json))); } catch { }
            return;
        }
        try
        {
            if (_previewPopout == null || _previewPopout.IsDisposed) return;
            _previewPopout.CoreWebView?.PostWebMessageAsJson(json);
        }
        catch { }
    }

    private void NotifyPreviewPopoutTheme()
    {
        try
        {
            if (_previewPopout == null || _previewPopout.IsDisposed) return;
            var sync = _lastThemeSync;
            if (sync != null && _previewPopout.IsHandleCreated && !string.IsNullOrWhiteSpace(sync.Caption))
                ApplyWindowChromeFromColorsToHandle(_previewPopout.Handle, sync.Caption, sync.Text, sync.Border);
            PostThemeSyncToPopout(_previewPopout.CoreWebView);
        }
        catch { }
    }

    private void ClosePreviewPopout()
    {
        try
        {
            if (_previewPopout == null || _previewPopout.IsDisposed) return;
            _previewPopout.HasUnsavedEdits = false;
            _previewPopout.Close();
            _previewPopout = null;
        }
        catch { }
    }

    internal void HandlePreviewPopoutWebMessage(PreviewPopoutForm popout, string webMessageAsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(webMessageAsJson);
            var root = doc.RootElement;
            if (!TryGetString(root, "type", out var type)) return;

            switch (type)
            {
                case "PREVIEW_POPOUT_READY":
                    NotifyPreviewPopoutTheme();
                    PostToPreviewPopout(_previewPopoutTargetJson);
                    break;
                case "GET_THEME_SYNC":
                    NotifyPreviewPopoutTheme();
                    break;
                case "BUNDLE_PREVIEW_FILE":
                    if (TryGetString(root, "source", out var source, required: true) &&
                        TryGetString(root, "path", out var path, required: true))
                    {
                        TryGetString(root, "mode", out var mode);
                        int requestId = root.TryGetProperty("requestId", out var idProp) && idProp.TryGetInt32(out var idValue) ? idValue : 0;
                        bool wantModel = root.TryGetProperty("model", out var modelProp) &&
                            (modelProp.ValueKind == JsonValueKind.True ||
                             (modelProp.ValueKind == JsonValueKind.String &&
                              string.Equals(modelProp.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
                        HandleBundlePreviewFile(source, string.IsNullOrWhiteSpace(mode) ? "ba2" : mode, path, requestId, PostToPreviewPopout, wantModel);
                    }
                    break;
                case "BUNDLE_LOAD_TEXT_EDIT":
                    HandleBundleLoadTextEdit(root, PostToPreviewPopout);
                    break;
                case "BUNDLE_SAVE_TEXT_EDIT":
                    HandleBundleSaveTextEdit(root, PostToPreviewPopout);
                    break;
                case "SAVE_CONFIG_EDITOR_FONT":
                    if (root.TryGetProperty("fontSize", out var fs))
                        configEditorFontSize = NormalizeConfigEditorFontSize(fs, configEditorFontSize);
                    if (root.TryGetProperty("fontFamily", out var ff))
                        configEditorFontFamily = NormalizeConfigEditorFontFamily(ff, configEditorFontFamily);
                    SaveSettings();
                    SendMessageToWeb(BuildConfigEditorFontChangedJson());
                    break;
                case "PREVIEW_POPOUT_DIRTY":
                    popout.HasUnsavedEdits = root.TryGetProperty("dirty", out var dirty) && dirty.ValueKind == JsonValueKind.True;
                    break;
                case "PREVIEW_POPOUT_CLOSE_CONFIRMED":
                    popout.ConfirmClose();
                    break;
            }
        }
        catch (Exception ex)
        {
            LogError($"[PREVIEW] Pop-out message failed: {ex.Message}");
        }
    }

    private void ArmPreviewFolderWatch(string fullPath, string source, string relativePath, long servedStamp)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(() => ArmPreviewFolderWatch(fullPath, source, relativePath, servedStamp)); }
            catch { }
            return;
        }
        if (IsDisposed || string.IsNullOrWhiteSpace(fullPath)) return;

        string full = Path.GetFullPath(fullPath);
        bool same = _previewFolderWatcher != null
            && string.Equals(_watchedPreviewFullPath, full, StringComparison.OrdinalIgnoreCase);
        if (!same)
        {
            StopPreviewFolderWatch();
            string? dir = Path.GetDirectoryName(full);
            string name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || !Directory.Exists(dir)) return;

            _previewWatchGeneration++;
            _watchedPreviewFullPath = full;
            _previewFolderDebounce = new System.Windows.Forms.Timer { Interval = 400 };
            _previewFolderDebounce.Tick += (_, _) => OnPreviewFolderDebounced();
            _previewFolderWatcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            FileSystemEventHandler onChange = (_, _) => SchedulePreviewFolderReload();
            _previewFolderWatcher.Changed += onChange;
            _previewFolderWatcher.Created += onChange;
            _previewFolderWatcher.Renamed += (_, _) => SchedulePreviewFolderReload();
            _previewFolderWatcher.EnableRaisingEvents = true;
        }

        _watchedPreviewSource = source;
        _watchedPreviewPath = relativePath;
        _watchedPreviewStamp = servedStamp;
        try
        {
            long now = PreviewFileStamp(full);
            if (now != 0 && now != servedStamp)
                SchedulePreviewFolderReload();
        }
        catch { }
    }

    private void SchedulePreviewFolderReload()
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(SchedulePreviewFolderReload); }
            catch { }
            return;
        }
        if (_previewFolderDebounce == null || IsDisposed) return;
        _previewFolderDebounce.Stop();
        _previewFolderDebounce.Start();
    }

    private void OnPreviewFolderDebounced()
    {
        _previewFolderDebounce?.Stop();
        string? full = _watchedPreviewFullPath;
        string? source = _watchedPreviewSource;
        string? path = _watchedPreviewPath;
        long served = _watchedPreviewStamp;
        int generation = _previewWatchGeneration;
        if (string.IsNullOrEmpty(full) || IsDisposed) return;

        Task.Run(() =>
        {
            if (!TryStablePreviewStamp(full, out long stamp) || stamp == 0 || stamp == served) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed || generation != _previewWatchGeneration) return;
                    if (!string.Equals(full, _watchedPreviewFullPath, StringComparison.OrdinalIgnoreCase)) return;
                    if (stamp == _watchedPreviewStamp) return;
                    _watchedPreviewStamp = stamp;
                    string json = JsonSerializer.Serialize(new
                    {
                        type = "BUNDLE_PREVIEW_SOURCE_CHANGED",
                        source,
                        mode = "folder",
                        path
                    });
                    SendMessageToWeb(json);
                    PostToPreviewPopout(json);
                });
            }
            catch { }
        });
    }

    internal static long PreviewFileStamp(string full)
    {
        var info = new FileInfo(full);
        info.Refresh();
        if (!info.Exists) return 0;
        return unchecked(info.Length * 397) ^ info.LastWriteTimeUtc.Ticks;
    }

    private static bool TryStablePreviewStamp(string full, out long stamp)
    {
        stamp = 0;
        long previous = -1;
        for (int i = 0; i < 8; i++)
        {
            try
            {
                using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length == 0 && i < 7)
                    {
                        Thread.Sleep(40);
                        continue;
                    }
                }
                long now = PreviewFileStamp(full);
                if (now != 0 && now == previous)
                {
                    stamp = now;
                    return true;
                }
                previous = now;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(40);
        }
        if (previous > 0)
        {
            stamp = previous;
            return true;
        }
        return false;
    }

    private void StopPreviewFolderWatch()
    {
        _previewWatchGeneration++;
        _watchedPreviewFullPath = null;
        _watchedPreviewSource = null;
        _watchedPreviewPath = null;
        _watchedPreviewStamp = 0;
        if (_previewFolderWatcher != null)
        {
            _previewFolderWatcher.EnableRaisingEvents = false;
            _previewFolderWatcher.Dispose();
            _previewFolderWatcher = null;
        }
        if (_previewFolderDebounce != null)
        {
            _previewFolderDebounce.Stop();
            _previewFolderDebounce.Dispose();
            _previewFolderDebounce = null;
        }
    }
}
