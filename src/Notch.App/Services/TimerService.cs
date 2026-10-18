using System.Windows.Threading;
using Notch.Models;

namespace Notch.Services;

/// <summary>Fokus-Timer: Fokusphase, danach automatisch eine Pause. Live-Countdown in der Pill.</summary>
public sealed class TimerService
{
    private const string Source = "timer";

    private readonly EventResolver _resolver;
    private readonly int _focusMinutes;
    private readonly int _breakMinutes;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _endUtc;
    private bool _isBreak;

    public TimerService(EventResolver resolver, int focusMinutes, int breakMinutes)
    {
        _resolver = resolver;
        _focusMinutes = Math.Clamp(focusMinutes, 1, 180);
        _breakMinutes = Math.Clamp(breakMinutes, 1, 60);
        _tick.Tick += (_, _) => OnTick();
    }

    public bool IsRunning => _tick.IsEnabled;
    public int FocusMinutes => _focusMinutes;

    public void Toggle()
    {
        if (IsRunning) Stop();
        else StartFocus();
    }

    public void StartFocus() => Start(isBreak: false);

    public void Stop()
    {
        _tick.Stop();
        _resolver.Dismiss(Source, null);
        Log.Info("Timer gestoppt");
    }

    private void Start(bool isBreak)
    {
        _isBreak = isBreak;
        _endUtc = DateTime.UtcNow.AddMinutes(isBreak ? _breakMinutes : _focusMinutes);
        _tick.Start();
        Log.Info(isBreak ? "Pause gestartet" : "Fokus gestartet");
        Push();
    }

    private void OnTick()
    {
        if (DateTime.UtcNow < _endUtc)
        {
            Push();
            return;
        }

        _tick.Stop();
        if (!_isBreak)
        {
            _resolver.Submit(new NotchEvent(Source, EventKind.Done, "Fokus vorbei",
                $"{_breakMinutes} min Pause startet", TtlSeconds: 6, Id: "end"));
            Start(isBreak: true);
        }
        else
        {
            _resolver.Dismiss(Source, null);
            _resolver.Submit(new NotchEvent(Source, EventKind.Done, "Pause vorbei",
                "Zurück an die Arbeit", TtlSeconds: 6, Id: "end"));
        }
    }

    private void Push()
    {
        var left = _endUtc - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        var label = _isBreak ? "Pause" : "Fokus";
        _resolver.Submit(new NotchEvent(Source, EventKind.Timer,
            $"{label} {(int)left.TotalMinutes:00}:{left.Seconds:00}",
            _isBreak ? "Kurz abschalten" : "Konzentriert arbeiten",
            Sticky: true));
    }
}
