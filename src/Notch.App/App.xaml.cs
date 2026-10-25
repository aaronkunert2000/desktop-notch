using System.Diagnostics;
using System.IO;
using System.Windows;
using Notch.Models;
using Notch.Services;
using Notch.ViewModels;
using Notch.Views;

namespace Notch;

public partial class App : Application
{
    private const string QuitEventName = @"Local\Notch.Quit";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _quitEvent;
    private DemoEventService? _demo;
    private EventServer? _server;
    private HotkeyService? _hotkey;
    private TrayService? _tray;
    private MediaService? _media;
    private TimerService? _timer;
    private PeekService? _peek;
    private InboxService? _inbox;
    private ToastListenerService? _toasts;
    private ClaudeDesktopWatcher? _claudeDiag;
    private GmailService? _gmail;
    private ClaudeDesktopMonitor? _claudeDesktop;
    private EventResolver? _resolver;
    private IslandViewModel? _vm;
    private AppConfig _config = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Local\Notch.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            // Zweiter Start = Umschalter (z. B. Ctrl+Alt+Shift+N): laufendes Notch beenden
            try
            {
                using var quit = EventWaitHandle.OpenExisting(QuitEventName);
                quit.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // ältere Version ohne Umschalter: einfach nichts tun
            }
            Shutdown();
            return;
        }

        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            Log.Info("Beendet per Umschalter (erneuter Start)");
            Shutdown();
        })), null, Timeout.Infinite, executeOnlyOnce: true);

        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unbehandelter UI-Fehler", ex.Exception);
            ex.Handled = true;
        };

        var config = _config = AppConfig.Load();
        var vm = _vm = new IslandViewModel();
        var resolver = _resolver = new EventResolver(vm);
        var inbox = _inbox = new InboxService();

        // Ablage + Seiten: jedes Event wird für die Ablage erfasst, jede Änderung aktualisiert die Punkte
        vm.PageProvider = BuildPages;
        vm.InboxProvider = () => inbox.Items;
        resolver.Submitted += inbox.Track;
        resolver.Changed += vm.RefreshPages;
        inbox.Changed += OnInboxChanged;
        vm.Acknowledged += ev => inbox.RemoveByKey(ev.Source, ev.Id);
        vm.DismissRequested += ev => resolver.Dismiss(ev.Source, ev.Id);
        vm.InboxItemOpened += OpenInboxItem;
        vm.InboxReadAll += inbox.Clear;
        var window = new IslandWindow(vm);
        window.Show();

        _timer = new TimerService(resolver, config.FocusMinutes, config.BreakMinutes);
        vm.ActionInvoked += OnAction;
        vm.Activated += ev =>
        {
            if (ev.HostPid is int pid)
            {
                if (!WindowFocus.FocusProcessWindow(pid)) Launch(config.ClaudeCommand);
            }
            else if (ev.Open is not null)
            {
                Launch(ev.Open);
            }
        };

        _peek = new PeekService();
        _peek.Triggered += OnPeek;

        _hotkey = new HotkeyService();
        _hotkey.Pressed += OnHotkey;
        if (!_hotkey.Register(window, config.Hotkey))
        {
            resolver.Submit(new NotchEvent("notch", EventKind.Error,
                "Hotkey nicht verfügbar", $"{config.Hotkey} ist belegt oder ungültig (config.json)", Sticky: true));
        }

        _tray = new TrayService(new TrayActions(
            IsDoNotDisturb: () => resolver.DoNotDisturb,
            ToggleDoNotDisturb: () => resolver.DoNotDisturb = !resolver.DoNotDisturb,
            IsTimerRunning: () => _timer.IsRunning,
            ToggleTimer: _timer.Toggle,
            ToggleIsland: OnHotkey,
            TestEvent: () => resolver.Submit(new NotchEvent("notch", EventKind.Done,
                "Test-Event", "Tray · alles ok", Id: "test")),
            OpenLog: () => Launch(Path.Combine(Log.Dir, "notch.log")),
            IsGmailConnected: () => GmailCredentials.Exists,
            GmailConnect: ConnectGmail,
            IsNotionConnected: () => NotionCredentials.Exists,
            NotionConnect: ConnectNotion,
            NotionDisconnect: () =>
            {
                NotionCredentials.Delete();
                Log.Info("Notion getrennt");
            },
            GmailDisconnect: () =>
            {
                _gmail?.Stop();
                GmailCredentials.Delete();
                inbox.RemoveBySource("gmail");
                resolver.Dismiss("gmail", null);
                Log.Info("Gmail getrennt");
            },
            Exit: Shutdown));

        if (config.Spotify)
        {
            _media = new MediaService(resolver, Dispatcher);
            vm.MediaCommand += _media.Command;
            _ = _media.StartAsync();
        }

        if (config.WhatsApp)
        {
            // In WhatsApp gelesen = Windows-Benachrichtigung weg = in der Ablage gelesen
            _toasts = new ToastListenerService(resolver, config.WhatsAppPreview,
                onRemoved: id => inbox.RemoveByKey("whatsapp", id));
            _ = _toasts.StartAsync();
        }

        if (config.ClaudeDesktop)
        {
            _claudeDesktop = new ClaudeDesktopMonitor(resolver, Dispatcher);
            _claudeDesktop.Start();
        }

        if (config.Gmail)
        {
            _gmail = new GmailService(resolver, Dispatcher);
            _gmail.Start();   // tut nichts ohne gespeichertes App-Passwort
        }

        if (config.ClaudeDiag)
        {
            _claudeDiag = new ClaudeDesktopWatcher();
            _claudeDiag.Start();
        }

        try
        {
            _server = new EventServer(config.Port, TokenStore.GetOrCreate(), resolver, Dispatcher);
            _server.Start();
            Log.Info($"EventServer läuft: http://localhost:{config.Port}/");
        }
        catch (Exception ex)
        {
            Log.Error("EventServer konnte nicht starten", ex);
            resolver.Submit(new NotchEvent("notch", EventKind.Error,
                "EventServer-Fehler", $"Port {config.Port}: {ex.Message}", Sticky: true));
        }

        // Demo: config "demo": true, --demo (einmal) oder --demo-loop (Endlosschleife)
        var loop = e.Args.Contains("--demo-loop");
        if (config.Demo || loop || e.Args.Contains("--demo"))
        {
            _demo = new DemoEventService(resolver, loop);
            _demo.Start();
        }
    }

    /// <summary>Hotkey/Tray-Klick: Anzeige auf/zu. Leer → Quick Actions, sichtbar → alles ausblenden.</summary>
    private void OnHotkey()
    {
        if (_vm is null || _resolver is null) return;

        if (_vm.State != IslandState.Idle)
        {
            _resolver.HideAll();
            return;
        }

        // Ausgeblendete Dauer-Vorgänge (z. B. Musik) zurückholen
        if (_resolver.RestoreParked()) return;

        // Sonst Ungelesenes zuerst; Uhr und Quick Actions sind dann die nächste Seite
        _resolver.Submit(_inbox is { Count: > 0 } ? _inbox.CreateEvent(8) : StandardEvent(8, openLarge: true));
    }

    /// <summary>Standard-Ansicht: Uhrzeit, Datum, Quick Actions. Immer als letzte Seite erreichbar.</summary>
    private void ConnectGmail()
    {
        var current = GmailCredentials.Load()?.User;
        using var dialog = new GmailSetupDialog(current);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (dialog.User.Length == 0 || dialog.Password.Length == 0) return;

        GmailCredentials.Save(dialog.User, dialog.Password);
        _gmail ??= new GmailService(_resolver!, Dispatcher);
        _gmail.Start();
        _resolver!.Submit(new NotchEvent("notch", EventKind.Info, "Gmail verbunden",
            dialog.User, Id: "gmail-setup", TtlSeconds: 4));
    }

    private NotchEvent StandardEvent(int ttlSeconds, bool openLarge)
    {
        var now = DateTime.Now;
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        return new NotchEvent("notch", EventKind.Info, now.ToString("HH:mm", de), now.ToString("dddd, d. MMMM", de),
            TtlSeconds: ttlSeconds, Actions: BuildQuickActions(), Id: "quick", OpenLarge: openLarge,
            Glyph: "\uE823");   // Uhr-Symbol
    }

    /// <summary>Seiten für die Punkte: aktive Vorgänge, Ablage (falls ungelesen), ggf. Quick Actions.</summary>
    private IReadOnlyList<NotchEvent> BuildPages()
    {
        if (_resolver is null || _inbox is null) return Array.Empty<NotchEvent>();

        var pages = _resolver.LivePages().ToList();
        // Per Hotkey ausgeblendete Vorgänge bleiben als Seite erreichbar (ohne wieder aufzutauchen)
        foreach (var parked in _resolver.ParkedPages())
            if (pages.All(p => p.Source != parked.Source || p.Id != parked.Id)) pages.Add(parked);
        if (_inbox.Count > 0 && !pages.Any(InboxService.IsInboxEvent)) pages.Add(_inbox.CreateEvent(8));
        // Standard-Ansicht (Uhr + Quick Actions) immer als letzte Seite
        if (pages.Count > 0 && !pages.Any(p => p is { Source: "notch", Id: "quick" or "peek" }))
            pages.Add(StandardEvent(8, openLarge: true));

        return pages.OrderByDescending(PagePriority).ToList();
    }

    private static double PagePriority(NotchEvent e) =>
        InboxService.IsInboxEvent(e) ? 2.5
        : e is { Source: "notch", Id: "quick" or "peek" } ? -1
        : EventResolver.Priority(e.Kind);

    private void OnInboxChanged()
    {
        _vm?.RefreshInbox();
        _vm?.RefreshPages();
        // Alles gelesen: Ablage-Seite schließen, falls sie gerade die Vorgabe ist
        if (_inbox is { Count: 0 } && InboxService.IsInboxEvent(_resolver?.Current))
            _resolver?.Dismiss("notch", "inbox");
    }

    /// <summary>Eintrag aus der Ablage: als gelesen entfernen und die Quelle öffnen.</summary>
    private void OpenInboxItem(string uid)
    {
        var item = _inbox?.Take(uid);
        if (item is null) return;

        if (item.HostPid is int pid && WindowFocus.FocusProcessWindow(pid)) return;
        if (item.Source == "claude-code") FocusClaude();
        else if (item.Open is not null) Launch(item.Open);
    }

    /// <summary>Maus oben mittig am Rand: Uhrzeit, beim Drüberfahren Quick Actions.</summary>
    private void OnPeek()
    {
        if (_vm is null || _resolver is null || _vm.State != IslandState.Idle) return;

        // Ungelesenes statt Uhrzeit; Uhr und Quick Actions sind dann die nächste Seite
        _resolver.Submit(_inbox is { Count: > 0 } ? _inbox.CreateEvent(4) : StandardEvent(3, openLarge: false));
    }

    private List<NotchAction> BuildQuickActions()
    {
        var running = _timer?.IsRunning ?? false;
        var actions = new List<NotchAction>
        {
            new("quick.timer", running ? "Timer stoppen" : $"Fokus {_timer?.FocusMinutes ?? 25} min", Primary: true),
        };
        if (NotionReady || !string.IsNullOrWhiteSpace(_config.VaultInbox)) actions.Add(new("quick.note", "Neue Notiz"));
        actions.Add(new("quick.claude", "Claude"));
        return actions;
    }

    private void OnAction(NotchEvent ev, string actionId)
    {
        if (ev is not { Source: "notch", Id: "quick" or "peek" }) return;
        switch (actionId)
        {
            case "quick.timer": _timer?.Toggle(); break;
            case "quick.note": CreateNote(); break;
            case "quick.claude": FocusClaude(); break;
        }
    }

    /// <summary>Zur zuletzt aktiven Claude-Code-Session springen, sonst die Claude-App öffnen.</summary>
    private void FocusClaude()
    {
        if (_resolver is not null && _resolver.TryGetHostPid("claude-code", out var pid)
            && WindowFocus.FocusProcessWindow(pid))
            return;
        Launch(_config.ClaudeCommand);
    }

    private bool NotionReady => NotionCredentials.Exists && !string.IsNullOrWhiteSpace(_config.NotionNotesDb);

    private void ConnectNotion()
    {
        using var dialog = new SecretPromptDialog("Notion verbinden",
            "Interne Integration unter notion.so/my-integrations anlegen, [DB] Notes mit ihr teilen " +
            "(Seite \"...\" > Verbindungen) und das Token (secret_… / ntn_…) hier einfügen.",
            "Integrations-Token");
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || dialog.Secret.Length == 0) return;

        NotionCredentials.Save(dialog.Secret);
        _resolver!.Submit(new NotchEvent("notch", EventKind.Info, "Notion verbunden",
            string.IsNullOrWhiteSpace(_config.NotionNotesDb) ? "notionNotesDb in config fehlt" : "Neue Notiz legt Seiten an",
            Id: "notion-setup", TtlSeconds: 4));
    }

    private async void CreateNote()
    {
        if (NotionReady)
        {
            try
            {
                var token = NotionCredentials.Load() ?? throw new InvalidOperationException("Token nicht lesbar");
                var url = await NotionNoteService.CreateAsync(token, _config.NotionNotesDb, _config.NotionTitleProperty, "Neue Notiz");
                Log.Info("Notion-Notiz angelegt");
                Launch(NotionNoteService.ToAppUrl(url));
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Notion-Notiz fehlgeschlagen", ex);
                ReportError("Notion fehlgeschlagen", ex.Message);
                if (string.IsNullOrWhiteSpace(_config.VaultInbox)) return;
            }
        }

        CreateVaultNote();
    }

    private void CreateVaultNote()
    {
        var dir = _config.VaultInbox;
        if (!Directory.Exists(dir))
        {
            ReportError("Vault-Ordner nicht gefunden", dir);
            return;
        }
        try
        {
            var path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd HHmm} Notiz.md");
            File.WriteAllText(path, $"---\ncreated: {DateTime.Now:yyyy-MM-ddTHH:mm}\nsource: notch\n---\n\n");
            Log.Info($"Notiz angelegt: {path}");
            Launch("obsidian://open?path=" + Uri.EscapeDataString(path));
        }
        catch (Exception ex)
        {
            Log.Error("Notiz konnte nicht angelegt werden", ex);
            ReportError("Notiz fehlgeschlagen", ex.Message);
        }
    }

    private void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Start fehlgeschlagen: {target}", ex);
            ReportError("Start fehlgeschlagen", target);
        }
    }

    private void ReportError(string title, string subtitle) =>
        _resolver?.Submit(new NotchEvent("notch", EventKind.Error, title, subtitle, Sticky: true, Id: "error"));

    protected override void OnExit(ExitEventArgs e)
    {
        _demo?.Stop();
        _peek?.Dispose();
        _toasts?.Dispose();
        _claudeDesktop?.Dispose();
        _gmail?.Dispose();
        _claudeDiag?.Dispose();
        _tray?.Dispose();
        _hotkey?.Dispose();
        _media?.Dispose();
        _server?.Dispose();
        _quitEvent?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
