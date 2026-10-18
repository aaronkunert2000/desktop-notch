using System.Diagnostics;
using System.IO;

namespace Notch.Services;

/// <summary>Minimales Datei-Log unter %APPDATA%\Notch\notch.log (rotiert bei 1 MB).</summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    /// <summary>Datenordner: %APPDATA%\Notch, oder NOTCH_DATA_DIR (z. B. für Tests, damit die echte Ablage unberührt bleibt).</summary>
    public static string Dir { get; } =
        Environment.GetEnvironmentVariable("NOTCH_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch");

    private static readonly string FilePath = Path.Combine(Dir, "notch.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging darf die App nie stoppen
        }
    }
}
