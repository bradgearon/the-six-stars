@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Full AI companion launcher.
rem This is the redistributable/prebuilt-engine path: it starts the local engine
rem services through the integrated launcher and opens the desktop app.
rem
rem Use run-ai-services.cmd when you only want the services window and want the
rem Python services to run from the repo .venv for debugging.
echo Starting Six Stars AI Companion using the prebuilt engine launcher.
echo This should start the local LLM/ASR/TTS engine services and open the desktop app.
echo.
call launch.cmd %*
