using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace F76ManagerApp;

public partial class Form1
{
    private string _lastDropSignature = "";
    private DateTime _lastDropSignatureAtUtc = DateTime.MinValue;
    private readonly object _uriDropBatchLock = new();
    private readonly HashSet<string> _uriDropBatchPaths = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _uriDropBatchTimer;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    public static extern int DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, int cch);
    
    [DllImport("shell32.dll")]
    public static extern void DragFinish(IntPtr hDrop);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, ref CHANGEFILTERSTRUCT pChangeFilterStruct);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool ChangeWindowMessageFilter(uint msg, uint action);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;
    private const int MAX_PATH = 260;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DROPFILES)
        {
            LogActivity("[WndProc] WM_DROPFILES received! Intercepting...");
            try
            {
                IntPtr hDrop = m.WParam;
                int count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                
                List<string> droppedFiles = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    StringBuilder sb = new StringBuilder(MAX_PATH);
                    DragQueryFile(hDrop, i, sb, MAX_PATH);
                    if (sb.Length > 0) droppedFiles.Add(sb.ToString());
                }
                DragFinish(hDrop);

                if (droppedFiles.Count > 0)
                {
                    LogActivity($"[WndProc] Processing {droppedFiles.Count} files via direct hook.");
                    ProcessDroppedFiles(droppedFiles.ToArray(), "WndProc");
                }
            }
            catch (Exception ex)
            {
                LogError($"[WndProc] Error handling drop: {ex.Message}");
            }
            return;
        }
        base.WndProc(ref m);
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == WM_DROPFILES)
        {
            LogActivity("[IMessageFilter] WM_DROPFILES Intercepted!");
            try 
            {
                IntPtr hDrop = m.WParam;
                int count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                var files = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    StringBuilder sb = new StringBuilder(MAX_PATH);
                    DragQueryFile(hDrop, i, sb, MAX_PATH);
                    files.Add(sb.ToString());
                }
                DragFinish(hDrop);

                if (files.Count > 0)
                {
                    LogActivity($"[IMessageFilter] Dropped {files.Count} files.");
                    this.BeginInvoke(new Action(() => ProcessDroppedFiles(files.ToArray(), "MessageFilter")));
                    return true;
                }
            } catch (Exception ex) { LogError($"[IMessageFilter] Error: {ex.Message}"); }
        }
        return false;
    }

    private void EnableDragDropMessages(IntPtr hwnd)
    {
        DragAcceptFiles(hwnd, true);

        if (!IsRunningAsAdmin()) return; 

        try {
            CHANGEFILTERSTRUCT filterStatus = new CHANGEFILTERSTRUCT();
            filterStatus.cbSize = (uint)Marshal.SizeOf(typeof(CHANGEFILTERSTRUCT));
            
            ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, ref filterStatus);
            ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, ref filterStatus);
            ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, ref filterStatus);
            
            ChangeWindowMessageFilter(WM_DROPFILES, MSGFLT_ALLOW);
        } catch (Exception ex) {
            LogError($"[DND] Failed to configure elevated drag/drop message filters: {ex.Message}");
        }
    }

    private void ApplyDeepUIPI()
    {
         EnableDragDropMessages(this.Handle);
         EnumChildWindows(this.Handle, (hwnd, lParam) => {
             EnableDragDropMessages(hwnd);
             return true; 
         }, IntPtr.Zero);
    }

    private void WebView_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
             e.Effect = DragDropEffects.Copy;
        } else {
             e.Effect = DragDropEffects.None;
        }
    }

    private void WebView_DragDrop(object? sender, DragEventArgs e)
    {
        LogActivity("[DND] Drop Event Detected.");
        if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[]? files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                LogActivity($"[DND] Found {files.Length} files. Processing...");
                ProcessDroppedFiles(files, "Native WinForms Drop");
            }
        }
    }

    private void ProcessDroppedFiles(string[] files, string source)
    {
        if (files == null || files.Length == 0) return;

        var normalized = files
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.GetFullPath(f).Trim().ToLowerInvariant())
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (normalized.Count == 0) return;

        string signature = string.Join("|", normalized);
        var now = DateTime.UtcNow;
        if (string.Equals(signature, _lastDropSignature, StringComparison.Ordinal) &&
            (now - _lastDropSignatureAtUtc).TotalMilliseconds < 1500)
        {
            LogActivity($"[PROCESS] Duplicate drop ignored from {source}.");
            return;
        }
        _lastDropSignature = signature;
        _lastDropSignatureAtUtc = now;

        LogActivity($"[PROCESS] {source} dropped {files.Length} items.");
        HandleAddModFiles(files.ToList());
    }

    private bool IsModFileSimple(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".ba2" || ext == ".esm" || ext == ".esp" || 
               ext == ".strings" || ext == ".dlstrings" || ext == ".ilstrings" || ext == ".zip";
    }

    private bool IsModFile(string uriString)
    {
        if (string.IsNullOrEmpty(uriString)) return false;
        if (uriString.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return true;
        string clean = uriString.ToLowerInvariant();
        return clean.EndsWith(".ba2") || clean.EndsWith(".esm") || clean.EndsWith(".esp") || clean.EndsWith(".zip") ||
               clean.EndsWith(".7z") || clean.EndsWith(".rar");
    }

    private void HandleModImportUri(string uriString)
    {
        try {
            Uri uri = new Uri(uriString);
            if (!uri.IsFile) return;
            string sourcePath = uri.LocalPath;
            
            LogActivity($"[URI_IMPORT] Intercepted Drop: {sourcePath}");

            if (Directory.Exists(sourcePath))
            {
                QueueUriDropPath(sourcePath);
            }
            else if (File.Exists(sourcePath)) 
            {
                QueueUriDropPath(sourcePath);
            }
        } catch (Exception ex) {
            LogError($"Import failed: {ex.Message}");
            SendStatusMessage("error", "Failed to import mod.");
        }
    }

    private void QueueUriDropPath(string sourcePath)
    {
        string normalized = Path.GetFullPath(sourcePath).Trim();
        lock (_uriDropBatchLock)
        {
            _uriDropBatchPaths.Add(normalized);
            if (_uriDropBatchTimer == null)
            {
                _uriDropBatchTimer = new System.Threading.Timer(_ => FlushQueuedUriDrops(), null, 750, Timeout.Infinite);
            }
            else
            {
                _uriDropBatchTimer.Change(750, Timeout.Infinite);
            }
        }
    }

    private void FlushQueuedUriDrops()
    {
        List<string> paths;
        lock (_uriDropBatchLock)
        {
            paths = _uriDropBatchPaths.ToList();
            _uriDropBatchPaths.Clear();
        }

        if (paths.Count == 0) return;
        try
        {
            this.BeginInvoke(new Action(() => ProcessDroppedFiles(paths.ToArray(), "URI Batch")));
        }
        catch (Exception ex)
        {
            LogError($"[DND] Failed to flush queued URI drops: {ex.Message}");
        }
    }
}
