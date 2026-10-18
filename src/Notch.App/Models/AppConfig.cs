using System.IO;
using System.Text.Json;
using Notch.Services;

namespace Notch.Models;

public sealed class AppConfig
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public int Port { get; set; } = 47321;
    public string Hotkey { get; set; } = "Ctrl+Alt+N";
    public bool Demo { get; set; }
    public bool Spotify { get; set; } = true;
    /// <summary>WhatsApp-Benachrichtigungen ins Notch übernehmen (experimentell).</summary>
    public bool WhatsApp { get; set; } = true;
    /// <summary>Nachrichtentext anzeigen; false = nur Absender + "Neue Nachricht".</summary>
    public bool WhatsAppPreview { get; set; } = true;
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    /// <summary>Ordner für Quick Action "Neue Notiz" (leer = Aktion ausgeblendet).</summary>
    public string VaultInbox { get; set; } = "";
    /// <summary>Was "Claude" startet: URI, Programm oder Verknüpfung.</summary>
    public string ClaudeCommand { get; set; } = "claude://";

    /// <summary>Reihenfolge: %APPDATA%\Notch\config.json, dann config.json neben der .exe, sonst Defaults.</summary>
    public static AppConfig Load()
    {
        string[] candidates =
        {
            Path.Combine(Log.Dir, "config.json"),
            Path.Combine(AppContext.BaseDirectory, "config.json"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
                if (config is null) continue;
                Log.Info($"Config geladen: {path}");
                return config.Validate();
            }
            catch (Exception ex)
            {
                Log.Error($"Config ungültig, wird übersprungen: {path}", ex);
            }
        }

        Log.Info("Keine Config gefunden, nutze Defaults");
        return new AppConfig();
    }

    private AppConfig Validate()
    {
        if (Port is < 1024 or > 65535)
        {
            Log.Error($"Port {Port} ungültig, nutze 47321");
            Port = 47321;
        }
        return this;
    }
}
