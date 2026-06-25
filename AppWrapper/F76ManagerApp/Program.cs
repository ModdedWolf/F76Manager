using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;

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
            try {
                using (var client = new System.IO.Pipes.NamedPipeClientStream(".", "F76ManagerPipe", System.IO.Pipes.PipeDirection.Out))
                {
                    client.Connect(1000);
                    using (var writer = new StreamWriter(client))
                    {
                        if (args != null && args.Length > 0 && args[0].StartsWith("nxm://"))
                        {
                            writer.Write(args[0]);
                        }
                        else
                        {
                            writer.Write("SHOW");
                        }
                        writer.Flush();
                    }
                }
            } catch { }

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

        ApplicationConfiguration.Initialize();
        
        Application.ApplicationExit += (s, e) => {
            if (_mutex != null) {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
            }
        };

        var form = new Form1();
        if (args != null && args.Length > 0 && args[0].StartsWith("nxm://"))
        {
             form.InitialNxmLink = args[0];
        }

        Application.Run(form);
    }    
}
