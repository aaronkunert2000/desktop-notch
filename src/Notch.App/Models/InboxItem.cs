namespace Notch.Models;

/// <summary>Ungelesene Benachrichtigung in der Ablage (wird in %APPDATA%\Notch\inbox.json gespeichert).</summary>
public sealed record InboxItem(
    string Uid,
    string Source,
    string? EventId,
    EventKind Kind,
    string Title,
    string Subtitle,
    DateTime Time,
    string? Open = null,
    int? HostPid = null);

/// <summary>Seiten-Punkt unten in der Insel.</summary>
public sealed record PageDot(int Index, bool Active);
