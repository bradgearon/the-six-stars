@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Full AI companion launcher.
rem This is the redistributable/prebuilt-engine path: it starts the local engine
rem services through the integrated launcher and opens the desktop app.
rem
rem Use run-ai-services.cmd when you only want the native services window.
rem Use run-ai-source-services.cmd for the legacy repo .venv/Python debug path.
echo Starting Six Stars AI Companion using the prebuilt engine launcher.
echo This should start the local LLM/ASR/TTS engine services and open the desktop app.
echo.
call launch.cmd %*
