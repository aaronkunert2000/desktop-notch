using System.IO;
using System.Text.Json;
using Notch.Models;

namespace Notch.Services;

/// <summary>
/// Ablage ungelesener Benachrichtigungen. Bleibt gespeichert, bis die Meldung gelesen ist:
///  - Klick in der Insel (Meldung oder Eintrag in der Ablage) oder "Alle gelesen"
///  - neue Meldung desselben Vorgangs (gleiche source + id), z. B. Claude arbeitet in derselben Session weiter
///  - WhatsApp: Benachrichtigung in Windows verschwunden (in der App gelesen)
/// </summary>
public sealed class InboxService
{
    private const int MaxItems = 200;
    private static readonly string FilePath = Path.Combine(Log.Dir, "inbox.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly List<InboxItem> _items = new();   // neueste zuerst

    public InboxService() => Load();

    public event Action? Changed;

    public int Count => _items.Count;
    public IReadOnlyList<InboxItem> Items => _items;

    /// <summary>Abgeschlossene Meldungen; laufende Zustände, Musik, Timer und Notch-intern nicht.</summary>
    public static bool IsWorthy(NotchEvent e) =>
        e.Kind is EventKind.Done or EventKind.Error or EventKind.InputNeeded or EventKind.Message
        && e.Source is not ("notch" or "notch-update" or "dev-sync");

    /// <summary>Für jedes eingehende Event aufrufen (auch bei "Nicht stören").</summary>
    public void Track(NotchEvent e)
    {
        var changed = _items.RemoveAll(i => i.Source == e.Source && i.EventId == e.Id) > 0;
        if (IsWorthy(e))
        {
            _items.Insert(0, new InboxItem(Guid.NewGuid().ToString("N"), e.Source, e.Id, e.Kind,
                e.Title, e.Subtitle, DateTime.Now, e.Open, e.HostPid));
            if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
            changed = true;
        }
        if (changed) Commit();
    }

    public InboxItem? Take(string uid)
    {
        var item = _items.FirstOrDefault(i => i.Uid == uid);
        if (item is null) return null;
        _items.Remove(item);
        Commit();
        return item;
    }

    public void RemoveByKey(string source, string? eventId)
    {
        if (_items.RemoveAll(i => i.Source == source && i.EventId == eventId) > 0) Commit();
    }

    public void RemoveBySource(string source)
    {
        if (_items.RemoveAll(i => i.Source == source) > 0) Commit();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Commit();
    }

    /// <summary>Seite "Benachrichtigungen" für die Insel.</summary>
    public NotchEvent CreateEvent(int ttlSeconds) =>
        new("notch", EventKind.Info, "Benachrichtigungen", $"{Count} ungelesen",
            TtlSeconds: ttlSeconds, Id: "inbox", OpenLarge: true, Glyph: "");

    public static bool IsInboxEvent(NotchEvent? e) => e is { Source: "notch", Id: "inbox" };

    private void Commit()
    {
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var items = JsonSerializer.Deserialize<List<InboxItem>>(File.ReadAllText(FilePath), Json);
            if (items is not null) _items.AddRange(items.Take(MaxItems));
            Log.Info($"Ablage geladen: {_items.Count} ungelesen");
        }
        catch (Exception ex)
        {
            Log.Error("Ablage nicht lesbar, starte leer", ex);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Ablage nicht speicherbar", ex);
        }
    }
}
