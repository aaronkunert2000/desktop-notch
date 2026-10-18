using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Notch.Services;

/// <summary>
/// Holt das Fenster eines Prozesses nach vorne. Hat der Prozess selbst kein Fenster (z. B. Claude Code
/// in einer Konsole), wird die Elternkette hochgegangen, bis ein Fenster gefunden ist (Terminal, VS Code …).
/// </summary>
public static class WindowFocus
{
    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const int SW_RESTORE = 9;
    private const int MaxDepth = 12;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    public static bool FocusProcessWindow(int pid)
    {
        var parents = ParentMap();
        var current = pid;

        for (var depth = 0; depth < MaxDepth && current > 0; depth++)
        {
            var hwnd = MainWindowOf(current);
            if (hwnd != IntPtr.Zero)
            {
                var ok = Activate(hwnd);
                Log.Info($"Fenster von PID {current} (Start {pid}) aktiviert: {ok}");
                return ok;
            }
            if (!parents.TryGetValue(current, out var parent) || parent == current) break;
            current = parent;
        }

        Log.Info($"Kein Fenster für PID {pid} gefunden");
        return false;
    }

    private static IntPtr MainWindowOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainWindowHandle;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return IntPtr.Zero;   // Prozess beendet
        }
    }

    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref e)) return map;
            do map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
            while (Process32Next(snap, ref e));
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }

    private static bool Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (SetForegroundWindow(hwnd)) return true;

        // Windows blockiert Fokuswechsel teils: kurz an den Eingabe-Thread des Vordergrundfensters koppeln
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var ownThread = GetCurrentThreadId();
        if (fgThread == 0 || fgThread == ownThread) return SetForegroundWindow(hwnd);

        AttachThreadInput(ownThread, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            AttachThreadInput(ownThread, fgThread, false);
        }
    }
}
