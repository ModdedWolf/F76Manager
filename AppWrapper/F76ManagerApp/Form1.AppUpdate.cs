using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using F76ManagerApp.Managers;

namespace F76ManagerApp;

public partial class Form1
{
    private bool _appUpdateCheckStarted;
    private bool _appUpdateInstallRunning;
    private string _appUpdatePhase = "idle";
    private int _appUpdatePercent;
    private NexusManager.AppUpdateInfo? _appUpdateInfo;
    private CancellationTokenSource? _appUpdateCts;

    private CancellationToken BeginAppUpdateInstall()
    {
        _appUpdateInstallRunning = true;
        _appUpdateCts?.Dispose();
        _appUpdateCts = new CancellationTokenSource();
        return _appUpdateCts.Token;
    }

    private void HandleCancelAppUpdate()
    {
        if (!_appUpdateInstallRunning || _appUpdateCts == null) return;
        LogActivity("[APP-UPDATE] Cancel requested.");
        try { _appUpdateCts.Cancel(); } catch {  }
    }

    private void CancelledAppUpdate(NexusManager.AppUpdateInfo info)
    {
        RunOnUiThreadSafe(() =>
        {
            _appUpdateInstallRunning = false;
            LogActivity("[APP-UPDATE] Update cancelled.");
            SendAppUpdateStatus(info, "idle");
            SendStatusMessage("info", "Update cancelled.", "update_cancelled");
        });
    }

    private void EnsureAppUpdateCheck()
    {
        if (_appUpdateInfo != null)
        {
            SendAppUpdateStatus(_appUpdateInfo, _appUpdatePhase, _appUpdatePercent);
            return;
        }
        if (_appUpdateCheckStarted) return;
        if (_nexusManager == null || !_nexusManager.IsAuthenticated) return;

        _appUpdateCheckStarted = true;
        Task.Run(async () =>
        {
            NexusManager.AppUpdateInfo info;
            try
            {
                info = await _nexusManager.CheckAppUpdateAsync(CurrentVersion).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogError($"[APP-UPDATE] Check failed: {ex.Message}");
                info = new NexusManager.AppUpdateInfo
                {
                    LoggedIn = true,
                    CurrentVersion = CurrentVersion,
                    Error = ex.Message
                };
            }

            RunOnUiThreadSafe(() =>
            {
                _appUpdateInfo = info;
                SendAppUpdateStatus(info, "idle");
            });
        });
    }

    private void ReplayAppUpdateStatus()
    {
        if (_appUpdateInfo != null)
        {
            SendAppUpdateStatus(_appUpdateInfo, _appUpdatePhase, _appUpdatePercent);
            return;
        }
        EnsureAppUpdateCheck();
    }

    private void HandleInstallAppUpdate()
    {
        if (_appUpdateInstallRunning) return;
        if (_nexusManager == null || !_nexusManager.IsAuthenticated)
        {
            SendAppUpdateStatus(new NexusManager.AppUpdateInfo
            {
                LoggedIn = false,
                CurrentVersion = CurrentVersion,
                Error = "not_logged_in"
            }, "idle");
            return;
        }

        var token = BeginAppUpdateInstall();
        Task.Run(() => InstallAppUpdateAsync(token));
    }

    private void HandleOpenAppUpdatePage()
    {
        long fileId = _appUpdateInfo?.LatestFileId ?? 0;
        NexusManager.OpenModInBrowser(NexusManager.AppNexusModId, fileId, triggerModManagerDownload: true);
    }

    private async Task InstallAppUpdateAsync(CancellationToken token)
    {
        NexusManager.AppUpdateInfo info = _appUpdateInfo ?? new NexusManager.AppUpdateInfo
        {
            LoggedIn = true,
            CurrentVersion = CurrentVersion
        };

        try
        {
            info = await _nexusManager!.CheckAppUpdateAsync(CurrentVersion).ConfigureAwait(false);
            RunOnUiThreadSafe(() => _appUpdateInfo = info);

            if (!info.LatestFileId.HasValue || info.LatestFileId.Value <= 0)
            {
                FailAppUpdate(info, string.IsNullOrWhiteSpace(info.Error) ? "No update file on Nexus." : info.Error);
                return;
            }

            if (!info.UpdateAvailable)
            {
                RunOnUiThreadSafe(() =>
                {
                    _appUpdateInstallRunning = false;
                    SendAppUpdateStatus(info, "idle");
                });
                return;
            }

            if (!info.Premium)
            {
                NexusManager.OpenModInBrowser(NexusManager.AppNexusModId, info.LatestFileId.Value, triggerModManagerDownload: true);
                RunOnUiThreadSafe(() =>
                {
                    _appUpdateInstallRunning = false;
                    SendAppUpdateStatus(info, "needs_browser", message: "premium");
                });
                return;
            }

            string safeName = Path.GetFileName(string.IsNullOrWhiteSpace(info.LatestFileName)
                ? "F76Manager_Nexus.zip"
                : info.LatestFileName);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "F76Manager_Nexus.zip";

            string tempRoot = Path.Combine(Path.GetTempPath(), "F76ManagerUpdate");
            Directory.CreateDirectory(tempRoot);
            string zipPath = Path.Combine(tempRoot, safeName);

            RunOnUiThreadSafe(() => SendAppUpdateStatus(info, "downloading", 0));

            var download = await _nexusManager.DownloadFileToAsync(
                NexusManager.AppNexusModId,
                info.LatestFileId.Value,
                zipPath,
                percent => { if (!token.IsCancellationRequested) RunOnUiThreadSafe(() => SendAppUpdateStatus(info, "downloading", percent)); },
                token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                CancelledAppUpdate(info);
                return;
            }

            if (download.NeedsBrowser)
            {
                NexusManager.OpenModInBrowser(NexusManager.AppNexusModId, info.LatestFileId.Value, triggerModManagerDownload: true);
                RunOnUiThreadSafe(() =>
                {
                    _appUpdateInstallRunning = false;
                    SendAppUpdateStatus(info, "needs_browser", message: download.Error);
                });
                return;
            }

            if (!download.Ok || string.IsNullOrWhiteSpace(download.LocalPath) || !File.Exists(download.LocalPath))
            {
                FailAppUpdate(info, string.IsNullOrWhiteSpace(download.Error) ? "Download failed." : download.Error);
                return;
            }

            RunOnUiThreadSafe(() => SendAppUpdateStatus(info, "preparing", 100));
            await ApplyDownloadedAppPackageAsync(download.LocalPath, info, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CancelledAppUpdate(info);
        }
        catch (Exception ex)
        {
            LogError($"[APP-UPDATE] Install failed: {ex.Message}");
            FailAppUpdate(info, ex.Message);
        }
    }

    private void HandleAppUpdateFromNxm(long fileId, string? nxmKey, string? nxmExpires)
    {
        if (_appUpdateInstallRunning) return;
        if (fileId <= 0)
        {
            SendStatusMessage("error", "Nexus did not include an update file.", "update_failed", new object[] { "missing file" });
            return;
        }

        var token = BeginAppUpdateInstall();
        Task.Run(() => InstallAppUpdateFromNxmAsync(fileId, nxmKey, nxmExpires, token));
    }

    private async Task InstallAppUpdateFromNxmAsync(long fileId, string? nxmKey, string? nxmExpires, CancellationToken token)
    {
        NexusManager.AppUpdateInfo info = _appUpdateInfo ?? new NexusManager.AppUpdateInfo
        {
            LoggedIn = _nexusManager?.IsAuthenticated == true,
            CurrentVersion = CurrentVersion,
            LatestFileId = fileId,
            UpdateAvailable = true
        };
        info.LatestFileId = fileId;

        try
        {
            string safeName = Path.GetFileName(string.IsNullOrWhiteSpace(info.LatestFileName)
                ? "F76Manager_Nexus.zip"
                : info.LatestFileName);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "F76Manager_Nexus.zip";

            string tempRoot = Path.Combine(Path.GetTempPath(), "F76ManagerUpdate");
            Directory.CreateDirectory(tempRoot);
            string zipPath = Path.Combine(tempRoot, safeName);

            RunOnUiThreadSafe(() => SendAppUpdateStatus(info, "downloading", 0));

            var download = await _nexusManager!.DownloadFileToAsync(
                NexusManager.AppNexusModId,
                fileId,
                zipPath,
                percent => { if (!token.IsCancellationRequested) RunOnUiThreadSafe(() => SendAppUpdateStatus(info, "downloading", percent)); },
                token,
                nxmKey: nxmKey,
                nxmExpires: nxmExpires).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                CancelledAppUpdate(info);
                return;
            }

            if (!download.Ok || string.IsNullOrWhiteSpace(download.LocalPath) || !File.Exists(download.LocalPath))
            {
                FailAppUpdate(info, string.IsNullOrWhiteSpace(download.Error) ? "Download failed." : download.Error);
                return;
            }

            RunOnUiThreadSafe(() => SendAppUpdateStatus(info, "preparing", 100));
            await ApplyDownloadedAppPackageAsync(download.LocalPath, info, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CancelledAppUpdate(info);
        }
        catch (Exception ex)
        {
            LogError($"[APP-UPDATE] NXM install failed: {ex.Message}");
            FailAppUpdate(info, ex.Message);
        }
    }

    private Task ApplyDownloadedAppPackageAsync(string zipPath, NexusManager.AppUpdateInfo info, CancellationToken token)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "F76ManagerUpdate");
        string extractDir = Path.Combine(tempRoot, "extract");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        string? srcExe = FindManagerExe(extractDir);
        if (string.IsNullOrWhiteSpace(srcExe))
        {
            FailAppUpdate(info, "The Nexus archive does not contain F76Manager.exe.");
            return Task.CompletedTask;
        }

        string destExe = Application.ExecutablePath;
        string installDir = Path.GetDirectoryName(destExe) ?? AppDomain.CurrentDomain.BaseDirectory;
        string? srcTools = FindToolsDir(Path.GetDirectoryName(srcExe) ?? extractDir);
        string destTools = Path.Combine(installDir, "Tools");
        int pid = Environment.ProcessId;

        token.ThrowIfCancellationRequested();

        string scriptPath = Path.Combine(tempRoot, "apply-update.ps1");
        File.WriteAllText(scriptPath, BuildAppUpdateScript(pid, srcExe, destExe, srcTools, destTools));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        LogActivity("[APP-UPDATE] Install helper started. Closing so the new executable can be copied.");
        RunOnUiThreadSafe(() =>
        {
            try { Application.Exit(); }
            catch { Close(); }
        });
        return Task.CompletedTask;
    }

    private void FailAppUpdate(NexusManager.AppUpdateInfo info, string message)
    {
        RunOnUiThreadSafe(() =>
        {
            _appUpdateInstallRunning = false;
            info.Error = message;
            SendAppUpdateStatus(info, "error", message: message);
        });
    }

    private void SendAppUpdateStatus(NexusManager.AppUpdateInfo info, string phase, int percent = 0, string? message = null)
    {
        _appUpdateInfo = info;
        _appUpdatePhase = string.IsNullOrWhiteSpace(phase) ? "idle" : phase;
        _appUpdatePercent = percent;
        SendMessageToWeb(JsonSerializer.Serialize(new
        {
            type = "APP_UPDATE_STATUS",
            loggedIn = info.LoggedIn,
            premium = info.Premium,
            updateAvailable = info.UpdateAvailable,
            currentVersion = string.IsNullOrWhiteSpace(info.CurrentVersion) ? CurrentVersion : info.CurrentVersion,
            latestVersion = info.LatestVersion ?? "",
            latestFileId = info.LatestFileId,
            latestFileName = info.LatestFileName ?? "",
            changelog = info.Changelog ?? "",
            error = info.Error,
            phase,
            percent,
            message
        }));
    }

    private static string? FindManagerExe(string root)
    {
        string direct = Path.Combine(root, "F76Manager.exe");
        if (File.Exists(direct)) return direct;

        string? named = Directory.EnumerateFiles(root, "F76Manager.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(named)) return named;

        return Directory.EnumerateFiles(root, "F76ManagerApp.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string? FindToolsDir(string exeDir)
    {
        string tools = Path.Combine(exeDir, "Tools");
        return Directory.Exists(tools) ? tools : null;
    }

    private static string PsQuote(string path) => "'" + path.Replace("'", "''") + "'";

    private static string BuildAppUpdateScript(int pid, string srcExe, string destExe, string? srcTools, string destTools)
    {
        string toolsCopy = string.IsNullOrWhiteSpace(srcTools)
            ? ""
            : $@"
if (Test-Path -LiteralPath {PsQuote(srcTools)}) {{
  New-Item -ItemType Directory -Force -Path {PsQuote(destTools)} | Out-Null
  Copy-Item -LiteralPath (Join-Path {PsQuote(srcTools)} '*') -Destination {PsQuote(destTools)} -Recurse -Force
}}";

        return $@"
$ErrorActionPreference = 'Stop'
Wait-Process -Id {pid} -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
$deadline = (Get-Date).AddSeconds(45)
$copied = $false
while ((Get-Date) -lt $deadline) {{
  try {{
    Copy-Item -LiteralPath {PsQuote(srcExe)} -Destination {PsQuote(destExe)} -Force -ErrorAction Stop
    $copied = $true
    break
  }} catch {{
    Start-Sleep -Milliseconds 500
  }}
}}
if ($copied) {{
{toolsCopy}
}}
Start-Process -FilePath {PsQuote(destExe)}
";
    }
}
