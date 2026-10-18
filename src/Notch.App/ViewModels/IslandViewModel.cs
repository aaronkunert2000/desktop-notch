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

public sealed class IslandViewModel : ObservableObject
{
    private readonly DispatcherTimer _ttl = new();
    private IslandState _state = IslandState.Idle;
    private EventKind _kind = EventKind.Info;
    private string _title = "";
    private string _subtitle = "";
    private string _glyph = "\uE946";
    private Brush _accent = Brushes.White;
    private IReadOnlyList<NotchAction> _actions = Array.Empty<NotchAction>();
    private bool _sticky;
    private bool _hoverExpanded;
    private NotchEvent? _currentEvent;
    private ImageSource? _artwork;
    private bool _mediaPlaying;

    public IslandViewModel()
    {
        _ttl.Tick += (_, _) => { _ttl.Stop(); Complete(); };
    }

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

    public IslandState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(ShowSubtitle));
            Raise(nameof(ShowActions));
            Raise(nameof(ShowMediaControls));
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

    public bool IsMedia => Kind == EventKind.Media;
    public bool ShowArtwork => IsMedia && Artwork is not null;
    public bool ShowGlyph => !IsRunning && !ShowArtwork;
    public bool ShowMediaControls => IsMedia && State is IslandState.Expanded or IslandState.Large;
    public string PlayPauseGlyph => MediaPlaying ? "" : "";

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }
    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }
    public Brush Accent { get => _accent; private set => Set(ref _accent, value); }

    public IReadOnlyList<NotchAction> Actions
    {
        get => _actions;
        private set { if (Set(ref _actions, value)) Raise(nameof(ShowActions)); }
    }

    public bool IsRunning => Kind == EventKind.Running;
    public bool ShowSubtitle => State != IslandState.Compact && !string.IsNullOrEmpty(Subtitle);
    public bool ShowActions => State == IslandState.Large && Actions.Count > 0;

    /// <param name="silent">Update desselben Vorgangs: Zustand, Hover und Größe bleiben, kein Leuchten/Puls.</param>
    public void Show(NotchEvent e, bool silent = false)
    {
        _ttl.Stop();
        _sticky = e.Sticky;
        _currentEvent = e;

        Title = e.Title;
        Subtitle = e.Subtitle;
        Raise(nameof(ShowSubtitle));
        Actions = e.Actions ?? Array.Empty<NotchAction>();
        Artwork = e.Media?.Artwork;
        MediaPlaying = e.Media?.Playing ?? false;
        (Glyph, Accent) = StyleFor(e.Kind);
        if (e.Glyph is not null) Glyph = e.Glyph;
        Kind = e.Kind;

        if (silent && State != IslandState.Idle)
        {
            if (!_hoverExpanded) StartTtl(e.TtlSeconds);
            return;
        }

        _hoverExpanded = false;
        State = e.Kind is EventKind.InputNeeded or EventKind.Error
            ? IslandState.Expanded
            : e.OpenLarge && Actions.Count > 0 ? IslandState.Large : IslandState.Compact;

        EventShown?.Invoke(e.Kind);
        StartTtl(e.TtlSeconds);
    }

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

    /// <summary>Insel einklappen (ohne Nachfolger).</summary>
    public void Hide()
    {
        _ttl.Stop();
        _hoverExpanded = false;
        _currentEvent = null;
        State = IslandState.Idle;
    }

    /// <summary>Aktuelles Event beenden; ohne Resolver wird direkt eingeklappt.</summary>
    public void Complete()
    {
        _ttl.Stop();
        _hoverExpanded = false;
        if (Completed is null) Hide();
        else Completed.Invoke();
    }

    public void SetHover(bool over)
    {
        if (State == IslandState.Idle) return;

        if (over)
        {
            _ttl.Stop();
            if (State == IslandState.Compact && (Actions.Count > 0 || !string.IsNullOrEmpty(Subtitle)))
            {
                _hoverExpanded = true;
                State = Actions.Count > 0 ? IslandState.Large : IslandState.Expanded;
            }
        }
        else
        {
            if (_hoverExpanded && State is IslandState.Expanded or IslandState.Large) State = IslandState.Compact;
            _hoverExpanded = false;
            StartTtl(2);
        }
    }

    public void Click()
    {
        _hoverExpanded = false;
        if (IsMedia) return;   // Medien werden über die Buttons gesteuert
        if (_currentEvent is { } ev && Actions.Count == 0 && (ev.HostPid is not null || ev.Open is not null))
        {
            Activated?.Invoke(ev);
            if (ev.Kind != EventKind.Running) Complete();
            return;
        }
        if (Actions.Count > 0)
        {
            _ttl.Stop();
            State = State == IslandState.Large ? IslandState.Expanded : IslandState.Large;
            return;
        }
        if (_sticky && State == IslandState.Expanded) Complete();   // Fehler quittieren
    }

    public void InvokeAction(string id)
    {
        if (_currentEvent is not null) ActionInvoked?.Invoke(_currentEvent, id);
        Complete();
    }

    private void StartTtl(int seconds)
    {
        if (_sticky || State == IslandState.Idle) return;
        _ttl.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
        _ttl.Start();
    }

    private static (string Glyph, Brush Accent) StyleFor(EventKind kind) => kind switch
    {
        EventKind.Running     => ("\uE946", Res("AccentBlueBrush")),
        EventKind.InputNeeded => ("\uE7BA", Res("AccentOrangeBrush")),
        EventKind.Error       => ("\uE711", Res("AccentRedBrush")),
        EventKind.Done        => ("\uE73E", Res("AccentGreenBrush")),
        EventKind.Timer       => ("\uE916", Res("AccentBlueBrush")),
        EventKind.Message     => ("", Res("AccentMessageBrush")),
        EventKind.Media       => ("\uE8D6", Res("AccentMediaBrush")),
        _                     => ("\uE946", Res("TextPrimaryBrush")),
    };

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
