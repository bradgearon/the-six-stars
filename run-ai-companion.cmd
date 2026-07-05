@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo Starting Six Stars local AI services only. The desktop app will NOT be launched.
echo Leave this window open while using the desktop UI or Android bridge.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Run-AiCompanionLauncher.ps1 -NoUi %*
