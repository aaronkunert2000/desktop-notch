using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Notch.Services;

/// <summary>Globaler Hotkey über RegisterHotKey, z. B. "Ctrl+Alt+N".</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4E01;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private IntPtr _hwnd;

    public event Action? Pressed;

    /// <summary>Muss nach Window.Show() aufgerufen werden.</summary>
    public bool Register(Window window, string spec)
    {
        if (!TryParse(spec, out var modifiers, out var vk))
        {
            Log.Error($"Hotkey ungültig: '{spec}'");
            return false;
        }

        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        if (!RegisterHotKey(_hwnd, HotkeyId, modifiers | MOD_NOREPEAT, vk))
        {
            Log.Error($"Hotkey '{spec}' belegt (Win32-Fehler {Marshal.GetLastWin32Error()})");
            return false;
        }

        Log.Info($"Hotkey registriert: {spec}");
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static bool TryParse(string spec, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        foreach (var mod in parts[..^1])
        {
            modifiers |= mod.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "strg" => MOD_CONTROL,
                "alt" => MOD_ALT,
                "shift" => MOD_SHIFT,
                "win" => MOD_WIN,
                _ => 0u,
            };
        }
        if (modifiers == 0) return false;

        if (!Enum.TryParse<Key>(parts[^1], ignoreCase: true, out var key)) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyId);
        _source?.RemoveHook(WndProc);
    }
}
