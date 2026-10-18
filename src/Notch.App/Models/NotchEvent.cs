using System.Windows.Media;

namespace Notch.Models;

public enum EventKind { Running, InputNeeded, Error, Done, Timer, Info, Media, Message }

public sealed record NotchAction(string Id, string Label, bool Primary = false);

/// <summary>Zusatzdaten für Medien-Events (Spotify).</summary>
public sealed record MediaInfo(string Artist, bool Playing, ImageSource? Artwork);

public sealed record NotchEvent(
    string Source,
    EventKind Kind,
    string Title,
    string Subtitle = "",
    bool Sticky = false,
    int TtlSeconds = 4,
    IReadOnlyList<NotchAction>? Actions = null,
    string? Id = null,
    MediaInfo? Media = null,
    bool OpenLarge = false,
    int? HostPid = null,    // Prozess, dessen Fenster ein Klick nach vorne holt (Claude Code)
    string? Open = null,    // URI/Programm, das ein Klick öffnet (z. B. whatsapp://)
    string? Glyph = null);  // eigenes Symbol statt des Standard-Symbols der Art
