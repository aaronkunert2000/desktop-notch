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
    private Mutex? _mutex;
    private bool _ownsMutex;
    private DemoEventService? _demo;
    private EventServer? _server;
    private HotkeyService? _hotkey;
    private TrayService? _tray;
    private MediaService? _media;
    private TimerService? _timer;
    private PeekService? _peek;
    private ToastListenerService? _toasts;
    private EventResolver? _resolver;
    private IslandViewModel? _vm;
    private AppConfig _config = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Local\Notch.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unbehandelter UI-Fehler", ex.Exception);
            ex.Handled = true;
        };

        var config = _config = AppConfig.Load();
        var vm = _vm = new IslandViewModel();
        var resolver = _resolver = new EventResolver(vm);
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
            Exit: Shutdown));

        if (config.Spotify)
        {
            _media = new MediaService(resolver, Dispatcher);
            vm.MediaCommand += _media.Command;
            _ = _media.StartAsync();
        }

        if (config.WhatsApp)
        {
            _toasts = new ToastListenerService(resolver, config.WhatsAppPreview);
            _ = _toasts.StartAsync();
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

        _resolver.Submit(new NotchEvent("notch", EventKind.Info, "Quick Actions", _config.Hotkey,
            TtlSeconds: 8, Actions: BuildQuickActions(), Id: "quick", OpenLarge: true));
    }

    /// <summary>Maus oben mittig am Rand: Uhrzeit, beim Drüberfahren Quick Actions.</summary>
    private void OnPeek()
    {
        if (_vm is null || _resolver is null || _vm.State != IslandState.Idle) return;

        var now = DateTime.Now;
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        _resolver.Submit(new NotchEvent("notch", EventKind.Info, now.ToString("HH:mm", de),
            now.ToString("dddd, d. MMMM", de), TtlSeconds: 3, Actions: BuildQuickActions(), Id: "peek",
            Glyph: ""));   // Uhr-Symbol
    }

    private List<NotchAction> BuildQuickActions()
    {
        var running = _timer?.IsRunning ?? false;
        var actions = new List<NotchAction>
        {
            new("quick.timer", running ? "Timer stoppen" : $"Fokus {_timer?.FocusMinutes ?? 25} min", Primary: true),
        };
        if (!string.IsNullOrWhiteSpace(_config.VaultInbox)) actions.Add(new("quick.note", "Neue Notiz"));
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

    private void CreateNote()
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
        _tray?.Dispose();
        _hotkey?.Dispose();
        _media?.Dispose();
        _server?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
