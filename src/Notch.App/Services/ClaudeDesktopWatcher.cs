using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Notch.Services;

/// <summary>
/// SPIKE, nur Diagnose: sucht Signale, an denen man "Claude Desktop arbeitet" erkennen kann.
/// Zeigt nichts an und schreibt ausschließlich ins Log (Präfix "ClaudeDiag").
/// Signale: Fenstertitel, Stop-artige Buttons (UI Automation), Dateiaktivität unter %APPDATA%\Claude.
/// Beendet sich nach <see cref="RunMinutes"/> Minuten selbst. Aktivierung: config "claudeDiag": true.
/// </summary>
public sealed class ClaudeDesktopWatcher : IDisposable
{
    private const int RunMinutes = 45;
    private const int MaxFileKeysLogged = 8;

    private static readonly Regex StopLike = new(
        @"stop|stopp|beenden|abbrechen|cancel|interrupt|unterbrech",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
    private readonly Dictionary<string, int> _fileHits = new();
    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _timer;
    private DateTime _stopAtUtc;
    private int _busy;
    private string _lastTitle = "\0";
    private string _lastStopButtons = "\0";

    public void Start()
    {
        _stopAtUtc = DateTime.UtcNow.AddMinutes(RunMinutes);
        Log.Info($"ClaudeDiag gestartet (läuft {RunMinutes} min). Claude-Prozesse: " +
                 string.Join(", ", Process.GetProcesses()
                     .Where(p => p.ProcessName.Contains("claude", StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.ProcessName).Distinct()));

        try
        {
            if (Directory.Exists(_root))
            {
                _watcher = new FileSystemWatcher(_root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                _watcher.Changed += OnFile;
                _watcher.Created += OnFile;
                _watcher.Error += (_, _) => { };   // Puffer-Überlauf ist für die Diagnose egal
                _watcher.EnableRaisingEvents = true;
            }
            else
            {
                Log.Info($"ClaudeDiag: Ordner fehlt: {_root}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("ClaudeDiag: Dateiüberwachung nicht möglich", ex);
        }

        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void OnFile(object sender, FileSystemEventArgs e)
    {
        try
        {
            var parts = Path.GetRelativePath(_root, e.FullPath).Split(Path.DirectorySeparatorChar);
            var dirs = parts.Length > 1 ? parts[..^1] : new[] { "(root)" };
            var key = string.Join('/', dirs.Take(3));
            lock (_lock) _fileHits[key] = _fileHits.GetValueOrDefault(key) + 1;
        }
        catch { /* Diagnose darf nie stören */ }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // vorheriger Durchlauf läuft noch
        try
        {
            if (DateTime.UtcNow >= _stopAtUtc)
            {
                Log.Info("ClaudeDiag beendet (Zeitlimit)");
                Dispose();
                return;
            }
            ProbeWindow();
            FlushFileHits();
        }
        catch (Exception ex)
        {
            Log.Error("ClaudeDiag: Fehler im Durchlauf", ex);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void ProbeWindow()
    {
        Process? window = null;
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            try
            {
                if (window is null && p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0) window = p;
            }
            catch { /* Prozess beendet */ }
        }

        if (window is null)
        {
            if (_lastTitle != "\0none") Log.Info("ClaudeDiag: kein Claude-Fenster gefunden");
            _lastTitle = "\0none";
            return;
        }

        var title = window.MainWindowTitle;
        if (title != _lastTitle)
        {
            Log.Info($"ClaudeDiag Titel: \"{title}\"");
            _lastTitle = title;
        }

        var root = AutomationElement.FromHandle(window.MainWindowHandle);
        var buttons = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        var stopNames = new List<string>();
        foreach (AutomationElement b in buttons)
        {
            var name = b.Current.Name?.Trim() ?? "";
            if (name.Length > 0 && StopLike.IsMatch(name)) stopNames.Add(name);
        }
        stopNames.Sort(StringComparer.Ordinal);

        var key = string.Join(" | ", stopNames);
        if (key != _lastStopButtons)
        {
            Log.Info($"ClaudeDiag UIA: {buttons.Count} Buttons gesamt; stop-artig: [{key}]");
            _lastStopButtons = key;
        }
    }

    private void FlushFileHits()
    {
        KeyValuePair<string, int>[] hits;
        lock (_lock)
        {
            if (_fileHits.Count == 0) return;
            hits = _fileHits.OrderByDescending(kv => kv.Value).Take(MaxFileKeysLogged).ToArray();
            _fileHits.Clear();
        }
        Log.Info("ClaudeDiag Dateien: " + string.Join(", ", hits.Select(kv => $"{kv.Key} x{kv.Value}")));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
