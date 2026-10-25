using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Threading;
using Notch.Models;

namespace Notch.Services;

/// <summary>
/// Erkennt, ob Claude Desktop (Chat/Cowork) gerade antwortet, und zeigt es im Notch:
/// Dauer-Anzeige "Claude arbeitet" solange der Stop-Button im Fenster steht, danach "Claude fertig".
/// Signal: Stop-Button der Claude-Oberfläche (UI Automation), das Fenster wird nur gelesen.
/// Hooks gibt es in Chat/Cowork nicht; Claude Code meldet sich weiter selbst über claude_hook.py.
/// </summary>
public sealed class ClaudeDesktopMonitor : IDisposable
{
    private const string Source = "claude-desktop";
    private const string EventId = "app";
    private const int PollMs = 1500;
    private const int MissesToFinish = 3;     // ~4,5 s ohne Stop-Button = fertig (der Button flackert zwischen Werkzeugaufrufen)
    private const int MinWorkingSeconds = 4;  // kürzere Läufe sind kein "fertig" wert
    private const int RefreshSeconds = 60;
    // Im Hintergrund (minimiert/verdeckt) friert Chromium den UI-Baum ein: der Stop-Button bliebe "sichtbar".
    // Dort entscheidet die CPU-Last der Claude-Prozesse; so lange Ruhe = fertig.
    private const double ActiveCpuPercent = 2.0;   // Prozent eines Kerns
    private const int BackgroundQuietSeconds = 15;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private static readonly Regex StopLike = new(
        @"^(stop( response| generating)?|stopp(en)?|antwort (stoppen|abbrechen)|generierung (stoppen|abbrechen))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly EventResolver _resolver;
    private readonly Dispatcher _dispatcher;
    private System.Threading.Timer? _timer;
    private int _busy;
    private bool _working;
    private int _misses;
    private DateTime _startedAt;
    private DateTime _lastRefresh;
    private int _pid;
    private double _lastPercent;
    private TimeSpan _cpuLast;
    private DateTime _cpuAt = DateTime.UtcNow;
    private DateTime _lastCpuActive = DateTime.UtcNow;

    public ClaudeDesktopMonitor(EventResolver resolver, Dispatcher dispatcher)
    {
        _resolver = resolver;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(PollMs));
        Log.Info("Claude-Desktop-Anzeige aktiv");
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var state = Probe();   // true/false, null = unklar (Fenster im Umbau): Zustand beibehalten
            if (state is null) return;
            Apply(state.Value);
        }
        catch (Exception ex)
        {
            Log.Error("Claude-Desktop-Anzeige: Fehler im Durchlauf", ex);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private bool? Probe()
    {
        Process? window = null;
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0) { window = p; break; }
            }
            catch { /* Prozess beendet */ }
        }
        if (window is null) return false;
        _pid = window.Id;

        var cpuActive = SampleCpu();
        var background = IsIconic(window.MainWindowHandle) || GetForegroundWindow() != window.MainWindowHandle;

        try
        {
            var root = AutomationElement.FromHandle(window.MainWindowHandle);
            var buttons = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement b in buttons)
            {
                try
                {
                    var name = b.Current.Name?.Trim();
                    if (!string.IsNullOrEmpty(name) && StopLike.IsMatch(name))
                        return !background || (DateTime.UtcNow - _lastCpuActive).TotalSeconds < BackgroundQuietSeconds;
                }
                catch (ElementNotAvailableException) { return null; }
            }
            return false;
        }
        catch (ElementNotAvailableException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>CPU-Last aller Claude-Prozesse seit dem letzten Aufruf; merkt sich den Zeitpunkt der letzten Aktivität.</summary>
    private bool SampleCpu()
    {
        var total = TimeSpan.Zero;
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            try { total += p.TotalProcessorTime; }
            catch { /* Prozess beendet oder kein Zugriff */ }
            finally { p.Dispose(); }
        }

        var now = DateTime.UtcNow;
        var elapsed = (now - _cpuAt).TotalMilliseconds;
        var percent = elapsed > 0 && _cpuLast > TimeSpan.Zero
            ? Math.Max(0, (total - _cpuLast).TotalMilliseconds) / elapsed * 100.0
            : 0;
        _cpuLast = total;
        _cpuAt = now;

        var active = percent >= ActiveCpuPercent;
        if (active) _lastCpuActive = now;
        _lastPercent = percent;
        return active;
    }

    private void Apply(bool stopVisible)
    {
        var now = DateTime.UtcNow;
        if (stopVisible)
        {
            _misses = 0;
            if (!_working)
            {
                _working = true;
                _startedAt = _lastRefresh = now;
                Log.Info($"Claude Desktop arbeitet (CPU {_lastPercent:0.0} %)");
                Post(Running());
                return;
            }
            if ((now - _lastRefresh).TotalSeconds >= RefreshSeconds)
            {
                _lastRefresh = now;
                Post(Running());   // verhindert Ablauf der Anzeige bei langen Läufen
            }
            return;
        }

        if (!_working) return;
        if (++_misses < MissesToFinish) return;

        _working = false;
        _misses = 0;
        Log.Info($"Claude Desktop fertig nach {(now - _startedAt).TotalSeconds:0} s (CPU {_lastPercent:0.0} %)");
        if ((now - _startedAt).TotalSeconds < MinWorkingSeconds)
        {
            Post(null);
            return;
        }
        Post(new NotchEvent(Source, EventKind.Done, "Claude fertig", "Claude Desktop",
            TtlSeconds: 10, Id: EventId, HostPid: _pid));
    }

    private NotchEvent Running() =>
        new(Source, EventKind.Running, "Claude arbeitet", "Claude Desktop",
            Sticky: false, TtlSeconds: 300, Id: EventId, HostPid: _pid);

    /// <summary>null = Anzeige ohne Abschluss-Meldung beenden.</summary>
    private void Post(NotchEvent? e) => _dispatcher.BeginInvoke(new Action(() =>
    {
        if (e is null) _resolver.Dismiss(Source, EventId);
        else _resolver.Submit(e);
    }));

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
