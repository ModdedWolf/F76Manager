using System;
using System.Diagnostics;
using NvAPIWrapper;
using NvAPIWrapper.DRS;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.DRS;
using NvAPIWrapper.Native.DRS.Structures;

namespace F76ManagerApp.Managers
{
    public static class GpuFpsLimiter
    {
        private const uint FRL_FPS_ID = 0x10835002;

        private static Action<string> _logger;

        public static void SetLogger(Action<string> logger) => _logger = logger;

        public static bool SetFpsLimit(string appName, int fpsLimit)
        {
            var result = TrySetViaNvApi(appName, fpsLimit);
            if (result == SetResult.Success) return true;
            
            if (result == SetResult.PrivilegeError)
            {
                Log("Requires elevation — retrying with admin privileges...");
                return TrySetElevated(appName, fpsLimit);
            }

            return false;
        }

        private enum SetResult { Success, PrivilegeError, OtherError }

        private static SetResult TrySetViaNvApi(string appName, int fpsLimit)
        {
            try
            {
                NVIDIA.Initialize();

                using (var session = DriverSettingsSession.CreateAndLoad())
                {
                    DriverSettingsProfile profile = null;
                    string exeName = appName.ToLower();

                    try
                    {
                        var app = session.FindApplication(exeName);
                        if (app != null)
                        {
                            profile = app.Profile;
                            Log($"Found existing NVIDIA profile for {exeName}: \"{profile.Name}\"");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"FindApplication lookup failed for {exeName}: {ex.Message}");
                    }

                    if (profile == null)
                    {
                        profile = DriverSettingsProfile.CreateProfile(session, "Fallout 76");
                        var appInfo = new DRSApplicationV1(exeName);
                        DRSApi.CreateApplication(session.Handle, profile.Handle, appInfo);
                        Log($"Created new NVIDIA profile for {exeName}");
                    }

                    if (fpsLimit <= 0)
                    {
                        profile.SetSetting(FRL_FPS_ID, (uint)0);
                        Log("NVIDIA frame rate limiter disabled (Unlimited)");
                    }
                    else
                    {
                        profile.SetSetting(FRL_FPS_ID, (uint)fpsLimit);
                        Log($"NVIDIA frame rate limit set to {fpsLimit} FPS");
                    }

                    session.Save();
                    Log("NVIDIA driver profile saved");
                }

                return SetResult.Success;
            }
            catch (Exception ex)
            {
                string msg = ex.Message ?? "";
                if (msg.Contains("PRIVILEGE", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("ACCESS", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"NvAPI privilege error: {msg}");
                    return SetResult.PrivilegeError;
                }
                Log($"NvAPI error: {msg}");
                return SetResult.OtherError;
            }
        }

        private static bool TrySetElevated(string appName, int fpsLimit)
        {
            try
            {
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string nvApiDll = System.IO.Path.Combine(appDir, "NvAPIWrapper.dll");


                string exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    Log("Cannot determine own process path for elevation");
                    return false;
                }

                uint frlValue = fpsLimit <= 0 ? 0 : (uint)fpsLimit;
                string script = $@"
Add-Type -Path '{nvApiDll.Replace("'", "''")}'
[NvAPIWrapper.NVIDIA]::Initialize()
$session = [NvAPIWrapper.DRS.DriverSettingsSession]::CreateAndLoad()
try {{
    $app = $session.FindApplication('{appName.ToLower()}')
    if ($app) {{
        $profile = $app.Profile
    }} else {{
        $profile = [NvAPIWrapper.DRS.DriverSettingsProfile]::CreateProfile($session, 'Fallout 76')
        $appInfo = New-Object NvAPIWrapper.Native.DRS.Structures.DRSApplicationV1('{appName.ToLower()}')
        [NvAPIWrapper.Native.DRS.DRSApi]::CreateApplication($session.Handle, $profile.Handle, $appInfo)
    }}
    $profile.SetSetting([uint32]0x10835002, [uint32]{frlValue})
    $session.Save()
    Write-Output 'SUCCESS'
}} finally {{
    $session.Dispose()
}}
";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(10000);
                    if (proc.ExitCode == 0)
                    {
                        Log($"NVIDIA FPS limit set to {fpsLimit} via elevated process");
                        return true;
                    }
                    else
                    {
                        Log($"Elevated process exited with code {proc.ExitCode}");
                        return false;
                    }
                }
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Log("User declined admin elevation — FPS cap not applied to GPU driver");
                return false;
            }
            catch (Exception ex)
            {
                Log($"Elevated fallback failed: {ex.Message}");
                return false;
            }
        }

        private static void Log(string msg) => _logger?.Invoke($"[GPU-FPS] {msg}");
    }
}
