## Neu in 1.1

- **Ablage für ungelesene Meldungen:** Claude fertig/wartet, Pipeline, WhatsApp und Timer-Ende bleiben gespeichert, bis du sie liest (Klick, „Alle gelesen", in WhatsApp gelesen oder Claude arbeitet weiter). Übersteht Neustarts.
- **Seiten-Punkte:** Laufen mehrere Dinge gleichzeitig, wechselst du per Mausrad oder Klick auf die Punkte zwischen Musik, Timer, Claude, Ablage und Uhr-Ansicht.
- **`Ctrl+Alt+N`** blendet aus und holt beim nächsten Druck Musik/Timer zurück. **`Ctrl+Alt+Shift+N`** schaltet das Notch komplett an/aus.
- Autostart bei der Windows-Anmeldung, robustere Updates.

## Installation

1. **`Notch.exe` herunterladen** und in einen festen Ordner legen, z. B. `%LOCALAPPDATA%\Programs\Notch`. Ein .NET-Download ist nicht nötig.
2. Starten. Windows zeigt eventuell „Der Computer wurde durch Windows geschützt", weil die Datei nicht signiert ist: **Weitere Informationen → Trotzdem ausführen**.
3. Rechtsklick auf das Tray-Icon (kleine Pill in der Taskleiste) → **Mit Windows starten**.

Wer Einstellungen anpassen will, nimmt stattdessen `Notch-win-x64.zip`. Darin liegt zusätzlich die `config.json`.

**Bedienung:** `Ctrl+Alt+N` öffnet Ablage bzw. Uhr mit Quick Actions. Maus oben mittig an den Rand zeigt Ungelesenes oder die Uhrzeit. Spotify erscheint automatisch, solange Musik läuft.

**Voraussetzungen:** Windows 10 (2004) oder 11, Intel/AMD (x64).

Claude-Code-Hooks und eigene Events: siehe [README](https://github.com/aaronkunert2000/desktop-notch#readme).
