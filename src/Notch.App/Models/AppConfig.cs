using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
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
    /// <summary>Gmail-Pop-ups (IMAP); aktiv erst, wenn im Tray ein App-Passwort hinterlegt ist.</summary>
    public bool Gmail { get; set; } = true;
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    /// <summary>Ordner für Quick Action "Neue Notiz" (leer = Aktion ausgeblendet).</summary>
    public string VaultInbox { get; set; } = "";
    /// <summary>Was "Claude" startet: URI, Programm oder Verknüpfung.</summary>
    public string ClaudeCommand { get; set; } = "claude://";
    /// <summary>Spike: Signale für "Claude Desktop arbeitet" ins Log schreiben (läuft 45 min, zeigt nichts an).</summary>
    public bool ClaudeDiag { get; set; }

    /// <summary>
    /// Lädt die Config in Schichten; spätere überschreiben einzelne Werte der früheren:
    ///   1. config.json neben der .exe        (Standardwerte aus dem Repo)
    ///   2. config.local.json neben der .exe  (persönlich, nicht im Repo)
    ///   3. %APPDATA%\Notch\config.json       (persönlich, unabhängig vom Installationsordner)
    /// Fehlt alles, gelten die Defaults dieser Klasse.
    /// </summary>
    public static AppConfig Load()
    {
        string[] layers =
        {
            Path.Combine(AppContext.BaseDirectory, "config.json"),
            Path.Combine(AppContext.BaseDirectory, "config.local.json"),
            Path.Combine(Log.Dir, "config.json"),
        };

        var merged = new JsonObject();
        foreach (var path in layers)
        {
            if (!File.Exists(path)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions) is not JsonObject layer)
                    continue;
                foreach (var (key, value) in layer)
                    merged[key.ToLowerInvariant()] = value?.DeepClone();   // gleiche Schlüssel unabhängig von Schreibweise
                Log.Info($"Config geladen: {path}");
            }
            catch (Exception ex)
            {
                Log.Error($"Config ungültig, wird übersprungen: {path}", ex);
            }
        }

        try
        {
            return (JsonSerializer.Deserialize<AppConfig>(merged, Options) ?? new AppConfig()).Validate();
        }
        catch (Exception ex)
        {
            Log.Error("Config-Werte ungültig, nutze Defaults", ex);
            return new AppConfig();
        }
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
