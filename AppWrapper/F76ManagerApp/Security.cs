using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Management;
using System.Windows.Forms;

namespace F76ManagerApp;

public static class Security
{
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref bool isDebuggerPresent);

    [DllImport("kernel32.dll")]
    private static extern bool IsDebuggerPresent();

    private static readonly string[] BlacklistedProcessNames = {
        "dnspy",
        "dnspy-x86",
        "ida",
        "ida64",
        "idag",
        "idag64",
        "x64dbg",
        "x32dbg",
        "ollydbg",
        "fiddler",
        "httpdebugger",
        "wireshark",
        "cheatengine",
        "cheat engine",
        "de4dot",
        "ilspy",
        "dotpeek",
        "procmon"
    };

    private static bool _running;
    private static CancellationTokenSource _cts = new CancellationTokenSource();
    private static Action<string> _secLogger;
    private static Action<int>? _requestGracefulExit;

    public static void Init(Action<string>? logger = null, Action<int>? requestGracefulExit = null)
    {
        _secLogger = logger ?? Console.WriteLine;
        _requestGracefulExit = requestGracefulExit;
    }

    public static void StartMonitoring()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();

        var thread = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "SecurityMonitor"
        };
        thread.Start();
    }

    public static void StopMonitoring()
    {
        _running = false;
        _cts.Cancel();
    }

    private static void MonitorLoop()
    {
        var token = _cts.Token;
        while (_running && !token.IsCancellationRequested)
        {
            try
            {
                string reason = "";
                if (IsDebuggerAttached(ref reason))
                {
                    _secLogger?.Invoke($"Security Violation: Debugger/Profiling Detected ({reason}). Closing application.");
                    if (_requestGracefulExit != null)
                    {
                        _requestGracefulExit(0xDEAD);
                        return;
                    }
                    Environment.Exit(0xDEAD);
                }
                if (IsBlacklistedProcessRunning(ref reason))
                {
                    _secLogger?.Invoke($"Security Violation: Blacklisted Process Detected ({reason}). Closing application.");
                    if (_requestGracefulExit != null)
                    {
                        _requestGracefulExit(0xDEAD);
                        return;
                    }
                    Environment.Exit(0xDEAD);
                }
            }
            catch (Exception ex)
            {
                _secLogger?.Invoke($"Monitor internal error: {ex.GetType().Name} - {ex.Message}");
            }

            try
            {
                if (token.WaitHandle.WaitOne(5000)) break; 
            }
            catch (Exception ex)
            {
                _secLogger?.Invoke($"[SECURITY] WaitHandle check failed: {ex.GetType().Name} - {ex.Message}");
            }
        }
    }


    private static bool IsDebuggerAttached(ref string reason)
    {
        bool isMonitoringToolRunning = Process.GetProcessesByName("SystemInformer").Length > 0 || 
                                     Process.GetProcessesByName("ProcessHacker").Length > 0;

        if (Debugger.IsAttached) { reason = "Managed_Debugger"; return true; }
        if (IsDebuggerPresent()) { reason = "Unmanaged_Debugger"; return true; }

        bool remoteDebugger = false;
        CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, ref remoteDebugger);
        if (remoteDebugger) 
        { 
            if (isMonitoringToolRunning)
            {
                _secLogger?.Invoke("[SECURITY] Remote debugger signal detected, but whitelisted monitoring tool is active. Ignoring.");
            }
            else
            {
                reason = "Remote_Debugger"; 
                return true; 
            }
        }

        if (Environment.GetEnvironmentVariable("COR_ENABLE_PROFILING") == "1") { reason = "COR_Profiling"; return true; }
        if (Environment.GetEnvironmentVariable("WINDBG") != null) { reason = "WinDbg_Env"; return true; }

        try
        {
            var current = Process.GetCurrentProcess();
            var parent = GetParentProcess(current);
            if (parent != null)
            {
                string pName = parent.ProcessName.ToLower();
                if (BlacklistedProcessNames.Contains(pName))
                {
                    reason = $"Parent_{pName}";
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _secLogger?.Invoke($"[SECURITY] Parent process check failed: {ex.GetType().Name} - {ex.Message}");
        }

        return false;
    }

    private static Process GetParentProcess(Process current)
    {
        try
        {
            var query = new ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {current.Id}");
            var results = query.Get();
            var parentId = (uint)results.Cast<ManagementObject>().First()["ParentProcessId"];
            return Process.GetProcessById((int)parentId);
        }
        catch (Exception ex)
        {
            _secLogger?.Invoke($"[SECURITY] Failed to resolve parent process: {ex.GetType().Name} - {ex.Message}");
            return null;
        }
    }

    private static bool IsBlacklistedProcessRunning(ref string reason)
    {
        var processes = Process.GetProcesses();
        foreach (var p in processes)
        {
            try
            {
                string processName = p.ProcessName.ToLower();
                if (BlacklistedProcessNames.Contains(processName))
                {
                    reason = processName;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _secLogger?.Invoke($"[SECURITY] Failed inspecting process: {ex.GetType().Name} - {ex.Message}");
            }
        }
        return false;
    }
}