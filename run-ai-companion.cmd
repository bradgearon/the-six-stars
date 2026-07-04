@echo off
setlocal EnableExtensions
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Run-AiCompanionLauncher.ps1 %*
