using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Notch.Services;

/// <summary>Notion-Integrations-Token, mit Windows-DPAPI an das Benutzerkonto gebunden gespeichert.</summary>
public static class NotionCredentials
{
    private static readonly string FilePath = Path.Combine(Log.Dir, "notion.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Notch.Notion.v1");

    public static bool Exists => File.Exists(FilePath);

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Log.Error("Notion-Token nicht lesbar", ex);
            return null;
        }
    }

    public static void Save(string token)
    {
        Directory.CreateDirectory(Log.Dir);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) { Log.Error("Notion-Token nicht löschbar", ex); }
    }
}
