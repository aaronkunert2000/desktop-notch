using System.IO;
using System.Security.Cryptography;

namespace Notch.Services;

/// <summary>Token für den lokalen Endpunkt: NOTCH_TOKEN oder einmalig erzeugt in %APPDATA%\Notch\token.</summary>
public static class TokenStore
{
    public static string GetOrCreate()
    {
        var env = Environment.GetEnvironmentVariable("NOTCH_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var path = Path.Combine(Log.Dir, "token");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 16) return existing;
        }

        Directory.CreateDirectory(Log.Dir);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        File.WriteAllText(path, token);
        Log.Info($"Neues Token erzeugt: {path}");
        return token;
    }
}
