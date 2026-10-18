using Microsoft.Win32;

namespace Notch.Services;

/// <summary>Autostart über HKCU\...\Run (nur für den aktuellen Benutzer, ohne Adminrechte).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Notch";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath unbekannt");
                key.SetValue(ValueName, $"\"{exe}\"");
                Log.Info($"Autostart an: {exe}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Autostart aus");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Autostart konnte nicht gesetzt werden", ex);
        }
    }
}
