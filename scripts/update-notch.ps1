# update-notch.ps1 - Aenderungen von GitHub holen, Release bauen, Notch neu starten.
# Aufruf: powershell -ExecutionPolicy Bypass -File scripts\update-notch.ps1 [-NoPull]
# Baut zuerst in einen Temp-Ordner: schlaegt der Build fehl, laeuft das alte Notch unveraendert weiter.
# Hinweis: Datei absichtlich ohne Umlaute (Windows PowerShell 5.1 liest UTF-8 sonst falsch).

param([switch]$NoPull)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\Notch'
$build  = Join-Path $env:TEMP 'notch-build'
$send   = Join-Path $root 'scripts\notch_send.py'

function Notify([string]$kind, [string]$title, [string]$subtitle) {
    try { & python $send --source notch-update --kind $kind --title $title --subtitle $subtitle } catch { }
}

Set-Location $root

# 1. Aenderungen holen (nur wenn lokal nichts Uncommittetes liegt)
if (-not $NoPull) {
    $dirty = git status --porcelain
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

# 3. Austauschen und neu starten
Stop-Process -Name Notch -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $build '*') -Destination $target -Recurse -Force
Start-Process -FilePath (Join-Path $target 'Notch.exe')
Start-Sleep -Seconds 3

$commit = git log -1 --pretty=%s
Notify 'done' 'Notch aktualisiert' $commit
Write-Host "Notch aktualisiert: $commit"
