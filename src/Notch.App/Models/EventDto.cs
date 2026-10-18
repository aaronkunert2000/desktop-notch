namespace Notch.Models;

/// <summary>JSON-Eingang für POST /event. Wird validiert und in ein NotchEvent übersetzt.</summary>
public sealed class EventDto
{
    public string? Source { get; set; }
    public string? Id { get; set; }
    public string? Kind { get; set; }
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public bool? Sticky { get; set; }
    public int? Ttl { get; set; }
    public List<ActionDto>? Actions { get; set; }
    public int? HostPid { get; set; }

    public NotchEvent? ToEvent(out string? error)
    {
        error = null;

        EventKind? kind = Kind?.Trim().ToLowerInvariant() switch
        {
            "running" => EventKind.Running,
            "input_needed" => EventKind.InputNeeded,
            "error" => EventKind.Error,
            "done" => EventKind.Done,
            "timer" => EventKind.Timer,
            "info" => EventKind.Info,
            _ => null,
        };
        if (kind is null)
        {
            error = "kind fehlt oder ungültig (running|input_needed|error|done|timer|info)";
            return null;
        }
        if (string.IsNullOrWhiteSpace(Title))
        {
            error = "title fehlt";
            return null;
        }

        var actions = Actions?
            .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Label))
            .Take(3)
            .Select(a => new NotchAction(Clip(a.Id!, 40), Clip(a.Label!, 24), a.Primary))
            .ToList();

        // Zustände, die "andauern", bleiben standardmäßig stehen
        var stickyDefault = kind.Value is EventKind.Running or EventKind.InputNeeded or EventKind.Error;

        return new NotchEvent(
            Source: Clip(string.IsNullOrWhiteSpace(Source) ? "unknown" : Source, 40),
            Kind: kind.Value,
            Title: Clip(Title, 80),
            Subtitle: Clip(Subtitle ?? "", 140),
            Sticky: Sticky ?? stickyDefault,
            TtlSeconds: Math.Clamp(Ttl ?? 4, 1, 600),
            Actions: actions is { Count: > 0 } ? actions : null,
            Id: string.IsNullOrWhiteSpace(Id) ? null : Clip(Id, 80),
            HostPid: HostPid is > 0 ? HostPid : null);
    }

    private static string Clip(string value, int max)
    {
        value = value.Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}

public sealed class ActionDto
{
    public string? Id { get; set; }
    public string? Label { get; set; }
    public bool Primary { get; set; }
}

public sealed class DismissDto
{
    public string? Source { get; set; }
    public string? Id { get; set; }
}
