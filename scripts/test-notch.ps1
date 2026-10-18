# test-notch.ps1 - Smoke-Test: bauen, starten, Testfolge senden, Screenshots.
# Ergebnisse landen in <Projekt>\test-results\ (wird bei jedem Lauf geleert).
# Aufruf: powershell -ExecutionPolicy Bypass -File scripts\test-notch.ps1
# Hinweis: Datei absichtlich ohne Umlaute (Windows PowerShell 5.1 liest UTF-8 sonst falsch).

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'test-results'
$send = Join-Path $root 'scripts\notch_send.py'
$hook = Join-Path $root 'scripts\claude_hook.py'

New-Item -ItemType Directory -Force -Path $out | Out-Null
Get-ChildItem -Path $out | Remove-Item -Recurse -Force
$summary = Join-Path $out 'summary.txt'

# Eigener Datenordner fuer den Test: echte Ablage, Token und Log bleiben unberuehrt
$env:NOTCH_DATA_DIR = Join-Path $out 'data'
New-Item -ItemType Directory -Force -Path $env:NOTCH_DATA_DIR | Out-Null

function Step([string]$msg) {
    $line = "$(Get-Date -Format 'HH:mm:ss') $msg"
    Write-Host $line
    Add-Content -Path $summary -Value $line -Encoding UTF8
}

function Send {
    & python $send @args
    Add-Content -Path $summary -Value "    notch_send $($args -join ' ') -> exit $LASTEXITCODE" -Encoding UTF8
}

# Screenshots in echten Pixeln (DPI-aware), oberer mittlerer Bereich
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Namespace Native -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[Native.Dpi]::SetProcessDPIAware() | Out-Null

function Shot([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $w = [Math]::Min(1400, $b.Width); $h = 520
    $x = $b.X + [int](($b.Width - $w) / 2)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $b.Y, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Step "Screenshot $name.png"
}

# 1. Installiertes Notch merken und beenden (wird am Ende wieder gestartet), bauen
$installed = Get-Process -Name Notch -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Path
Get-Process -Name Notch -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name Notch -ErrorAction SilentlyContinue | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
Step 'Build ...'
dotnet build (Join-Path $root 'src\Notch.App') -c Debug *> (Join-Path $out 'build.txt')
if ($LASTEXITCODE -ne 0) { Step 'BUILD FEHLGESCHLAGEN - siehe build.txt'; exit 1 }
Step 'Build ok'

# 2. Starten
$exe = Get-ChildItem -Path (Join-Path $root 'src\Notch.App\bin\Debug') -Recurse -Filter 'Notch.exe' |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { Step 'Notch.exe nicht gefunden'; exit 1 }
$proc = Start-Process -FilePath $exe.FullName -PassThru
Start-Sleep -Seconds 3
Step "Gestartet: $($exe.FullName) (PID $($proc.Id))"
Step "Health: $(& python $send --health)"
Shot '00-start'

# 3. Testfolge
Send --source test --kind running --title 'Test laeuft' --subtitle 'Smoke-Test'
Start-Sleep -Milliseconds 1200; Shot '01-running'

Send --source test --kind input_needed --title 'Freigabe?' --subtitle 'Smoke-Test' --action approve:Freigeben:primary --action deny:Ablehnen
Start-Sleep -Milliseconds 1200; Shot '02-input-needed'

# Prioritaet: running einer anderen Quelle darf die Freigabe NICHT verdraengen
Send --source other --kind running --title 'Darf nicht erscheinen' --subtitle 'Prioritaet'
Start-Sleep -Milliseconds 800; Shot '03-priority-input-bleibt'
Send --source test --dismiss
Start-Sleep -Milliseconds 1200; Shot '04-nach-dismiss-other-running'
Send --source other --dismiss

Send --source test --kind done --title 'Fertig' --subtitle 'Smoke-Test' --ttl 3
Start-Sleep -Milliseconds 1200; Shot '05-done'

Send --source test --kind error --title 'Fehler-Test' --subtitle 'Exit 1'
Start-Sleep -Milliseconds 1200; Shot '06-error'
Send --source test --dismiss

# Stille Updates: gleicher Vorgang, nur Titel aendert sich
Send --source test --id t1 --kind timer --title 'Fokus 24:59' --subtitle 'Update-Test'
Start-Sleep -Milliseconds 300
Send --source test --id t1 --kind timer --title 'Fokus 24:58' --subtitle 'Update-Test'
Start-Sleep -Milliseconds 800; Shot '07-silent-update'
Send --source test --id t1 --dismiss

# Claude-Hook simulieren
'{"hook_event_name":"UserPromptSubmit","session_id":"smoketest1","cwd":"C:\\Projekte\\Demo"}' | python $hook
Start-Sleep -Milliseconds 1200; Shot '08-hook-running'
'{"hook_event_name":"Stop","session_id":"smoketest1","cwd":"C:\\Projekte\\Demo"}' | python $hook
Start-Sleep -Milliseconds 1200; Shot '09-hook-done'

Start-Sleep -Seconds 6; Shot '10-idle'

# Maus und Tastatur simulieren (Maus waehrenddessen nicht bewegen)
Add-Type -Namespace Native -Name Input -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, System.IntPtr extra);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, System.IntPtr extra);
'@
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$cx = $screen.X + [int]($screen.Width / 2)
$orig = [System.Windows.Forms.Cursor]::Position
function MoveTo([int]$y) { [Native.Input]::SetCursorPos($cx, $screen.Y + $y) | Out-Null }
function MoveAway { [Native.Input]::SetCursorPos($screen.X + 40, $screen.Bottom - 120) | Out-Null }
function WheelDown { [Native.Input]::mouse_event(0x0800, 0, 0, -120, [IntPtr]::Zero) }
function Hotkey {   # Ctrl+Alt+N
    [Native.Input]::keybd_event(0x11, 0, 0, [IntPtr]::Zero); [Native.Input]::keybd_event(0x12, 0, 0, [IntPtr]::Zero)
    [Native.Input]::keybd_event(0x4E, 0, 0, [IntPtr]::Zero); [Native.Input]::keybd_event(0x4E, 0, 2, [IntPtr]::Zero)
    [Native.Input]::keybd_event(0x12, 0, 2, [IntPtr]::Zero); [Native.Input]::keybd_event(0x11, 0, 2, [IntPtr]::Zero)
}

# 4. Ablage: Fehler-Test und Claude fertig sind ungelesen -> Maus-Reveal zeigt die Ablage statt der Uhrzeit
MoveTo 0
Start-Sleep -Milliseconds 900; Shot '11-peek-ablage'
MoveTo 30
Start-Sleep -Milliseconds 900; Shot '12-ablage-hover'
WheelDown
Start-Sleep -Milliseconds 900; Shot '13-mausrad-quick-actions'
MoveAway
Start-Sleep -Seconds 13; Shot '14-weg'   # 8 s Rueckkehr + 4 s Anzeigedauer

# 5. Hotkey: oeffnet die Ablage, zweites Druecken blendet aus
Hotkey
Start-Sleep -Milliseconds 1200; Shot '15-hotkey-ablage'
Hotkey
Start-Sleep -Milliseconds 1200; Shot '16-hotkey-aus'

# 6. Seiten-Punkte: laufender Vorgang + Timer + Ablage = 3 Seiten
Send --source build --kind running --title 'Build laeuft' --subtitle 'Seiten-Test'
Send --source test --id t2 --kind timer --title 'Fokus 12:00' --subtitle 'Seiten-Test' --sticky
Start-Sleep -Milliseconds 1200; Shot '17-kompakt-badge'
MoveTo 30
Start-Sleep -Milliseconds 900; Shot '18-hover-punkte'
WheelDown
Start-Sleep -Milliseconds 900; Shot '19-seite-2'
WheelDown
Start-Sleep -Milliseconds 900; Shot '20-seite-3'
MoveAway
Start-Sleep -Seconds 10; Shot '21-zurueck-zur-vorgabe'
Send --source build --dismiss
Send --source test --id t2 --dismiss

# 7. Abschluss
Copy-Item (Join-Path $env:NOTCH_DATA_DIR 'notch.log') (Join-Path $out 'notch.log') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $env:NOTCH_DATA_DIR 'inbox.json') (Join-Path $out 'inbox.json') -ErrorAction SilentlyContinue
Stop-Process -Id $proc.Id -ErrorAction SilentlyContinue
Wait-Process -Id $proc.Id -Timeout 10 -ErrorAction SilentlyContinue
Remove-Item Env:\NOTCH_DATA_DIR
if ($installed) { Start-Process -FilePath $installed; Step "Installiertes Notch wieder gestartet: $installed" }
Step 'Fertig. Ergebnisse in test-results\'
