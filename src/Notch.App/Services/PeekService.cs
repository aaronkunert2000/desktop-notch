using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Notch.Services;

/// <summary>
/// Maus-Reveal: Stößt der Mauszeiger oben mittig an den Bildschirmrand (kurz verweilen),
/// wird <see cref="Triggered"/> ausgelöst. Nicht bei Vollbild-Programmen. Nur Abfrage, kein Eingriff in Klicks.
/// </summary>
public sealed class PeekService : IDisposable
{
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(150);
    private const double ZoneWidthFraction = 0.24;   // 24 % der Bildschirmbreite, mittig
    private const int ZoneHeightPx = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _inZone;
    private bool _fired;
    private DateTime _enteredUtc;

    public event Action? Triggered;

    public PeekService()
    {
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    private void Poll()
    {
        if (!GetCursorPos(out var p)) return;

        var width = GetSystemMetrics(SM_CXSCREEN);
        var inZone = p.Y <= ZoneHeightPx && Math.Abs(p.X - width / 2.0) <= width * ZoneWidthFraction / 2;

        if (!inZone)
        {
            _inZone = false;
            _fired = false;   // erst nach Verlassen erneut auslösen
            return;
        }
        if (!_inZone)
        {
            _inZone = true;
            _enteredUtc = DateTime.UtcNow;
            return;
        }
        if (_fired || DateTime.UtcNow - _enteredUtc < Dwell) return;

        _fired = true;
        if (!IsFullscreenForeground()) Triggered?.Invoke();
    }

    private static bool IsFullscreenForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;

        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW") return false;   // Desktop

        return r.Left <= 0 && r.Top <= 0
               && r.Right >= GetSystemMetrics(SM_CXSCREEN)
               && r.Bottom >= GetSystemMetrics(SM_CYSCREEN);
    }

    public void Dispose() => _poll.Stop();
}
