# Desktop Notch

Eine Dynamic Island für Windows: eine schwarze Insel oben mittig auf dem Bildschirm, die zeigt, was deine eigenen Systeme gerade tun. Gebaut mit WPF (.NET 8) für ein Galaxy Book3 Ultra, läuft auf jedem Windows-10/11-PC mit Intel/AMD.

## Was das Notch anzeigt

| Quelle | Anzeige |
|---|---|
| Claude Code (Hooks) | „Claude arbeitet", „Claude wartet" (Eingabe nötig), „Claude fertig". Klick holt das Terminal nach vorne |
| VaultPipeline | Läuft / fertig mit Zahlen / Fehler |
| Spotify | Cover und Titel während der Wiedergabe, beim Drüberfahren Zurück, Play/Pause, Weiter |
| WhatsApp (experimentell) | Absender und Nachricht, Klick öffnet WhatsApp |
| Fokus-Timer | 25 min Fokus + 5 min Pause mit Live-Countdown |
| Eigene Skripte | beliebige Events über `scripts/notch_send.py` |

**Prioritäten:** Freigaben und Fehler werden nie verdrängt. Musik und Timer laufen im Hintergrund und kommen nach anderen Meldungen zurück. Kurze Meldungen verschwinden nach ein paar Sekunden.

## Bedienung

- **`Ctrl+Alt+N`:** Ist nichts zu sehen → Quick Actions (Fokus-Timer, Neue Notiz, Claude). Ist etwas zu sehen → alles ausblenden.
- **Maus oben mittig an den Rand:** Uhrzeit, beim Drüberfahren Quick Actions.
- **Tray-Icon:** Linksklick wie der Hotkey. Rechtsklick: Nicht stören, Timer, Test-Event, Log, Autostart, Beenden.
- **`Ctrl+Alt+Shift+N`:** Notch komplett an/aus (Startmenü-Verknüpfung, wird von `update-notch.ps1` angelegt). Ein erneuter Start der exe beendet ein laufendes Notch.
- **Autostart:** startet bei der Windows-Anmeldung. Abschalten im Tray („Mit Windows starten"), das bleibt dann auch nach Updates aus.

## Download (ohne Entwicklungsumgebung)

Fertige `Notch.exe` unter [Releases](../../releases/latest). Läuft ohne installiertes .NET auf Windows 10 (2004) oder 11 mit Intel/AMD. Die Datei ist nicht signiert: Bei „Der Computer wurde durch Windows geschützt" auf **Weitere Informationen → Trotzdem ausführen** klicken.

## Installation aus dem Code

Voraussetzung: [.NET 8 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet publish src\Notch.App -c Release -o "$env:LOCALAPPDATA\Programs\Notch"
Start-Process "$env:LOCALAPPDATA\Programs\Notch\Notch.exe"
```

Danach im Tray „Mit Windows starten" aktivieren.

**Update:** `powershell -ExecutionPolicy Bypass -File scripts\update-notch.ps1` holt Änderungen von GitHub, baut neu und startet das Notch neu. Schlägt der Build fehl, läuft die alte Version weiter.

**Neues Release:** Tag pushen (`git tag v1.1.0` + `git push origin v1.1.0`). GitHub baut die eigenständige exe und hängt sie ans Release (`.github/workflows/release.yml`).

## Konfiguration

Drei Schichten, spätere überschreiben einzelne Werte der früheren:

1. `config.json` im Repo: Standardwerte, ohne persönliche Pfade
2. `config.local.json` im Projektordner: persönlich, nicht im Repo, wird beim Build mitkopiert
3. `%APPDATA%\Notch\config.json`: persönlich, unabhängig vom Installationsordner

Beispiel `config.local.json`:

```json
{ "vaultInbox": "C:\\Pfad\\zum\\Vault\\Inbox" }
```

| Schlüssel | Bedeutung |
|---|---|
| `port` | Port des lokalen Endpunkts (Standard 47321, nur localhost) |
| `hotkey` | Globaler Hotkey (Standard `Ctrl+Alt+N`) |
| `spotify`, `whatsApp`, `whatsAppPreview` | Quellen an/aus, WhatsApp-Text anzeigen |
| `focusMinutes`, `breakMinutes` | Fokus-Timer |
| `vaultInbox` | Zielordner für „Neue Notiz" (leer = Button aus) |
| `claudeCommand` | Fallback, wenn keine Claude-Code-Session bekannt ist |

Log: `%APPDATA%\Notch\notch.log`

## Events senden

Lokaler HTTP-Endpunkt `http://localhost:47321`, geschützt mit einem Token in `%APPDATA%\Notch\token` (oder Umgebungsvariable `NOTCH_TOKEN`).

```powershell
python scripts\notch_send.py --source meinskript --kind running --title "Import" --subtitle "läuft …"
python scripts\notch_send.py --source meinskript --kind done --title "Import fertig" --subtitle "42 Dateien"
python scripts\notch_send.py --source meinskript --dismiss
```

Arten: `running`, `input_needed`, `error`, `done`, `timer`, `info`. Mit `--action id:Label[:primary]` und `--wait 30` lassen sich Buttons anzeigen und die Auswahl abwarten. Läuft das Notch nicht, endet das Skript nach max. 1 s still.

**Claude-Code-Hooks:** Block aus `scripts/claude-hooks.json` in `%USERPROFILE%\.claude\settings.json` übernehmen, am besten mit vollem Pfad zu `python.exe`.

## Entwicklung

```
src/Notch.App/
├─ Views/IslandWindow.xaml(.cs)   Insel, Animationen
├─ ViewModels/IslandViewModel.cs  Zustände (Idle/Compact/Expanded/Large)
├─ Services/EventServer.cs        HTTP-Endpunkt
├─ Services/EventResolver.cs      Prioritäten, Queue, Ausblenden
├─ Services/*Service.cs           Hotkey, Tray, Timer, Spotify, WhatsApp, Peek
scripts/
├─ notch_send.py      Client für eigene Skripte
├─ claude_hook.py     Claude-Code-Hook
├─ test-notch.ps1     Smoke-Test mit Screenshots → test-results/
└─ update-notch.ps1   Pull + Build + Neustart
```
