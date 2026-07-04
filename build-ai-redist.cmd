@echo off
setlocal EnableExtensions
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build-AiRuntimeRedist.ps1 %*
