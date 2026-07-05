using System.Security.Cryptography;
using System.Text;

namespace F76ManagerApp.Managers;

public static class NexusCredentialStore
{
    private static string StoragePath => Path.Combine(AppPaths.SettingsFolder, "nexus_auth.dat");

    public static void Save(string credential)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            Clear();
            return;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.SettingsFolder);
            byte[] plain = Encoding.UTF8.GetBytes(credential);
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(StoragePath, protectedBytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to save Nexus session: {ex.Message}", ex);
        }
    }

    public static string? TryLoad()
    {
        try
        {
            if (!File.Exists(StoragePath)) return null;

            byte[] protectedBytes = File.ReadAllBytes(StoragePath);
            if (protectedBytes.Length == 0) return null;

            byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            string credential = Encoding.UTF8.GetString(plain);
            return string.IsNullOrWhiteSpace(credential) ? null : credential;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(StoragePath)) File.Delete(StoragePath);
        }
        catch { }
    }

    public static bool TryImportProtectedBase64(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return false;

        try
        {
            byte[] protectedBytes = Convert.FromBase64String(protectedBase64);
            byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            string credential = Encoding.UTF8.GetString(plain);
            if (string.IsNullOrWhiteSpace(credential)) return false;
            Save(credential);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
