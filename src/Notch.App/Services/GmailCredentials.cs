using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Notch.Services;

/// <summary>Gmail-Zugangsdaten (App-Passwort), mit Windows-DPAPI an das Benutzerkonto gebunden gespeichert.</summary>
public static class GmailCredentials
{
    private static readonly string FilePath = Path.Combine(Log.Dir, "gmail.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Notch.Gmail.v1");

    public sealed record Login(string User, string Password);

    public static bool Exists => File.Exists(FilePath);

    public static Login? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Login>(plain);
        }
        catch (Exception ex)
        {
            Log.Error("Gmail-Zugangsdaten nicht lesbar", ex);
            return null;
        }
    }

    public static void Save(string user, string password)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Login(user.Trim(), password.Replace(" ", "")));
        Directory.CreateDirectory(Log.Dir);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
    }

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) { Log.Error("Gmail-Zugangsdaten nicht löschbar", ex); }
    }
}
