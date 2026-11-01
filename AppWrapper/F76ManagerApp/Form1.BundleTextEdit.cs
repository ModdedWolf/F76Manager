using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using F76ManagerApp.Managers;

namespace F76ManagerApp;

public partial class Form1
{
    private const long BundleTextEditMaxBytes = 8L * 1024 * 1024;

    private void HandleBundleLoadTextEdit(JsonElement root, Action<string> post)
    {
        TryGetString(root, "source", out var source);
        TryGetString(root, "path", out var entryPath);
        TryGetString(root, "mode", out var mode);
        int requestId = root.TryGetProperty("requestId", out var idProp) && idProp.TryGetInt32(out var idValue) ? idValue : 0;

        Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(entryPath))
                {
                    post(JsonSerializer.Serialize(new
                    {
                        type = "BUNDLE_TEXT_EDIT_CONTENT",
                        requestId,
                        path = entryPath,
                        error = "missing_edit"
                    }));
                    return;
                }

                byte[] bytes = string.Equals(mode, "folder", StringComparison.OrdinalIgnoreCase)
                    ? ReadPreviewFolderFile(source, entryPath)
                    : BA2Utility.ReadEntryBytes(ResolveBundleBa2Path(source), entryPath, LogActivity);

                if (bytes.LongLength > BundleTextEditMaxBytes)
                {
                    post(JsonSerializer.Serialize(new
                    {
                        type = "BUNDLE_TEXT_EDIT_CONTENT",
                        requestId,
                        path = entryPath,
                        size = bytes.LongLength,
                        error = "too_large"
                    }));
                    return;
                }

                string text = DecodeEditableText(bytes, out string encoding);
                string eol = text.Contains("\r\n") ? "crlf" : "lf";
                post(JsonSerializer.Serialize(new
                {
                    type = "BUNDLE_TEXT_EDIT_CONTENT",
                    requestId,
                    path = entryPath,
                    size = bytes.LongLength,
                    text = text.Replace("\r\n", "\n"),
                    encoding,
                    eol
                }));
            }
            catch (BA2Utility.PreviewFileTooLargeException ex)
            {
                post(JsonSerializer.Serialize(new
                {
                    type = "BUNDLE_TEXT_EDIT_CONTENT",
                    requestId,
                    path = entryPath,
                    size = ex.Size,
                    error = "too_large"
                }));
            }
            catch (Exception ex)
            {
                LogError($"[BUNDLE] Load text for edit failed: {ex.Message}");
                post(JsonSerializer.Serialize(new
                {
                    type = "BUNDLE_TEXT_EDIT_CONTENT",
                    requestId,
                    path = entryPath,
                    error = ex.Message
                }));
            }
        });
    }

    private void HandleBundleSaveTextEdit(JsonElement root, Action<string> reply)
    {
        TryGetString(root, "archive", out var archive);
        TryGetString(root, "path", out var entryPath);
        TryGetString(root, "text", out var text);
        TryGetString(root, "encoding", out var encoding);
        TryGetString(root, "eol", out var eol);
        int requestId = root.TryGetProperty("requestId", out var idProp) && idProp.TryGetInt32(out var idValue) ? idValue : 0;

        Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(archive) || string.IsNullOrWhiteSpace(entryPath))
                {
                    reply(JsonSerializer.Serialize(new
                    {
                        type = "BUNDLE_TEXT_EDIT_SAVE_FAILED",
                        requestId,
                        path = entryPath,
                        error = "missing_save"
                    }));
                    return;
                }

                string rel = entryPath.Replace('\\', '/').TrimStart('/');
                string editsRoot = Path.GetFullPath(Path.Combine(AppPaths.BundleIsolatePath, "_edits", HashEditKey(archive)));
                string destPath = Path.GetFullPath(Path.Combine(editsRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                string rootPrefix = editsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!destPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    reply(JsonSerializer.Serialize(new
                    {
                        type = "BUNDLE_TEXT_EDIT_SAVE_FAILED",
                        requestId,
                        path = entryPath,
                        error = "outside_workspace"
                    }));
                    return;
                }

                string normalized = text.Replace("\r\n", "\n");
                if (eol == "crlf") normalized = normalized.Replace("\n", "\r\n");

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.WriteAllBytes(destPath, EncodeEditableText(normalized, encoding));
                long size = new FileInfo(destPath).Length;
                LogActivity($"[BUNDLE] Saved text edit for {rel} -> {destPath}");

                string json = JsonSerializer.Serialize(new
                {
                    type = "BUNDLE_TEXT_EDIT_SAVED",
                    requestId,
                    archive,
                    path = rel,
                    source = destPath,
                    size
                });
                SendMessageToWeb(json);
                PostToPreviewPopout(json);
            }
            catch (Exception ex)
            {
                LogError($"[BUNDLE] Save text edit failed: {ex.Message}");
                reply(JsonSerializer.Serialize(new
                {
                    type = "BUNDLE_TEXT_EDIT_SAVE_FAILED",
                    requestId,
                    path = entryPath,
                    error = ex.Message
                }));
            }
        });
    }

    private static string HashEditKey(string archive)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(archive.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 8);
    }

    private static string DecodeEditableText(byte[] data, out string encoding)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            encoding = "utf8bom";
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            encoding = "utf16le";
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            encoding = "utf16be";
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }
        encoding = "utf8";
        return Encoding.UTF8.GetString(data);
    }

    private static byte[] EncodeEditableText(string text, string encoding)
    {
        (Encoding enc, byte[] bom) = encoding switch
        {
            "utf8bom" => ((Encoding)new UTF8Encoding(false), new byte[] { 0xEF, 0xBB, 0xBF }),
            "utf16le" => (new UnicodeEncoding(false, false), new byte[] { 0xFF, 0xFE }),
            "utf16be" => (new UnicodeEncoding(true, false), new byte[] { 0xFE, 0xFF }),
            _ => (new UTF8Encoding(false), Array.Empty<byte>())
        };
        byte[] body = enc.GetBytes(text);
        if (bom.Length == 0) return body;
        var result = new byte[bom.Length + body.Length];
        Buffer.BlockCopy(bom, 0, result, 0, bom.Length);
        Buffer.BlockCopy(body, 0, result, bom.Length, body.Length);
        return result;
    }
}
