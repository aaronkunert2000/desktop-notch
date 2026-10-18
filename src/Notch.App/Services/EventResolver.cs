using Notch.Models;
using Notch.ViewModels;

namespace Notch.Services;

/// <summary>
/// Entscheidet, welches Event die Insel zeigt. Läuft ausschließlich auf dem UI-Thread.
/// Regeln:
///  - Gleicher Schlüssel (source + id) ersetzt sich, statt sich zu stapeln.
///  - "Aufmerksamkeit" (input_needed, error) wird nur von höherer Priorität verdrängt.
///  - Hintergrund (timer, media, info) verdrängt keine echten Vorgänge; Updates wartender Vorgänge bleiben in der Queue.
///  - Alles andere: das Neueste wird gezeigt; ein verdrängtes sticky Event kommt zurück in die Queue.
///  - Ist das aktuelle Event fertig, rückt das Event mit der höchsten Priorität nach.
///  - Nicht-sticky Events verfallen nach 60 s in der Queue.
/// </summary>
public sealed class EventResolver
{
    private static readonly TimeSpan QueueExpiry = TimeSpan.FromSeconds(60);
    private const int MaxResults = 50;

    private readonly IslandViewModel _vm;
    private readonly List<(NotchEvent Event, DateTime At)> _queue = new();
    private readonly Dictionary<string, string> _results = new();
    private readonly Dictionary<string, NotchEvent> _parked = new();
    private readonly Dictionary<string, int> _hostPids = new();
    private NotchEvent? _current;

    public EventResolver(IslandViewModel vm)
    {
        _vm = vm;
        _vm.Completed += OnCompleted;
        _vm.ActionInvoked += StoreResult;
    }

    public static int Priority(EventKind kind) => kind switch
    {
        EventKind.InputNeeded => 5,
        EventKind.Error => 4,
        EventKind.Running => 3,
        EventKind.Done or EventKind.Message => 2,
        EventKind.Timer => 1,
        _ => 0,
    };

    /// <summary>Nicht stören: nur Freigaben und Fehler werden angezeigt.</summary>
    public bool DoNotDisturb { get; set; }

    public bool TryGetHostPid(string source, out int pid) => _hostPids.TryGetValue(source, out pid);

    /// <summary>Jedes eingehende Event, vor allen Filtern (auch "Nicht stören"): für die Ablage.</summary>
    public event Action<NotchEvent>? Submitted;

    /// <summary>Angezeigtes Event oder Warteschlange haben sich geändert (für die Seiten-Punkte).</summary>
    public event Action? Changed;

    public NotchEvent? Current => _current;

    /// <summary>Alle aktiven Vorgänge: aktuelles Event plus Warteschlange (ohne ausgeblendete).</summary>
    public IReadOnlyList<NotchEvent> LivePages()
    {
        var now = DateTime.UtcNow;
        var list = new List<NotchEvent>();
        if (_current is not null) list.Add(_current);
        foreach (var (ev, at) in _queue)
        {
            if (!ev.Sticky && now - at > QueueExpiry) continue;
            if (list.All(x => Key(x) != Key(ev))) list.Add(ev);
        }
        return list;
    }

    /// <summary>
    /// Hotkey "Anzeige zu": alles ausblenden. Dauerhafte Vorgänge werden geparkt und erscheinen erst
    /// wieder, wenn sich bei ihnen etwas ändert (anderer Zustand oder neuer Song).
    /// </summary>
    public void HideAll()
    {
        try { HideAllCore(); } finally { Changed?.Invoke(); }
    }

    /// <summary>Per Hotkey ausgeblendete Dauer-Vorgänge (für die Seiten-Punkte).</summary>
    public IReadOnlyList<NotchEvent> ParkedPages() => _parked.Values.ToList();

    /// <summary>Hotkey "Anzeige auf": ausgeblendete Vorgänge zurückholen. false = es gab nichts.</summary>
    public bool RestoreParked()
    {
        try
        {
            if (_parked.Count == 0) return false;
            var now = DateTime.UtcNow;
            foreach (var e in _parked.Values) _queue.Add((e, now));
            _parked.Clear();
            if (_current is null) ShowNext();
            return true;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    private void HideAllCore()
    {
        if (_current is { Sticky: true }) _parked[Key(_current)] = _current;
        foreach (var q in _queue.Where(q => q.Event.Sticky)) _parked[Key(q.Event)] = q.Event;
        _queue.Clear();
        _current = null;
        _vm.Hide();
    }

    public void Submit(NotchEvent e)
    {
        Submitted?.Invoke(e);
        try { SubmitCore(e); } finally { Changed?.Invoke(); }
    }

    private void SubmitCore(NotchEvent e)
    {
        if (e.HostPid is int pid) _hostPids[e.Source] = pid;
        // Vom Nutzer ausgelöste Anzeigen (source "notch") ignorieren "Nicht stören"
        if (DoNotDisturb && !IsAttention(e) && e.Source != "notch") return;

        var key = Key(e);
        if (_parked.TryGetValue(key, out var parked))
        {
            if (!IsChange(parked, e))
            {
                _parked[key] = e;   // still mitführen, bleibt verborgen
                return;
            }
            _parked.Remove(key);
        }

        _results.Remove(key);
        var wasQueued = _queue.RemoveAll(q => Key(q.Event) == key) > 0;
        if (!IsAmbient(e)) Log.Info($"Event {e.Source}/{e.Id ?? "-"} {e.Kind}: {e.Title}");

        // Update desselben Vorgangs: still ersetzen, kein erneutes Aufleuchten
        if (_current is not null && Key(_current) == key)
        {
            Show(e, silent: _current.Kind == e.Kind);
            return;
        }
        if (_current is null)
        {
            Show(e);
            return;
        }

        // Wartende Vorgänge bleiben bei Updates in der Queue (z. B. Timer-Sekunden)
        if (wasQueued && !IsAttention(e))
        {
            _queue.Add((e, DateTime.UtcNow));
            return;
        }

        var blocked =
            (IsAttention(_current) && Priority(e.Kind) <= Priority(_current.Kind)) ||
            (IsAmbient(e) && !IsAmbient(_current));
        if (blocked)
        {
            _queue.Add((e, DateTime.UtcNow));
            return;
        }

        if (_current.Sticky) _queue.Add((_current, DateTime.UtcNow));
        Show(e);
    }

    /// <summary>Ohne source: aktuelles Event beenden. Mit source (optional id): passende Events entfernen.</summary>
    public void Dismiss(string? source, string? id)
    {
        try { DismissCore(source, id); } finally { Changed?.Invoke(); }
    }

    private void DismissCore(string? source, string? id)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            if (_current is not null) _vm.Complete();
            return;
        }

        _queue.RemoveAll(q => Matches(q.Event, source, id));
        foreach (var k in _parked.Where(p => Matches(p.Value, source, id)).Select(p => p.Key).ToList())
            _parked.Remove(k);
        if (_current is not null && Matches(_current, source, id)) _vm.Complete();
    }

    /// <summary>Liefert die gewählte Aktion einmalig zurück (für notch_send.py --wait).</summary>
    public string? TakeResult(string source, string? id)
    {
        var key = Key(source, id);
        return _results.Remove(key, out var action) ? action : null;
    }

    private void OnCompleted()
    {
        _current = null;
        ShowNext();
        Changed?.Invoke();
    }

    private void ShowNext()
    {
        var now = DateTime.UtcNow;
        _queue.RemoveAll(q => !q.Event.Sticky && now - q.At > QueueExpiry);

        if (_queue.Count == 0)
        {
            _vm.Hide();
            return;
        }

        var next = _queue.OrderByDescending(q => Priority(q.Event.Kind)).ThenBy(q => q.At).First();
        _queue.Remove(next);
        Show(next.Event);
    }

    private void Show(NotchEvent e, bool silent = false)
    {
        _current = e;
        _vm.Show(e, silent);
    }

    private void StoreResult(NotchEvent e, string action)
    {
        if (_results.Count >= MaxResults) _results.Remove(_results.Keys.First());
        _results[Key(e)] = action;
        // Beim Blättern beantwortet: wartendes Event ist damit erledigt
        _queue.RemoveAll(q => Key(q.Event) == Key(e));
        Log.Info($"Aktion {e.Source}/{e.Id ?? "-"}: {action}");
    }

    private static bool IsAttention(NotchEvent e) => e.Kind is EventKind.InputNeeded or EventKind.Error;

    /// <summary>Holt ein geparktes Event zurück: anderer Zustand oder (bei Musik) neuer Titel.</summary>
    private static bool IsChange(NotchEvent before, NotchEvent after) =>
        before.Kind != after.Kind || (after.Kind == EventKind.Media && before.Title != after.Title);

    /// <summary>Hintergrund-Anzeigen verdrängen nie echte Vorgänge.</summary>
    private static bool IsAmbient(NotchEvent e) => e.Kind is EventKind.Timer or EventKind.Media or EventKind.Info
                                                    && e.Source != "notch";

    private static bool Matches(NotchEvent e, string source, string? id) =>
        e.Source == source && (string.IsNullOrWhiteSpace(id) || e.Id == id);

    private static string Key(NotchEvent e) => Key(e.Source, e.Id);
    private static string Key(string source, string? id) => $"{source}\u001f{id}";
}
