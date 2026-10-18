@echo off
rem Doppelklick: neuesten Stand holen und Smoke-Test starten. Ergebnisse: test-results\
cd /d "%~dp0"
git pull --ff-only
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\test-notch.ps1
echo.
echo Fertig. Ergebnisse liegen in test-results. Maus und Tastatur sind wieder frei.
pause
