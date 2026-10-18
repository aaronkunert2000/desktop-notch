# update-notch.ps1 - Aenderungen von GitHub holen, Release bauen, Notch neu starten.
# Aufruf: powershell -ExecutionPolicy Bypass -File scripts\update-notch.ps1 [-NoPull]
# Baut zuerst in einen Temp-Ordner: schlaegt der Build fehl, laeuft das alte Notch unveraendert weiter.
# Hinweis: Datei absichtlich ohne Umlaute (Windows PowerShell 5.1 liest UTF-8 sonst falsch).

param([switch]$NoPull)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\Notch'
$build  = Join-Path $env:TEMP 'notch-build'

# Meldung ans Notch direkt per HTTP (ohne Python, funktioniert auch im Hintergrund-Watcher)
function Notify([string]$kind, [string]$title, [string]$subtitle) {
    try {
        $token = (Get-Content (Join-Path $env:APPDATA 'Notch\token') -Raw).Trim()
        $body  = @{ source = 'notch-update'; kind = $kind; title = $title; subtitle = $subtitle } | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri 'http://localhost:47321/event' -Method Post -TimeoutSec 2 `
            -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8' `
            -Headers @{ 'X-Notch-Token' = $token } | Out-Null
    } catch { }
}

Set-Location $root

# 1. Aenderungen holen (nur wenn lokal nichts Uncommittetes liegt)
if (-not $NoPull) {
    $dirty = git status --porcelain --untracked-files=no
    if ($dirty) {
        Write-Host 'Lokale, nicht committete Aenderungen - Update abgebrochen:'
        $dirty | Write-Host
        Notify 'error' 'Notch-Update abgebrochen' 'Lokale Aenderungen im Projektordner'
        exit 1
    }
    $before = git rev-parse HEAD
    git pull --ff-only --quiet
    if ($LASTEXITCODE -ne 0) {
        Notify 'error' 'Notch-Update fehlgeschlagen' 'git pull nicht moeglich'
        exit 1
    }
    if ((git rev-parse HEAD) -eq $before) {
        Write-Host 'Bereits aktuell.'
        exit 0
    }
}

# 2. Bauen (Temp-Ordner)
Write-Host 'Baue Release ...'
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
dotnet publish (Join-Path $root 'src\Notch.App') -c Release -o $build --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Notify 'error' 'Notch-Build fehlgeschlagen' 'Altes Notch laeuft weiter'
    exit 1
}

# 3. Austauschen
# Alle Prozesse beenden, die Dateien aus dem Installationsordner nutzen, und warten bis sie weg sind
# (ein zweiter Start wuerde sonst als Aus-Schalter wirken)
function Stop-Notch {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'Notch' -or ($_.Path -and $_.Path.StartsWith($target, 'OrdinalIgnoreCase')) } |
        ForEach-Object {
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $_.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
}
Stop-Notch
New-Item -ItemType Directory -Force -Path $target | Out-Null

# Dateiweise kopieren mit Wiederholung: Virenscanner oder ein langsam beendeter Prozess sperren Dateien kurz
$failed = @()
foreach ($file in Get-ChildItem -Path $build -Recurse -File) {
    $dest = Join-Path $target ($file.FullName.Substring($build.Length).TrimStart('\'))
    New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
    $ok = $false
    for ($try = 1; $try -le 10 -and -not $ok; $try++) {
        try { Copy-Item -LiteralPath $file.FullName -Destination $dest -Force -ErrorAction Stop; $ok = $true }
        catch { if ($try -eq 5) { Stop-Notch }; Start-Sleep -Milliseconds 500 }
    }
    if (-not $ok) { $failed += $file.Name }
}
if ($failed.Count -gt 0) {
    Write-Host "Nicht kopiert (gesperrt): $($failed -join ', ')"
    Notify 'error' 'Notch-Update unvollstaendig' "$($failed.Count) Datei(en) gesperrt, bitte PC neu starten"
    Start-Process -FilePath (Join-Path $target 'Notch.exe') -ErrorAction SilentlyContinue
    exit 1
}
$exe = Join-Path $target 'Notch.exe'

# 4. Autostart bei der Anmeldung (ausser im Tray bewusst ausgeschaltet)
if (-not (Test-Path (Join-Path $env:APPDATA 'Notch\autostart-off'))) {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Notch' -Value "`"$exe`""
}

# 5. Startmenue-Verknuepfung: Ctrl+Alt+Shift+N schaltet das Notch an und aus
$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Notch.lnk'))
$lnk.TargetPath       = $exe
$lnk.WorkingDirectory = $target
$lnk.Hotkey           = 'CTRL+ALT+SHIFT+N'
$lnk.Description      = 'Desktop Notch an/aus'
$lnk.Save()

Start-Process -FilePath $exe
Start-Sleep -Seconds 3

$commit = git log -1 --pretty=%s
Notify 'done' 'Notch aktualisiert' $commit
Write-Host "Notch aktualisiert: $commit"
