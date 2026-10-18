using Notch.Models;

namespace Notch.Services;

/// <summary>Etappe 1: spielt eine Schleife von Fake-Events ab, um UI und Animationen zu testen.</summary>
public sealed class DemoEventService
{
    private readonly EventResolver _resolver;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _loop;

    public DemoEventService(EventResolver resolver, bool loop = false)
    {
        _resolver = resolver;
        _loop = loop;
    }

    public void Start() => _ = RunAsync(_cts.Token);

    public void Stop() => _cts.Cancel();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            do
            {
                await Task.Delay(2000, ct);

                _resolver.Submit(new NotchEvent("claude-code", EventKind.Running,
                    "Claude Code", "Notch · arbeitet …", Sticky: true));
                await Task.Delay(4500, ct);

                _resolver.Submit(new NotchEvent("claude-code", EventKind.InputNeeded,
                    "Freigabe nötig", "Bash: dotnet build", Sticky: true,
                    Actions: new[]
                    {
                        new NotchAction("approve", "Freigeben", Primary: true),
                        new NotchAction("deny", "Ablehnen"),
                    }));
                await Task.Delay(8000, ct);

                _resolver.Submit(new NotchEvent("claude-code", EventKind.Done,
                    "Fertig", "Notch · 42 s", TtlSeconds: 4));
                await Task.Delay(6000, ct);

                _resolver.Submit(new NotchEvent("vaultpipeline", EventKind.Error,
                    "Pipeline fehlgeschlagen", "VaultPipeline · Exit-Code 1", Sticky: true));
                await Task.Delay(5000, ct);
                _resolver.Dismiss("vaultpipeline", null);

                await Task.Delay(1500, ct);
                _resolver.Submit(new NotchEvent("timer", EventKind.Timer,
                    "Fokus 24:12", "Pomodoro · Runde 2", TtlSeconds: 4));
                await Task.Delay(6000, ct);
            }
            while (_loop && !ct.IsCancellationRequested);
        }
        catch (TaskCanceledException)
        {
            // Beenden
        }
    }
}
