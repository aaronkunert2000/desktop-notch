using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Notch.Models;

namespace Notch.ViewModels;

public enum IslandState { Idle, Compact, Expanded, Large }

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Zeile in der Ablage-Ansicht.</summary>
public sealed record InboxRow(string Uid, string Glyph, Brush Accent, string Text, string Time);

/// <summary>
/// Zustand der Insel. Zeigt normalerweise das Event, das der Resolver vorgibt.
/// Mit den Seiten-Punkten (Klick oder Mausrad) kann man zu anderen Seiten blättern
/// (Musik, Timer, Ablage …); nach 8 s ohne Bedienung geht es zurück zur Vorgabe.
/// </summary>
public sealed class IslandViewModel : ObservableObject
{
    private static readonly TimeSpan BrowseReturn = TimeSpan.FromSeconds(8);
    private const int InboxRowsShown = 3;

    private readonly DispatcherTimer _ttl = new();
    private readonly DispatcherTimer _browseTimer = new() { Interval = BrowseReturn };

    private IslandState _state = IslandState.Idle;
    private EventKind _kind = EventKind.Info;
    private string _title = "";
    private string _subtitle = "";
    private string _glyph = "";
    private Brush _accent = Brushes.White;
    private IReadOnlyList<NotchAction> _actions = Array.Empty<NotchAction>();
    private ImageSource? _artwork;
    private bool _mediaPlaying;
    private bool _sticky;
    private bool _hoverExpanded;
    private bool _hovering;

    private NotchEvent? _currentEvent;    // was gerade angezeigt wird
    private NotchEvent? _resolverEvent;   // was der Resolver anzeigen will
    private bool _browsing;
    private bool _refreshing;

    private IReadOnlyList<NotchEvent> _pages = Array.Empty<NotchEvent>();
    private IReadOnlyList<PageDot> _pageDots = Array.Empty<PageDot>();
    private int _pageIndex;

    private bool _showInbox;
    private IReadOnlyList<InboxRow> _inboxRows = Array.Empty<InboxRow>();

    public IslandViewModel()
    {
        _ttl.Tick += (_, _) => { _ttl.Stop(); Complete(); };
        _browseTimer.Tick += (_, _) =>
        {
            if (_hovering) return;   // solange die Maus drauf ist, bleibt die Seite
            EndBrowse(userInitiated: false);
        };
    }

    // ---- Anbindung (von App gesetzt) ------------------------------------------

    /// <summary>Liefert alle Seiten in Anzeigereihenfolge.</summary>
    public Func<IReadOnlyList<NotchEvent>>? PageProvider { get; set; }

    /// <summary>Liefert die ungelesenen Einträge der Ablage (neueste zuerst).</summary>
    public Func<IReadOnlyList<InboxItem>>? InboxProvider { get; set; }

    /// <summary>Aktuelles Event ist fertig (TTL, Quittieren, Aktion). Der Resolver entscheidet, was folgt.</summary>
    public event Action? Completed;

    /// <summary>Wird bei jedem neuen Event ausgelöst (für einmalige Effekte wie Puls).</summary>
    public event Action<EventKind>? EventShown;

    /// <summary>Wird ausgelöst, wenn ein Action-Button geklickt wurde.</summary>
    public event Action<NotchEvent, string>? ActionInvoked;

    /// <summary>Medien-Steuerung: "prev" | "toggle" | "next".</summary>
    public event Action<string>? MediaCommand;

    /// <summary>Klick auf ein Event mit HostPid (Fenster nach vorne) oder Open (URI/Programm starten).</summary>
    public event Action<NotchEvent>? Activated;

    /// <summary>Nutzer hat ein Event zur Kenntnis genommen (Klick). Für "gelesen" in der Ablage.</summary>
    public event Action<NotchEvent>? Acknowledged;

    /// <summary>Beim Blättern quittiertes Dauer-Event (z. B. Fehler), das nicht die Vorgabe ist: aus dem Resolver entfernen.</summary>
    public event Action<NotchEvent>? DismissRequested;

    /// <summary>Eintrag in der Ablage angeklickt (Uid).</summary>
    public event Action<string>? InboxItemOpened;

    /// <summary>"Alle gelesen" in der Ablage.</summary>
    public event Action? InboxReadAll;

    // ---- Anzeige-Eigenschaften -------------------------------------------------

    public IslandState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(ShowSubtitle));
            Raise(nameof(ShowActions));
            Raise(nameof(ShowMediaControls));
            Raise(nameof(ShowDots));
            Raise(nameof(ShowBadge));
            Raise(nameof(ShowInboxList));
        }
    }

    public EventKind Kind
    {
        get => _kind;
        private set
        {
            if (!Set(ref _kind, value)) return;
            Raise(nameof(IsRunning));
            Raise(nameof(IsMedia));
            Raise(nameof(ShowGlyph));
            Raise(nameof(ShowArtwork));
            Raise(nameof(ShowMediaControls));
        }
    }

    public ImageSource? Artwork
    {
        get => _artwork;
        private set
        {
            if (!Set(ref _artwork, value)) return;
            Raise(nameof(ShowArtwork));
            Raise(nameof(ShowGlyph));
        }
    }

    public bool MediaPlaying
    {
        get => _mediaPlaying;
        private set { if (Set(ref _mediaPlaying, value)) Raise(nameof(PlayPauseGlyph)); }
    }

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }
    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }
    public Brush Accent { get => _accent; private set => Set(ref _accent, value); }

    public IReadOnlyList<NotchAction> Actions
    {
        get => _actions;
        private set { if (Set(ref _actions, value)) Raise(nameof(ShowActions)); }
    }

    public bool ShowInbox
    {
        get => _showInbox;
        private set { if (Set(ref _showInbox, value)) Raise(nameof(ShowInboxList)); }
    }

    public IReadOnlyList<InboxRow> InboxRows { get => _inboxRows; private set => Set(ref _inboxRows, value); }

    public IReadOnlyList<PageDot> PageDots { get => _pageDots; private set => Set(ref _pageDots, value); }

    public bool IsMedia => Kind == EventKind.Media;
    public bool IsRunning => Kind == EventKind.Running;
    public bool ShowArtwork => IsMedia && Artwork is not null;
    public bool ShowGlyph => !IsRunning && !ShowArtwork;
    public bool ShowMediaControls => IsMedia && IsOpen;
    public string PlayPauseGlyph => MediaPlaying ? "" : "";
    public bool ShowSubtitle => State != IslandState.Compact && !string.IsNullOrEmpty(Subtitle);
    public bool ShowActions => State == IslandState.Large && Actions.Count > 0 && !ShowInbox;
    public bool ShowInboxList => State == IslandState.Large && ShowInbox;
    public bool ShowDots => _pages.Count > 1 && IsOpen;
    public bool ShowBadge => _pages.Count > 1 && State == IslandState.Compact;
    public string PageBadge => _pages.Count.ToString();

    private bool IsOpen => State is IslandState.Expanded or IslandState.Large;

    // ---- Vom Resolver ----------------------------------------------------------

    /// <param name="silent">Update desselben Vorgangs: Zustand, Hover und Größe bleiben, kein Leuchten/Puls.</param>
    public void Show(NotchEvent e, bool silent = false)
    {
        _resolverEvent = e;

        if (_browsing)
        {
            if (silent)
            {
                if (SameKey(_currentEvent, e)) ApplyContent(e);
                RefreshPages();
                return;
            }
            StopBrowsing();   // neues Ereignis hat Vorrang vor dem Blättern
        }

        _ttl.Stop();
        ApplyContent(e);

        if (silent && State != IslandState.Idle)
        {
            if (!_hoverExpanded) StartTtl(e.TtlSeconds);
            RefreshPages();
            return;
        }

        _hoverExpanded = false;
        State = DefaultState(e);
        EventShown?.Invoke(e.Kind);
        StartTtl(e.TtlSeconds);
        RefreshPages();
    }

    /// <summary>Insel einklappen (ohne Nachfolger).</summary>
    public void Hide()
    {
        _ttl.Stop();
        StopBrowsing();
        _hoverExpanded = false;
        _currentEvent = null;
        _resolverEvent = null;
        State = IslandState.Idle;
        RefreshPages();
    }

    /// <summary>Aktuelles Event beenden; ohne Resolver wird direkt eingeklappt.</summary>
    public void Complete()
    {
        _ttl.Stop();
        _hoverExpanded = false;
        if (Completed is null) Hide();
        else Completed.Invoke();
    }

    // ---- Seiten ----------------------------------------------------------------

    /// <summary>Seitenliste neu holen (nach jeder Änderung im Resolver oder in der Ablage).</summary>
    public void RefreshPages()
    {
        if (_refreshing) return;

        var pages = PageProvider?.Invoke()
                    ?? (_currentEvent is null ? Array.Empty<NotchEvent>() : new[] { _currentEvent });
        if (State == IslandState.Idle) pages = Array.Empty<NotchEvent>();
        _pages = pages;

        if (_browsing)
        {
            var shown = pages.FirstOrDefault(p => SameKey(p, _currentEvent));
            if (shown is null)
            {
                EndBrowse(userInitiated: false);   // angezeigte Seite gibt es nicht mehr; ruft RefreshPages erneut
                return;
            }
            if (!ReferenceEquals(shown, _currentEvent)) ApplyContent(shown);   // aktualisierte Daten
        }

        _refreshing = true;
        try
        {
            if (ShowInbox) RefreshInbox();

            _pageIndex = Math.Max(0, pages.ToList().FindIndex(p => SameKey(p, _currentEvent)));
            PageDots = pages.Select((_, i) => new PageDot(i, i == _pageIndex)).ToList();
            Raise(nameof(ShowDots));
            Raise(nameof(ShowBadge));
            Raise(nameof(PageBadge));
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Mausrad: eine Seite vor/zurück.</summary>
    public void Browse(int step)
    {
        if (State == IslandState.Idle) return;
        RefreshPages();
        if (_pages.Count < 2) return;
        BrowseTo(((_pageIndex + step) % _pages.Count + _pages.Count) % _pages.Count);
    }

    /// <summary>Klick auf einen Punkt.</summary>
    public void BrowseTo(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        var target = _pages[index];

        if (SameKey(target, _resolverEvent))
        {
            EndBrowse(userInitiated: true);
            return;
        }

        _browsing = true;
        _ttl.Stop();
        ApplyContent(target);
        State = Actions.Count > 0 || ShowInbox ? IslandState.Large : IslandState.Expanded;
        _browseTimer.Stop();
        if (!_hovering) _browseTimer.Start();
        RefreshPages();
    }

    private void StopBrowsing()
    {
        _browsing = false;
        _browseTimer.Stop();
    }

    /// <summary>Zurück zur Vorgabe des Resolvers.</summary>
    private void EndBrowse(bool userInitiated)
    {
        StopBrowsing();
        if (_resolverEvent is null)
        {
            Hide();
            return;
        }

        ApplyContent(_resolverEvent);
        if (userInitiated)
        {
            // Nutzer ist gerade auf der Insel: offen lassen, beim Verlassen einklappen
            _hoverExpanded = DefaultState(_resolverEvent) == IslandState.Compact;
            State = Actions.Count > 0 || ShowInbox ? IslandState.Large : IslandState.Expanded;
        }
        else
        {
            _hoverExpanded = false;
            State = DefaultState(_resolverEvent);
            StartTtl(_resolverEvent.TtlSeconds);
        }
        RefreshPages();
    }

    // ---- Ablage ----------------------------------------------------------------

    public void RefreshInbox()
    {
        var items = InboxProvider?.Invoke() ?? Array.Empty<InboxItem>();
        InboxRows = items.Take(InboxRowsShown).Select(i =>
        {
            var (glyph, accent) = StyleFor(i.Kind);
            var text = string.IsNullOrEmpty(i.Subtitle) ? i.Title : $"{i.Title} · {i.Subtitle}";
            var time = i.Time.Date == DateTime.Today ? i.Time.ToString("HH:mm") : i.Time.ToString("dd.MM.");
            return new InboxRow(i.Uid, glyph, accent, text, time);
        }).ToList();

        if (ShowInbox)
        {
            var more = items.Count > InboxRowsShown ? $" · {items.Count - InboxRowsShown} weitere" : "";
            Subtitle = $"{items.Count} ungelesen{more}";
            Raise(nameof(ShowSubtitle));
        }
    }

    public void OpenInboxItem(string uid) => InboxItemOpened?.Invoke(uid);

    public void ReadAllInbox() => InboxReadAll?.Invoke();

    // ---- Bedienung -------------------------------------------------------------

    /// <summary>Hotkey/Tray: sichtbare Insel auf- bzw. zuklappen.</summary>
    public void Toggle()
    {
        if (State == IslandState.Idle) return;
        _hoverExpanded = false;
        _ttl.Stop();

        if (State == IslandState.Large && _currentEvent is { Source: "notch", Id: "quick" })
        {
            Complete();
            return;
        }

        State = State switch
        {
            IslandState.Compact => Actions.Count > 0 ? IslandState.Large : IslandState.Expanded,
            IslandState.Expanded => Actions.Count > 0 ? IslandState.Large : IslandState.Compact,
            _ => Kind is EventKind.InputNeeded or EventKind.Error ? IslandState.Expanded : IslandState.Compact,
        };
        if (State == IslandState.Compact) StartTtl(4);
    }

    public void InvokeMedia(string command) => MediaCommand?.Invoke(command);

    public void SetHover(bool over)
    {
        _hovering = over;
        if (State == IslandState.Idle) return;

        if (_browsing)
        {
            _browseTimer.Stop();
            if (!over) _browseTimer.Start();
            return;
        }

        if (over)
        {
            _ttl.Stop();
            var hasMore = Actions.Count > 0 || ShowInbox || !string.IsNullOrEmpty(Subtitle) || _pages.Count > 1;
            if (State == IslandState.Compact && hasMore)
            {
                _hoverExpanded = true;
                State = Actions.Count > 0 || ShowInbox ? IslandState.Large : IslandState.Expanded;
            }
        }
        else
        {
            if (_hoverExpanded && IsOpen) State = IslandState.Compact;
            _hoverExpanded = false;
            StartTtl(2);
        }
    }

    public void Click()
    {
        var ev = _currentEvent;
        if (IsMedia || ShowInbox || ev is null) return;   // Medien und Ablage haben eigene Buttons

        if (Actions.Count == 0 && (ev.HostPid is not null || ev.Open is not null))
        {
            Activated?.Invoke(ev);
            Acknowledged?.Invoke(ev);
            if (_browsing) EndBrowse(userInitiated: false);
            else if (ev.Kind != EventKind.Running) Complete();
            return;
        }

        if (Actions.Count > 0)
        {
            _ttl.Stop();
            State = State == IslandState.Large ? IslandState.Expanded : IslandState.Large;
            return;
        }

        if (_sticky && State == IslandState.Expanded)   // Fehler quittieren
        {
            Acknowledged?.Invoke(ev);
            if (_browsing)
            {
                DismissRequested?.Invoke(ev);
                EndBrowse(userInitiated: false);
            }
            else
            {
                _hoverExpanded = false;
                Complete();
            }
        }
    }

    public void InvokeAction(string id)
    {
        var ev = _currentEvent;
        if (ev is not null) ActionInvoked?.Invoke(ev, id);
        if (_browsing) EndBrowse(userInitiated: false);
        else Complete();
    }

    // ---- Intern ----------------------------------------------------------------

    private void ApplyContent(NotchEvent e)
    {
        _sticky = e.Sticky;
        _currentEvent = e;

        Title = e.Title;
        Subtitle = e.Subtitle;
        Actions = e.Actions ?? Array.Empty<NotchAction>();
        Artwork = e.Media?.Artwork;
        MediaPlaying = e.Media?.Playing ?? false;
        (Glyph, Accent) = StyleFor(e.Kind);
        if (e.Glyph is not null) Glyph = e.Glyph;
        Kind = e.Kind;
        ShowInbox = e is { Source: "notch", Id: "inbox" };
        if (ShowInbox) RefreshInbox();
        Raise(nameof(ShowSubtitle));
        Raise(nameof(ShowActions));
    }

    private IslandState DefaultState(NotchEvent e)
    {
        if (e.Kind is EventKind.InputNeeded or EventKind.Error) return IslandState.Expanded;
        var hasLargeContent = (e.Actions?.Count ?? 0) > 0 || e is { Source: "notch", Id: "inbox" };
        return e.OpenLarge && hasLargeContent ? IslandState.Large : IslandState.Compact;
    }

    private static bool SameKey(NotchEvent? a, NotchEvent? b) =>
        a is not null && b is not null && a.Source == b.Source && a.Id == b.Id;

    private void StartTtl(int seconds)
    {
        if (_sticky || State == IslandState.Idle || _browsing) return;
        _ttl.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
        _ttl.Start();
    }

    public static (string Glyph, Brush Accent) StyleFor(EventKind kind) => kind switch
    {
        EventKind.Running     => ("", Res("AccentBlueBrush")),
        EventKind.InputNeeded => ("", Res("AccentOrangeBrush")),
        EventKind.Error       => ("", Res("AccentRedBrush")),
        EventKind.Done        => ("", Res("AccentGreenBrush")),
        EventKind.Timer       => ("", Res("AccentBlueBrush")),
        EventKind.Message     => ("", Res("AccentMessageBrush")),
        EventKind.Media       => ("", Res("AccentMediaBrush")),
        _                     => ("", Res("TextPrimaryBrush")),
    };

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
