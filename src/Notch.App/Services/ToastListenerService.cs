using System.Windows.Threading;
using Notch.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Notch.Services;

/// <summary>
/// Experimentell: liest Windows-Benachrichtigungen (WhatsApp) und zeigt sie im Notch.
/// Fragt alle 2 s ab, statt auf das Änderungs-Event zu hören: das Event braucht eine
/// App-Paket-Identität (MSIX), das Abfragen funktioniert eventuell auch ohne.
/// Der Original-Toast bleibt bestehen; in Windows "Benachrichtigungsbanner anzeigen" für WhatsApp abschalten.
/// </summary>
public sealed class ToastListenerService : IDisposable
{
    private const string Source = "whatsapp";
    private const int MaxErrors = 3;

    private readonly EventResolver _resolver;
    private readonly bool _preview;
    private readonly Action<string>? _onRemoved;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<uint> _seen = new();
    private UserNotificationListener? _listener;
    private bool _primed;
    private bool _busy;
    private int _errors;

    public ToastListenerService(EventResolver resolver, bool preview, Action<string>? onRemoved = null)
    {
        _resolver = resolver;
        _preview = preview;
        _onRemoved = onRemoved;
        _poll.Tick += async (_, _) => await PollAsync();
    }

    public async Task StartAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var status = await _listener.RequestAccessAsync();
            Log.Info($"Benachrichtigungszugriff: {status}");
            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                Report("Kein Zugriff auf Benachrichtigungen",
                    "Einstellungen → Datenschutz → Benachrichtigungen");
                return;
            }
            await PollAsync();
            _poll.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Benachrichtigungen nicht lesbar (evtl. App-Paket nötig)", ex);
            Report("WhatsApp-Übernahme nicht möglich", "Details im Log (Tray → Log öffnen)");
        }
    }

    private async Task PollAsync()
    {
        if (_busy || _listener is null) return;
        _busy = true;
        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var current = new HashSet<uint>();

            foreach (var n in list)
            {
                current.Add(n.Id);
                if (!_seen.Add(n.Id) || !_primed) continue;   // beim Start: vorhandene nicht erneut zeigen
                TryShow(n);
            }

            // Aus Windows verschwunden (in der App gelesen oder weggeklickt)
            if (_primed && _onRemoved is not null)
                foreach (var gone in _seen.Where(id => !current.Contains(id)).ToList())
                    _onRemoved(gone.ToString());

            _seen.IntersectWith(current);   // nur IDs merken, die noch im Benachrichtigungscenter liegen
            _primed = true;
            _errors = 0;
        }
        catch (Exception ex)
        {
            Log.Error("Benachrichtigungen abfragen fehlgeschlagen", ex);
            if (++_errors >= MaxErrors)
            {
                _poll.Stop();
                Report("WhatsApp-Übernahme gestoppt", "Wiederholte Fehler, Details im Log");
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void TryShow(UserNotification n)
    {
        try
        {
            var app = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
            Log.Info($"Toast von: {(app.Length == 0 ? "?" : app)}");
            if (!app.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase)) return;

            var texts = n.Notification?.Visual?
                .GetBinding(KnownNotificationBindings.ToastGeneric)?
                .GetTextElements()
                .Select(t => t.Text?.Trim() ?? "")
                .Where(t => t.Length > 0)
                .ToList() ?? new List<string>();

            var sender = texts.FirstOrDefault() ?? "WhatsApp";
            var body = _preview ? string.Join(" ", texts.Skip(1)) : "Neue Nachricht";
            if (body.Length == 0) body = "Neue Nachricht";

            _resolver.Submit(new NotchEvent(Source, EventKind.Message, sender, body,
                TtlSeconds: 6, Id: n.Id.ToString(), Open: "whatsapp://"));
        }
        catch (Exception ex)
        {
            Log.Error("Benachrichtigung nicht lesbar", ex);
        }
    }

    private void Report(string title, string subtitle) =>
        _resolver.Submit(new NotchEvent("notch", EventKind.Error, title, subtitle, Sticky: true, Id: "whatsapp"));

    public void Dispose() => _poll.Stop();
}
