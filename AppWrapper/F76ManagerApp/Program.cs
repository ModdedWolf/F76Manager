// Modded Wolf Was Here!!
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace F76ManagerApp;

static class Program
{
    private static Mutex? _mutex = null;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    [STAThread]
    static void Main(string[] args)
    {
        const string appName = "F76ManagerApp-SingleInstance-Mutex";
        bool createdNew;

        _mutex = new Mutex(true, appName, out createdNew);

        if (!createdNew)
        {
            string message = args != null && args.Length > 0 && args[0].StartsWith("nxm://") ? args[0] : "SHOW";
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                try
                {
                    using var client = new System.IO.Pipes.NamedPipeClientStream(".", "F76ManagerPipe", System.IO.Pipes.PipeDirection.Out);
                    client.Connect(2000);
                    using var writer = new StreamWriter(client);
                    writer.Write(message);
                    writer.Flush();
                    break;
                }
                catch
                {
                    if (DateTime.UtcNow >= deadline) break;
                    Thread.Sleep(250);
                }
            }

            Process current = Process.GetCurrentProcess();
            foreach (Process process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id != current.Id)
                {
                    IntPtr handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        ShowWindow(handle, SW_RESTORE);
                        SetForegroundWindow(handle);
                    }
                    else
                    {
                    }
                    break;
                }
            }
            return;
        }

        StartupTrace.BeginSession(Form1.CurrentVersion);
        StartupTrace.Mark("Main: single-instance mutex acquired");

        ApplicationConfiguration.Initialize();
        StartupTrace.Mark("Main: ApplicationConfiguration.Initialize done");
        
        Application.ApplicationExit += (s, e) => {
            if (_mutex != null) {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
            }
        };

        var webViewEnvironment = BeginPrewarmWebViewEnvironment();
        StartupTrace.Mark("Main: WebView2 environment prewarm started");
        var form = new Form1(webViewEnvironment);
        StartupTrace.Mark("Main: Form1 constructed");
        if (args != null && args.Length > 0 && args[0].StartsWith("nxm://"))
        {
             form.InitialNxmLink = args[0];
        }

        Application.Run(form);
    }

    private static Task<CoreWebView2Environment> BeginPrewarmWebViewEnvironment()
    {
        try
        {
            Directory.CreateDirectory(Form1.GetDefaultWebViewUserDataFolder());
        }
        catch { }

        return CoreWebView2Environment.CreateAsync(null, Form1.GetDefaultWebViewUserDataFolder(), null);
    }
}
